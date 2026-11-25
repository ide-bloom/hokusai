using System.CommandLine;
using System.Globalization;
using System.Threading.Channels;
using Hokusai.Cli;
using Hokusai.Core;
using Hokusai.Engine;
using Hokusai.Runtime;

var workspaceOption = new Option<string>("--workspace", "-w") { Description = "Workspace folder (default: current directory).", DefaultValueFactory = _ => Environment.CurrentDirectory, Recursive = true };
var root = new RootCommand("Hokusai turns demonstrations in a desktop app into a verified REST API, MCP tools and regression tests.") { workspaceOption };

HokusaiEngine Engine(ParseResult r) => new(new Workspace(r.GetValue(workspaceOption)!));

// connect
var sqlServer = new Option<string?>("--sqlserver") { Description = "SQL Server connection string (Extended Events capture and state diffs)." };
var odbc = new Option<string?>("--odbc") { Description = "ODBC connection string or DSN=name (captured by the in-process agent)." };
var sqlite = new Option<string?>("--sqlite") { Description = "SQLite database file (state diffs)." };
var access = new Option<string?>("--access") { Description = "Access .accdb/.mdb file through the ACE OLE DB provider (state diffs)." };
var dbFile = new Option<string?>("--file") { Description = "Database file behind an ODBC source, for baselines and copies (detected from Database=/DBQ= when omitted)." };
var watch = new Option<string[]>("--watch") { Description = "Folder whose INI/CSV/XML/JSON files are snapshotted for file-effect diffs (repeatable).", AllowMultipleArgumentsPerToken = true };
var connect = new Command("connect", "Configure the data source to observe. Credentials are stored encrypted with DPAPI.") { sqlServer, odbc, sqlite, access, dbFile, watch };
connect.SetAction(async (r, ct) =>
{
    var source = Sources.Build(r.GetValue(sqlServer), r.GetValue(odbc), r.GetValue(sqlite), r.GetValue(access), r.GetValue(dbFile));
    var engine = Engine(r);
    var schema = await engine.ConnectAsync(source, ct);
    var folders = r.GetValue(watch) ?? [];
    if (folders.Length > 0)
    {
        var config = await engine.Workspace.LoadConfigAsync(ct);
        config.WatchFolders = [.. config.WatchFolders.Union(folders.Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase)];
        await engine.Workspace.SaveConfigAsync(config, ct);
    }

    Console.WriteLine($"Connected to {source.Provider}: {source.Display}");
    Console.WriteLine($"{schema.Tables.Count} tables: {string.Join(", ", schema.Tables.Select(t => t.Name))}");
    return 0;
});
root.Add(connect);

// record
var recordAction = new Argument<string>("action") { Description = "Action name, for example \"create invoice\"." };
var commandLine = new Argument<string[]>("command") { Description = "Program to start, after --.", Arity = ArgumentArity.ZeroOrMore };
var attach = new Option<int?>("--attach") { Description = "Attach to a running process instead of starting one." };
var yes = new Option<bool>("--yes") { Description = "Confirm attaching without a prompt." };
var noAgent = new Option<bool>("--no-agent") { Description = "Do not load the ODBC agent even for ODBC sources." };
var agentOption = new Option<bool>("--agent") { Description = "Load the ODBC agent even for non-ODBC sources (for example SQLite through ODBC)." };
var poll = new Option<int>("--poll-ms") { Description = "UI polling interval in milliseconds.", DefaultValueFactory = _ => 200 };
var recordCommand = new Command("record", "Capture demonstrations of an action. Mark each with S/E (or Ctrl+Alt+F9); finish with Q (or Ctrl+Alt+F12). With redirected input, reads 'start', 'end' and 'quit' lines.")
{
    recordAction, commandLine, attach, yes, noAgent, agentOption, poll,
};
recordCommand.SetAction(async (r, ct) =>
{
    var action = r.GetValue(recordAction)!;
    var command = r.GetValue(commandLine) ?? [];
    RecordTarget target;
    if (r.GetValue(attach) is int pid)
    {
        if (!r.GetValue(yes) && !Console.IsInputRedirected)
        {
            Console.Write($"Load Hokusai's capture into process {pid}? Only do this for an app you are allowed to inspect. [y/N] ");
            if (!string.Equals(Console.ReadLine()?.Trim(), "y", StringComparison.OrdinalIgnoreCase))
            {
                return 2;
            }
        }

        target = RecordTarget.Attach(pid);
    }
    else if (command.Length > 0)
    {
        target = RecordTarget.Launch(command[0], command[1..]);
    }
    else
    {
        Console.Error.WriteLine("Give the program after --, or --attach <pid>.");
        return 2;
    }

    var engine = Engine(r);
    bool? useAgent = r.GetValue(noAgent) ? false : r.GetValue(agentOption) ? true : null;
    await using var session = await engine.StartRecordingAsync(action, target, new RecordOptions { UseOdbcAgent = useAgent, PollInterval = TimeSpan.FromMilliseconds(r.GetValue(poll)) }, ct);
    Console.WriteLine($"Recording '{action}' in process {session.ProcessId}.");
    if (session.FileCaptureNote is { } note)
    {
        Console.WriteLine("Note: " + note);
    }

    var marks = Channel.CreateUnbounded<string>();
    using var hotkeys = new GlobalHotkeys(id => marks.Writer.TryWrite(id == GlobalHotkeys.FinishId ? "quit" : "toggle"));
    _ = Task.Run(async () =>
    {
        if (Console.IsInputRedirected)
        {
            while (await Console.In.ReadLineAsync(ct) is { } line)
            {
                marks.Writer.TryWrite(line.Trim().ToLowerInvariant());
            }

            marks.Writer.TryWrite("quit");
            return;
        }

        Console.WriteLine("S = start demonstration, E = end demonstration, Q = finish.");
        while (!ct.IsCancellationRequested)
        {
            var key = Console.ReadKey(intercept: true).KeyChar;
            marks.Writer.TryWrite(char.ToUpperInvariant(key) switch { 'S' => "start", 'E' => "end", 'Q' => "quit", _ => "" });
        }
    }, ct);

    var count = 0;
    await foreach (var mark in marks.Reader.ReadAllAsync(ct))
    {
        var verb = mark == "toggle" ? (session.InDemonstration ? "end" : "start") : mark;
        if (verb == "start" && !session.InDemonstration)
        {
            await session.BeginDemonstrationAsync(ct);
            Console.WriteLine("Demonstration started. Perform the action in the app.");
        }
        else if (verb == "end" && session.InDemonstration)
        {
            var demo = await session.EndDemonstrationAsync(ct);
            count++;
            Console.WriteLine($"Demonstration {demo.Index} saved: {demo.UiEvents.Count} UI events, {demo.Statements.Count} statements, {demo.StateDiff?.ChangeCount ?? 0} row changes, {demo.FileEffects.Count} file effects.");
        }
        else if (verb == "quit")
        {
            if (session.InDemonstration)
            {
                await session.EndDemonstrationAsync(ct);
                count++;
            }

            break;
        }
    }

    Console.WriteLine($"Recorded {count} demonstration(s). Next: hokusai infer \"{action}\"");
    return 0;
});
root.Add(recordCommand);

// infer
var inferAction = new Argument<string>("action");
var infer = new Command("infer", "Align the demonstrations of an action and infer its template.") { inferAction };
infer.SetAction(async (r, ct) =>
{
    var template = await Engine(r).InferAsync(r.GetValue(inferAction)!, ct);
    Printer.Template(template);
    return template.Issues.Count == 0 ? 0 : 1;
});
root.Add(infer);

// verify
var verifyAction = new Argument<string>("action");
var copyFrom = new Option<string>("--copy-from") { Description = "'recorded' (each demonstration's own baseline) or a .bak/.mdf/.db/.accdb/.sql file to copy for every replay.", DefaultValueFactory = _ => "recorded" };
var verify = new Command("verify", "Replay the template on fresh database copies with each demonstration's inputs and compare the changes with the app's.") { verifyAction, copyFrom };
verify.SetAction(async (r, ct) =>
{
    var report = await Engine(r).VerifyAsync(r.GetValue(verifyAction)!, r.GetValue(copyFrom)!, ct);
    Printer.Verification(report);
    return report.Verified ? 0 : 1;
});
root.Add(verify);

// generate
var actionsOption = new Option<string[]>("--actions") { Description = "Actions to generate (comma-separated or repeated).", Required = true, AllowMultipleArgumentsPerToken = true };
var outOption = new Option<string>("--out") { Description = "Output folder.", Required = true };
var apiOption = new Option<bool>("--api") { Description = "Generate the REST API." };
var mcpOption = new Option<bool>("--mcp") { Description = "Generate the MCP server." };
var testsOption = new Option<bool>("--tests") { Description = "Generate the regression tests." };
var nameOption = new Option<string>("--name") { Description = "Solution and namespace prefix.", DefaultValueFactory = _ => "Hokusai.Generated" };
var generate = new Command("generate", "Generate projects for the actions. Unverified actions are generated disabled, with the reason.") { actionsOption, outOption, apiOption, mcpOption, testsOption, nameOption };
generate.SetAction(async (r, ct) =>
{
    var actions = (r.GetValue(actionsOption) ?? []).SelectMany(a => a.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToList();
    bool api = r.GetValue(apiOption), mcp = r.GetValue(mcpOption), tests = r.GetValue(testsOption);
    if (!api && !mcp && !tests)
    {
        api = mcp = tests = true;
    }

    var files = await Engine(r).GenerateAsync(actions, Path.GetFullPath(r.GetValue(outOption)!), api, mcp, tests, r.GetValue(nameOption)!, ct);
    Console.WriteLine($"Wrote {files.Count} files to {Path.GetFullPath(r.GetValue(outOption)!)}.");
    return 0;
});
root.Add(generate);

// guard
var guardAction = new Argument<string>("action");
var guardStep = new Option<int?>("--step") { Description = "The destructive step that needs a guard." };
var guardWhere = new Option<string?>("--where") { Description = "SQL condition appended to the statement, for example \"Status = 'Draft'\"." };
var guardNone = new Option<bool>("--unconditional") { Description = "Confirm the app deletes without any further condition." };
var guard = new Command("guard", "List or supply the conditions of destructive statements that state-diff capture cannot see. Actions needing one stay disabled until it is supplied and verified.")
{
    guardAction, guardStep, guardWhere, guardNone,
};
guard.SetAction(async (r, ct) =>
{
    var engine = Engine(r);
    var action = r.GetValue(guardAction)!;
    if (r.GetValue(guardStep) is not int step)
    {
        var template = await engine.Workspace.LoadTemplateAsync(action, ct) ?? throw new InvalidOperationException($"No template for '{action}'.");
        foreach (var s in template.AllStatements().Where(s => s.GuardRequired))
        {
            Console.WriteLine($"step {s.Id}: {s.Sql}  guard: {s.Guard ?? "MISSING"}");
        }

        return 0;
    }

    var where = r.GetValue(guardWhere);
    if ((where is null) == !r.GetValue(guardNone))
    {
        Console.Error.WriteLine("Give exactly one of --where or --unconditional.");
        return 2;
    }

    var updated = await engine.SetGuardAsync(action, step, where ?? Guards.Unconditional, ct);
    Printer.Template(updated);
    Console.WriteLine($"Next: hokusai verify \"{action}\"");
    return 0;
});
root.Add(guard);

// schema-check
var schemaCheck = new Command("schema-check", "Detect schema drift against what the templates were inferred from.");
schemaCheck.SetAction(async (r, ct) =>
{
    var drift = await Engine(r).SchemaCheckAsync(ct);
    if (drift.Count == 0)
    {
        Console.WriteLine("No schema drift.");
        return 0;
    }

    foreach (var d in drift)
    {
        Console.WriteLine($"{d.Action}: changed tables {string.Join(", ", d.ChangedTables)}");
    }

    return 1;
});
root.Add(schemaCheck);

// doctor
var cleanOption = new Option<bool>("--clean") { Description = "Drop leftover Hokusai event sessions." };
var doctor = new Command("doctor", "Check Extended Events permissions, ODBC drivers, LocalDB, the ODBC agent and ETW.") { cleanOption };
doctor.SetAction(async (r, ct) =>
{
    var checks = await Doctor.RunAsync(new Workspace(r.GetValue(workspaceOption)!), r.GetValue(cleanOption), ct);
    foreach (var c in checks)
    {
        Console.WriteLine($"[{c.Status switch { CheckStatus.Ok => " ok ", CheckStatus.Warning => "warn", _ => "FAIL" }}] {c.Name}: {c.Detail}");
    }

    return checks.Any(c => c.Status == CheckStatus.Failed) ? 1 : 0;
});
root.Add(doctor);

// actions
var list = new Command("actions", "List recorded actions and their status.");
list.SetAction(async (r, ct) =>
{
    var ws = new Workspace(r.GetValue(workspaceOption)!);
    foreach (var action in ws.Actions())
    {
        var template = await ws.LoadTemplateAsync(action, ct);
        var demos = (await ws.LoadDemonstrationsAsync(action, ct)).Count;
        Console.WriteLine($"{action,-30} {demos,3} demonstration(s)  {(template is null ? "not inferred" : template.Status.ToString().ToLowerInvariant())}");
    }

    return 0;
});
root.Add(list);

try
{
    return await root.Parse(args).InvokeAsync();
}
catch (Exception ex) when (ex is InvalidOperationException or System.Data.Common.DbException or IOException or TimeoutException or HkInputException)
{
    Console.Error.WriteLine("error: " + ex.Message);
    return 1;
}

internal static class Sources
{
    public static DataSourceConfig Build(string? sqlServer, string? odbc, string? sqlite, string? access, string? file)
    {
        var given = new[] { sqlServer, odbc, sqlite, access }.Count(s => s is not null);
        if (given != 1)
        {
            throw new InvalidOperationException("Give exactly one of --sqlserver, --odbc, --sqlite or --access.");
        }

        if (sqlServer is not null)
        {
            return new DataSourceConfig { Provider = ProviderKind.SqlServer, ConnectionString = sqlServer };
        }

        if (sqlite is not null)
        {
            var path = Path.GetFullPath(sqlite);
            return new DataSourceConfig { Provider = ProviderKind.Sqlite, FilePath = path, ConnectionString = $"Data Source={path}" };
        }

        if (access is not null)
        {
            var path = Path.GetFullPath(access);
            var provider = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey("Microsoft.ACE.OLEDB.16.0") is not null ? "Microsoft.ACE.OLEDB.16.0" : "Microsoft.ACE.OLEDB.12.0";
            return new DataSourceConfig { Provider = ProviderKind.Access, FilePath = path, ConnectionString = $"Provider={provider};Data Source={path}" };
        }

        var builder = new System.Data.Common.DbConnectionStringBuilder { ConnectionString = odbc! };
        file ??= builder.TryGetValue("Database", out var db) ? db as string : builder.TryGetValue("DBQ", out var dbq) ? dbq as string : null;
        return new DataSourceConfig { Provider = ProviderKind.Odbc, ConnectionString = odbc!, FilePath = file is null ? null : Path.GetFullPath(file) };
    }
}

internal static class Printer
{
    public static void Template(ActionTemplate t)
    {
        Console.WriteLine($"Action '{t.Name}': {t.Kind.ToString().ToLowerInvariant()}{(t.Destructive ? ", DESTRUCTIVE" : "")}, {t.DemonstrationCount} demonstration(s), provider {t.Provider}");
        Console.WriteLine("Parameters:");
        foreach (var p in t.Parameters)
        {
            var extra = p.Lookup is { } l ? $" lookup {l.Table}.{l.DisplayColumn} -> {l.KeyColumn}" : p.TargetColumn is null ? "" : " -> " + p.TargetColumn;
            Console.WriteLine($"  {p.Name,-20} {p.ClrType,-9} {(p.Collection is null ? "" : "[" + p.Collection + "] ")}{(p.Required ? "required " : "")}from '{p.Label ?? p.AutomationId}'{extra}");
        }

        Console.WriteLine("Steps:");
        Steps(t.Steps, "  ");
        if (t.FileOutput is { } f)
        {
            Console.WriteLine($"File output: CSV from step {f.SourceStep} (delimiter '{f.Csv.Delimiter}', header {(f.Csv.Header ? "yes" : "no")})");
        }

        Console.WriteLine($"Evidence: {t.Evidence.Count} decision(s) recorded; open the Studio to review them.");
        foreach (var issue in t.Issues)
        {
            Console.WriteLine("Issue: " + issue);
        }
    }

    private static void Steps(List<TemplateStep> steps, string indent)
    {
        foreach (var step in steps)
        {
            switch (step)
            {
                case StatementStep s:
                    Console.WriteLine($"{indent}{s.Id}. {s.Sql}");
                    foreach (var b in s.Bindings)
                    {
                        var what = b.Kind switch
                        {
                            BindingKind.Input => $"input {b.Parameter} ({b.Encoding})",
                            BindingKind.Dependent => $"dependent on step {b.SourceStep}.{b.SourceColumn}{(b.Encoding.Kind == EncodingKind.Increment ? " + 1" : "")}",
                            BindingKind.Generated => $"generated {b.Generator} ({b.Encoding})",
                            _ => $"constant {HkValue.Describe(b.Constant)}",
                        };
                        Console.WriteLine($"{indent}     {b.Placeholder} = {what}, confidence {b.Confidence.ToString("0.00", CultureInfo.InvariantCulture)}");
                    }

                    break;
                case LoopStep l:
                    Console.WriteLine($"{indent}for each row of {l.Collection}:");
                    Steps(l.Body, indent + "  ");
                    break;
                case ConditionalStep c:
                    Console.WriteLine($"{indent}when {c.Condition.Description}:");
                    Steps(c.Body, indent + "  ");
                    break;
            }
        }
    }

    public static void Verification(VerificationReport report)
    {
        Console.WriteLine($"'{report.Action}': {(report.Verified ? "VERIFIED" : "NOT VERIFIED")} ({report.Demonstrations.Count(d => d.Matched)}/{report.Demonstrations.Count} demonstrations match)");
        foreach (var issue in report.Issues)
        {
            Console.WriteLine("  issue: " + issue);
        }

        foreach (var d in report.Demonstrations.Where(d => !d.Matched))
        {
            Console.WriteLine($"  demonstration {d.Index}:{(d.Error is null ? "" : " " + d.Error)}");
            foreach (var m in d.Mismatches)
            {
                Console.WriteLine("    " + m);
            }

            foreach (var m in d.FileMismatches)
            {
                Console.WriteLine("    " + m);
            }
        }
    }
}
