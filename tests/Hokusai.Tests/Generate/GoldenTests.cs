using System.Globalization;
using Hokusai.Core;
using Hokusai.Generate;
using Hokusai.Infer;
using Hokusai.Runtime;
using Hokusai.Tests.Verify;

namespace Hokusai.Tests.Generate;

/// <summary>
/// Generates a project from SQLite demonstrations and compares every text file with the committed golden copy.
/// Set HOKUSAI_UPDATE_GOLDEN=1 to rewrite the golden files after an intended change.
/// </summary>
public sealed class GoldenTests : IDisposable
{
    private readonly SqliteApp _app = new();
    private readonly string _out = Path.Combine(Path.GetTempPath(), "hokusai-golden", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _app.Dispose();
        if (Environment.GetEnvironmentVariable("HOKUSAI_KEEP_GENERATED") is null)
        {
            HkCopies.TryDelete(_out);
        }
    }

    internal static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "hokusai.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    internal static async Task<List<GenerationAction>> SampleActionsAsync(SqliteApp app, string baselineDirectory)
    {
        var ctx = new InferenceContext
        {
            Schema = await app.SchemaAsync(),
            Provider = ProviderKind.Sqlite,
            Dialect = SqlDialect.Sqlite,
            Cultures = ["en-US"],
            BeforeSnapshot = _ => app.SeedSnapshotAsync().GetAwaiter().GetResult(),
        };
        Directory.CreateDirectory(baselineDirectory);
        var baseline = Path.Combine(baselineDirectory, "baseline.db");
        File.Copy(app.SeedPath, baseline, overwrite: true);

        async Task<GenerationAction> Action(string name, params Demonstration[] demos)
        {
            foreach (var d in demos)
            {
                await app.RecordAsync(d);
            }

            var template = TemplateInferrer.Infer(name, demos, ctx);
            var report = new VerificationReport { Action = name, Verified = template.Issues.Count == 0 };
            foreach (var d in demos)
            {
                var result = new DemoVerification { Index = d.Index };
                await Hokusai.Verify.Verifier.VerifyOnAsync(template, d, SqliteApp.ConnectionString(app.Copy()), result, TestContext.Current.CancellationToken);
                report.Demonstrations.Add(result);
            }

            report.Verified &= report.Demonstrations.All(r => r.Matched);
            template.Status = report.Verified ? TemplateStatus.Verified : TemplateStatus.Unverified;
            return new GenerationAction { Template = template, Report = report, Demonstrations = demos, BaselinePath = _ => baseline };
        }

        Demonstration Invoice(int i, string customer, int id, bool taxable, params string[][] lines)
        {
            var b = new DemoBuilder("create invoice", i, ProviderKind.Sqlite)
                .Select("cboCustomer", "Customer:", customer).Toggle("chkTaxable", "Taxable", taxable)
                .Grid("grdLines", "Lines", ["Description", "Qty", "Price"], lines).Click("btnSave", "Save")
                .Sql("INSERT INTO Invoice (CustomerId, Status, Taxable) VALUES (@c, 'Draft', @t)", null, ("c", id.ToString(CultureInfo.InvariantCulture)), ("t", taxable ? "1" : "0"))
                .Sql("SELECT last_insert_rowid() AS NewId");
            return b.Build().WithIdentityChain(lines, taxable);
        }

        Demonstration Search(int i, string text) => new DemoBuilder("search customers", i, ProviderKind.Sqlite)
            .Text("txtSearch", "Search:", text).Click("btnSearch", "Search")
            .Sql("SELECT CustomerId, Name, City FROM Customer WHERE Name LIKE @p ORDER BY Name", null, ("p", "%" + text + "%"))
            .Build();

        Demonstration Export(int i, string path) => new DemoBuilder("export customers", i, ProviderKind.Sqlite)
            .Text("txtPath", "File:", path).Click("btnExport", "Export")
            .Sql("SELECT CustomerId, Name, CreditLimit FROM Customer ORDER BY CustomerId")
            .File(path, "csv", null, "Id;Name;Limit\r\n1;Acme Ltd;1000.00\r\n2;Globex;500.00\r\n3;Initech;250.00\r\n")
            .Build();

        Demonstration Delete(int i, string customer, string number) => new DemoBuilder("delete draft invoice", i, ProviderKind.Sqlite)
            .Select("grdInvoices", "Invoices", number).Click("btnDelete", "Delete")
            .Sql("DELETE FROM Invoice WHERE InvoiceId = @id AND Status = 'Draft'", null, ("id", number))
            .Build();

        return
        [
            await Action("create invoice", Invoice(1, "Globex", 2, true, ["Widget", "2", "9.5"], ["Gadget", "1", "120"]), Invoice(2, "Acme Ltd", 1, false, ["Service", "3", "45"])),
            await Action("search customers", Search(1, "Ac"), Search(2, "lo")),
            await Action("export customers", Export(1, @"C:\out\a.csv"), Export(2, @"C:\out\b.csv")),
            await Action("delete draft invoice", Delete(1, "Acme Ltd", "10"), Delete(2, "Globex", "11")),
        ];
    }

    [Fact]
    public async Task Generated_project_matches_golden_files()
    {
        var actions = await SampleActionsAsync(_app, Path.Combine(_out, "_baselines"));
        var files = ProjectGenerator.Generate(actions, new GenerationOptions
        {
            Name = "Invoicing",
            OutputDirectory = _out,
            FileConnectionTemplate = "Data Source={file};Pooling=False",
        });

        var golden = Path.Combine(RepositoryRoot(), "tests", "Hokusai.Tests", "Golden", "Invoicing");
        var update = Environment.GetEnvironmentVariable("HOKUSAI_UPDATE_GOLDEN") == "1";
        var compared = files.Where(f => !f.Contains("/Runtime/", StringComparison.Ordinal) && !Path.GetFileName(f).StartsWith("baseline", StringComparison.Ordinal)).ToList();
        if (update)
        {
            HkCopies.TryDelete(golden);
        }

        foreach (var file in compared)
        {
            var actual = await File.ReadAllTextAsync(Path.Combine(_out, file), TestContext.Current.CancellationToken);
            var goldenPath = Path.Combine(golden, file);
            if (update)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(goldenPath)!);
                await File.WriteAllTextAsync(goldenPath, actual, TestContext.Current.CancellationToken);
                continue;
            }

            Assert.True(File.Exists(goldenPath), $"No golden file for {file}; run with HOKUSAI_UPDATE_GOLDEN=1.");
            Assert.Equal((await File.ReadAllTextAsync(goldenPath, TestContext.Current.CancellationToken)).ReplaceLineEndings(), actual.ReplaceLineEndings());
        }

        Assert.All(actions, a => Assert.Equal(TemplateStatus.Verified, a.Template.Status));
        Assert.Contains("src/Invoicing.Actions/Runtime/HkRun.cs", files);
    }

    [Fact]
    public async Task Unverified_actions_are_generated_disabled_with_the_reason()
    {
        var actions = await SampleActionsAsync(_app, Path.Combine(_out, "_baselines"));
        actions[0].Template.Status = TemplateStatus.Unverified;
        var disabled = new GenerationAction
        {
            Template = actions[0].Template,
            Report = new VerificationReport { Issues = ["Step 3: value varies"] },
            Demonstrations = actions[0].Demonstrations,
            BaselinePath = actions[0].BaselinePath,
        };

        ProjectGenerator.Generate([disabled], new GenerationOptions { Name = "Disabled", OutputDirectory = _out, Tests = true, FileConnectionTemplate = "Data Source={file}" });

        var code = await File.ReadAllTextAsync(Path.Combine(_out, "src/Disabled.Actions/Actions/CreateInvoiceAction.cs"), TestContext.Current.CancellationToken);
        Assert.Contains("Verified = false", code, StringComparison.Ordinal);
        Assert.Contains("DisabledReason = \"not verified: Step 3: value varies\"", code, StringComparison.Ordinal);
        var tests = await File.ReadAllTextAsync(Path.Combine(_out, "tests/Disabled.Tests/DemonstrationTests.cs"), TestContext.Current.CancellationToken);
        Assert.Contains("Skip = \"Action disabled:", tests, StringComparison.Ordinal);
    }

    [Fact]
    public void Generated_package_versions_match_the_repository()
    {
        var props = File.ReadAllText(Path.Combine(RepositoryRoot(), "Directory.Packages.props"));

        Assert.Contains($"\"Microsoft.Data.SqlClient\" Version=\"{GeneratedVersions.SqlClient}\"", props, StringComparison.Ordinal);
        Assert.Contains($"\"ModelContextProtocol\" Version=\"{GeneratedVersions.Mcp}\"", props, StringComparison.Ordinal);
        Assert.Contains($"\"xunit.v3\" Version=\"{GeneratedVersions.Xunit}\"", props, StringComparison.Ordinal);
        Assert.Contains($"\"Microsoft.Data.Sqlite\" Version=\"{GeneratedVersions.Sqlite}\"", props, StringComparison.Ordinal);
        Assert.Contains($"\"Microsoft.Extensions.Hosting\" Version=\"{GeneratedVersions.Extensions}\"", props, StringComparison.Ordinal);
    }

    [Fact]
    public void Connection_templates_with_passwords_are_not_written() =>
        Assert.True(ProjectGenerator.ContainsSecret("Provider=Microsoft.ACE.OLEDB.16.0;Data Source={file};Jet OLEDB:Database Password=x"));
}
