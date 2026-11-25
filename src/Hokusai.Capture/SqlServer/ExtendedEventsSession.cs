using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Xml.Linq;
using Hokusai.Core;
using Microsoft.Data.SqlClient;

namespace Hokusai.Capture.SqlServer;

/// <summary>
/// A server-side Extended Events session filtered to one client process. It captures batch and RPC completions
/// (statement text with parameter values, row counts, session id) without any change to the app, and is dropped
/// when recording ends.
/// </summary>
/// <remarks>
/// Required permissions: ALTER ANY EVENT SESSION (SQL Server 2019 and earlier) or CREATE/ALTER/DROP ANY EVENT
/// SESSION (2022 and later) to manage the session, and VIEW SERVER STATE (2019 and earlier) or VIEW SERVER
/// PERFORMANCE STATE (2022 and later) to read its target. The LocalDB instance owner has all of them.
/// </remarks>
public sealed class ExtendedEventsSession : IAsyncDisposable
{
    private readonly string _connectionString;
    private string? _lastFile;
    private long? _lastOffset;

    private ExtendedEventsSession(string connectionString, string name, string filePattern)
    {
        _connectionString = connectionString;
        Name = name;
        FilePattern = filePattern;
    }

    public string Name { get; }

    public string FilePattern { get; }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "The session name is generated; the pid is an integer; the host and path are escaped literals required by CREATE EVENT SESSION.")]
    public static async Task<ExtendedEventsSession> StartAsync(string connectionString, int clientProcessId, string? clientHost, CancellationToken ct)
    {
        var name = "hokusai_" + Guid.NewGuid().ToString("N")[..12];
        await using var connection = new SqlConnection(Master(connectionString));
        await connection.OpenAsync(ct).ConfigureAwait(false);

        // The server's own log folder is always writable by the server, which matters for non-LocalDB instances.
        string directory;
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT CAST(SERVERPROPERTY('ErrorLogFileName') AS nvarchar(4000))";
            var log = (string?)await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? throw new InvalidOperationException("Cannot determine the server log folder.");
            directory = log[..log.LastIndexOf('\\')];
        }

        var file = $@"{directory}\{name}.xel";
        var predicate = $"sqlserver.client_pid = {clientProcessId.ToString(CultureInfo.InvariantCulture)}";
        if (!string.IsNullOrEmpty(clientHost))
        {
            predicate += $" AND sqlserver.client_hostname = N'{clientHost.Replace("'", "''", StringComparison.Ordinal)}'";
        }

        const string actions = "ACTION(sqlserver.client_pid, sqlserver.client_hostname, sqlserver.session_id, sqlserver.database_name)";
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = $"""
                CREATE EVENT SESSION [{name}] ON SERVER
                ADD EVENT sqlserver.sql_batch_completed({actions} WHERE ({predicate})),
                ADD EVENT sqlserver.rpc_completed({actions} WHERE ({predicate}))
                ADD TARGET package0.event_file(SET filename = N'{file.Replace("'", "''", StringComparison.Ordinal)}', max_file_size = 64)
                WITH (MAX_DISPATCH_LATENCY = 1 SECONDS, EVENT_RETENTION_MODE = NO_EVENT_LOSS, STARTUP_STATE = OFF);
                ALTER EVENT SESSION [{name}] ON SERVER STATE = START;
                """;
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        return new ExtendedEventsSession(connectionString, name, $@"{directory}\{name}*.xel");
    }

    private static string Master(string cs) => new SqlConnectionStringBuilder(cs) { InitialCatalog = "master" }.ConnectionString;

    /// <summary>Returns events written since the previous call, oldest first.</summary>
    public async Task<List<StatementEvent>> ReadNewAsync(CancellationToken ct)
    {
        var events = new List<StatementEvent>();
        await using var connection = new SqlConnection(Master(_connectionString));
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT CAST(event_data AS nvarchar(max)), file_name, file_offset FROM sys.fn_xe_file_target_read_file(@pattern, NULL, @file, @offset)";
        cmd.Parameters.AddWithValue("@pattern", FilePattern);
        cmd.Parameters.AddWithValue("@file", (object?)_lastFile ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@offset", (object?)_lastOffset ?? DBNull.Value);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            // A buffer is written to the file once and never changes, so resuming after the last offset read
            // (which skips that whole buffer) neither loses nor repeats events.
            _lastFile = reader.GetString(1);
            _lastOffset = reader.GetInt64(2);
            if (Parse(reader.GetString(0)) is { } e)
            {
                events.Add(e);
            }
        }

        events.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
        return events;
    }

    /// <summary>Parses one event_data XML document into a statement event.</summary>
    public static StatementEvent? Parse(string xml)
    {
        var e = XElement.Parse(xml);
        var name = (string?)e.Attribute("name");
        string? Data(string n) => e.Elements("data").FirstOrDefault(d => (string?)d.Attribute("name") == n)?.Element("value")?.Value;
        string? Action(string n) => e.Elements("action").FirstOrDefault(d => (string?)d.Attribute("name") == n)?.Element("value")?.Value;
        var text = name switch
        {
            "sql_batch_completed" => Data("batch_text"),
            "rpc_completed" => Data("statement"),
            _ => null,
        };
        if (text is null || (string?)e.Attribute("timestamp") is not { } ts)
        {
            return null;
        }

        // sp_reset_connection is connection-pool housekeeping, not app work.
        if (name == "rpc_completed" && string.Equals(Data("object_name"), "sp_reset_connection", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return new StatementEvent
        {
            Source = "xe",
            Timestamp = DateTimeOffset.Parse(ts, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal),
            Text = text,
            RowCount = long.TryParse(Data("row_count"), CultureInfo.InvariantCulture, out var rows) ? rows : null,
            SessionId = int.TryParse(Action("session_id"), CultureInfo.InvariantCulture, out var sid) ? sid : null,
        };
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Generated session name.")]
    public async ValueTask DisposeAsync()
    {
        try
        {
            await using var connection = new SqlConnection(Master(_connectionString));
            await connection.OpenAsync().ConfigureAwait(false);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = $"IF EXISTS (SELECT 1 FROM sys.server_event_sessions WHERE name = N'{Name}') DROP EVENT SESSION [{Name}] ON SERVER;";
            await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        catch (SqlException)
        {
            // The server may be gone; a leftover session is stopped and harmless, and 'hokusai doctor' lists it.
        }

        var directory = Path.GetDirectoryName(FilePattern);
        if (directory is not null && Directory.Exists(directory))
        {
            foreach (var f in Directory.EnumerateFiles(directory, Name + "*.xel"))
            {
                try
                {
                    File.Delete(f);
                }
                catch (IOException)
                {
                    // Still held by the server for a moment after the drop; it is tiny and removed by doctor --clean.
                }
            }
        }
    }
}
