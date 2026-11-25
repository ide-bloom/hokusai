using System;
using System.Collections.Generic;
using System.Data;
using System.Data.OleDb;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Hokusai.TestApps;

/// <summary>An Access database through the ACE OLE DB provider with positional parameters.</summary>
public sealed class AccessStore : IInvoiceStore
{
    private static readonly string[] BatchSeparator = ["\nGO"];
    private readonly OleDbConnection _connection;

    public AccessStore(string path)
    {
        _connection = new OleDbConnection(ConnectionString(path));
        _connection.Open();
    }

    public static string Provider { get; } = Type.GetTypeFromProgID("Microsoft.ACE.OLEDB.16.0") is not null ? "Microsoft.ACE.OLEDB.16.0" : "Microsoft.ACE.OLEDB.12.0";

    public static string ConnectionString(string path) => $"Provider={Provider};Data Source={path}";

    /// <summary>Creates a new database from the seed script (one statement per GO batch).</summary>
    public static void Create(string path, string seedScript)
    {
        dynamic catalog = Activator.CreateInstance(Type.GetTypeFromProgID("ADOX.Catalog", throwOnError: true)!)!;
        catalog.Create(ConnectionString(path));
        catalog.ActiveConnection.Close();
        using var connection = new OleDbConnection(ConnectionString(path));
        connection.Open();
        var script = string.Join("\n", File.ReadAllLines(seedScript).Where(l => !l.TrimStart().StartsWith("--", StringComparison.Ordinal)));
        foreach (var batch in script.Split(BatchSeparator, StringSplitOptions.RemoveEmptyEntries).Select(b => b.Trim()).Where(b => b.Length > 0))
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = batch;
            cmd.ExecuteNonQuery();
        }
    }

    private OleDbCommand Command(string sql, OleDbTransaction? tx, params object?[] values)
    {
        var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        foreach (var v in values)
        {
            cmd.Parameters.Add(new OleDbParameter { Value = v ?? DBNull.Value });
        }

        return cmd;
    }

    private DataTable Query(string sql, OleDbTransaction? tx, params object?[] values)
    {
        using var cmd = Command(sql, tx, values);
        using var reader = cmd.ExecuteReader();
        var table = new DataTable();
        table.Load(reader!);
        return table;
    }

    private void Execute(string sql, OleDbTransaction? tx, params object?[] values)
    {
        using var cmd = Command(sql, tx, values);
        cmd.ExecuteNonQuery();
    }

    public List<KeyValuePair<int, string>> CustomerNames() =>
        Query("SELECT CustomerId, Name FROM Customer ORDER BY Name", null).Rows.Cast<DataRow>()
            .Select(r => new KeyValuePair<int, string>(Convert.ToInt32(r[0], CultureInfo.InvariantCulture), Convert.ToString(r[1], CultureInfo.InvariantCulture)!)).ToList();

    public void CreateCustomer(string name, string city, decimal creditLimit) =>
        Execute("INSERT INTO Customer (Name, City, CreditLimit) VALUES (?, ?, ?)", null, name, city.Length == 0 ? null : city, creditLimit);

    public DataTable SearchCustomers(string text) =>
        Query("SELECT CustomerId, Name, City FROM Customer WHERE Name LIKE ? ORDER BY Name", null, "%" + text + "%");

    public void CreateInvoice(int customerId, DateTime date, bool taxable, IList<InvoiceLineInput> lines)
    {
        using var tx = _connection.BeginTransaction();
        var next = Convert.ToInt32(Query("SELECT NextValue FROM [Counter] WHERE Name = 'Invoice'", tx).Rows[0][0], CultureInfo.InvariantCulture);
        Execute("UPDATE [Counter] SET NextValue = ? WHERE Name = 'Invoice'", tx, next + 1);
        Execute("INSERT INTO Invoice (InvoiceNo, CustomerId, InvoiceDate, Taxable, Status) VALUES (?, ?, ?, ?, 'Draft')", tx, next, customerId, date.Date, taxable);
        var id = Convert.ToInt32(Query("SELECT @@IDENTITY", tx).Rows[0][0], CultureInfo.InvariantCulture);
        foreach (var line in lines)
        {
            Execute("INSERT INTO InvoiceLine (InvoiceId, Description, Qty, Price) VALUES (?, ?, ?, ?)", tx, id, line.Description, line.Qty, line.Price);
        }

        if (taxable)
        {
            Execute("INSERT INTO InvoiceTax (InvoiceId, Rate) VALUES (?, 20)", tx, id);
        }

        tx.Commit();
    }

    public DataTable Invoices() =>
        // Jet SQL cannot keep a stored total current in one UPDATE, so this app computes totals when listing.
        Query("SELECT i.InvoiceId, i.InvoiceNo, c.Name AS Customer, i.Status, (SELECT SUM(l.Qty * l.Price) FROM InvoiceLine AS l WHERE l.InvoiceId = i.InvoiceId) AS Total " +
              "FROM Invoice AS i INNER JOIN Customer AS c ON c.CustomerId = i.CustomerId ORDER BY i.InvoiceNo", null);

    public void ChangeStatus(int invoiceId, string status) => Execute("UPDATE Invoice SET Status = ? WHERE InvoiceId = ?", null, status, invoiceId);

    public void DeleteDraft(int invoiceId)
    {
        using var tx = _connection.BeginTransaction();
        Execute("DELETE FROM InvoiceTax WHERE InvoiceId = ?", tx, invoiceId);
        Execute("DELETE FROM InvoiceLine WHERE InvoiceId = ?", tx, invoiceId);
        Execute("DELETE FROM Invoice WHERE InvoiceId = ? AND Status = 'Draft'", tx, invoiceId);
        tx.Commit();
    }

    public DataTable CustomersForExport() => Query("SELECT CustomerId, Name, City, CreditLimit FROM Customer ORDER BY CustomerId", null);

    public void Dispose() => _connection.Dispose();
}
