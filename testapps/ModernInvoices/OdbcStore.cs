using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Odbc;
using System.Globalization;
using System.Linq;

namespace Hokusai.TestApps;

/// <summary>ODBC with bound parameters (positional ? markers), the way C++ and Delphi apps use ODBC.</summary>
public sealed class OdbcStore : IInvoiceStore
{
    private readonly OdbcConnection _connection;

    public OdbcStore(string connectionString)
    {
        _connection = new OdbcConnection(connectionString);
        _connection.Open();
    }

    private OdbcCommand Command(string sql, OdbcTransaction? tx, params object?[] values)
    {
        var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        foreach (var v in values)
        {
            cmd.Parameters.Add(new OdbcParameter { Value = v ?? DBNull.Value });
        }

        return cmd;
    }

    private DataTable Query(string sql, OdbcTransaction? tx, params object?[] values)
    {
        using var cmd = Command(sql, tx, values);
        using var reader = cmd.ExecuteReader();
        var table = new DataTable();
        table.Load(reader);
        return table;
    }

    private void Execute(string sql, OdbcTransaction? tx, params object?[] values)
    {
        using var cmd = Command(sql, tx, values);
        cmd.ExecuteNonQuery();
    }

    public List<KeyValuePair<int, string>> CustomerNames() =>
        Query("SELECT CustomerId, Name FROM Customer ORDER BY Name", null).Rows.Cast<DataRow>()
            .Select(r => new KeyValuePair<int, string>(Convert.ToInt32(r[0], CultureInfo.InvariantCulture), Convert.ToString(r[1], CultureInfo.InvariantCulture)!)).ToList();

    public void CreateCustomer(string name, string city, decimal creditLimit) =>
        Execute("INSERT INTO Customer (Name, City, CreditLimit) VALUES (?, ?, ?)", null, name, city.Length == 0 ? null : city, creditLimit.ToString(CultureInfo.InvariantCulture));

    public DataTable SearchCustomers(string text) =>
        Query("SELECT CustomerId, Name, City FROM Customer WHERE Name LIKE ? ORDER BY Name", null, "%" + text + "%");

    public void CreateInvoice(int customerId, DateTime date, bool taxable, IList<InvoiceLineInput> lines)
    {
        using var tx = _connection.BeginTransaction();
        var next = Convert.ToInt32(Query("SELECT NextValue FROM Counter WHERE Name = 'Invoice'", tx).Rows[0][0], CultureInfo.InvariantCulture);
        Execute("UPDATE Counter SET NextValue = ? WHERE Name = 'Invoice'", tx, next + 1);
        Execute("INSERT INTO Invoice (InvoiceNo, CustomerId, InvoiceDate, Taxable, Status) VALUES (?, ?, ?, ?, 'Draft')", tx,
            next, customerId, date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), taxable ? 1 : 0);
        var id = Convert.ToInt32(Query("SELECT last_insert_rowid() AS NewId", tx).Rows[0][0], CultureInfo.InvariantCulture);
        foreach (var line in lines)
        {
            Execute("INSERT INTO InvoiceLine (InvoiceId, Description, Qty, Price) VALUES (?, ?, ?, ?)", tx, id, line.Description, line.Qty, line.Price.ToString(CultureInfo.InvariantCulture));
        }

        if (taxable)
        {
            Execute("INSERT INTO InvoiceTax (InvoiceId, Rate) VALUES (?, 20)", tx, id);
        }

        Execute("UPDATE Invoice SET Total = (SELECT IFNULL(SUM(Qty * Price), 0) FROM InvoiceLine WHERE InvoiceId = ?) WHERE InvoiceId = ?", tx, id, id);
        tx.Commit();
    }

    public DataTable Invoices() =>
        Query("SELECT i.InvoiceId, i.InvoiceNo, c.Name AS Customer, i.Status, i.Total FROM Invoice i JOIN Customer c ON c.CustomerId = i.CustomerId ORDER BY i.InvoiceNo", null);

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
