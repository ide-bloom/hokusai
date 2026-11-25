using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;

namespace Hokusai.TestApps;

/// <summary>
/// Classic ADO (ADODB.Connection / ADODB.Recordset) through late-bound COM, with SQL assembled by string
/// concatenation, the way VB6 applications talked to SQL Server.
/// </summary>
public sealed class AdoStore : IInvoiceStore
{
    private const int AdUseClient = 3;
    private const int AdOpenStatic = 3;
    private const int AdLockReadOnly = 1;

    private readonly dynamic _connection;

    public AdoStore(string connectionString)
    {
        _connection = Activator.CreateInstance(Type.GetTypeFromProgID("ADODB.Connection", throwOnError: true));
        _connection.CursorLocation = AdUseClient;
        _connection.Open(connectionString);
    }

    private static string Q(string value) => "N'" + value.Replace("'", "''") + "'";

    private static string N(decimal value) => value.ToString(CultureInfo.InvariantCulture);

    private void Execute(string sql) => _connection.Execute(sql);

    private DataTable Query(string sql)
    {
        dynamic rs = Activator.CreateInstance(Type.GetTypeFromProgID("ADODB.Recordset", throwOnError: true));
        rs.CursorLocation = AdUseClient;
        rs.Open(sql, _connection, AdOpenStatic, AdLockReadOnly);
        var table = new DataTable();
        int count = rs.Fields.Count;
        for (var i = 0; i < count; i++)
        {
            table.Columns.Add((string)rs.Fields[i].Name, typeof(object));
        }

        while (!(bool)rs.EOF)
        {
            var row = table.NewRow();
            for (var i = 0; i < count; i++)
            {
                object value = rs.Fields[i].Value;
                row[i] = value ?? DBNull.Value;
            }

            table.Rows.Add(row);
            rs.MoveNext();
        }

        rs.Close();
        return table;
    }

    public List<KeyValuePair<int, string>> CustomerNames() =>
        Query("SELECT CustomerId, Name FROM Customer ORDER BY Name").Rows.Cast<DataRow>()
            .Select(r => new KeyValuePair<int, string>(Convert.ToInt32(r[0], CultureInfo.InvariantCulture), (string)r[1])).ToList();

    public void CreateCustomer(string name, string city, decimal creditLimit) =>
        Execute("INSERT INTO Customer (Name, City, CreditLimit) VALUES (" + Q(name) + ", " + (city.Length == 0 ? "NULL" : Q(city)) + ", " + N(creditLimit) + ")");

    public DataTable SearchCustomers(string text) =>
        Query("SELECT CustomerId, Name, City FROM Customer WHERE Name LIKE N'%" + text.Replace("'", "''") + "%' ORDER BY Name");

    public void CreateInvoice(int customerId, DateTime date, bool taxable, IList<InvoiceLineInput> lines)
    {
        _connection.BeginTrans();
        try
        {
            var next = Convert.ToInt32(Query("SELECT NextValue FROM Counter WHERE Name = N'Invoice'").Rows[0][0], CultureInfo.InvariantCulture);
            Execute("UPDATE Counter SET NextValue = " + (next + 1) + " WHERE Name = N'Invoice'");
            var id = Convert.ToInt32(Query(
                "SET NOCOUNT ON; INSERT INTO Invoice (InvoiceNo, CustomerId, InvoiceDate, Taxable, Status) VALUES (" + next + ", " + customerId + ", '" +
                date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "', " + (taxable ? "1" : "0") + ", N'Draft'); SELECT CAST(SCOPE_IDENTITY() AS int) AS NewId").Rows[0][0], CultureInfo.InvariantCulture);
            foreach (var line in lines)
            {
                Execute("INSERT INTO InvoiceLine (InvoiceId, Description, Qty, Price) VALUES (" + id + ", " + Q(line.Description) + ", " + line.Qty + ", " + N(line.Price) + ")");
            }

            if (taxable)
            {
                Execute("INSERT INTO InvoiceTax (InvoiceId, Rate) VALUES (" + id + ", 20)");
            }

            Execute("UPDATE Invoice SET Total = (SELECT ISNULL(SUM(Qty * Price), 0) FROM InvoiceLine WHERE InvoiceId = " + id + ") WHERE InvoiceId = " + id);
            _connection.CommitTrans();
        }
        catch
        {
            _connection.RollbackTrans();
            throw;
        }
    }

    public DataTable Invoices() =>
        Query("SELECT i.InvoiceId, i.InvoiceNo, c.Name AS Customer, i.Status, i.Total FROM Invoice i JOIN Customer c ON c.CustomerId = i.CustomerId ORDER BY i.InvoiceNo");

    public void ChangeStatus(int invoiceId, string status) =>
        Execute("UPDATE Invoice SET Status = " + Q(status) + " WHERE InvoiceId = " + invoiceId);

    public void DeleteDraft(int invoiceId)
    {
        _connection.BeginTrans();
        Execute("DELETE FROM InvoiceTax WHERE InvoiceId = " + invoiceId);
        Execute("DELETE FROM InvoiceLine WHERE InvoiceId = " + invoiceId);
        Execute("DELETE FROM Invoice WHERE InvoiceId = " + invoiceId + " AND Status = N'Draft'");
        _connection.CommitTrans();
    }

    public DataTable CustomersForExport() => Query("SELECT CustomerId, Name, City, CreditLimit FROM Customer ORDER BY CustomerId");

    public void Dispose() => _connection.Close();
}
