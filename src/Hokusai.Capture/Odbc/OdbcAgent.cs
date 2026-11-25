using System.Diagnostics;
using System.Globalization;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text.Json;
using Hokusai.Core;

namespace Hokusai.Capture.Odbc;

/// <summary>
/// Starts an app with the ODBC agent loaded (or loads it into a process the user explicitly chose) and turns the
/// agent's events into <see cref="StatementEvent"/>s with parameter values and fetched rows.
/// </summary>
public sealed partial class OdbcAgent : IDisposable
{
    private readonly Dictionary<string, StatementEvent> _lastByStatement = new(StringComparer.Ordinal);
    private readonly List<StatementEvent> _pending = [];
    private EventRing? _ring;

    private OdbcAgent(int processId) => ProcessId = processId;

    public int ProcessId { get; }

    public int HookError { get; private set; }

    public long LostEvents => _ring?.Lost ?? 0;

    /// <summary>Directory containing hokusai_agent{32,64}.dll and hokusai_launcher{32,64}.exe.</summary>
    public static string? FindAgentDirectory()
    {
        var candidates = new List<string?>
        {
            Environment.GetEnvironmentVariable("HOKUSAI_AGENT_DIR"),
            Path.Combine(AppContext.BaseDirectory, "agents"),
            AppContext.BaseDirectory,
        };
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            candidates.Add(Path.Combine(dir.FullName, "native", "agent", "build", "x64", "Release"));
            dir = dir.Parent;
        }

        return candidates.OfType<string>().FirstOrDefault(c => File.Exists(Path.Combine(c, "hokusai_agent64.dll")) || File.Exists(Path.Combine(c, "hokusai_agent32.dll")));
    }

    private static string Locate(string agentDirectory, string file)
    {
        foreach (var candidate in new[] { Path.Combine(agentDirectory, file), Path.Combine(agentDirectory, "..", "..", "x86", "Release", file) })
        {
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        throw new FileNotFoundException($"{file} not found next to the agent. Build native/agent for both architectures.", file);
    }

    /// <summary>Tells whether an executable runs as a 32-bit process, including managed AnyCPU "prefer 32-bit" builds.</summary>
    public static bool Is32BitExecutable(string path)
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        var headers = pe.PEHeaders;
        if (headers.CoffHeader.Machine != Machine.I386)
        {
            return false;
        }

        // Managed IL-only images marked i386 are AnyCPU unless they require or prefer 32-bit.
        var cor = headers.CorHeader;
        return cor is null || !cor.Flags.HasFlag(CorFlags.ILOnly) || cor.Flags.HasFlag(CorFlags.Requires32Bit) || cor.Flags.HasFlag(CorFlags.Prefers32Bit);
    }

    public static OdbcAgent Launch(string executable, IEnumerable<string> arguments, string agentDirectory, TimeSpan timeout)
    {
        var x86 = Is32BitExecutable(executable);
        var bits = x86 ? "32" : "64";
        var output = RunLauncher(Locate(agentDirectory, $"hokusai_launcher{bits}.exe"), ["launch", Locate(agentDirectory, $"hokusai_agent{bits}.dll"), Path.GetFullPath(executable), .. arguments]);
        return Connect(ParsePid(output), timeout);
    }

    public static OdbcAgent Attach(int processId, string agentDirectory, TimeSpan timeout)
    {
        using var process = Process.GetProcessById(processId);
        var bits = Is32BitProcess(process) ? "32" : "64";
        RunLauncher(Locate(agentDirectory, $"hokusai_launcher{bits}.exe"), ["attach", Locate(agentDirectory, $"hokusai_agent{bits}.dll"), processId.ToString(CultureInfo.InvariantCulture)]);
        return Connect(processId, timeout);
    }

    private static bool Is32BitProcess(Process process) => IsWow64Process(process.Handle, out var wow64) && wow64;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWow64Process(IntPtr process, [MarshalAs(UnmanagedType.Bool)] out bool wow64);

    private static string RunLauncher(string launcher, IEnumerable<string> arguments)
    {
        var info = new ProcessStartInfo(launcher) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in arguments)
        {
            info.ArgumentList.Add(a);
        }

        using var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start the launcher.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"The launcher failed ({process.ExitCode}): {stderr.Trim()}");
        }

        return stdout;
    }

    private static int ParsePid(string output)
    {
        var line = output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("pid=", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("The launcher did not report a process id.");
        return int.Parse(line[4..], CultureInfo.InvariantCulture);
    }

    private static OdbcAgent Connect(int processId, TimeSpan timeout)
    {
        var agent = new OdbcAgent(processId);
        var deadline = DateTime.UtcNow + timeout;
        while (agent._ring is null && DateTime.UtcNow < deadline)
        {
            agent._ring = EventRing.TryOpen(processId);
            if (agent._ring is null)
            {
                Thread.Sleep(50);
            }
        }

        if (agent._ring is null)
        {
            throw new TimeoutException($"The ODBC agent did not start in process {processId}.");
        }

        return agent;
    }

    /// <summary>
    /// Returns statements executed since the previous call. Rows fetched later are still appended to the returned
    /// objects, because the agent reports them against the statement handle.
    /// </summary>
    public List<StatementEvent> Drain()
    {
        foreach (var json in _ring?.ReadAvailable() ?? [])
        {
            Apply(json);
        }

        var result = new List<StatementEvent>(_pending);
        _pending.Clear();
        return result;
    }

    internal void Apply(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var e = doc.RootElement;
        var type = e.GetProperty("t").GetString();
        var ts = e.TryGetProperty("ts", out var t) ? DateTimeOffset.FromFileTime(t.GetInt64()) : DateTimeOffset.Now;
        var stmt = e.TryGetProperty("stmt", out var s) ? s.GetString() ?? "" : "";
        switch (type)
        {
            case "hello":
                HookError = e.GetProperty("hookError").GetInt32();
                break;
            case "exec":
                if (e.GetProperty("rc").GetInt32() < 0)
                {
                    break; // failed statements change nothing
                }

                var ev = new StatementEvent { Source = "odbc", Timestamp = ts, Text = e.GetProperty("sql").GetString() ?? "" };
                foreach (var p in e.GetProperty("params").EnumerateArray())
                {
                    ev.Parameters.Add(new StatementParameter
                    {
                        Name = p.GetProperty("i").GetInt32().ToString(CultureInfo.InvariantCulture),
                        Value = p.GetProperty("v").ValueKind == JsonValueKind.Null ? null : p.GetProperty("v").GetString(),
                        Type = p.GetProperty("s").GetInt32().ToString(CultureInfo.InvariantCulture),
                    });
                }

                _pending.Add(ev);
                _lastByStatement[stmt] = ev;
                break;
            case "row" when _lastByStatement.TryGetValue(stmt, out var owner):
                owner.Result ??= new ResultData { Columns = [.. e.GetProperty("cols").EnumerateArray().Select(c => c.GetString() ?? "")] };
                owner.Result.Rows.Add([.. e.GetProperty("values").EnumerateArray().Select(v => v.ValueKind == JsonValueKind.Null ? null : v.GetString())]);
                break;
            case "rowcount" when _lastByStatement.TryGetValue(stmt, out var counted):
                counted.RowCount = e.GetProperty("n").GetInt64();
                break;
        }
    }

    public void Dispose() => _ring?.Dispose();
}
