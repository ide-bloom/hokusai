using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Hokusai.Core;
using Hokusai.Engine;
using Hokusai.Runtime;

namespace Hokusai.Studio;

public sealed record TimelineRow(string Time, string Kind, string Subject, string Detail);

public sealed record DemoRow(int Index, string When, int UiEvents, int Statements, int RowChanges, int FileEffects, string Provider);

public sealed record BindingRow(string Placeholder, string Class, string Detail, string Confidence, Brush Brush);

public sealed record StatementRow(string Step, string Context, string Sql, IReadOnlyList<BindingRow> Bindings);

public sealed record EvidenceRow(string Subject, string Rule, string Detail, string Confidence);

public sealed record StructureRow(string Kind, string Description, string Steps);

public sealed record DemoResultRow(int Index, string Result, IReadOnlyList<DiffMismatch> Mismatches, IReadOnlyList<string> Files);

public sealed class GenerateChoice(string action, string status) : Observable
{
    private bool _selected = true;

    public string Action { get; } = action;

    public string Status { get; } = status;

    public bool Selected { get => _selected; set => Set(ref _selected, value); }
}

/// <summary>State and commands behind the Studio's views. All work goes through <see cref="HokusaiEngine"/>.</summary>
public sealed class MainViewModel : Observable
{
    private string _workspacePath = Environment.CurrentDirectory;
    private string? _selectedAction;
    private string _status = "Ready.";
    private ProviderKind _provider = ProviderKind.SqlServer;
    private string _connectionString = "";
    private string _databaseFile = "";
    private string _recordAction = "";
    private string _executable = "";
    private string _arguments = "";
    private string _attachPid = "";
    private bool _useAgent;
    private RecordingSession? _session;
    private string _templateSummary = "";
    private string _verificationSummary = "";
    private DemoResultRow? _selectedResult;
    private string _outputFolder = "";
    private string _projectName = "Hokusai.Generated";
    private bool _api = true;
    private bool _mcp = true;
    private bool _tests = true;

    public MainViewModel()
    {
        void OnError(Exception ex) => Status = "Error: " + ex.Message;
        OpenWorkspace = new AsyncCommand(LoadWorkspaceAsync, OnError);
        Connect = new AsyncCommand(ConnectAsync, OnError);
        RunDoctor = new AsyncCommand(DoctorAsync, OnError);
        StartRecording = new AsyncCommand(StartRecordingAsync, OnError, () => _session is null);
        BeginDemonstration = new AsyncCommand(BeginDemonstrationAsync, OnError, () => _session is { InDemonstration: false });
        EndDemonstration = new AsyncCommand(EndDemonstrationAsync, OnError, () => _session is { InDemonstration: true });
        StopRecording = new AsyncCommand(StopRecordingAsync, OnError, () => _session is not null);
        Infer = new AsyncCommand(InferAsync, OnError, () => SelectedAction is not null);
        Verify = new AsyncCommand(VerifyAsync, OnError, () => SelectedAction is not null);
        Generate = new AsyncCommand(GenerateAsync, OnError);
    }

    private HokusaiEngine Engine => new(new Workspace(WorkspacePath));

    private static void UiThread(Action action) => Application.Current.Dispatcher.Invoke(action);

    public string WorkspacePath { get => _workspacePath; set => Set(ref _workspacePath, value); }

    public string Status { get => _status; set => Set(ref _status, value); }

    public ObservableCollection<string> Actions { get; } = [];

    public string? SelectedAction
    {
        get => _selectedAction;
        set
        {
            if (Set(ref _selectedAction, value))
            {
                Infer.Refresh();
                Verify.Refresh();
                _ = LoadActionAsync();
            }
        }
    }

    public AsyncCommand OpenWorkspace { get; }

    // Data source
    public IReadOnlyList<ProviderKind> Providers { get; } = Enum.GetValues<ProviderKind>();

    public ProviderKind Provider { get => _provider; set => Set(ref _provider, value); }

    public string ConnectionString { get => _connectionString; set => Set(ref _connectionString, value); }

    public string DatabaseFile { get => _databaseFile; set => Set(ref _databaseFile, value); }

    public ObservableCollection<string> Tables { get; } = [];

    public ObservableCollection<DoctorCheck> Checks { get; } = [];

    public AsyncCommand Connect { get; }

    public AsyncCommand RunDoctor { get; }

    // Recording
    public string RecordAction { get => _recordAction; set => Set(ref _recordAction, value); }

    public string Executable { get => _executable; set => Set(ref _executable, value); }

    public string Arguments { get => _arguments; set => Set(ref _arguments, value); }

    public string AttachPid { get => _attachPid; set => Set(ref _attachPid, value); }

    public bool UseAgent { get => _useAgent; set => Set(ref _useAgent, value); }

    public ObservableCollection<TimelineRow> UiTimeline { get; } = [];

    public ObservableCollection<TimelineRow> DataTimeline { get; } = [];

    public AsyncCommand StartRecording { get; }

    public AsyncCommand BeginDemonstration { get; }

    public AsyncCommand EndDemonstration { get; }

    public AsyncCommand StopRecording { get; }

    // Demonstrations, template, structure, verification
    public ObservableCollection<DemoRow> Demonstrations { get; } = [];

    public string TemplateSummary { get => _templateSummary; set => Set(ref _templateSummary, value); }

    public ObservableCollection<StatementRow> Statements { get; } = [];

    public ObservableCollection<EvidenceRow> Evidence { get; } = [];

    public ObservableCollection<string> Issues { get; } = [];

    public ObservableCollection<StructureRow> Structure { get; } = [];

    public string VerificationSummary { get => _verificationSummary; set => Set(ref _verificationSummary, value); }

    public ObservableCollection<DemoResultRow> Results { get; } = [];

    public DemoResultRow? SelectedResult { get => _selectedResult; set => Set(ref _selectedResult, value); }

    public AsyncCommand Infer { get; }

    public AsyncCommand Verify { get; }

    // Generation
    public ObservableCollection<GenerateChoice> GenerateChoices { get; } = [];

    public string OutputFolder { get => _outputFolder; set => Set(ref _outputFolder, value); }

    public string ProjectName { get => _projectName; set => Set(ref _projectName, value); }

    public bool Api { get => _api; set => Set(ref _api, value); }

    public bool Mcp { get => _mcp; set => Set(ref _mcp, value); }

    public bool Tests { get => _tests; set => Set(ref _tests, value); }

    public ObservableCollection<string> GeneratedFiles { get; } = [];

    public AsyncCommand Generate { get; }

    private async Task LoadWorkspaceAsync()
    {
        var engine = Engine;
        var config = await engine.Workspace.LoadConfigAsync(CancellationToken.None);
        if (config.DataSource is { } ds)
        {
            Provider = ds.Provider;
            ConnectionString = ds.ConnectionString;
            DatabaseFile = ds.FilePath ?? "";
        }

        Actions.Clear();
        GenerateChoices.Clear();
        foreach (var action in engine.Workspace.Actions())
        {
            Actions.Add(action);
            var template = await engine.Workspace.LoadTemplateAsync(action, CancellationToken.None);
            GenerateChoices.Add(new GenerateChoice(action, template is null ? "not inferred" : template.Status.ToString().ToLowerInvariant()));
        }

        OutputFolder = OutputFolder.Length > 0 ? OutputFolder : System.IO.Path.Combine(WorkspacePath, "generated");
        SelectedAction ??= Actions.FirstOrDefault();
        Status = $"Workspace {WorkspacePath}: {Actions.Count} action(s).";
    }

    private async Task ConnectAsync()
    {
        var file = DatabaseFile.Length == 0 ? null : System.IO.Path.GetFullPath(DatabaseFile);
        var cs = Provider switch
        {
            ProviderKind.Sqlite when file is not null => $"Data Source={file}",
            ProviderKind.Access when file is not null => $"Provider=Microsoft.ACE.OLEDB.16.0;Data Source={file}",
            _ => ConnectionString,
        };
        var schema = await Engine.ConnectAsync(new DataSourceConfig { Provider = Provider, ConnectionString = cs, FilePath = file }, CancellationToken.None);
        Tables.Clear();
        foreach (var t in schema.Tables)
        {
            Tables.Add($"{t.Name} ({t.Columns.Count} columns{(t.PrimaryKey.Count > 0 ? ", key " + string.Join("+", t.PrimaryKey) : "")})");
        }

        Status = $"Connected: {schema.Tables.Count} tables. The connection string is stored encrypted for your Windows account.";
    }

    private async Task DoctorAsync()
    {
        Checks.Clear();
        foreach (var c in await Doctor.RunAsync(new Workspace(WorkspacePath), clean: false, CancellationToken.None))
        {
            Checks.Add(c);
        }

        Status = "Doctor finished.";
    }

    private void RefreshRecordingCommands()
    {
        StartRecording.Refresh();
        BeginDemonstration.Refresh();
        EndDemonstration.Refresh();
        StopRecording.Refresh();
    }

    private async Task StartRecordingAsync()
    {
        if (RecordAction.Trim().Length == 0)
        {
            throw new InvalidOperationException("Enter the action name, for example \"create invoice\".");
        }

        var target = int.TryParse(AttachPid, CultureInfo.InvariantCulture, out var pid)
            ? RecordTarget.Attach(pid)
            : RecordTarget.Launch(Executable, [.. Arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries)]);
        if (target.AttachProcessId is not null &&
            MessageBox.Show($"Load Hokusai's capture into process {pid}? Only do this for an app you are allowed to inspect.", "Attach", MessageBoxButton.YesNo) != MessageBoxResult.Yes)
        {
            return;
        }

        UiTimeline.Clear();
        DataTimeline.Clear();
        _session = await Engine.StartRecordingAsync(RecordAction.Trim(), target, new RecordOptions { UseOdbcAgent = UseAgent ? true : null }, CancellationToken.None);
        _session.UiEventRecorded += e => UiThread(() => UiTimeline.Insert(0, new TimelineRow(e.Timestamp.ToString("HH:mm:ss.fff", CultureInfo.CurrentCulture), e.Kind.ToString(), e.Label ?? e.Key, e.Value ?? "")));
        _session.DataEventRecorded += e => UiThread(() => DataTimeline.Insert(0, new TimelineRow(e.Timestamp.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.CurrentCulture), e.Source, e.RowCount is null ? "" : e.RowCount + " row(s)", e.Text)));
        Status = $"Recording '{RecordAction}' in process {_session.ProcessId}. Start a demonstration, perform the action, then end it.";
        RefreshRecordingCommands();
    }

    private async Task BeginDemonstrationAsync()
    {
        await _session!.BeginDemonstrationAsync(CancellationToken.None);
        Status = "Demonstration started: perform the action in the app, then end the demonstration.";
        RefreshRecordingCommands();
    }

    private async Task EndDemonstrationAsync()
    {
        var demo = await _session!.EndDemonstrationAsync(CancellationToken.None);
        Status = $"Demonstration {demo.Index} saved: {demo.Statements.Count} statements, {demo.StateDiff?.ChangeCount ?? 0} row changes, {demo.FileEffects.Count} file effects.";
        RefreshRecordingCommands();
    }

    private async Task StopRecordingAsync()
    {
        if (_session is { InDemonstration: true })
        {
            await _session.EndDemonstrationAsync(CancellationToken.None);
        }

        if (_session is not null)
        {
            await _session.DisposeAsync();
        }

        _session = null;
        RefreshRecordingCommands();
        await LoadWorkspaceAsync();
        SelectedAction = Workspace.SafeName(RecordAction);
    }

    private async Task LoadActionAsync()
    {
        if (SelectedAction is null)
        {
            return;
        }

        var ws = new Workspace(WorkspacePath);
        Demonstrations.Clear();
        foreach (var d in await ws.LoadDemonstrationsAsync(SelectedAction, CancellationToken.None))
        {
            Demonstrations.Add(new DemoRow(d.Index, d.Start.LocalDateTime.ToString("g", CultureInfo.CurrentCulture), d.UiEvents.Count, d.Statements.Count, d.StateDiff?.ChangeCount ?? 0, d.FileEffects.Count, d.Provider.ToString()));
        }

        ShowTemplate(await ws.LoadTemplateAsync(SelectedAction, CancellationToken.None));
        ShowVerification(await ws.LoadVerificationAsync(SelectedAction, CancellationToken.None));
    }

    private async Task InferAsync()
    {
        ShowTemplate(await Engine.InferAsync(SelectedAction!, CancellationToken.None));
        Status = "Inferred. Review the evidence for each value before verifying.";
    }

    private async Task VerifyAsync()
    {
        ShowVerification(await Engine.VerifyAsync(SelectedAction!, "recorded", CancellationToken.None));
        await LoadWorkspaceAsync();
    }

    private void ShowTemplate(ActionTemplate? t)
    {
        Statements.Clear();
        Evidence.Clear();
        Issues.Clear();
        Structure.Clear();
        if (t is null)
        {
            TemplateSummary = "Not inferred yet.";
            return;
        }

        TemplateSummary = $"{t.Kind} action{(t.Destructive ? ", destructive" : "")} · {t.Parameters.Count} parameter(s) · {t.DemonstrationCount} demonstration(s) · {t.Status}";
        void Walk(IEnumerable<TemplateStep> steps, string context)
        {
            foreach (var step in steps)
            {
                switch (step)
                {
                    case StatementStep s:
                        var trigger = s.Trigger is null ? "" : $"after '{s.Trigger}'";
                        Statements.Add(new StatementRow(s.Id.ToString(CultureInfo.InvariantCulture), string.Join(" · ", new[] { trigger, context }.Where(x => x.Length > 0)), s.Sql, [.. s.Bindings.Select(Describe)]));
                        break;
                    case LoopStep l:
                        Structure.Add(new StructureRow("Loop", $"for each row of '{l.Collection}'", string.Join(", ", l.Statements().Select(x => x.Id))));
                        Walk(l.Body, $"for each {l.Collection}");
                        break;
                    case ConditionalStep c:
                        Structure.Add(new StructureRow("Condition", "when " + c.Condition.Description, string.Join(", ", c.Statements().Select(x => x.Id))));
                        Walk(c.Body, "when " + c.Condition.Description);
                        break;
                }
            }
        }

        Walk(t.Steps, "");
        foreach (var e in t.Evidence)
        {
            Evidence.Add(new EvidenceRow(e.Subject, e.Rule, e.Detail, e.Confidence.ToString("0.00", CultureInfo.CurrentCulture)));
        }

        foreach (var i in t.Issues)
        {
            Issues.Add(i);
        }
    }

    private static BindingRow Describe(Binding b)
    {
        var resources = Application.Current.Resources;
        var (cls, detail, brush) = b.Kind switch
        {
            BindingKind.Input => ("input", $"{b.Parameter} ({b.Encoding})", "InputBrush"),
            BindingKind.Dependent => ("dependent", $"step {b.SourceStep}.{b.SourceColumn}{(b.Encoding.Kind == EncodingKind.Increment ? " + 1" : "")}", "DependentBrush"),
            BindingKind.Generated => ("generated", $"{b.Generator} ({b.Encoding})", "GeneratedBrush"),
            _ => ("constant", HkValue.Describe(b.Constant), "ConstantBrush"),
        };
        return new BindingRow(b.Placeholder, cls, detail + (b.TargetColumn is null ? "" : " → " + b.TargetColumn), b.Confidence.ToString("0.00", CultureInfo.CurrentCulture), (Brush)resources[brush]);
    }

    private void ShowVerification(VerificationReport? report)
    {
        Results.Clear();
        if (report is null)
        {
            VerificationSummary = "Not verified yet.";
            return;
        }

        VerificationSummary = $"{(report.Verified ? "Verified" : "Not verified")} on {report.At.LocalDateTime:g} against {report.Source} copies: {report.Demonstrations.Count(d => d.Matched)}/{report.Demonstrations.Count} demonstrations match." +
            (report.Issues.Count > 0 ? " Issues: " + string.Join(" ", report.Issues) : "");
        foreach (var d in report.Demonstrations)
        {
            Results.Add(new DemoResultRow(d.Index, d.Matched ? "match" : d.Error ?? $"{d.Mismatches.Count + d.FileMismatches.Count} difference(s)", d.Mismatches, d.FileMismatches));
        }

        SelectedResult = Results.FirstOrDefault(r => r.Result != "match") ?? Results.FirstOrDefault();
    }

    private async Task GenerateAsync()
    {
        var actions = GenerateChoices.Where(c => c.Selected).Select(c => c.Action).ToList();
        var files = await Engine.GenerateAsync(actions, OutputFolder, Api, Mcp, Tests, ProjectName, CancellationToken.None);
        GeneratedFiles.Clear();
        foreach (var f in files)
        {
            GeneratedFiles.Add(f);
        }

        Status = $"Wrote {files.Count} files to {OutputFolder}.";
    }
}
