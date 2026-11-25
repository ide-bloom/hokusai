using System.Diagnostics.CodeAnalysis;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;

namespace Hokusai.Runtime;

/// <summary>A throwaway database. Disposing it drops the database or deletes the file.</summary>
public sealed class HkCopy(string connectionString, Func<Task> cleanup) : IAsyncDisposable
{
    public string ConnectionString { get; } = connectionString;

    public async ValueTask DisposeAsync() => await cleanup().ConfigureAwait(false);
}

/// <summary>Creates fresh database copies from baselines: a SQL Server backup restored under a new name, or a file copy.</summary>
public static class HkCopies
{
    /// <param name="serverConnectionString">Any connection to the server; the catalog is replaced.</param>
    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Generated database names and local file paths only.")]
    public static async Task<HkCopy> RestoreSqlServerAsync(string serverConnectionString, string backupPath, CancellationToken ct)
    {
        var name = "hokusai_copy_" + Guid.NewGuid().ToString("N")[..12];
        var work = WorkDirectory();
        var master = new SqlConnectionStringBuilder(serverConnectionString) { InitialCatalog = "master", Pooling = false }.ConnectionString;
        await using (var connection = new SqlConnection(master))
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            var moves = new List<string>();
            await using (var list = connection.CreateCommand())
            {
                list.CommandText = "RESTORE FILELISTONLY FROM DISK = @path";
                list.Parameters.AddWithValue("@path", Path.GetFullPath(backupPath));
                await using var reader = await list.ExecuteReaderAsync(ct).ConfigureAwait(false);
                var index = 0;
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    var logical = reader.GetString(reader.GetOrdinal("LogicalName"));
                    var type = reader.GetString(reader.GetOrdinal("Type"));
                    var file = Path.Combine(work, $"{name}_{index++}{(type == "L" ? ".ldf" : ".mdf")}");
                    moves.Add($"MOVE N'{Escape(logical)}' TO N'{Escape(file)}'");
                }
            }

            await using var restore = connection.CreateCommand();
            restore.CommandText = $"RESTORE DATABASE [{name}] FROM DISK = N'{Escape(Path.GetFullPath(backupPath))}' WITH {string.Join(", ", moves)}, RECOVERY";
            restore.CommandTimeout = 600;
            await restore.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        var cs = new SqlConnectionStringBuilder(serverConnectionString) { InitialCatalog = name }.ConnectionString;
        return new HkCopy(cs, () => DropSqlServerAsync(master, name, work));
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Generated database names only.")]
    public static async Task DropSqlServerAsync(string masterConnectionString, string name, string? work)
    {
        SqlConnection.ClearAllPools();
        await using (var connection = new SqlConnection(masterConnectionString))
        {
            await connection.OpenAsync(CancellationToken.None).ConfigureAwait(false);
            await using var drop = connection.CreateCommand();
            drop.CommandText = $"IF DB_ID(N'{Escape(name)}') IS NOT NULL BEGIN ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}]; END";
            await drop.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
        }

        if (work is not null)
        {
            TryDelete(work);
        }
    }

    /// <param name="connectionStringTemplate">Connection string containing <c>{file}</c> where the database path goes.</param>
    public static HkCopy CopyFile(string baselinePath, string connectionStringTemplate)
    {
        var work = WorkDirectory();
        var target = Path.Combine(work, "copy" + Path.GetExtension(baselinePath));
        File.Copy(baselinePath, target);
        return new HkCopy(connectionStringTemplate.Replace("{file}", target, StringComparison.Ordinal), () =>
        {
            ReleasePools();
            TryDelete(work);
            return Task.CompletedTask;
        });
    }

    public static string WorkDirectory()
    {
        var work = Path.Combine(Path.GetTempPath(), "hokusai-copies", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        return work;
    }

    public static void ReleasePools()
    {
        SqliteConnection.ClearAllPools();
        System.Data.Odbc.OdbcConnection.ReleaseObjectPool();
        if (OperatingSystem.IsWindows())
        {
            System.Data.OleDb.OleDbConnection.ReleaseObjectPool();
        }
    }

    private static string Escape(string text) => text.Replace("'", "''", StringComparison.Ordinal);

    public static void TryDelete(string directory)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }

                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // File databases are released asynchronously by some providers (ACE in particular).
                Thread.Sleep(200 * (attempt + 1));
            }
        }
    }
}
