using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Hokusai.Core;
using Hokusai.Runtime;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;

namespace Hokusai.Verify;

/// <summary>
/// Baselines and copies. Verification never touches the configured database: SQL Server sources are backed up at
/// each start mark and restored under a new name, file databases are copied, and small test databases can be
/// rebuilt from a seed script.
/// </summary>
public static partial class DatabaseCopies
{
    /// <summary>Captures the database as it is now into <paramref name="directory"/> and returns the file name.</summary>
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "The catalog name is a quoted identifier from configuration; the path is a parameter.")]
    public static async Task<string> CreateBaselineAsync(DataSourceConfig source, string directory, CancellationToken ct)
    {
        Directory.CreateDirectory(directory);
        switch (source.Provider)
        {
            case ProviderKind.SqlServer:
                {
                    var builder = new SqlConnectionStringBuilder(source.ConnectionString);
                    var path = Path.Combine(Path.GetFullPath(directory), "baseline.bak");
                    await using var connection = new SqlConnection(source.ConnectionString);
                    await connection.OpenAsync(ct).ConfigureAwait(false);
                    await using var command = connection.CreateCommand();
                    command.CommandText = $"BACKUP DATABASE {SqlNames.Quote(builder.InitialCatalog, ProviderKind.SqlServer)} TO DISK = @path WITH COPY_ONLY, INIT, FORMAT";
                    command.Parameters.AddWithValue("@path", path);
                    command.CommandTimeout = 600;
                    await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                    return "baseline.bak";
                }

            case ProviderKind.Sqlite:
            case ProviderKind.Odbc when IsSqliteFile(source.FilePath):
                {
                    var name = "baseline" + Path.GetExtension(source.FilePath!);
                    await using var from = new SqliteConnection($"Data Source={source.FilePath};Mode=ReadOnly;Pooling=False");
                    await using var to = new SqliteConnection($"Data Source={Path.Combine(directory, name)};Pooling=False");
                    await from.OpenAsync(ct).ConfigureAwait(false);
                    await to.OpenAsync(ct).ConfigureAwait(false);

                    // The online backup API gives a consistent copy even while the app holds the file open.
                    from.BackupDatabase(to);
                    return name;
                }

            default:
                {
                    if (source.FilePath is null)
                    {
                        throw new InvalidOperationException($"Baselines for {source.Provider} sources need a database file.");
                    }

                    var name = "baseline" + Path.GetExtension(source.FilePath);
                    File.Copy(source.FilePath, Path.Combine(directory, name), overwrite: true);
                    return name;
                }
        }
    }

    public static bool IsSqliteFile(string? path) =>
        path is not null && (path.EndsWith(".db", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".sqlite", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".sqlite3", StringComparison.OrdinalIgnoreCase));

    /// <summary>The connection string with the database file replaced by <c>{file}</c>, as copies expect.</summary>
    public static string FileTemplate(DataSourceConfig source) => source.ConnectionStringFor("{file}");

    /// <summary>Creates a fresh copy from a baseline file (.bak, .mdf, .db, .accdb) or seed script (.sql).</summary>
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Generated database names and local paths only.")]
    public static async Task<HkCopy> CreateCopyAsync(DataSourceConfig source, string file, CancellationToken ct)
    {
        var extension = Path.GetExtension(file).ToUpperInvariant();
        if (source.Provider == ProviderKind.SqlServer)
        {
            if (extension == ".BAK")
            {
                return await HkCopies.RestoreSqlServerAsync(source.ConnectionString, file, ct).ConfigureAwait(false);
            }

            var name = "hokusai_copy_" + Guid.NewGuid().ToString("N")[..12];
            var work = HkCopies.WorkDirectory();
            var master = new SqlConnectionStringBuilder(source.ConnectionString) { InitialCatalog = "master", Pooling = false }.ConnectionString;
            await using (var connection = new SqlConnection(master))
            {
                await connection.OpenAsync(ct).ConfigureAwait(false);
                await using var command = connection.CreateCommand();
                var mdfPath = Path.Combine(work, name + ".mdf");
                if (extension == ".MDF")
                {
                    File.Copy(file, mdfPath);
                }

                var mdf = mdfPath.Replace("'", "''", StringComparison.Ordinal);
                command.CommandText = extension switch
                {
                    ".MDF" => $"CREATE DATABASE [{name}] ON (FILENAME = N'{mdf}') FOR ATTACH_REBUILD_LOG",
                    ".SQL" => $"CREATE DATABASE [{name}] ON (NAME = N'{name}', FILENAME = N'{mdf}')",
                    _ => throw new NotSupportedException($"Cannot create a SQL Server copy from '{file}'. Use a .bak, .mdf or .sql file."),
                };
                command.CommandTimeout = 600;
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            var cs = source.ConnectionStringFor(name);
            if (extension == ".SQL")
            {
                await RunScriptAsync(ProviderKind.SqlServer, cs, await File.ReadAllTextAsync(file, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
            }

            return new HkCopy(cs, () => HkCopies.DropSqlServerAsync(master, name, work));
        }

        if (extension == ".SQL")
        {
            var work = HkCopies.WorkDirectory();
            var seeded = Path.Combine(work, "seed" + Path.GetExtension(source.FilePath ?? ".db"));
            await using (var seed = new SqliteConnection($"Data Source={seeded};Pooling=False"))
            {
                await seed.OpenAsync(ct).ConfigureAwait(false);
                await RunBatchesAsync(seed, await File.ReadAllTextAsync(file, ct).ConfigureAwait(false), ct).ConfigureAwait(false);
            }

            SqliteConnection.ClearAllPools();
            var copy = HkCopies.CopyFile(seeded, FileTemplate(source));
            return new HkCopy(copy.ConnectionString, async () =>
            {
                await copy.DisposeAsync().ConfigureAwait(false);
                HkCopies.TryDelete(work);
            });
        }

        return HkCopies.CopyFile(file, FileTemplate(source));
    }

    /// <summary>Runs a seed script, splitting T-SQL batches on GO lines.</summary>
    public static async Task RunScriptAsync(ProviderKind provider, string connectionString, string script, CancellationToken ct)
    {
        await using var connection = await HkDb.OpenAsync(provider, connectionString, ct).ConfigureAwait(false);
        await RunBatchesAsync(connection, script, ct).ConfigureAwait(false);
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Seed scripts are trusted files supplied by the user.")]
    private static async Task RunBatchesAsync(System.Data.Common.DbConnection connection, string script, CancellationToken ct)
    {
        foreach (var batch in GoSeparator().Split(script).Where(b => !string.IsNullOrWhiteSpace(b)))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = batch;
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    [GeneratedRegex(@"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex GoSeparator();
}
