using Hokusai.Core;
using Hokusai.Infer;
using Hokusai.Runtime;

namespace Hokusai.Tests.Infer;

public sealed class TemplateInferenceTests
{
    private static InferenceContext Context(ProviderKind provider = ProviderKind.SqlServer, SqlDialect dialect = SqlDialect.SqlServer) => new()
    {
        Schema = TestSchema.Invoicing(),
        Provider = provider,
        Dialect = dialect,
        Cultures = ["en-US"],
        BeforeSnapshot = _ => TestSchema.Customers(),
    };

    private static ActionTemplate Infer(params Demonstration[] demos) => TemplateInferrer.Infer(demos[0].Action, demos, Context());

    private static Demonstration CreateCustomer(int index, string name, string city, string limit) =>
        new DemoBuilder("create customer", index, culture: "de-DE")
            .Text("txtName", "Name:", name).Text("txtCity", "City:", city).Text("txtCredit", "Credit limit:", limit)
            .Click("btnSave", "Save")
            .Sql($"INSERT INTO Customer (Name, City, CreditLimit, CreatedAt) VALUES ('{name.ToUpperInvariant()}', '{city}', {decimal.Parse(limit, System.Globalization.CultureInfo.GetCultureInfo("de-DE")).ToString(System.Globalization.CultureInfo.InvariantCulture)}, GETDATE())")
            .Build();

    [Fact]
    public void Create_customer_infers_inputs_with_transforms_and_generated_columns()
    {
        var t = Infer(CreateCustomer(1, "Acme Ltd", "Graz", "1.500,50"), CreateCustomer(2, "Globex", "Linz", "250"));

        Assert.Empty(t.Issues);
        Assert.Equal(ActionKind.Write, t.Kind);
        Assert.False(t.Destructive);
        var step = Assert.IsType<StatementStep>(Assert.Single(t.Steps));
        Assert.Equal("INSERT INTO Customer (Name, City, CreditLimit, CreatedAt) VALUES (@hk0, @hk1, @hk2, GETDATE())", step.Sql);
        Assert.Equal([EncodingKind.Upper, EncodingKind.Identity, EncodingKind.Number], step.Bindings.Select(b => b.Encoding.Kind));
        Assert.Equal(["name", "city", "creditLimit"], t.Parameters.Select(p => p.Name));
        var credit = t.Parameter("creditLimit")!;
        Assert.Equal(("decimal", "de-DE"), (credit.ClrType, credit.UiCulture));
        Assert.Equal(100, t.Parameter("name")!.MaxLength);
        Assert.True(t.Parameter("name")!.Required);
        Assert.False(t.Parameter("city")!.Required);
        Assert.Contains("Customer.CreatedAt", t.GeneratedColumns);
        Assert.Contains(t.Evidence, e => e.Rule == "input:number:de-DE");
    }

    private static Demonstration CreateInvoice(int index, (string Name, string Id) customer, string date, bool taxable, int next, int identity, params string[][] lines)
    {
        var b = new DemoBuilder("create invoice", index, culture: "de-DE")
            .Select("cboCustomer", "Customer:", customer.Name)
            .Text("txtDate", "Date:", date)
            .Toggle("chkTaxable", "Taxable", taxable)
            .Grid("grdLines", "Lines", ["Description", "Qty", "Price"], lines)
            .Click("btnSave", "Save")
            .Sql("SELECT NextValue FROM Counter WHERE Name = 'Invoice'", DemoBuilder.Result(["NextValue"], [next.ToString(System.Globalization.CultureInfo.InvariantCulture)]))
            .Sql("UPDATE Counter SET NextValue = NextValue + 1 WHERE Name = 'Invoice'")
            .Sql($"INSERT INTO Invoice (Number, CustomerId, Status, Taxable, InvoiceDate, Total) VALUES ('{next}', {customer.Id}, 'Draft', {(taxable ? 1 : 0)}, '{DateTime.ParseExact(date, "dd.MM.yyyy", null):yyyy-MM-dd}', 0)")
            .Sql("SELECT CAST(SCOPE_IDENTITY() AS int) AS NewId", DemoBuilder.Result(["NewId"], [identity.ToString(System.Globalization.CultureInfo.InvariantCulture)]));
        foreach (var line in lines)
        {
            var price = decimal.Parse(line[2], System.Globalization.CultureInfo.GetCultureInfo("de-DE")).ToString(System.Globalization.CultureInfo.InvariantCulture);
            b.Sql($"INSERT INTO InvoiceLine (InvoiceId, Description, Qty, Price) VALUES ({identity}, N'{line[0]}', {line[1]}, {price})");
        }

        if (taxable)
        {
            b.Sql($"INSERT INTO InvoiceTax (InvoiceId, Rate) VALUES ({identity}, 20)");
        }

        b.Sql($"UPDATE Invoice SET Total = (SELECT SUM(Qty * Price) FROM InvoiceLine WHERE InvoiceId = {identity}) WHERE InvoiceId = {identity}");
        return b.Build();
    }

    [Fact]
    public void Create_invoice_infers_lookup_dependents_loop_and_condition()
    {
        var t = Infer(
            CreateInvoice(1, ("Globex", "2"), "24.11.2026", true, 1005, 37, ["Widget", "2", "9,50"], ["Gadget", "1", "120"], ["Bolt", "10", "0,25"]),
            CreateInvoice(2, ("Acme Ltd", "1"), "25.11.2026", false, 1006, 38, ["Service", "3", "45"]));

        Assert.Empty(t.Issues);

        var customer = t.Parameter("customer")!;
        Assert.Equal("Customer", customer.Lookup?.Table);
        Assert.Equal(("CustomerId", "Name"), (customer.Lookup!.KeyColumn, customer.Lookup.DisplayColumn));

        var date = t.Parameter("date")!;
        Assert.Equal(("DateOnly", "de-DE"), (date.ClrType, date.UiCulture));

        var taxable = t.Parameter("taxable")!;
        Assert.Equal("bool", taxable.ClrType);

        var statements = t.AllStatements().ToList();
        var invoice = statements.Single(s => s.Kind == "INSERT" && s.Table == "Invoice");
        Assert.Equal(BindingKind.Dependent, invoice.Bindings[0].Kind);
        Assert.Equal("NextValue", invoice.Bindings[0].SourceColumn);
        Assert.Equal(EncodingKind.BoolMap, invoice.Bindings[3].Encoding.Kind);
        Assert.Equal("1|0", invoice.Bindings[3].Encoding.Format);

        var loop = Assert.Single(t.Steps.OfType<LoopStep>());
        Assert.Equal("lines", loop.Collection);
        var line = Assert.IsType<StatementStep>(Assert.Single(loop.Body));
        Assert.Equal(BindingKind.Dependent, line.Bindings[0].Kind);
        Assert.Equal("NewId", line.Bindings[0].SourceColumn);
        Assert.Equal(["description", "qty", "price"], t.Collections.Single().Parameters);
        Assert.Equal("int", t.Parameter("qty")!.ClrType);
        Assert.Equal(("decimal", "de-DE"), (t.Parameter("price")!.ClrType, t.Parameter("price")!.UiCulture));

        var conditional = Assert.Single(t.Steps.OfType<ConditionalStep>());
        Assert.Equal(("taxable", ConditionOp.IsTrue), (conditional.Condition.Parameter, conditional.Condition.Op));

        Assert.All(statements.Single(s => s.Kind == "UPDATE" && s.Table == "Invoice").Bindings, b => Assert.Equal(BindingKind.Dependent, b.Kind));
        Assert.Equal(["Counter", "Customer", "Invoice", "InvoiceLine", "InvoiceTax"], t.Tables);
    }

    [Fact]
    public void Demonstration_inputs_are_rebuilt_typed_from_the_ui()
    {
        var demo = CreateInvoice(1, ("Globex", "2"), "24.11.2026", true, 1005, 37, ["Widget", "2", "9,50"], ["Gadget", "1", "120"]);
        var t = Infer(demo, CreateInvoice(2, ("Acme Ltd", "1"), "25.11.2026", false, 1006, 38, ["Service", "3", "45"]));

        var input = DemonstrationInputs.Build(t, demo);

        Assert.Equal("Globex", input.Scalars["customer"]);
        Assert.Equal(new DateOnly(2026, 11, 24), input.Scalars["date"]);
        Assert.Equal(true, input.Scalars["taxable"]);
        Assert.Equal(2, input.Collections["lines"].Count);
        Assert.Equal(9.5m, input.Collections["lines"][0]["price"]);
        Assert.Equal(2, input.Collections["lines"][0]["qty"]);
    }

    private static Demonstration Search(int index, string text, params string?[][] rows) =>
        new DemoBuilder("search customers", index)
            .Text("txtSearch", "Search:", text).Click("btnSearch", "Search")
            .Sql($"SELECT CustomerId, Name, City FROM Customer WHERE Name LIKE '%{text}%' ORDER BY Name", DemoBuilder.Result(["CustomerId", "Name", "City"], rows))
            .Build();

    [Fact]
    public void Search_is_a_read_action_with_typed_results()
    {
        var t = Infer(Search(1, "Ac", ["1", "Acme Ltd", "Graz"]), Search(2, "lo", ["2", "Globex", null]));

        Assert.Equal(ActionKind.Read, t.Kind);
        Assert.Equal(EncodingKind.LikeContains, t.AllStatements().Single().Bindings.Single().Encoding.Kind);
        Assert.Equal(t.AllStatements().Single().Id, t.ResultStep);
        Assert.Equal(["CustomerId:int:False", "Name:string:False", "City:string:True"], t.ResultColumns.Select(c => $"{c.Name}:{c.ClrType}:{c.Nullable}"));
    }

    private static Demonstration ChangeStatus(int index, string number, string id, string status) =>
        new DemoBuilder("change invoice status", index)
            .Select("grdInvoices", "Invoices", number, number, "Acme Ltd", "Draft")
            .Select("cboStatus", "Status:", status)
            .Click("btnApply", "Apply")
            .Sql($"UPDATE Invoice SET Status = '{status}' WHERE InvoiceId = {id}")
            .Build();

    [Fact]
    public void Selected_grid_row_maps_to_the_row_key_through_a_lookup()
    {
        var t = Infer(ChangeStatus(1, "INV-1001", "10", "Sent"), ChangeStatus(2, "INV-1002", "11", "Paid"));

        Assert.Empty(t.Issues);
        var invoice = t.Parameters.Single(p => p.Lookup is not null);
        Assert.Equal(("Invoice", "InvoiceId", "Number"), (invoice.Lookup!.Table, invoice.Lookup.KeyColumn, invoice.Lookup.DisplayColumn));
        Assert.Equal("selection", t.Parameter("status")!.UiKind);
    }

    [Fact]
    public void Lookup_through_a_result_the_app_read_before_the_demonstration()
    {
        Demonstration Demo(int i, string number, string id, string status) => new DemoBuilder("status", i)
            .Context("SELECT InvoiceId, Number FROM Invoice ORDER BY Number", DemoBuilder.Result(["InvoiceId", "Number"], ["10", "INV-1001"], ["11", "INV-1002"]))
            .Select("cboInvoice", "Invoice:", number).Select("cboStatus", "Status:", status).Click("btnApply", "Apply")
            .Sql($"UPDATE Invoice SET Status = '{status}' WHERE InvoiceId = {id}")
            .Build();

        // No start-of-demonstration snapshot: the lookup must come from the query that filled the combo box.
        var t = TemplateInferrer.Infer("status", [Demo(1, "INV-1001", "10", "Sent"), Demo(2, "INV-1002", "11", "Paid")], new InferenceContext { Schema = TestSchema.Invoicing() });

        Assert.Empty(t.Issues);
        var lookup = t.Parameters.Single(p => p.Lookup is not null).Lookup!;
        Assert.Equal(("Invoice", "InvoiceId", "Number"), (lookup.Table, lookup.KeyColumn, lookup.DisplayColumn));
    }

    [Fact]
    public void Delete_marks_the_action_destructive()
    {
        Demonstration Delete(int i, string number, string id) => new DemoBuilder("delete draft invoice", i)
            .Select("grdInvoices", "Invoices", number).Click("btnDelete", "Delete")
            .Sql($"DELETE FROM InvoiceLine WHERE InvoiceId = {id}")
            .Sql($"DELETE FROM Invoice WHERE InvoiceId = {id} AND Status = 'Draft'")
            .Build();

        var t = Infer(Delete(1, "INV-1001", "10"), Delete(2, "INV-1002", "11"));

        Assert.True(t.Destructive);
        Assert.Empty(t.Issues);
        Assert.Single(t.Parameters);
    }

    [Fact]
    public void Text_presence_condition_when_no_toggle_separates()
    {
        Demonstration Demo(int i, string name, string note)
        {
            var b = new DemoBuilder("note", i).Text("txtName", "Name:", name).Text("txtNote", "Note:", note).Click("btnSave", "Save")
                .Sql($"INSERT INTO Customer (Name, City, CreditLimit) VALUES ('{name}', 'X', 0)");
            if (note.Length > 0)
            {
                b.Sql($"UPDATE Customer SET City = '{note}' WHERE Name = '{name}'");
            }

            return b.Build();
        }

        var t = Infer(Demo(1, "A", "hello"), Demo(2, "B", ""), Demo(3, "C", "x"));

        var c = Assert.Single(t.Steps.OfType<ConditionalStep>()).Condition;
        Assert.Equal(ConditionOp.IsNotEmpty, c.Op);
        Assert.Equal("note", c.Parameter);
    }

    [Fact]
    public void Unexplained_varying_values_are_reported()
    {
        Demonstration Demo(int i, string v) => new DemoBuilder("odd", i).Text("txtName", "Name:", "same").Click("btnSave", "Save")
            .Sql($"UPDATE Customer SET City = '{v}' WHERE CustomerId = 1").Build();

        var t = Infer(Demo(1, "q1"), Demo(2, "q2"));

        Assert.Contains(t.Issues, i => i.Contains("varies across demonstrations", StringComparison.Ordinal));
    }

    [Fact]
    public void No_data_effect_is_reported_as_out_of_scope()
    {
        var t = Infer(new DemoBuilder("print", 1).Click("btnPrint", "Print").Build(), new DemoBuilder("print", 2).Click("btnPrint", "Print").Build());

        Assert.Contains(t.Issues, i => i.Contains("out of scope", StringComparison.Ordinal));
    }

    [Fact]
    public void Unused_selects_are_pruned()
    {
        Demonstration Demo(int i, string name) => new DemoBuilder("c", i).Text("txtName", "Name:", name).Click("btnSave", "Save")
            .Sql("SELECT COUNT(*) FROM Customer", DemoBuilder.Result(["n"], ["3"]))
            .Sql($"INSERT INTO Customer (Name, City, CreditLimit) VALUES ('{name}', NULL, 0)")
            .Sql("SELECT CustomerId, Name FROM Customer ORDER BY Name", DemoBuilder.Result(["CustomerId", "Name"], ["1", "A"]))
            .Build();

        var t = Infer(Demo(1, "A"), Demo(2, "B"));

        Assert.Equal("INSERT", Assert.IsType<StatementStep>(Assert.Single(t.Steps)).Kind);
        Assert.Contains(t.Evidence, e => e.Rule == "prune");
    }

    [Fact]
    public void Counter_written_back_incremented_is_a_dependent_increment()
    {
        Demonstration Demo(int i, int next) => new DemoBuilder("n", i).Text("txtName", "Name:", "N" + i).Click("btnSave", "Save")
            .Sql("SELECT NextValue FROM Counter WHERE Name = 'Invoice'", DemoBuilder.Result(["NextValue"], [next.ToString(System.Globalization.CultureInfo.InvariantCulture)]))
            .Sql($"UPDATE Counter SET NextValue = {next + 1} WHERE Name = 'Invoice'")
            .Build();

        var t = Infer(Demo(1, 7), Demo(2, 12));

        var update = t.AllStatements().Single(s => s.Kind == "UPDATE");
        Assert.Equal(EncodingKind.Increment, update.Bindings[0].Encoding.Kind);
    }
}
