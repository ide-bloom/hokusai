using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hokusai.Core;
using Hokusai.Runtime;
using Hokusai.Sql;
using Scriban;
using Scriban.Runtime;

namespace Hokusai.Generate;

/// <summary>One action to generate, with what verification said about it.</summary>
public sealed class GenerationAction
{
    public required ActionTemplate Template { get; init; }
    public VerificationReport? Report { get; init; }
    public IReadOnlyList<Demonstration> Demonstrations { get; init; } = [];

    /// <summary>Full path of a demonstration's baseline file, or null when none was captured.</summary>
    public Func<Demonstration, string?> BaselinePath { get; init; } = _ => null;
}

public sealed class GenerationOptions
{
    public string Name { get; init; } = "Hokusai.Generated";
    public required string OutputDirectory { get; init; }
    public bool Api { get; init; } = true;
    public bool Mcp { get; init; } = true;
    public bool Tests { get; init; } = true;

    /// <summary>Connection string with <c>{file}</c> in place of the database path, for tests on file databases.</summary>
    public string? FileConnectionTemplate { get; init; }
}

/// <summary>Package versions used by generated projects; kept equal to the repository's Directory.Packages.props.</summary>
public static class GeneratedVersions
{
    public const string SqlClient = "7.1.2";
    public const string Odbc = "11.0.0";
    public const string OleDb = "11.0.0";
    public const string Sqlite = "11.0.0";
    public const string Extensions = "10.0.15";
    public const string Aspnet = "10.0.15";
    public const string Mcp = "2.4.0";
    public const string Xunit = "4.0.3";
}

/// <summary>Writes the generated solution: actions library, REST API, MCP server and regression tests.</summary>
public static class ProjectGenerator
{
    private static readonly string[] CSharpKeywords =
    [
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class", "const", "continue", "decimal", "default",
        "delegate", "do", "double", "else", "enum", "event", "explicit", "extern", "false", "finally", "fixed", "float", "for", "foreach", "goto", "if",
        "implicit", "in", "int", "interface", "internal", "is", "lock", "long", "namespace", "new", "null", "object", "operator", "out", "override",
        "params", "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed", "short", "sizeof", "stackalloc", "static", "string",
        "struct", "switch", "this", "throw", "true", "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "virtual", "void",
        "volatile", "while",
    ];

    private static readonly JsonSerializerOptions IndentedJson = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static IReadOnlyList<string> Generate(IReadOnlyList<GenerationAction> actions, GenerationOptions options)
    {
        if (actions.Count == 0)
        {
            throw new ArgumentException("No actions to generate.", nameof(actions));
        }

        var providers = actions.Select(a => a.Template.Provider).Distinct().ToList();
        if (providers.Count > 1)
        {
            throw new InvalidOperationException("All actions in one generated project must use the same provider.");
        }

        var name = options.Name;
        var ns = name + ".Actions";
        var written = new List<string>();
        var models = actions.Select(a => Model(a, ns)).ToList();
        var common = new Dictionary<string, object?>
        {
            ["name"] = name,
            ["ns"] = ns,
            ["ns_api"] = name + ".Api",
            ["ns_mcp"] = name + ".Mcp",
            ["ns_tests"] = name + ".Tests",
            ["provider"] = providers[0].ToString(),
            ["user_secrets_id"] = DeterministicGuid(name).ToString(),
            ["versions"] = new Dictionary<string, object?>
            {
                ["sql_client"] = GeneratedVersions.SqlClient,
                ["odbc"] = GeneratedVersions.Odbc,
                ["ole_db"] = GeneratedVersions.OleDb,
                ["sqlite"] = GeneratedVersions.Sqlite,
                ["extensions"] = GeneratedVersions.Extensions,
                ["aspnet"] = GeneratedVersions.Aspnet,
                ["mcp"] = GeneratedVersions.Mcp,
                ["xunit"] = GeneratedVersions.Xunit,
            },
            ["actions"] = models.Select(m => m.Template).ToList(),
            ["api"] = options.Api,
            ["mcp"] = options.Mcp,
            ["tests"] = options.Tests,
            ["tool_prefix"] = name.ToLowerInvariant().Replace('.', '-'),
        };

        void Write(string relative, string content)
        {
            var path = Path.Combine(options.OutputDirectory, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content.ReplaceLineEndings("\r\n"), new UTF8Encoding(false));
            written.Add(relative.Replace('\\', '/'));
        }

        void Render(string template, string relative, Dictionary<string, object?>? extra = null)
        {
            var model = new Dictionary<string, object?>(common);
            foreach (var (k, v) in extra ?? [])
            {
                model[k] = v;
            }

            Write(relative, RenderTemplate(template, model));
        }

        Render("root/solution.slnx.sbn", name + ".slnx");
        Render("root/Directory.Build.props.sbn", "Directory.Build.props");
        Render("root/global.json.sbn", "global.json");
        Render("root/README.md.sbn", "README.md");

        var actionsDir = $"src/{name}.Actions/";
        Render("actions/Actions.csproj.sbn", actionsDir + name + ".Actions.csproj");
        Render("actions/ActionSupport.cs.sbn", actionsDir + "ActionSupport.cs");
        Render("actions/ActionCatalog.cs.sbn", actionsDir + "ActionCatalog.cs");
        foreach (var m in models)
        {
            Write(actionsDir + "Actions/" + m.Names.Pascal + "Action.cs", m.Code);
        }

        foreach (var (resource, file) in RuntimeSources())
        {
            Write(actionsDir + "Runtime/" + file, "// <auto-generated>\r\n//   Hokusai runtime, copied from the Hokusai repository. Regenerate instead of editing.\r\n// </auto-generated>\r\n#nullable enable\r\n" + ReadResource(resource));
        }

        if (options.Api)
        {
            var apiDir = $"src/{name}.Api/";
            Render("api/Api.csproj.sbn", apiDir + name + ".Api.csproj");
            Render("api/Program.cs.sbn", apiDir + "Program.cs");
            Render("api/ApiSupport.cs.sbn", apiDir + "ApiSupport.cs");
            Render("api/appsettings.json.sbn", apiDir + "appsettings.json");
        }

        if (options.Mcp)
        {
            var mcpDir = $"src/{name}.Mcp/";
            Render("mcp/Mcp.csproj.sbn", mcpDir + name + ".Mcp.csproj");
            Render("mcp/Program.cs.sbn", mcpDir + "Program.cs");
            Render("mcp/Tools.cs.sbn", mcpDir + "Tools.cs");
        }

        if (options.Tests && options.Api)
        {
            var testsDir = $"tests/{name}.Tests/";
            var tests = new List<Dictionary<string, object?>>();
            foreach (var (action, model) in actions.Zip(models))
            {
                foreach (var demo in action.Demonstrations)
                {
                    var skip = model.DisabledReason is null ? null : CSharpEmitter.Lit("Action disabled: " + model.DisabledReason);
                    var baseline = action.BaselinePath(demo);
                    if (skip is null && (baseline is null || !File.Exists(baseline) || demo.StateDiff is null))
                    {
                        skip = CSharpEmitter.Lit("The demonstration has no baseline or recorded state diff.");
                    }

                    tests.Add(new Dictionary<string, object?>
                    {
                        ["method"] = $"{model.Names.Pascal}_Demonstration{demo.Index}",
                        ["pascal"] = model.Names.Pascal,
                        ["index"] = demo.Index,
                        ["skip"] = skip,
                    });
                    if (skip is not null)
                    {
                        continue;
                    }

                    var dir = $"{testsDir}TestData/{model.Names.Route}/demo-{demo.Index.ToString("000", CultureInfo.InvariantCulture)}/";
                    Write(dir + "input.json", InputJson(action.Template, demo));
                    Write(dir + "expected-diff.json", JsonSerializer.Serialize(demo.StateDiff, HkJson.Options));
                    if (action.Template.FileOutput is not null && demo.FileEffects.LastOrDefault(f => f.Format == "csv")?.After is { } csv)
                    {
                        Write(dir + "expected.csv", csv);
                    }

                    if (ExpectedRows(action.Template, demo) is { } rows)
                    {
                        Write(dir + "expected-rows.json", rows);
                    }

                    var target = Path.Combine(options.OutputDirectory, dir, "baseline" + Path.GetExtension(baseline));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(baseline!, target, overwrite: true);
                    written.Add(dir + "baseline" + Path.GetExtension(baseline));
                }
            }

            Render("tests/Tests.csproj.sbn", testsDir + name + ".Tests.csproj");
            Render("tests/Harness.cs.sbn", testsDir + "Harness.cs");
            Render("tests/DemonstrationTests.cs.sbn", testsDir + "DemonstrationTests.cs", new() { ["tests"] = tests });
            Write(testsDir + "TestData/copy.json", CopyInfo(providers[0], options.FileConnectionTemplate));
        }

        return written;
    }

    private sealed record ActionModel(ActionNames Names, string Code, string? DisabledReason, Dictionary<string, object?> Template);

    public static string? DisabledReason(ActionTemplate template, VerificationReport? report)
    {
        if (template.Status == TemplateStatus.Verified && report is { Verified: true })
        {
            return null;
        }

        if (report is null)
        {
            return "not verified yet (run 'hokusai verify')";
        }

        if (report.Issues.Count > 0)
        {
            return "not verified: " + report.Issues[0];
        }

        var failed = report.Demonstrations.FirstOrDefault(d => !d.Matched);
        if (failed is null)
        {
            return "not verified";
        }

        return failed.Error is not null
            ? $"not verified: demonstration {failed.Index} failed to replay ({failed.Error})"
            : $"not verified: demonstration {failed.Index} differs in {failed.Mismatches.Count + failed.FileMismatches.Count} place(s), first: {failed.Mismatches.Select(m => m.ToString()).Concat(failed.FileMismatches).First()}";
    }

    private static ActionModel Model(GenerationAction action, string ns)
    {
        var t = action.Template;
        var names = ActionNames.For(t.Name);
        var reason = DisabledReason(t, action.Report);
        var emitter = new CSharpEmitter(t, ns, reason);
        var code = emitter.Emit();

        var used = new HashSet<string>(StringComparer.Ordinal) { "confirm", "dryRun", "cancellationToken", "input", "runner" };
        string Param(string n)
        {
            var id = Infer.Naming.Unique(n, used);
            return CSharpKeywords.Contains(id) ? "@" + id : id;
        }

        var parameters = new List<string>();
        var assignments = new List<string>();
        foreach (var p in t.Parameters.Where(p => p.Collection is null).OrderBy(p => p.Required || p.Lookup is not null ? 0 : 1))
        {
            var required = p.Required || p.Lookup is not null;
            var id = Param(p.Name);
            var type = required ? p.ClrType : CSharpEmitter.ClrNullable(p.ClrType);
            parameters.Add($"[Description({CSharpEmitter.Lit(CSharpEmitter.ParameterDescription(p))})] {type} {id}{(required ? "" : " = null")}");
            assignments.Add($"{Infer.Naming.Pascal(p.Name)} = {id}");
        }

        foreach (var c in t.Collections)
        {
            var id = Param(c.Name);
            var columns = string.Join(", ", t.Parameters.Where(p => p.Collection == c.Name).Select(p => p.Name + " (" + CSharpEmitter.ParameterDescription(p) + ")"));
            parameters.Insert(parameters.Count(x => !x.EndsWith(" = null", StringComparison.Ordinal)), $"[Description({CSharpEmitter.Lit($"Rows of '{c.Label ?? c.Name}'; each row has: {columns}")})] {names.Pascal}{Infer.Naming.Pascal(c.Name)}Item[] {id}");
            assignments.Add($"{Infer.Naming.Pascal(c.Name)} = {id}");
        }

        if (t.Kind == ActionKind.Write)
        {
            parameters.Add($"[Description({CSharpEmitter.Lit("Must be true to execute this write" + (t.Destructive ? " (it deletes rows)" : "") + "; without it nothing is written.")})] bool confirm = false");
        }

        parameters.Add($"[Description({CSharpEmitter.Lit("Return the planned statements without executing anything.")})] bool dryRun = false");

        var description = new StringBuilder(t.Kind == ActionKind.Write ? "Writes to the database: " : "Reads from the database: ").Append(t.Name).Append('.');
        if (t.Destructive)
        {
            description.Append(" DESTRUCTIVE: deletes rows; requires confirm=true.");
        }
        else if (t.Kind == ActionKind.Write)
        {
            description.Append(" Requires confirm=true; use dryRun=true to preview.");
        }

        if (t.FileOutput is not null)
        {
            description.Append(" Returns the exported CSV content in 'file'.");
        }

        if (reason is not null)
        {
            description.Append(" Currently disabled: ").Append(reason).Append('.');
        }

        var model = new Dictionary<string, object?>
        {
            ["display"] = t.Name,
            ["pascal"] = names.Pascal,
            ["route"] = names.Route,
            ["tool"] = names.Tool,
            ["kind"] = t.Kind == ActionKind.Write ? "write" : "read",
            ["status"] = reason is null ? "verified" : "disabled: " + reason,
            ["destructive"] = CSharpEmitter.Bool(t.Destructive),
            ["read_only"] = CSharpEmitter.Bool(t.Kind == ActionKind.Read),
            ["input_type"] = emitter.InputType,
            ["summary_lit"] = CSharpEmitter.Lit(t.Name),
            ["title_lit"] = CSharpEmitter.Lit(t.Name),
            ["description_lit"] = CSharpEmitter.Lit(description.ToString()),
            ["mcp_parameters"] = parameters,
            ["mcp_assignments"] = assignments,
            ["confirm_expr"] = t.Kind == ActionKind.Write ? "confirm" : "true",
        };
        return new ActionModel(names, code, reason, model);
    }

    private static string InputJson(ActionTemplate template, Demonstration demo)
    {
        var input = DemonstrationInputs.Build(template, demo);
        var obj = new Dictionary<string, object?>();
        foreach (var p in template.Parameters.Where(p => p.Collection is null))
        {
            obj[p.Name] = input.Scalars.GetValueOrDefault(p.Name);
        }

        foreach (var c in template.Collections)
        {
            obj[c.Name] = input.Collections.GetValueOrDefault(c.Name) ?? [];
        }

        return JsonSerializer.Serialize(obj, IndentedJson);
    }

    private static string? ExpectedRows(ActionTemplate template, Demonstration demo)
    {
        if (template.Kind != ActionKind.Read || template.ResultStep is not int step || template.ResultColumns.Count == 0)
        {
            return null;
        }

        var shape = template.AllStatements().First(s => s.Id == step).Shape;
        var dialect = template.Provider switch
        {
            ProviderKind.SqlServer => SqlDialect.SqlServer,
            ProviderKind.Access => SqlDialect.Access,
            ProviderKind.Sqlite => SqlDialect.Sqlite,
            _ => SqlDialect.Generic,
        };
        var recorded = demo.Statements.LastOrDefault(s => s.Result is not null &&
            SqlNormalizer.For(dialect).Normalize(s.Text, [.. s.Parameters.Select(p => new BoundValue(p.Name, p.Value))]).Shape == shape)?.Result;
        if (recorded is null)
        {
            return null;
        }

        var columns = template.ResultColumns.Select(c => JsonNamingPolicy.CamelCase.ConvertName(Infer.Naming.Pascal(Infer.Naming.Camel(c.Name)))).ToList();
        var indexes = template.ResultColumns.Select(c => recorded.Columns.FindIndex(x => string.Equals(x, c.Name, StringComparison.OrdinalIgnoreCase))).ToList();
        var rows = recorded.Rows.Select(r => indexes.Select(i => i < 0 ? null : r[i]).ToList()).ToList();
        return JsonSerializer.Serialize(new { columns, rows }, IndentedJson);
    }

    private static string CopyInfo(ProviderKind provider, string? template)
    {
        string? server = null;
        if (provider == ProviderKind.SqlServer)
        {
            // Regression tests restore baselines locally; they never connect to the recorded server.
            server = @"Data Source=(localdb)\MSSQLLocalDB;Integrated Security=true;TrustServerCertificate=true";
        }
        else if (template is not null && ContainsSecret(template))
        {
            template = null;
        }

        return JsonSerializer.Serialize(new { provider = provider.ToString(), server, template }, IndentedJson);
    }

    /// <summary>Connection strings with passwords are never written; tests then read HOKUSAI_TEST_CONNECTION_TEMPLATE.</summary>
    public static bool ContainsSecret(string connectionString)
    {
        var builder = new System.Data.Common.DbConnectionStringBuilder();
        try
        {
            builder.ConnectionString = connectionString;
        }
        catch (ArgumentException)
        {
            return true;
        }

        return builder.Keys.Cast<string>().Any(k => k.Contains("password", StringComparison.OrdinalIgnoreCase) || string.Equals(k, "pwd", StringComparison.OrdinalIgnoreCase));
    }

    private static Guid DeterministicGuid(string name) => new(SHA256.HashData(Encoding.UTF8.GetBytes("hokusai:" + name))[..16]);

    private static IEnumerable<(string Resource, string File)> RuntimeSources() =>
        typeof(ProjectGenerator).Assembly.GetManifestResourceNames()
            .Where(r => r.StartsWith("runtime/", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .Select(r => (r, r["runtime/".Length..]));

    private static string ReadResource(string name)
    {
        // MSBuild's RecursiveDir uses the platform separator, so resource names may contain backslashes.
        var actual = typeof(ProjectGenerator).Assembly.GetManifestResourceNames().FirstOrDefault(r => r.Replace('\\', '/') == name)
            ?? throw new InvalidOperationException($"Missing resource {name}.");
        using var stream = typeof(ProjectGenerator).Assembly.GetManifestResourceStream(actual)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string RenderTemplate(string name, Dictionary<string, object?> model)
    {
        var template = Template.Parse(ReadResource("templates/" + name), name);
        if (template.HasErrors)
        {
            throw new InvalidOperationException($"Template {name}: {string.Join("; ", template.Messages)}");
        }

        var globals = new ScriptObject();
        foreach (var (k, v) in model)
        {
            globals[k] = v;
        }

        var context = new TemplateContext { StrictVariables = true, MemberRenamer = m => m.Name };
        context.PushGlobal(globals);
        return template.Render(context);
    }
}
