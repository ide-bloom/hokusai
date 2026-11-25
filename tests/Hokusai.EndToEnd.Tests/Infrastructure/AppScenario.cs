using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Hokusai.Core;
using Hokusai.Engine;
using Hokusai.Runtime;
using Hokusai.Verify;
using ModelContextProtocol.Client;

namespace Hokusai.EndToEnd.Tests.Infrastructure;

/// <summary>
/// The full Hokusai workflow against one test app: record every action twice with different inputs, infer,
/// verify, generate, build and run the generated tests, then perform each action with new inputs in the app, the
/// generated API and the MCP server and require identical row changes.
/// </summary>
internal abstract class AppScenario : IAsyncDisposable
{
    private readonly List<Func<Task>> _cleanup = [];
    private readonly string _exports = Repo.NewTempDirectory("exports");

    protected abstract string AppName { get; }

    public abstract ProviderKind Provider { get; }

    /// <summary>State-diff-only sources cannot observe read actions.</summary>
    public virtual bool CapturesReads => true;

    public DataSourceConfig Source { get; protected set; } = new();

    public abstract Task InitializeAsync();

    protected abstract string AppArgument(string connectionString);

    protected void OnCleanup(Func<Task> action) => _cleanup.Add(action);

    private static void Log(string message) => TestContext.Current.TestOutputHelper?.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");

    private static readonly CustomerInput[] Customers = [new("Hooli", "Wien", 300m), new("  Umbrella  ", "Salzburg", 1200.5m)];
    private static readonly InvoiceInput[] Invoices =
    [
        new("Globex", new DateTime(2026, 11, 20), true, [new("Widget", 2, 9.5m), new("Gadget", 1, 120m)]),
        new("Acme Ltd", new DateTime(2026, 11, 22), false, [new("Service", 3, 45m)]),
    ];

    private static readonly CustomerInput NewCustomer = new("Stark Industries", "Innsbruck", 4500.75m);
    private static readonly InvoiceInput NewInvoice = new("Initech", new DateTime(2026, 11, 25), true, [new("Bolt", 10, 0.25m), new("Nut", 7, 0.1m), new("Washer", 1, 0.05m)]);

    private string ExportPath(string name) => Path.Combine(_exports, name + ".csv");

    private List<(string Action, Action<AppDriver>[] Demos)> Plan() =>
    [
        ("create customer", [d => d.CreateCustomer(Customers[0]), d => d.CreateCustomer(Customers[1])]),
        ("create invoice", [d => d.CreateInvoice(Invoices[0]), d => d.CreateInvoice(Invoices[1])]),
        ("search customers", [d => d.Search("ac"), d => d.Search("lo")]),
        ("change invoice status", [d => d.ChangeStatus(new("1002", "Paid")), d => d.ChangeStatus(new("1003", "Sent"))]),
        ("delete draft invoice", [d => d.DeleteDraft("1001"), d => d.DeleteDraft("1004")]),
        ("export customers", [d => d.Export(ExportPath("first")), d => d.Export(ExportPath("second"))]),
    ];

    public async Task RunAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        var workspace = new Workspace(Repo.NewTempDirectory("workspace-" + AppName));
        var engine = new HokusaiEngine(workspace);
        await engine.ConnectAsync(Source, ct);
        var exe = Repo.TestApp(AppName);

        foreach (var (action, demos) in Plan())
        {
            Log($"Recording '{action}'");
            await using var session = await engine.StartRecordingAsync(action, RecordTarget.Launch(exe, AppArgument(Source.ConnectionString)), new RecordOptions { WatchFolders = [_exports] }, ct);
            using var driver = new AppDriver(session.ProcessId);
            foreach (var demo in demos)
            {
                await session.BeginDemonstrationAsync(ct);
                demo(driver);
                var saved = await session.EndDemonstrationAsync(ct);
                Log($"  demonstration {saved.Index}: {saved.UiEvents.Count} UI events, {saved.Statements.Count} statements, {saved.StateDiff?.ChangeCount} row changes, {saved.FileEffects.Count} file effects; UI scan errors {session.UiScanErrors.Count} {session.UiScanErrors.Last}");
            }
        }

        var templates = new Dictionary<string, ActionTemplate>();
        foreach (var (action, _) in Plan())
        {
            var template = await engine.InferAsync(action, ct);
            var report = await engine.VerifyAsync(action, "recorded", ct);
            if (template.AllStatements().Any(s => s.GuardRequired))
            {
                // State-diff capture cannot see the app's delete condition: the action must stay disabled until the
                // user states it, as with 'hokusai guard'.
                Assert.False(report.Verified, $"'{action}' was verified without the guard its deletes need.");
                foreach (var step in template.AllStatements().Where(s => s.GuardRequired))
                {
                    template = await engine.SetGuardAsync(action, step.Id, step.Table == "Invoice" ? "Status = 'Draft'" : Guards.Unconditional, ct);
                }

                report = await engine.VerifyAsync(action, "recorded", ct);
            }
            templates[action] = template;
            Log($"'{action}': {(report.Verified ? "verified" : "NOT verified")} {string.Join(" | ", report.Issues.Concat(report.Demonstrations.SelectMany(d => d.Mismatches.Select(m => m.ToString()).Concat(d.FileMismatches).Append(d.Error ?? "")).Where(x => x.Length > 0)))}");
            if (action == "search customers" && !CapturesReads)
            {
                Assert.False(report.Verified, "A state-diff-only source cannot verify a read action.");
                continue;
            }

            Assert.True(report.Verified, $"'{action}' was not verified: {string.Join("; ", report.Issues)} {JsonSerializer.Serialize(report.Demonstrations.Where(d => !d.Matched).Select(d => new { d.Index, d.Error, Mismatches = d.Mismatches.Select(m => m.ToString()), d.FileMismatches }))}");
        }

        Assert.True(templates["delete draft invoice"].Destructive);

        var output = Repo.NewTempDirectory("generated-" + AppName);
        await engine.GenerateAsync([.. templates.Keys], output, api: true, mcp: true, tests: true, name: "Invoicing", ct);
        var solution = new GeneratedSolution(output, "Invoicing");
        Log("Building and testing the generated solution");
        solution.BuildAndTest();

        await CompareNewInputsAsync(solution, templates, ct);
    }

    private async Task<HkCopy> CloneAsync(CancellationToken ct)
    {
        var dir = Repo.NewTempDirectory("clone");
        var baseline = await DatabaseCopies.CreateBaselineAsync(Source, dir, ct);
        var copy = await DatabaseCopies.CreateCopyAsync(Source, Path.Combine(dir, baseline), ct);
        OnCleanup(async () => await copy.DisposeAsync());
        return copy;
    }

    private async Task<DatabaseSnapshot> SnapshotAsync(string connectionString, CancellationToken ct)
    {
        await using var connection = await HkDb.OpenAsync(Source.Provider, connectionString, ct);
        var schema = await SchemaReader.ReadAsync(connection, Source.Provider, ct);
        return await StateDiffer.SnapshotAsync(connection, null, Source.Provider, schema, schema.Tables.Select(t => t.Name), ct);
    }

    /// <summary>Runs the app itself on a fresh copy and returns the row changes it made.</summary>
    private async Task<(DatabaseDiff Diff, HkCopy Copy)> AppRunAsync(Action<AppDriver> perform, CancellationToken ct)
    {
        var copy = await CloneAsync(ct);
        var before = await SnapshotAsync(copy.ConnectionString, ct);
        var info = new ProcessStartInfo(Repo.TestApp(AppName)) { UseShellExecute = false };
        info.ArgumentList.Add(AppArgument(copy.ConnectionString));
        using (var process = Process.Start(info)!)
        using (var driver = new AppDriver(process.Id))
        {
            perform(driver);
        }

        HkCopies.ReleasePools();
        await Task.Delay(500, ct);
        return (StateDiffer.Diff(before, await SnapshotAsync(copy.ConnectionString, ct)), copy);
    }

    private async Task<DatabaseDiff> DiffAsync(Func<string, Task> perform, CancellationToken ct)
    {
        var copy = await CloneAsync(ct);
        var before = await SnapshotAsync(copy.ConnectionString, ct);
        await perform(copy.ConnectionString);
        HkCopies.ReleasePools();
        return StateDiffer.Diff(before, await SnapshotAsync(copy.ConnectionString, ct));
    }

    private static Dictionary<string, object?> Input(ActionTemplate t, Dictionary<string, object?> controls, List<Dictionary<string, object?>>? lines = null)
    {
        var input = new Dictionary<string, object?>();
        foreach (var p in t.Parameters.Where(p => p.Collection is null))
        {
            var value = controls.GetValueOrDefault(p.AutomationId ?? "");
            input[p.Name] = value is DateTime d ? (p.ClrType == "DateOnly" ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : d.ToString("s", CultureInfo.InvariantCulture)) : value;
        }

        foreach (var c in t.Collections)
        {
            input[c.Name] = (lines ?? []).Select(l => t.Parameters.Where(p => p.Collection == c.Name).ToDictionary(p => p.Name, p => l.GetValueOrDefault(p.Column ?? ""))).ToList();
        }

        return input;
    }

    private static Dictionary<string, object?> CustomerControls(CustomerInput c) => new() { ["txtName"] = c.Name, ["txtCity"] = c.City, ["txtCreditLimit"] = c.CreditLimit };

    private static Dictionary<string, object?> InvoiceControls(InvoiceInput i) => new() { ["cboCustomer"] = i.Customer, ["txtDate"] = i.Date, ["chkTaxable"] = i.Taxable };

    private static List<Dictionary<string, object?>> InvoiceLines(InvoiceInput i) =>
        [.. i.Lines.Select(l => new Dictionary<string, object?> { ["Item"] = l.Item, ["Quantity"] = l.Quantity, ["Unit price"] = l.UnitPrice })];

    private static void AssertSame(string what, ActionTemplate t, DatabaseDiff app, DatabaseDiff other)
    {
        var mismatches = DiffComparer.Compare(app, other, t.GeneratedColumns);
        Assert.True(mismatches.Count == 0, $"{what} differs from the app:\n{string.Join("\n", mismatches)}");
        Assert.False(app.IsEmpty, $"The app made no change for '{t.Name}'.");
    }

    private async Task CompareNewInputsAsync(GeneratedSolution solution, Dictionary<string, ActionTemplate> templates, CancellationToken ct)
    {
        var writes = new (string Action, Action<AppDriver> App, Dictionary<string, object?> Input)[]
        {
            ("create customer", d => d.CreateCustomer(NewCustomer), Input(templates["create customer"], CustomerControls(NewCustomer))),
            ("create invoice", d => d.CreateInvoice(NewInvoice), Input(templates["create invoice"], InvoiceControls(NewInvoice), InvoiceLines(NewInvoice))),
            ("change invoice status", d => d.ChangeStatus(new("1005", "Paid")), Input(templates["change invoice status"], new() { ["cboInvoice"] = "1005", ["cboStatus"] = "Paid" })),
            ("delete draft invoice", d => d.DeleteDraft("1006"), Input(templates["delete draft invoice"], new() { ["cboInvoice"] = "1006" })),
        };

        var appDiffs = new Dictionary<string, DatabaseDiff>();
        foreach (var (action, app, input) in writes)
        {
            Log($"New inputs for '{action}': app vs generated API");
            var (appDiff, _) = await AppRunAsync(app, ct);
            appDiffs[action] = appDiff;
            var route = action.Replace(' ', '-');
            var apiDiff = await DiffAsync(async cs =>
            {
                using var api = await solution.StartApiAsync(Provider, cs);
                using var dry = await api.PostAsync(route, input, dryRun: true);
                Assert.True(dry.RootElement.GetProperty("statements").GetArrayLength() > 0);
                using var _ = await api.PostAsync(route, input, idempotencyKey: "e2e-" + route);

                // A retried request with the same Idempotency-Key must not execute twice.
                using var replay = await api.PostAsync(route, input, idempotencyKey: "e2e-" + route);
            }, ct);
            AssertSame("Generated API", templates[action], appDiff, apiDiff);
        }

        Log("New inputs through the MCP server");
        var invoiceInput = writes[1].Input;
        var mcpDiff = await DiffAsync(async cs =>
        {
            await using var mcp = await solution.StartMcpAsync(Provider, cs);
            var tools = await mcp.ListToolsAsync(cancellationToken: ct);
            Assert.Contains(tools, t => t.Name == "create_invoice");
            var unconfirmed = await mcp.CallToolAsync("create_invoice", invoiceInput, cancellationToken: ct);
            Assert.Contains("confirmation_required", Text(unconfirmed), StringComparison.Ordinal);
            var confirmed = await mcp.CallToolAsync("create_invoice", new Dictionary<string, object?>(invoiceInput) { ["confirm"] = true }, cancellationToken: ct);
            Assert.DoesNotContain("\"error\"", Text(confirmed), StringComparison.Ordinal);
        }, ct);
        AssertSame("MCP create_invoice", templates["create invoice"], appDiffs["create invoice"], mcpDiff);

        var deleteInput = writes[3].Input;
        var mcpDelete = await DiffAsync(async cs =>
        {
            await using var mcp = await solution.StartMcpAsync(Provider, cs);
            var tool = (await mcp.ListToolsAsync(cancellationToken: ct)).Single(t => t.Name == "delete_draft_invoice");
            Assert.True(tool.ProtocolTool.Annotations?.DestructiveHint);
            Assert.Contains("confirmation_required", Text(await mcp.CallToolAsync("delete_draft_invoice", deleteInput, cancellationToken: ct)), StringComparison.Ordinal);
            Assert.Contains("statements", Text(await mcp.CallToolAsync("delete_draft_invoice", new Dictionary<string, object?>(deleteInput) { ["dryRun"] = true }, cancellationToken: ct)), StringComparison.Ordinal);
            Assert.DoesNotContain("\"error\"", Text(await mcp.CallToolAsync("delete_draft_invoice", new Dictionary<string, object?>(deleteInput) { ["confirm"] = true }, cancellationToken: ct)), StringComparison.Ordinal);
        }, ct);
        AssertSame("MCP delete_draft_invoice", templates["delete draft invoice"], appDiffs["delete draft invoice"], mcpDelete);

        Log("New inputs for 'export customers'");
        var exportFile = ExportPath("new");
        await AppRunAsync(d => d.Export(exportFile), ct);
        var exportCopy = await CloneAsync(ct);
        using (var api = await solution.StartApiAsync(Provider, exportCopy.ConnectionString))
        using (var response = await api.PostAsync("export-customers", new Dictionary<string, object?>()))
        {
            Assert.Equal(File.ReadAllText(exportFile).ReplaceLineEndings("\n"), response.RootElement.GetProperty("file").GetString()!.ReplaceLineEndings("\n"));
        }

        if (CapturesReads)
        {
            Log("New inputs for 'search customers'");
            List<string> appNames = [];
            await AppRunAsync(d =>
            {
                d.Search("tech");
                appNames = d.SearchResultNames();
            }, ct);
            var searchCopy = await CloneAsync(ct);
            using var api = await solution.StartApiAsync(Provider, searchCopy.ConnectionString);
            using var response = await api.PostAsync("search-customers", Input(templates["search customers"], new() { ["txtSearch"] = "tech" }));
            var apiNames = response.RootElement.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("name").GetString()!).ToList();
            Assert.Equal(appNames, apiNames);
            Assert.Contains("Initech", apiNames);
        }
    }

    private static string Text(ModelContextProtocol.Protocol.CallToolResult result) =>
        string.Concat(result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Select(c => c.Text));

    public async ValueTask DisposeAsync()
    {
        foreach (var action in Enumerable.Reverse(_cleanup))
        {
            try
            {
                await action();
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Data.Common.DbException)
            {
                Log("cleanup: " + ex.Message);
            }
        }
    }
}
