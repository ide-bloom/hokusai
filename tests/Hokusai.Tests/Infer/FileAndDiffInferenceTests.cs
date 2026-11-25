using Hokusai.Core;
using Hokusai.Infer;
using Hokusai.Runtime;
using static Hokusai.Tests.TestSchema;

namespace Hokusai.Tests.Infer;

public sealed class FileAndDiffInferenceTests
{
    private static readonly ResultData Customers = DemoBuilder.Result(
        ["CustomerId", "Name", "CreditLimit", "CreatedAt"],
        ["1", "Acme Ltd", "1000", "2026-01-01 00:00:00"],
        ["2", "Globex; Inc", "500.5", "2026-01-02 00:00:00"]);

    [Fact]
    public void Csv_is_matched_to_a_result_with_formats_quotes_and_header()
    {
        const string csv = "Id;Name;Limit;Since\r\n1;Acme Ltd;1000.00;01.01.2026\r\n2;\"Globex; Inc\";500.50;02.01.2026\r\n";

        var spec = FileOutputInference.Match(csv, Customers);

        Assert.NotNull(spec);
        Assert.Equal((";", true, "\r\n", true), (spec.Delimiter, spec.Header, spec.NewLine, spec.TrailingNewLine));
        Assert.Equal(["CustomerId", "Name", "CreditLimit", "CreatedAt"], spec.Columns.Select(c => c.SourceColumn));
        Assert.Equal([null, null, "n:0.00", "d:dd.MM.yyyy"], spec.Columns.Select(c => c.Format));

        var rendered = CsvOutput.Write(spec, new HkResult { Columns = { "CustomerId", "Name", "CreditLimit", "CreatedAt" }, Rows = { new object?[] { 1, "Acme Ltd", 1000m, new DateTime(2026, 1, 1) }, new object?[] { 2, "Globex; Inc", 500.5m, new DateTime(2026, 1, 2) } } });
        Assert.Equal(csv, rendered);
    }

    [Fact]
    public void Csv_without_header_and_quote_all_is_detected()
    {
        var spec = FileOutputInference.Match("\"1\",\"Acme Ltd\"\n\"2\",\"Globex; Inc\"\n", DemoBuilder.Result(["Id", "Name"], ["1", "Acme Ltd"], ["2", "Globex; Inc"]));

        Assert.NotNull(spec);
        Assert.False(spec.Header);
        Assert.True(spec.QuoteAll);
        Assert.Equal("\n", spec.NewLine);
    }

    [Fact]
    public void Csv_that_does_not_match_returns_null() =>
        Assert.Null(FileOutputInference.Match("Id,Name\n1,Somebody else\n2,Globex; Inc\n", Customers));

    [Fact]
    public void Export_query_is_synthesized_from_a_table_snapshot()
    {
        const string csv = "Name,Limit\r\nInitech,250\r\nGlobex,500\r\nAcme Ltd,1000\r\n";
        var before = new DatabaseSnapshot { Tables = [Customers2()] };

        var query = FileOutputInference.SynthesizeExportQuery(csv, before, SqlDialect.Access, DateTimeOffset.Now);

        Assert.NotNull(query);
        Assert.Equal("SELECT [Name], [CreditLimit] FROM [Customer] ORDER BY [CustomerId] DESC", query.Text);
        Assert.Equal(3, query.Result!.Rows.Count);

        static TableSnapshot Customers2() => new()
        {
            Table = "Customer",
            KeyColumns = ["CustomerId"],
            Columns = ["CustomerId", "Name", "CreditLimit"],
            Rows =
            [
                Row(("CustomerId", "1"), ("Name", "Acme Ltd"), ("CreditLimit", "1000")),
                Row(("CustomerId", "2"), ("Name", "Globex"), ("CreditLimit", "500")),
                Row(("CustomerId", "3"), ("Name", "Initech"), ("CreditLimit", "250")),
            ],
        };
    }

    [Fact]
    public void Diff_synthesis_orders_parents_first_and_reads_identities()
    {
        var diff = new DatabaseDiff
        {
            Tables =
            [
                new TableDiff
                {
                    Table = "InvoiceLine",
                    KeyColumns = ["LineId"],
                    Changes =
                    [
                        new RowChange { Kind = ChangeKind.Insert, Key = Row(("LineId", "8")), After = Row(("LineId", "8"), ("InvoiceId", "12"), ("Description", "B"), ("Qty", "1"), ("Price", "2")) },
                        new RowChange { Kind = ChangeKind.Insert, Key = Row(("LineId", "7")), After = Row(("LineId", "7"), ("InvoiceId", "12"), ("Description", "A"), ("Qty", "2"), ("Price", "3")) },
                    ],
                },
                new TableDiff
                {
                    Table = "Invoice",
                    KeyColumns = ["InvoiceId"],
                    Changes =
                    [
                        new RowChange { Kind = ChangeKind.Insert, Key = Row(("InvoiceId", "12")), After = Row(("InvoiceId", "12"), ("Number", "N"), ("CustomerId", "1"), ("Status", "Draft"), ("Taxable", "0"), ("InvoiceDate", "2026-11-24"), ("Total", "0")) },
                        new RowChange { Kind = ChangeKind.Update, Key = Row(("InvoiceId", "10")), Before = Row(("InvoiceId", "10"), ("Status", "Draft")), After = Row(("InvoiceId", "10"), ("Status", "Sent")) },
                    ],
                },
            ],
        };
        var demo = new DemoBuilder("x", 1, ProviderKind.Access).Click("btnSave", "Save").Diff(diff).Build();

        var events = DiffSynthesizer.Synthesize(demo, Invoicing(), SqlDialect.Access);

        Assert.Equal(
            [
                "UPDATE [Invoice] SET [Status] = ? WHERE [InvoiceId] = ?",
                "INSERT INTO [Invoice] ([Number], [CustomerId], [Status], [Taxable], [InvoiceDate], [Total]) VALUES (?, ?, ?, ?, ?, ?)",
                "SELECT @@IDENTITY AS [InvoiceId]",
                "INSERT INTO [InvoiceLine] ([InvoiceId], [Description], [Qty], [Price]) VALUES (?, ?, ?, ?)",
                "SELECT @@IDENTITY AS [LineId]",
                "INSERT INTO [InvoiceLine] ([InvoiceId], [Description], [Qty], [Price]) VALUES (?, ?, ?, ?)",
                "SELECT @@IDENTITY AS [LineId]",
            ],
            events.Select(e => e.Text));
        Assert.Equal("A", events[3].Parameters[1].Value);
        Assert.Equal("12", events[2].Result!.Rows[0][0]);
    }

    [Fact]
    public void Diff_synthesis_deletes_children_before_parents()
    {
        var diff = new DatabaseDiff
        {
            Tables =
            [
                new TableDiff { Table = "Invoice", KeyColumns = ["InvoiceId"], Changes = [new RowChange { Kind = ChangeKind.Delete, Key = Row(("InvoiceId", "10")), Before = Row(("InvoiceId", "10")) }] },
                new TableDiff { Table = "InvoiceLine", KeyColumns = ["LineId"], Changes = [new RowChange { Kind = ChangeKind.Delete, Key = Row(("LineId", "1")), Before = Row(("LineId", "1")) }] },
            ],
        };

        var events = DiffSynthesizer.Synthesize(new DemoBuilder("x", 1).Diff(diff).Build(), Invoicing(), SqlDialect.Sqlite);

        Assert.Equal(["DELETE FROM \"InvoiceLine\" WHERE \"LineId\" = ?", "DELETE FROM \"Invoice\" WHERE \"InvoiceId\" = ?"], events.Select(e => e.Text));
    }

    [Fact]
    public void Diff_synthesis_deletes_children_of_a_deleted_parent_through_the_foreign_key()
    {
        var diff = new DatabaseDiff
        {
            Tables =
            [
                new TableDiff { Table = "Invoice", KeyColumns = ["InvoiceId"], Changes = [new RowChange { Kind = ChangeKind.Delete, Key = Row(("InvoiceId", "10")), Before = Row(("InvoiceId", "10"), ("Status", "Draft")) }] },
                new TableDiff
                {
                    Table = "InvoiceLine",
                    KeyColumns = ["LineId"],
                    Changes =
                    [
                        new RowChange { Kind = ChangeKind.Delete, Key = Row(("LineId", "1")), Before = Row(("LineId", "1"), ("InvoiceId", "10")) },
                        new RowChange { Kind = ChangeKind.Delete, Key = Row(("LineId", "2")), Before = Row(("LineId", "2"), ("InvoiceId", "10")) },
                    ],
                },
            ],
        };

        var events = DiffSynthesizer.Synthesize(new DemoBuilder("x", 1).Diff(diff).Build(), Invoicing(), SqlDialect.Access);

        Assert.Equal(["DELETE FROM [InvoiceLine] WHERE [InvoiceId] = ?", "DELETE FROM [Invoice] WHERE [InvoiceId] = ?"], events.Select(e => e.Text));
        Assert.Equal("10", events[0].Parameters.Single().Value);
    }

    [Fact]
    public void Numeric_updates_of_existing_rows_get_the_read_that_explains_them()
    {
        Demonstration Demo(int i, int next) => new DemoBuilder("number", i, ProviderKind.Access).Text("txtName", "Name:", "N" + i).Click("btnSave", "Save").Diff(new DatabaseDiff
        {
            Tables =
            [
                new TableDiff
                {
                    Table = "Counter",
                    KeyColumns = ["Name"],
                    Changes = [new RowChange { Kind = ChangeKind.Update, Key = Row(("Name", "Invoice")), Before = Row(("Name", "Invoice"), ("NextValue", next.ToString(System.Globalization.CultureInfo.InvariantCulture))), After = Row(("Name", "Invoice"), ("NextValue", (next + 1).ToString(System.Globalization.CultureInfo.InvariantCulture))) }],
                },
            ],
        }).Build();

        var t = TemplateInferrer.Infer("number", [Demo(1, 1005), Demo(2, 1006)], new InferenceContext { Schema = Invoicing(), Dialect = SqlDialect.Access, Provider = ProviderKind.Access });

        Assert.Empty(t.Issues);
        var update = t.AllStatements().Single(s => s.Kind == "UPDATE");
        Assert.Equal(EncodingKind.Increment, update.Bindings[0].Encoding.Kind);
        Assert.Equal("SELECT [NextValue] FROM [Counter] WHERE [Name] = @hk0", t.AllStatements().Single(s => s.Kind == "SELECT").Sql);
    }

    [Fact]
    public void Export_action_gets_a_file_output_bound_to_its_query()
    {
        Demonstration Demo(int i, string path, string csv) => new DemoBuilder("export customers", i)
            .Text("txtPath", "File:", path).Click("btnExport", "Export")
            .Sql("SELECT CustomerId, Name, CreditLimit, CreatedAt FROM Customer ORDER BY CustomerId", Customers)
            .File(path, "csv", null, csv)
            .Build();
        const string csv = "CustomerId,Name,CreditLimit\r\n1,Acme Ltd,1000.00\r\n2,\"Globex; Inc\",500.50\r\n";

        var t = TemplateInferrer.Infer("export customers", [Demo(1, @"C:\out\a.csv", csv), Demo(2, @"C:\out\b.csv", csv)], new InferenceContext { Schema = Invoicing() });

        Assert.Empty(t.Issues);
        Assert.NotNull(t.FileOutput);
        Assert.Equal(t.ResultStep, t.FileOutput.SourceStep);
        Assert.Equal([@"C:\out\a.csv", @"C:\out\b.csv"], t.FileOutput.ObservedPaths);
    }
}
