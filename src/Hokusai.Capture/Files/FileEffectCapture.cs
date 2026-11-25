using System.Collections.Concurrent;
using Hokusai.Core;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace Hokusai.Capture.Files;

/// <summary>
/// Observes file writes by the app's processes through ETW kernel file events, and diffs their content.
/// Structured text files in the watch folders are snapshotted at each start mark so modifications show exactly
/// what changed; files outside them are reported with their new content.
/// </summary>
/// <remarks>Kernel ETW sessions need an elevated process. Without elevation, writes in the watch folders are still
/// detected by comparing snapshots, but cannot be attributed to the app's process.</remarks>
public sealed class FileEffectCapture : IDisposable
{
    private const long MaxSnapshotBytes = 2 * 1024 * 1024;

    private readonly ConcurrentDictionary<int, bool> _processes = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _written = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _watchFolders;
    private readonly Dictionary<string, string?> _snapshot = new(StringComparer.OrdinalIgnoreCase);
    private TraceEventSession? _session;
    private Thread? _thread;

    public FileEffectCapture(int processId, IEnumerable<string> watchFolders)
    {
        _processes[processId] = true;
        _watchFolders = [.. watchFolders.Where(Directory.Exists).Select(Path.GetFullPath)];
    }

    /// <summary>True when ETW is active and writes are attributed to the app's processes.</summary>
    public bool UsesEtw => _session is not null;

    public string? EtwError { get; private set; }

    public void Start()
    {
        if (TraceEventSession.IsElevated() != true)
        {
            EtwError = "Not elevated: file writes are detected only in the watch folders, by snapshot comparison.";
            return;
        }

        try
        {
            _session = new TraceEventSession("hokusai-files-" + Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            _session.EnableKernelProvider(KernelTraceEventParser.Keywords.FileIOInit | KernelTraceEventParser.Keywords.FileIO | KernelTraceEventParser.Keywords.Process);
            var kernel = _session.Source.Kernel;
            kernel.ProcessStart += d =>
            {
                // Child processes started by the app count as the app.
                if (_processes.ContainsKey(d.ParentID))
                {
                    _processes[d.ProcessID] = true;
                }
            };
            kernel.FileIOWrite += d =>
            {
                if (_processes.ContainsKey(d.ProcessID) && !string.IsNullOrEmpty(d.FileName))
                {
                    _written[d.FileName] = DateTimeOffset.Now;
                }
            };
            _thread = new Thread(() => _session.Source.Process()) { IsBackground = true, Name = "hokusai-etw" };
            _thread.Start();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            EtwError = ex.Message;
            _session?.Dispose();
            _session = null;
        }
    }

    /// <summary>Snapshots structured text files in the watch folders and starts collecting writes for a demonstration.</summary>
    public void BeginDemonstration()
    {
        _written.Clear();
        _snapshot.Clear();
        foreach (var file in WatchedFiles())
        {
            _snapshot[file] = TryRead(file);
        }
    }

    /// <summary>Returns the file effects since <see cref="BeginDemonstration"/>.</summary>
    public List<FileEffect> EndDemonstration(IReadOnlySet<string>? ignore = null)
    {
        // ETW delivers events with a small delay; give the last writes a moment to arrive.
        Thread.Sleep(UsesEtw ? 1500 : 0);
        var candidates = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        foreach (var (file, at) in _written)
        {
            candidates[file] = at;
        }

        foreach (var file in WatchedFiles().Concat(_snapshot.Keys).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var now = TryRead(file);
            if (!_snapshot.TryGetValue(file, out var before) || before != now)
            {
                candidates.TryAdd(file, File.Exists(file) ? File.GetLastWriteTime(file) : DateTimeOffset.Now);
            }
        }

        var effects = new List<FileEffect>();
        foreach (var (file, at) in candidates.OrderBy(c => c.Value))
        {
            if (ignore?.Contains(file) == true || IsNoise(file))
            {
                continue;
            }

            var hadBefore = _snapshot.TryGetValue(file, out var before);
            var after = TryRead(file);
            if (hadBefore && before == after)
            {
                continue;
            }

            var format = StructuredDiff.FormatOf(file, after ?? before);
            effects.Add(new FileEffect
            {
                Timestamp = at,
                Path = file,
                Kind = !File.Exists(file) ? FileEffectKind.Deleted : hadBefore && before is not null ? FileEffectKind.Modified : FileEffectKind.Created,
                Format = format,
                Before = format == "binary" ? null : before,
                After = format == "binary" ? null : after,
                Changes = StructuredDiff.Diff(format, before, after),
            });
        }

        return effects;
    }

    private static bool IsNoise(string file)
    {
        var ext = Path.GetExtension(file).ToUpperInvariant();

        // Database files are covered by state diffs; logs, temp and journal files are not business output.
        return ext is ".MDF" or ".LDF" or ".DB" or ".DB-JOURNAL" or ".DB-WAL" or ".DB-SHM" or ".ACCDB" or ".MDB" or ".LACCDB" or ".LDB" or ".TMP" or ".LOG" or ".ETL" or ".PF" ||
               file.Contains(@"\AppData\Local\Microsoft\", StringComparison.OrdinalIgnoreCase) ||
               file.Contains(@"\Windows\", StringComparison.OrdinalIgnoreCase);
    }

    private IEnumerable<string> WatchedFiles() =>
        _watchFolders.SelectMany(f => Directory.EnumerateFiles(f, "*", SearchOption.TopDirectoryOnly))
            .Where(f => StructuredDiff.FormatOf(f, null) != "text" || f.EndsWith(".txt", StringComparison.OrdinalIgnoreCase));

    private static string? TryRead(string file)
    {
        try
        {
            var info = new FileInfo(file);
            if (!info.Exists || info.Length > MaxSnapshotBytes)
            {
                return null;
            }

            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        _session?.Dispose();
        _thread?.Join(TimeSpan.FromSeconds(2));
    }
}
