using Hokusai.Runtime;

namespace Hokusai.Tests;

internal static class TestSchema
{
    public static SchemaInfo Invoicing()
    {
        static ColumnSchema C(string name, string type, int? max = null, bool nullable = false, bool identity = false, string? def = null) =>
            new() { Name = name, DataType = type, MaxLength = max, Nullable = nullable, IsIdentity = identity, HasDefault = def is not null, DefaultExpression = def };

        return new SchemaInfo
        {
            Tables =
            [
                new TableSchema
                {
                    Name = "Customer",
                    PrimaryKey = ["CustomerId"],
                    Columns =
                    [
                        C("CustomerId", "int", identity: true), C("Name", "nvarchar", 100), C("City", "nvarchar", 50, nullable: true),
                        C("CreditLimit", "decimal"), C("CreatedAt", "datetime2", def: "(getdate())"),
                    ],
                },
                new TableSchema
                {
                    Name = "Invoice",
                    PrimaryKey = ["InvoiceId"],
                    Columns =
                    [
                        C("InvoiceId", "int", identity: true), C("Number", "nvarchar", 20), C("CustomerId", "int"), C("Status", "nvarchar", 20),
                        C("Taxable", "bit"), C("InvoiceDate", "date"), C("Total", "decimal"),
                    ],
                    ForeignKeys = [new ForeignKeySchema { Column = "CustomerId", ReferencedTable = "Customer", ReferencedColumn = "CustomerId" }],
                },
                new TableSchema
                {
                    Name = "InvoiceLine",
                    PrimaryKey = ["LineId"],
                    Columns = [C("LineId", "int", identity: true), C("InvoiceId", "int"), C("Description", "nvarchar", 200), C("Qty", "int"), C("Price", "decimal")],
                    ForeignKeys = [new ForeignKeySchema { Column = "InvoiceId", ReferencedTable = "Invoice", ReferencedColumn = "InvoiceId" }],
                },
                new TableSchema
                {
                    Name = "InvoiceTax",
                    PrimaryKey = ["InvoiceId"],
                    Columns = [C("InvoiceId", "int"), C("Rate", "decimal")],
                    ForeignKeys = [new ForeignKeySchema { Column = "InvoiceId", ReferencedTable = "Invoice", ReferencedColumn = "InvoiceId" }],
                },
                new TableSchema { Name = "Counter", PrimaryKey = ["Name"], Columns = [C("Name", "nvarchar", 30), C("NextValue", "int")] },
            ],
        };
    }

    public static DatabaseSnapshot Customers() => new()
    {
        Tables =
        [
            new TableSnapshot
            {
                Table = "Customer",
                Columns = ["CustomerId", "Name", "City", "CreditLimit", "CreatedAt"],
                KeyColumns = ["CustomerId"],
                Rows =
                [
                    Row(("CustomerId", "1"), ("Name", "Acme Ltd"), ("City", "Graz"), ("CreditLimit", "1000"), ("CreatedAt", "2026-01-01 00:00:00")),
                    Row(("CustomerId", "2"), ("Name", "Globex"), ("City", "Linz"), ("CreditLimit", "500"), ("CreatedAt", "2026-01-02 00:00:00")),
                    Row(("CustomerId", "3"), ("Name", "Initech"), ("City", "Wels"), ("CreditLimit", "250"), ("CreatedAt", "2026-01-03 00:00:00")),
                ],
            },
            new TableSnapshot
            {
                Table = "Invoice",
                Columns = ["InvoiceId", "Number", "CustomerId", "Status", "Taxable", "InvoiceDate", "Total"],
                KeyColumns = ["InvoiceId"],
                Rows =
                [
                    Row(("InvoiceId", "10"), ("Number", "INV-1001"), ("CustomerId", "1"), ("Status", "Draft"), ("Taxable", "1"), ("InvoiceDate", "2026-11-01"), ("Total", "120")),
                    Row(("InvoiceId", "11"), ("Number", "INV-1002"), ("CustomerId", "2"), ("Status", "Draft"), ("Taxable", "0"), ("InvoiceDate", "2026-11-02"), ("Total", "80.5")),
                ],
            },
        ],
    };

    public static Dictionary<string, string?> Row(params (string Key, string? Value)[] values) =>
        values.ToDictionary(v => v.Key, v => v.Value, StringComparer.OrdinalIgnoreCase);
}
