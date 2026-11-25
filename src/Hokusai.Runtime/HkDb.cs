using System.Data.Common;
using System.Data.Odbc;
using System.Data.OleDb;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;

namespace Hokusai.Runtime;

/// <summary>SQL dialect behind a connection; an ODBC connection can front any of them.</summary>
public enum SqlDialect
{
    SqlServer,
    Sqlite,
    Access,
    Generic,
}

/// <summary>The result of one executed command: rows affected plus the last result set it produced.</summary>
public sealed class HkResult
{
    public long RowsAffected { get; set; }
    public List<string> Columns { get; } = [];
    public List<object?[]> Rows { get; } = [];

    public object? Value(int row, string column)
    {
        var index = Columns.FindIndex(c => string.Equals(c, column, StringComparison.OrdinalIgnoreCase));
        if (index < 0 && column.StartsWith('#') && int.TryParse(column[1..], CultureInfo.InvariantCulture, out var ordinal))
        {
            index = ordinal;
        }

        if (index < 0 || index >= Columns.Count)
        {
            throw new HokusaiExecutionException($"Result has no column '{column}'.");
        }

        if (row >= Rows.Count)
        {
            throw new HokusaiExecutionException($"Expected at least {row + 1} row(s) for column '{column}', got {Rows.Count}.");
        }

        return Rows[row][index];
    }
}

public sealed class HokusaiExecutionException : Exception
{
    public HokusaiExecutionException()
    {
    }

    public HokusaiExecutionException(string message)
        : base(message)
    {
    }

    public HokusaiExecutionException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>
/// Opens connections and runs parameterized commands for every provider. Templates store SQL with neutral
/// placeholders (<c>@hk0</c>, <c>@hk1</c>, ...) which are rendered into the provider's marker style here.
/// </summary>
public static class HkDb
{
    public const string PlaceholderPrefix = "@hk";

    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Ownership passes to the caller.")]
    public static async Task<DbConnection> OpenAsync(ProviderKind provider, string connectionString, CancellationToken ct)
    {
        DbConnection connection = provider switch
        {
            ProviderKind.SqlServer => new SqlConnection(connectionString),
            ProviderKind.Odbc => new OdbcConnection(connectionString),
            ProviderKind.Sqlite => new SqliteConnection(connectionString),
            ProviderKind.Access => CreateOleDb(connectionString),
            _ => throw new ArgumentOutOfRangeException(nameof(provider)),
        };
        try
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    [SuppressMessage("Performance", "CA1859:Use concrete types when possible for improved performance", Justification = "Returning OleDbConnection here would leak the Windows-only type to callers without a platform guard.")]
    private static DbConnection CreateOleDb(string cs)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Access databases require the ACE OLE DB provider on Windows.");
        }

        return CreateOleDbCore(cs);
    }

    [SupportedOSPlatform("windows")]
    private static OleDbConnection CreateOleDbCore(string cs) => new(cs);

    public static SqlDialect DialectOf(ProviderKind provider, DbConnection connection) => provider switch
    {
        ProviderKind.SqlServer => SqlDialect.SqlServer,
        ProviderKind.Sqlite => SqlDialect.Sqlite,
        ProviderKind.Access => SqlDialect.Access,
        _ => OdbcDialect(connection),
    };

    private static SqlDialect OdbcDialect(DbConnection connection)
    {
        var driver = connection is OdbcConnection odbc ? odbc.Driver : "";
        if (driver.Contains("sqlite", StringComparison.OrdinalIgnoreCase))
        {
            return SqlDialect.Sqlite;
        }

        if (driver.Contains("sqlsrv", StringComparison.OrdinalIgnoreCase) ||
            driver.Contains("sqlncli", StringComparison.OrdinalIgnoreCase) ||
            driver.Contains("msodbcsql", StringComparison.OrdinalIgnoreCase))
        {
            return SqlDialect.SqlServer;
        }

        if (driver.Contains("ace", StringComparison.OrdinalIgnoreCase) || driver.Contains("odbcjt", StringComparison.OrdinalIgnoreCase))
        {
            return SqlDialect.Access;
        }

        return SqlDialect.Generic;
    }

    public static bool UsesPositionalMarkers(ProviderKind provider) => provider is ProviderKind.Odbc or ProviderKind.Access;

    public static string Placeholder(int index) => PlaceholderPrefix + index.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Rewrites neutral placeholders into provider markers. Returns the parameter names in marker order, which
    /// matters for positional providers where the same placeholder used twice must be bound twice.
    /// </summary>
    public static (string Sql, IReadOnlyList<string> Order) Render(string sql, ProviderKind provider)
    {
        var order = new List<string>();
        var sb = new StringBuilder(sql.Length);
        var i = 0;
        while (i < sql.Length)
        {
            var ch = sql[i];
            if (ch is '\'' or '"' or '[' or '`')
            {
                var close = ch == '[' ? ']' : ch;
                var end = i + 1;
                while (end < sql.Length)
                {
                    if (sql[end] == close)
                    {
                        if (end + 1 < sql.Length && sql[end + 1] == close)
                        {
                            end += 2;
                            continue;
                        }

                        break;
                    }

                    end++;
                }

                end = Math.Min(end, sql.Length - 1);
                sb.Append(sql, i, end - i + 1);
                i = end + 1;
                continue;
            }

            if (ch == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                var end = sql.IndexOf('\n', i);
                end = end < 0 ? sql.Length : end;
                sb.Append(sql, i, end - i);
                i = end;
                continue;
            }

            if (ch == '@' && string.CompareOrdinal(sql, i, PlaceholderPrefix, 0, PlaceholderPrefix.Length) == 0)
            {
                var end = i + PlaceholderPrefix.Length;
                while (end < sql.Length && char.IsAsciiDigit(sql[end]))
                {
                    end++;
                }

                if (end > i + PlaceholderPrefix.Length && (end >= sql.Length || !(char.IsLetterOrDigit(sql[end]) || sql[end] == '_')))
                {
                    var name = sql[(i + 1)..end];
                    order.Add(name);
                    sb.Append(UsesPositionalMarkers(provider) ? "?" : "@" + name);
                    i = end;
                    continue;
                }
            }

            sb.Append(ch);
            i++;
        }

        return (sb.ToString(), order);
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Command text comes from verified templates; every value is bound as a parameter.")]
    public static async Task<HkResult> ExecuteAsync(
        DbConnection connection,
        DbTransaction? transaction,
        ProviderKind provider,
        string sql,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken ct)
    {
        var (text, order) = Render(sql, provider);
        var dialect = DialectOf(provider, connection);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = text;
        var bound = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in order)
        {
            if (!UsesPositionalMarkers(provider) && !bound.Add(name))
            {
                continue;
            }

            if (!arguments.TryGetValue(name, out var value))
            {
                throw new HokusaiExecutionException($"No value supplied for placeholder @{name}.");
            }

            var parameter = command.CreateParameter();
            parameter.ParameterName = UsesPositionalMarkers(provider) ? "?" : "@" + name;
            parameter.Value = ToParameterValue(value, provider, dialect);
            command.Parameters.Add(parameter);
        }

        var result = new HkResult();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        do
        {
            if (reader.FieldCount == 0)
            {
                continue;
            }

            result.Columns.Clear();
            result.Rows.Clear();
            for (var c = 0; c < reader.FieldCount; c++)
            {
                result.Columns.Add(reader.GetName(c));
            }

            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var row = new object?[reader.FieldCount];
                for (var c = 0; c < reader.FieldCount; c++)
                {
                    row[c] = await reader.IsDBNullAsync(c, ct).ConfigureAwait(false) ? null : reader.GetValue(c);
                }

                result.Rows.Add(row);
            }
        }
        while (await reader.NextResultAsync(ct).ConfigureAwait(false));

        result.RowsAffected = reader.RecordsAffected;
        return result;
    }

    private static object ToParameterValue(object? value, ProviderKind provider, SqlDialect dialect) => value switch
    {
        null => DBNull.Value,
        DateOnly d => d.ToDateTime(TimeOnly.MinValue),
        bool b when dialect == SqlDialect.Sqlite => b ? 1 : 0,

        // The SQLite ODBC driver rejects SQL_NUMERIC parameters (how System.Data.Odbc binds decimal); invariant text
        // is stored as the same number through SQLite's NUMERIC affinity.
        decimal m when provider == ProviderKind.Odbc && dialect == SqlDialect.Sqlite => HkValue.CanonDecimal(m),
        _ => value,
    };

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Identifiers are quoted from schema metadata, never taken from input.")]
    public static async Task<List<object?[]>> QueryRowsAsync(DbConnection connection, DbTransaction? transaction, string sql, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        var rows = new List<object?[]>();
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var row = new object?[reader.FieldCount];
            for (var c = 0; c < reader.FieldCount; c++)
            {
                row[c] = await reader.IsDBNullAsync(c, ct).ConfigureAwait(false) ? null : reader.GetValue(c);
            }

            rows.Add(row);
        }

        return rows;
    }

    public static string QuoteIdentifier(string name, SqlDialect dialect) =>
        SqlNames.Quote(name, dialect switch
        {
            SqlDialect.SqlServer => ProviderKind.SqlServer,
            SqlDialect.Access => ProviderKind.Access,
            _ => ProviderKind.Sqlite,
        });
}
