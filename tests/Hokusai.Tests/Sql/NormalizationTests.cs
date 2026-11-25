using Hokusai.Runtime;
using Hokusai.Sql;

namespace Hokusai.Tests.Sql;

public sealed class NormalizationTests
{
    private static NormalizedStatement TSql(string sql, params BoundValue[] bound) => SqlNormalizer.For(SqlDialect.SqlServer).Normalize(sql, bound);

    private static NormalizedStatement Sqlite(string sql, params BoundValue[] bound) => SqlNormalizer.For(SqlDialect.Sqlite).Normalize(sql, bound);

    private static NormalizedStatement Access(string sql, params BoundValue[] bound) => SqlNormalizer.For(SqlDialect.Access).Normalize(sql, bound);

    [Fact]
    public void TSql_insert_literals_become_placeholders_mapped_to_columns()
    {
        var n = TSql("INSERT INTO [dbo].[Customer] ([Name], [City], [CreditLimit]) VALUES (N'Acme O''Brien', 'Graz', 1500.50)");

        Assert.Equal("INSERT", n.Kind);
        Assert.Equal("Customer", n.Table);
        Assert.Equal("INSERT INTO DBO . CUSTOMER ( NAME , CITY , CREDITLIMIT ) VALUES ( ? , ? , ? )", n.Shape);
        Assert.Equal("INSERT INTO [dbo].[Customer] ([Name], [City], [CreditLimit]) VALUES (@hk0, @hk1, @hk2)", n.Template);
        Assert.Collection(
            n.Literals,
            l => Assert.Equal(("Acme O'Brien", "Name", LiteralKind.Text), (l.Value, l.Column, l.Kind)),
            l => Assert.Equal(("Graz", "City"), (l.Value, l.Column)),
            l => Assert.Equal(("1500.5", "CreditLimit", LiteralKind.Number), (l.Value, l.Column, l.Kind)));
    }

    [Fact]
    public void Shapes_ignore_whitespace_casing_and_identifier_quoting()
    {
        var a = TSql("update Invoice set Status = 'Paid' where InvoiceId = 4");
        var b = TSql("UPDATE   [Invoice]\r\n   SET [Status]='Draft'   WHERE [InvoiceId]=17 -- comment");

        Assert.Equal(a.Shape, b.Shape);
        Assert.Equal("Status", a.Literals[0].Column);
        Assert.Equal("InvoiceId", a.Literals[1].Column);
    }

    [Fact]
    public void Negative_numbers_fold_into_one_literal_but_subtraction_does_not()
    {
        var n = TSql("UPDATE Stock SET Qty = Qty - 3, Delta = -3 WHERE Id = 1");

        Assert.Equal(["3", "-3", "1"], n.Literals.Select(l => l.Value));
        Assert.Contains("QTY - ?", n.Shape, StringComparison.Ordinal);
        Assert.Contains("DELTA = ?", n.Shape, StringComparison.Ordinal);
    }

    [Fact]
    public void Null_in_value_position_is_a_literal_but_is_null_is_not()
    {
        var n = TSql("INSERT INTO T (A, B) VALUES (NULL, 1); SELECT * FROM T WHERE A IS NULL");

        Assert.Equal(LiteralKind.Null, n.Literals[0].Kind);
        Assert.Null(n.Literals[0].Value);
        Assert.Equal(2, n.Literals.Count);
        Assert.Contains("IS NULL", n.Shape, StringComparison.Ordinal);
    }

    [Fact]
    public void Sp_executesql_is_unwrapped_into_inner_statement_and_bound_values()
    {
        var n = TSql("exec sp_executesql N'INSERT INTO Customer (Name, City) VALUES (@P1, @P2)',N'@P1 nvarchar(50),@P2 nvarchar(50)',@P1=N'Acme',@P2=N'Linz'");

        Assert.Equal("INSERT INTO Customer (Name, City) VALUES (@hk0, @hk1)", n.Template);
        Assert.Equal(["Acme", "Linz"], n.Literals.Select(l => l.Value));
        Assert.All(n.Literals, l => Assert.Equal(LiteralKind.Bound, l.Kind));
        Assert.Equal(["Name", "City"], n.Literals.Select(l => l.Column));
    }

    [Fact]
    public void Sp_executesql_with_positional_arguments_uses_declared_names()
    {
        var n = TSql("exec sp_executesql N'SELECT * FROM Customer WHERE Id = @id',N'@id int',42");

        Assert.Equal("42", n.Literals.Single().Value);
        Assert.Equal("Id", n.Literals.Single().Column);
    }

    [Fact]
    public void Sp_prepexec_is_unwrapped()
    {
        var n = TSql("declare @p1 int set @p1=NULL exec sp_prepexec @p1 output,N'@P1 int',N'DELETE FROM Invoice WHERE InvoiceId = @P1',@P1=9");

        Assert.Equal("DELETE", n.Kind);
        Assert.Equal("9", n.Literals.Single().Value);
    }

    [Fact]
    public void Server_generator_functions_mark_generated_columns()
    {
        var n = TSql("INSERT INTO Invoice (CustomerId, CreatedAt, RowGuid) VALUES (3, GETDATE(), NEWID())");

        Assert.Equal(["Invoice.CreatedAt", "Invoice.RowGuid"], n.ServerGeneratedColumns);
        Assert.Single(n.Literals);
    }

    [Fact]
    public void Batch_kind_comes_from_first_data_statement()
    {
        var n = TSql("SET NOCOUNT ON; INSERT INTO Invoice (CustomerId) VALUES (5); SELECT CAST(SCOPE_IDENTITY() AS int) AS NewId");

        Assert.Equal("INSERT", n.Kind);
        Assert.Equal("Invoice", n.Table);
    }

    [Fact]
    public void Invalid_tsql_falls_back_to_generic_tokenizer_and_reports_errors()
    {
        var n = TSql("INSERT INTO T (A VALUES ('x'");

        Assert.NotEmpty(n.Errors);
        Assert.Equal("x", n.Literals.Single().Value);
    }

    [Fact]
    public void Odbc_markers_take_bound_values_in_order()
    {
        var n = Sqlite("INSERT INTO \"InvoiceLine\" (InvoiceId, Description, Qty, Price) VALUES (?, ?, ?, ?)", new("1", "7"), new("2", "Widget"), new("3", "2"), new("4", "9.5"));

        Assert.Equal("INSERT INTO INVOICELINE ( INVOICEID , DESCRIPTION , QTY , PRICE ) VALUES ( ? , ? , ? , ? )", n.Shape);
        Assert.Equal(["7", "Widget", "2", "9.5"], n.Literals.Select(l => l.Value));
        Assert.Equal(["InvoiceId", "Description", "Qty", "Price"], n.Literals.Select(l => l.Column));
        Assert.Equal("INSERT INTO \"InvoiceLine\" (InvoiceId, Description, Qty, Price) VALUES (@hk0, @hk1, @hk2, @hk3)", n.Template);
    }

    [Fact]
    public void Sqlite_named_markers_resolve_by_name()
    {
        var n = Sqlite("SELECT Name FROM Customer WHERE Name LIKE :pattern AND City = $city", new("city", "Wels"), new("pattern", "Ac%"));

        Assert.Equal(["Ac%", "Wels"], n.Literals.Select(l => l.Value));
        Assert.Equal(["Name", "City"], n.Literals.Select(l => l.Column));
        Assert.Equal("SELECT", n.Kind);
    }

    [Fact]
    public void Sqlite_datetime_now_is_a_generated_column()
    {
        var n = Sqlite("UPDATE Invoice SET Status = 'Paid', PaidAt = datetime('now') WHERE InvoiceId = 3");

        Assert.Equal(["Invoice.PaidAt"], n.ServerGeneratedColumns);
        Assert.Equal(["Paid", "now", "3"], n.Literals.Select(l => l.Value));
    }

    [Fact]
    public void Access_double_quotes_and_hash_dates_are_literals()
    {
        var n = Access("INSERT INTO [Invoice] ([CustomerId], [InvoiceDate], [Note]) VALUES (4, #2026-11-24#, \"rush\")");

        Assert.Equal(["4", "2026-11-24", "rush"], n.Literals.Select(l => l.Value));
        Assert.Equal(["CustomerId", "InvoiceDate", "Note"], n.Literals.Select(l => l.Column));
    }

    [Fact]
    public void System_variables_are_not_parameter_markers()
    {
        var n = Access("SELECT @@IDENTITY AS [InvoiceId]");

        Assert.Empty(n.Literals);
        Assert.Equal("SELECT @@IDENTITY AS [InvoiceId]", n.Template);
        Assert.Equal("SELECT @@IDENTITY AS INVOICEID", n.Shape);
    }

    [Fact]
    public void Generic_comments_and_strings_with_markers_inside_are_not_split()
    {
        var n = Sqlite("SELECT '?' AS q /* ? */ FROM T WHERE A = ? -- ?", new BoundValue("1", "5"));

        Assert.Equal(["?", "5"], n.Literals.Select(l => l.Value));
    }

    [Fact]
    public void Multi_row_values_map_each_row_to_columns()
    {
        var n = Sqlite("INSERT INTO L (A, B) VALUES (1, 'x'), (2, 'y')");

        Assert.Equal(["A", "B", "A", "B"], n.Literals.Select(l => l.Column));
    }

    [Fact]
    public void Render_translates_placeholders_per_provider()
    {
        const string sql = "UPDATE T SET A = @hk0, B = '@hk1' WHERE C = @hk1 OR D = @hk0";

        var (named, namedOrder) = HkDb.Render(sql, ProviderKind.SqlServer);
        var (positional, order) = HkDb.Render(sql, ProviderKind.Odbc);

        Assert.Equal(sql, named);
        Assert.Equal(["hk0", "hk1", "hk0"], namedOrder);
        Assert.Equal("UPDATE T SET A = ?, B = '@hk1' WHERE C = ? OR D = ?", positional);
        Assert.Equal(["hk0", "hk1", "hk0"], order);
    }
}
