using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.Versioning;
using Hokusai.Capture.Odbc;
using Hokusai.Core;
using Hokusai.Runtime;
using Microsoft.Data.SqlClient;
using Microsoft.Win32;

namespace Hokusai.Engine;

public enum CheckStatus
{
    Ok,
    Warning,
    Failed,
}

public sealed record DoctorCheck(string Name, CheckStatus Status, string Detail);

/// <summary>Environment checks: data source, Extended Events permissions, drivers, the ODBC agent and ETW.</summary>
[SupportedOSPlatform("windows")]
public static class Doctor
{
    private static readonly string[] AgentFiles = ["hokusai_agent64.dll", "hokusai_launcher64.exe", "hokusai_agent32.dll", "hokusai_launcher32.exe"];

    public static async Task<List<DoctorCheck>> RunAsync(Workspace workspace, bool clean, CancellationToken ct)
    {
        var checks = new List<DoctorCheck>();
        var config = await workspace.LoadConfigAsync(ct).ConfigureAwait(false);
        var source = config.DataSource;
        if (source is null)
        {
            checks.Add(new("Data source", CheckStatus.Warning, "None configured; run 'hokusai connect'."));
        }
        else
        {
            try
            {
                await using var connection = await HkDb.OpenAsync(source.Provider, source.ConnectionString, ct).ConfigureAwait(false);
                var schema = await SchemaReader.ReadAsync(connection, source.Provider, ct).ConfigureAwait(false);
                checks.Add(new("Data source", CheckStatus.Ok, $"{source.Provider}: {source.Display} ({schema.Tables.Count} tables)"));
            }
            catch (Exception ex) when (ex is System.Data.Common.DbException or InvalidOperationException or PlatformNotSupportedException)
            {
                checks.Add(new("Data source", CheckStatus.Failed, ex.Message));
            }

            if (source.Provider == ProviderKind.SqlServer)
            {
                checks.AddRange(await SqlServerChecksAsync(source.ConnectionString, clean, ct).ConfigureAwait(false));
            }
        }

        checks.Add(LocalDb());
        var drivers = OdbcDrivers();
        checks.Add(new("ODBC drivers (64-bit)", CheckStatus.Ok, string.Join(", ", drivers.Where(d => d.Bits == 64).Select(d => d.Name))));
        checks.Add(new("ODBC drivers (32-bit)", CheckStatus.Ok, string.Join(", ", drivers.Where(d => d.Bits == 32).Select(d => d.Name))));
        checks.Add(drivers.Any(d => d.Name.Contains("SQLite", StringComparison.OrdinalIgnoreCase))
            ? new("SQLite ODBC driver", CheckStatus.Ok, "Installed.")
            : new("SQLite ODBC driver", CheckStatus.Warning, "Not installed; needed only for apps that reach SQLite through ODBC."));
        checks.Add(Registry.ClassesRoot.OpenSubKey("Microsoft.ACE.OLEDB.16.0") is not null || Registry.ClassesRoot.OpenSubKey("Microsoft.ACE.OLEDB.12.0") is not null
            ? new("ACE OLE DB provider", CheckStatus.Ok, "Registered (Access databases).")
            : new("ACE OLE DB provider", CheckStatus.Warning, "Not registered; needed only for Access databases."));

        var agent = OdbcAgent.FindAgentDirectory();
        if (agent is null)
        {
            checks.Add(new("ODBC agent", CheckStatus.Warning, "hokusai_agent{32,64}.dll not found; ODBC capture is unavailable. Build native/agent or set HOKUSAI_AGENT_DIR."));
        }
        else
        {
            var present = AgentFiles
                .Select(f => (f, File.Exists(Path.Combine(agent, f)) || File.Exists(Path.Combine(agent, "..", "..", "x86", "Release", f)))).ToList();
            checks.Add(new("ODBC agent", present.All(p => p.Item2) ? CheckStatus.Ok : CheckStatus.Warning,
                string.Join(", ", present.Select(p => p.f + (p.Item2 ? "" : " (missing)"))) + " in " + agent));
        }

        checks.Add(Microsoft.Diagnostics.Tracing.Session.TraceEventSession.IsElevated() == true
            ? new("File capture (ETW)", CheckStatus.Ok, "Elevated: kernel file events attribute writes to the app.")
            : new("File capture (ETW)", CheckStatus.Warning, "Not elevated: file writes are detected only in watch folders."));
        return checks;
    }

    [SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Session names come from sys.server_event_sessions and are bracket-quoted.")]
    private static async Task<List<DoctorCheck>> SqlServerChecksAsync(string connectionString, bool clean, CancellationToken ct)
    {
        var checks = new List<DoctorCheck>();
        try
        {
            await using var connection = new SqlConnection(new SqlConnectionStringBuilder(connectionString) { InitialCatalog = "master" }.ConnectionString);
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT CAST(SERVERPROPERTY('ProductMajorVersion') AS int),
                       HAS_PERMS_BY_NAME(NULL, NULL, 'ALTER ANY EVENT SESSION'),
                       HAS_PERMS_BY_NAME(NULL, NULL, 'VIEW SERVER STATE'),
                       IS_SRVROLEMEMBER('sysadmin')
                """;
            await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                await reader.ReadAsync(ct).ConfigureAwait(false);
                var version = reader.IsDBNull(0) ? 0 : reader.GetInt32(0);
                var alter = !reader.IsDBNull(1) && reader.GetInt32(1) == 1;
                var view = !reader.IsDBNull(2) && reader.GetInt32(2) == 1;
                var sysadmin = !reader.IsDBNull(3) && reader.GetInt32(3) == 1;
                var ok = sysadmin || (alter && view);
                checks.Add(new("Extended Events permissions", ok ? CheckStatus.Ok : CheckStatus.Failed,
                    ok ? $"SQL Server {version}: can create, start, read and drop event sessions."
                       : $"SQL Server {version}: needs {(version >= 16 ? "CREATE/ALTER/DROP ANY EVENT SESSION and VIEW SERVER PERFORMANCE STATE" : "ALTER ANY EVENT SESSION and VIEW SERVER STATE")}."));
            }

            cmd.CommandText = "SELECT name FROM sys.server_event_sessions WHERE name LIKE 'hokusai[_]%'";
            var leftovers = new List<string>();
            await using (var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    leftovers.Add(reader.GetString(0));
                }
            }

            if (clean)
            {
                foreach (var name in leftovers)
                {
                    cmd.CommandText = $"DROP EVENT SESSION [{name.Replace("]", "]]", StringComparison.Ordinal)}] ON SERVER";
                    await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
            }

            checks.Add(new("Leftover event sessions", leftovers.Count == 0 || clean ? CheckStatus.Ok : CheckStatus.Warning,
                leftovers.Count == 0 ? "None." : clean ? $"Dropped {leftovers.Count}." : $"{string.Join(", ", leftovers)} (run 'hokusai doctor --clean')."));
        }
        catch (SqlException ex)
        {
            checks.Add(new("Extended Events permissions", CheckStatus.Failed, ex.Message));
        }

        return checks;
    }

    private static DoctorCheck LocalDb()
    {
        var exe = Directory.Exists(@"C:\Program Files\Microsoft SQL Server")
            ? Directory.EnumerateFiles(@"C:\Program Files\Microsoft SQL Server", "SqlLocalDB.exe", SearchOption.AllDirectories).OrderDescending().FirstOrDefault()
            : null;
        if (exe is null)
        {
            return new("SQL Server LocalDB", CheckStatus.Warning, "Not installed; regression tests for SQL Server sources restore baselines into LocalDB.");
        }

        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, "info") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true })!;
            var instances = p.StandardOutput.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            p.WaitForExit();
            return new("SQL Server LocalDB", CheckStatus.Ok, $"{exe} (instances: {string.Join(", ", instances)})");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return new("SQL Server LocalDB", CheckStatus.Warning, ex.Message);
        }
    }

    public static List<(string Name, int Bits)> OdbcDrivers()
    {
        var list = new List<(string, int)>();
        foreach (var (view, bits) in new[] { (RegistryView.Registry64, 64), (RegistryView.Registry32, 32) })
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var key = root.OpenSubKey(@"SOFTWARE\ODBC\ODBCINST.INI\ODBC Drivers");
            foreach (var name in key?.GetValueNames() ?? [])
            {
                list.Add((name, bits));
            }
        }

        return list;
    }
}
