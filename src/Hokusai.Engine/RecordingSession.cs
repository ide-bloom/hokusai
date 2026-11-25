using System.Diagnostics;
using Hokusai.Capture.Files;
using Hokusai.Capture.Odbc;
using Hokusai.Capture.SqlServer;
using Hokusai.Capture.Ui;
using Hokusai.Core;
using Hokusai.Runtime;
using Hokusai.Sql;
using Hokusai.Verify;

namespace Hokusai.Engine;

/// <summary>What to record: a program Hokusai starts, or a running process the user explicitly chose.</summary>
public sealed record RecordTarget(string? Executable, IReadOnlyList<string> Arguments, int? AttachProcessId)
{
    public static RecordTarget Launch(string executable, params string[] arguments) => new(executable, arguments, null);

    public static RecordTarget Attach(int processId) => new(null, [], processId);
}

public sealed class RecordOptions
{
    /// <summary>Load the ODBC agent; defaults to true for ODBC data sources.</summary>
    public bool? UseOdbcAgent { get; init; }
    public IReadOnlyList<string> WatchFolders { get; init; } = [];
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(200);

    /// <summary>Snapshot every table when the database holds at most this many rows; otherwise only touched tables.</summary>
    public int FullSnapshotRowBudget { get; init; } = 200_000;
}

/// <summary>
/// One recording: the target process with UI, data and file capture attached. Each Begin/End pair becomes a
/// demonstration with its baseline copy, statements, row-level state diff and file effects.
/// </summary>
public sealed class RecordingSession : IAsyncDisposable
{
    private readonly Workspace _workspace;
    private readonly DataSourceConfig _source;
    private readonly string _action;
    private readonly RecordOptions _options;
    private readonly List<StatementEvent> _data = [];
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private UiRecorder? _ui;
    private ExtendedEventsSession? _xe;
    private OdbcAgent? _agent;
    private FileEffectCapture? _files;
    private Task? _pump;
    private DateTimeOffset _lastMark = DateTimeOffset.Now;
    private Demonstration? _current;
    private DatabaseSnapshot? _before;

    private RecordingSession(Workspace workspace, DataSourceConfig source, string action, RecordOptions options)
    {
        _workspace = workspace;
        _source = source;
        _action = action;
        _options = options;
    }

    public int ProcessId { get; private set; }

    public bool InDemonstration => _current is not null;

    public string? FileCaptureNote => _files?.EtwError;

    /// <summary>UI scans that failed so far and the last failure, for diagnostics.</summary>
    public (int Count, string? Last) UiScanErrors => (_ui?.ErrorCount ?? 0, _ui?.LastError);

    public event Action<UiEvent>? UiEventRecorded;

    public event Action<StatementEvent>? DataEventRecorded;

    public static async Task<RecordingSession> StartAsync(Workspace workspace, DataSourceConfig source, string action, RecordTarget target, RecordOptions options, CancellationToken ct)
    {
        var session = new RecordingSession(workspace, source, action, options);
        try
        {
            await session.AttachAsync(target, ct).ConfigureAwait(false);
            return session;
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task AttachAsync(RecordTarget target, CancellationToken ct)
    {
        var useAgent = _options.UseOdbcAgent ?? _source.Provider == ProviderKind.Odbc;
        if (useAgent)
        {
            var dir = OdbcAgent.FindAgentDirectory() ?? throw new InvalidOperationException("The ODBC agent was not found. Build native/agent or set HOKUSAI_AGENT_DIR.");
            _agent = target.AttachProcessId is int pid
                ? OdbcAgent.Attach(pid, dir, TimeSpan.FromSeconds(15))
                : OdbcAgent.Launch(target.Executable!, target.Arguments, dir, TimeSpan.FromSeconds(15));
            ProcessId = _agent.ProcessId;
            if (_agent.HookError != 0)
            {
                throw new InvalidOperationException($"The ODBC agent loaded but could not hook ODBC (Detours error {_agent.HookError}).");
            }
        }
        else if (target.AttachProcessId is int pid)
        {
            ProcessId = pid;
        }
        else
        {
            var info = new ProcessStartInfo(target.Executable!) { UseShellExecute = false };
            foreach (var a in target.Arguments)
            {
                info.ArgumentList.Add(a);
            }

            using var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start " + target.Executable);
            ProcessId = process.Id;
        }

        if (_source.Provider == ProviderKind.SqlServer)
        {
            _xe = await ExtendedEventsSession.StartAsync(_source.ConnectionString, ProcessId, Environment.MachineName, ct).ConfigureAwait(false);
        }

        var folders = _options.WatchFolders.ToList();
        if (target.Executable is not null && Path.GetDirectoryName(Path.GetFullPath(target.Executable)) is { } exeDir)
        {
            folders.Add(exeDir);
        }

        _files = new FileEffectCapture(ProcessId, folders);
        _files.Start();
        _ui = new UiRecorder(ProcessId, _options.PollInterval);
        _ui.Recorded += e => UiEventRecorded?.Invoke(e);
        _ui.Start();
        _pump = Task.Run(() => PumpAsync(_stop.Token), CancellationToken.None);
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await PollDataAsync(ct).ConfigureAwait(false);
                await Task.Delay(500, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Microsoft.Data.SqlClient.SqlException)
            {
                // A transient read failure; the next poll resumes from the last offset.
            }
        }
    }

    private async Task PollDataAsync(CancellationToken ct)
    {
        var events = new List<StatementEvent>();
        if (_xe is not null)
        {
            events.AddRange(await _xe.ReadNewAsync(ct).ConfigureAwait(false));
        }

        if (_agent is not null)
        {
            events.AddRange(_agent.Drain());
        }

        lock (_gate)
        {
            _data.AddRange(events);
        }

        foreach (var e in events)
        {
            DataEventRecorded?.Invoke(e);
        }
    }

    /// <summary>Start mark: captures the baseline and starting state, then collects everything until the end mark.</summary>
    public async Task BeginDemonstrationAsync(CancellationToken ct)
    {
        if (_current is not null)
        {
            throw new InvalidOperationException("A demonstration is already in progress.");
        }

        var index = _workspace.NextDemonstrationIndex(_action);
        var dir = _workspace.DemonstrationDirectory(_action, index);
        Directory.CreateDirectory(dir);
        var baseline = await DatabaseCopies.CreateBaselineAsync(_source, dir, ct).ConfigureAwait(false);
        _before = await SnapshotAllAsync(ct).ConfigureAwait(false);
        if (_before is not null)
        {
            await HkJson.WriteAsync(Path.Combine(dir, "before.json"), _before, ct).ConfigureAwait(false);
        }

        _files!.BeginDemonstration();
        _current = new Demonstration
        {
            Action = _action,
            Index = index,
            Start = DateTimeOffset.Now,
            ProcessId = ProcessId,
            Provider = _agent is not null ? ProviderKind.Odbc : _source.Provider,
            Culture = System.Globalization.CultureInfo.CurrentCulture.Name,
            Baseline = baseline,
        };
        _ui!.Drain();
        _ui.EmitSnapshot();
    }

    /// <summary>End mark: gathers the demonstration's events, computes the state diff and saves it.</summary>
    public async Task<Demonstration> EndDemonstrationAsync(CancellationToken ct)
    {
        var demo = _current ?? throw new InvalidOperationException("No demonstration is in progress.");

        // Extended Events dispatch with up to one second of latency.
        await Task.Delay(_xe is null ? 300 : 1500, ct).ConfigureAwait(false);
        await PollDataAsync(ct).ConfigureAwait(false);
        demo.End = DateTimeOffset.Now;
        lock (_gate)
        {
            demo.Statements = [.. _data.Where(e => e.Timestamp >= demo.Start && e.Timestamp <= demo.End).OrderBy(e => e.Timestamp)];
            demo.Context = [.. _data.Where(e => e.Timestamp >= _lastMark && e.Timestamp < demo.Start).OrderBy(e => e.Timestamp)];
            _data.RemoveAll(e => e.Timestamp <= demo.End);
        }

        demo.UiEvents = [.. _ui!.Drain().Where(e => e.Timestamp >= demo.Start && e.Timestamp <= demo.End)];

        await using (var connection = await HkDb.OpenAsync(_source.Provider, _source.ConnectionString, ct).ConfigureAwait(false))
        {
            var schema = await SchemaReader.ReadAsync(connection, _source.Provider, ct).ConfigureAwait(false);
            var tables = _before?.Tables.Select(t => t.Table).ToList() ?? TouchedTables(demo, schema, connection);
            var before = _before ?? await SnapshotBaselineAsync(demo, schema, tables, ct).ConfigureAwait(false);
            var after = await StateDiffer.SnapshotAsync(connection, null, _source.Provider, schema, tables, ct).ConfigureAwait(false);
            demo.StateDiff = StateDiffer.Diff(before, after);
            demo.Tables = tables;
        }

        var ignore = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_source.FilePath is not null)
        {
            ignore.Add(Path.GetFullPath(_source.FilePath));
        }

        demo.FileEffects = _files!.EndDemonstration(ignore);
        await _workspace.SaveDemonstrationAsync(demo, ct).ConfigureAwait(false);
        _lastMark = demo.End;
        _current = null;
        _before = null;
        return demo;
    }

    private async Task<DatabaseSnapshot?> SnapshotAllAsync(CancellationToken ct)
    {
        await using var connection = await HkDb.OpenAsync(_source.Provider, _source.ConnectionString, ct).ConfigureAwait(false);
        var schema = await SchemaReader.ReadAsync(connection, _source.Provider, ct).ConfigureAwait(false);
        var snapshot = await StateDiffer.SnapshotAsync(connection, null, _source.Provider, schema, schema.Tables.Select(t => t.Name), ct).ConfigureAwait(false);

        // Large databases are diffed through the baseline copy and only the tables the statements touched.
        return snapshot.Tables.Sum(t => t.Rows.Count) <= _options.FullSnapshotRowBudget ? snapshot : null;
    }

    private List<string> TouchedTables(Demonstration demo, SchemaInfo schema, System.Data.Common.DbConnection connection)
    {
        var normalizer = SqlNormalizer.For(HkDb.DialectOf(_source.Provider, connection));
        var tables = demo.Statements.SelectMany(s => normalizer.Normalize(s.Text, [.. s.Parameters.Select(p => new BoundValue(p.Name, p.Value))]).Tables);
        return [.. tables.Where(t => schema.Table(t) is not null).Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private async Task<DatabaseSnapshot> SnapshotBaselineAsync(Demonstration demo, SchemaInfo schema, List<string> tables, CancellationToken ct)
    {
        var file = Path.Combine(_workspace.DemonstrationDirectory(demo.Action, demo.Index), demo.Baseline!);
        await using var copy = await DatabaseCopies.CreateCopyAsync(_source, file, ct).ConfigureAwait(false);
        await using var connection = await HkDb.OpenAsync(_source.Provider, copy.ConnectionString, ct).ConfigureAwait(false);
        return await StateDiffer.SnapshotAsync(connection, null, _source.Provider, schema, tables, ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_pump is not null)
        {
            try
            {
                await _pump.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Stopped.
            }
        }

        _ui?.Dispose();
        _files?.Dispose();
        _agent?.Dispose();
        if (_xe is not null)
        {
            await _xe.DisposeAsync().ConfigureAwait(false);
        }

        _stop.Dispose();
    }
}
