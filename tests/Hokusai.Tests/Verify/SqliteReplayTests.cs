using System.Globalization;
using Hokusai.Core;
using Hokusai.Infer;
using Hokusai.Runtime;
using Hokusai.Verify;

namespace Hokusai.Tests.Verify;

/// <summary>Infers templates from app runs on SQLite, then replays them on fresh copies: the platform-independent core of verification.</summary>
public sealed class SqliteReplayTests : IDisposable
{
    private readonly SqliteApp _app = new();

    public void Dispose() => _app.Dispose();

    private async Task<InferenceContext> ContextAsync() => new()
    {
        Schema = await _app.SchemaAsync(),
        Provider = ProviderKind.Sqlite,
        Dialect = SqlDialect.Sqlite,
        Cultures = ["en-US"],
        BeforeSnapshot = _ => _app.SeedSnapshotAsync().GetAwaiter().GetResult(),
    };

    private static DemoBuilder InvoiceUi(int index, string customer, bool taxable, params string[][] lines) =>
        new DemoBuilder("create invoice", index, ProviderKind.Sqlite)
            .Select("cboCustomer", "Customer:", customer)
            .Toggle("chkTaxable", "Taxable", taxable)
            .Grid("grdLines", "Lines", ["Description", "Qty", "Price"], lines)
            .Click("btnSave", "Save");

    /// <summary>What the app does on Save, written the way an ODBC app binds parameters.</summary>
    private static Demonstration InvoiceApp(DemoBuilder ui, int customerId, bool taxable, string[][] lines)
    {
        ui.Sql("INSERT INTO Invoice (CustomerId, Status, Taxable) VALUES (@c, 'Draft', @t)", null, ("c", customerId.ToString(CultureInfo.InvariantCulture)), ("t", taxable ? "1" : "0"))
          .Sql("SELECT last_insert_rowid() AS NewId");
        return ui.Build().WithIdentityChain(lines, taxable);
    }

    private async Task<Demonstration> RecordInvoiceAsync(int index, string customer, int customerId, bool taxable, params string[][] lines)
    {
        // Identity values are only known after running, so the app's statements are replayed step by step.
        var demo = InvoiceApp(InvoiceUi(index, customer, taxable, lines), customerId, taxable, lines);
        return await _app.RecordAsync(demo);
    }

    [Fact]
    public async Task Create_invoice_verifies_on_fresh_copies_and_reproduces_new_inputs()
    {
        var ct = TestContext.Current.CancellationToken;
        var d1 = await RecordInvoiceAsync(1, "Globex", 2, true, ["Widget", "2", "9.5"], ["Gadget", "1", "120"]);
        var d2 = await RecordInvoiceAsync(2, "Acme Ltd", 1, false, ["Service", "3", "45"]);
        var template = TemplateInferrer.Infer("create invoice", [d1, d2], await ContextAsync());

        Assert.Empty(template.Issues);
        foreach (var demo in new[] { d1, d2 })
        {
            var result = new DemoVerification { Index = demo.Index };
            await Verifier.VerifyOnAsync(template, demo, SqliteApp.ConnectionString(_app.Copy()), result, ct);
            Assert.True(result.Matched, string.Join("; ", result.Mismatches));
        }

        // New inputs never demonstrated: the template must do exactly what the app does.
        var fresh = InvoiceApp(InvoiceUi(3, "Initech", true, ["Bolt", "10", "0.25"], ["Nut", "7", "0.1"], ["Washer", "1", "0.05"]), 3, true,
            [["Bolt", "10", "0.25"], ["Nut", "7", "0.1"], ["Washer", "1", "0.05"]]);
        const string otherState = "INSERT INTO Invoice (InvoiceId, CustomerId, Status, Taxable) VALUES (40, 3, 'Paid', 0)";
        var appRun = await _app.RecordAsync(fresh, otherState);
        var check = new DemoVerification();
        await Verifier.VerifyOnAsync(template, appRun, SqliteApp.ConnectionString(_app.Copy(otherState)), check, ct);
        Assert.True(check.Matched, string.Join("; ", check.Mismatches));
        Assert.Equal(5, check.Actual!.ChangeCount);
        Assert.Contains(check.Actual.Tables.Single(t => t.Table == "InvoiceLine").Changes, c => c.After!["InvoiceId"] == "41");
    }

    [Fact]
    public async Task Mismatches_name_the_exact_row_and_column()
    {
        var ct = TestContext.Current.CancellationToken;
        var d1 = await RecordInvoiceAsync(1, "Globex", 2, true, ["Widget", "2", "9.5"]);
        var d2 = await RecordInvoiceAsync(2, "Acme Ltd", 1, false, ["Service", "3", "45"]);
        var template = TemplateInferrer.Infer("create invoice", [d1, d2], await ContextAsync());

        // Sabotage the template: write the status in upper case.
        var insert = template.AllStatements().First(s => s.Table == "Invoice" && s.Kind == "INSERT");
        insert.Bindings.Single(b => b.Kind == BindingKind.Constant && b.Constant == "Draft").Constant = "DRAFT";

        var result = new DemoVerification();
        await Verifier.VerifyOnAsync(template, d1, SqliteApp.ConnectionString(_app.Copy()), result, ct);

        Assert.False(result.Matched);
        var m = Assert.Single(result.Mismatches);
        Assert.Equal(("Invoice", "Status", "Draft", "DRAFT"), (m.Table, m.Column, m.Expected, m.Actual));
        Assert.Contains("InvoiceId=12", m.Key, StringComparison.Ordinal);
    }

    [Fact]
    public async Task State_diff_only_demonstrations_infer_and_verify()
    {
        var ct = TestContext.Current.CancellationToken;
        async Task<Demonstration> Run(int index, string name, string city, string limit)
        {
            var demo = new DemoBuilder("create customer", index, ProviderKind.Sqlite)
                .Text("txtName", "Name:", name).Text("txtCity", "City:", city).Text("txtLimit", "Credit limit:", limit).Click("btnSave", "Save")
                .Sql("INSERT INTO Customer (Name, City, CreditLimit) VALUES (@n, @c, @l)", null, ("n", name), ("c", city), ("l", limit))
                .Build();
            await _app.RecordAsync(demo);

            // Capture only the state diff, as for a file database without the ODBC agent.
            demo.Statements.Clear();
            return demo;
        }

        var d1 = await Run(1, "Hooli", "Wien", "300");
        var d2 = await Run(2, "Umbrella", "Salzburg", "1200.5");
        var template = TemplateInferrer.Infer("create customer", [d1, d2], await ContextAsync());

        Assert.Empty(template.Issues);
        Assert.Contains("Customer.CreatedAt", template.GeneratedColumns);
        var result = new DemoVerification();
        await Verifier.VerifyOnAsync(template, d2, SqliteApp.ConnectionString(_app.Copy()), result, ct);
        Assert.True(result.Matched, string.Join("; ", result.Mismatches));
    }

    private async Task<Demonstration> DiffOnlyDeleteAsync(int index, string invoiceId)
    {
        var demo = new DemoBuilder("delete draft invoice", index, ProviderKind.Sqlite)
            .Select("cboInvoice", "Invoice:", invoiceId).Click("btnDelete", "Delete draft")
            .Sql("DELETE FROM Invoice WHERE InvoiceId = @id AND Status = 'Draft'", null, ("id", invoiceId))
            .Build();
        await _app.RecordAsync(demo, "INSERT INTO Invoice (InvoiceId, CustomerId, Status, Taxable) VALUES (12, 3, 'Draft', 0)");

        // State-diff capture: only the deleted row is visible, not the app's "AND Status = 'Draft'".
        demo.Statements.Clear();
        return demo;
    }

    [Fact]
    public async Task Diff_only_delete_needs_a_guard_before_it_can_verify()
    {
        var ct = TestContext.Current.CancellationToken;
        var demos = new[] { await DiffOnlyDeleteAsync(1, "10"), await DiffOnlyDeleteAsync(2, "12") };
        var ctx = await ContextAsync();
        var unguarded = TemplateInferrer.Infer("delete draft invoice", demos, ctx);

        var step = unguarded.AllStatements().Single(s => s.Kind == "DELETE");
        Assert.True(step.GuardRequired);
        Assert.Null(step.Guard);
        Assert.Contains(unguarded.Issues, i => i.Contains("hokusai guard", StringComparison.Ordinal));
        Assert.Contains("not verified: Step", Hokusai.Generate.ProjectGenerator.DisabledReason(unguarded, new VerificationReport { Issues = [.. unguarded.Issues] }), StringComparison.Ordinal);

        // With the guard the user supplied, the demonstrations verify...
        var guards = new Guards();
        guards.ByShape[step.Shape] = "Status = 'Draft'";
        var guarded = TemplateInferrer.Infer("delete draft invoice", demos, new InferenceContext
        {
            Schema = ctx.Schema,
            Provider = ctx.Provider,
            Dialect = ctx.Dialect,
            Cultures = ctx.Cultures,
            BeforeSnapshot = ctx.BeforeSnapshot,
            Guards = guards,
        });
        Assert.Empty(guarded.Issues);
        Assert.EndsWith(" AND (Status = 'Draft')", guarded.AllStatements().Single(s => s.Kind == "DELETE").Sql, StringComparison.Ordinal);
        foreach (var demo in demos)
        {
            var result = new DemoVerification();
            var copy = _app.Copy("INSERT INTO Invoice (InvoiceId, CustomerId, Status, Taxable) VALUES (12, 3, 'Draft', 0)");
            await Verifier.VerifyOnAsync(guarded, demo, SqliteApp.ConnectionString(copy), result, ct);
            Assert.True(result.Matched, string.Join("; ", result.Mismatches));
        }

        // ...and the guard does what the app's condition did: a sent invoice (11) is not deleted.
        await using var connection = await HkDb.OpenAsync(ProviderKind.Sqlite, SqliteApp.ConnectionString(_app.Copy()), ct);
        var input = new ActionInput();
        input.Scalars[guarded.Parameters.Single().Name] = "11";
        var outcome = await TemplateExecutor.ExecuteAsync(guarded, connection, null, input, dryRun: false, ct);
        Assert.Equal(0, outcome.RowsAffected);
    }

    [Fact]
    public async Task Unconditional_guard_is_an_explicit_choice()
    {
        var demos = new[] { await DiffOnlyDeleteAsync(1, "10"), await DiffOnlyDeleteAsync(2, "12") };
        var ctx = await ContextAsync();
        var shape = TemplateInferrer.Infer("d", demos, ctx).AllStatements().Single(s => s.Kind == "DELETE").Shape;
        var guards = new Guards();
        guards.ByShape[shape] = Guards.Unconditional;

        var t = TemplateInferrer.Infer("d", demos, new InferenceContext { Schema = ctx.Schema, Provider = ctx.Provider, Dialect = ctx.Dialect, BeforeSnapshot = ctx.BeforeSnapshot, Guards = guards });

        Assert.Empty(t.Issues);
        var step = t.AllStatements().Single(s => s.Kind == "DELETE");
        Assert.Equal(Guards.Unconditional, step.Guard);
        Assert.DoesNotContain("AND (", step.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Captured_deletes_do_not_need_a_guard()
    {
        var demo = await _app.RecordAsync(new DemoBuilder("d", 1, ProviderKind.Sqlite).Select("cboInvoice", "Invoice:", "10").Click("btnDelete", "Delete")
            .Sql("DELETE FROM Invoice WHERE InvoiceId = @id AND Status = 'Draft'", null, ("id", "10")).Build());

        var t = TemplateInferrer.Infer("d", [demo], await ContextAsync());

        Assert.False(t.AllStatements().Single().GuardRequired);
    }

    [Fact]
    public async Task Dry_run_plans_statements_without_touching_the_database()
    {
        var d1 = await RecordInvoiceAsync(1, "Globex", 2, true, ["Widget", "2", "9.5"]);
        var d2 = await RecordInvoiceAsync(2, "Acme Ltd", 1, false, ["Service", "3", "45"]);
        var template = TemplateInferrer.Infer("create invoice", [d1, d2], await ContextAsync());

        var outcome = await TemplateExecutor.ExecuteAsync(template, null, null, DemonstrationInputs.Build(template, d1), dryRun: true, TestContext.Current.CancellationToken);

        Assert.Equal(4, outcome.Statements.Count);
        Assert.Contains("key of Customer", outcome.Statements[0].Arguments["hk0"], StringComparison.Ordinal);
        Assert.Contains("result of step", outcome.Statements[2].Arguments["hk0"], StringComparison.Ordinal);
    }
}

internal static class InvoiceDemoExtensions
{
    /// <summary>
    /// Appends the statements an app issues after reading the new invoice id. A parameter value "=NewId" is bound
    /// by <see cref="SqliteApp"/> to the id the app just read, as an ODBC app would bind it.
    /// </summary>
    public static Demonstration WithIdentityChain(this Demonstration demo, string[][] lines, bool taxable)
    {
        var t = demo.Statements[^1].Timestamp;
        foreach (var line in lines)
        {
            t = t.AddSeconds(1);
            demo.Statements.Add(new StatementEvent
            {
                Timestamp = t,
                Source = "odbc",
                Text = "INSERT INTO InvoiceLine (InvoiceId, Description, Qty, Price) VALUES (@inv, @d, @q, @p)",
                Parameters = [new() { Name = "inv", Value = "=NewId" }, new() { Name = "d", Value = line[0] }, new() { Name = "q", Value = line[1] }, new() { Name = "p", Value = line[2] }],
            });
        }

        if (taxable)
        {
            t = t.AddSeconds(1);
            demo.Statements.Add(new StatementEvent { Timestamp = t, Source = "odbc", Text = "INSERT INTO InvoiceTax (InvoiceId, Rate) VALUES (@inv, 20)", Parameters = [new() { Name = "inv", Value = "=NewId" }] });
        }

        demo.End = t.AddSeconds(1);
        return demo;
    }
}
