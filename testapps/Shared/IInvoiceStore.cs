using System;
using System.Collections.Generic;
using System.Data;

namespace Hokusai.TestApps;

/// <summary>One line typed into the "New invoice" form.</summary>
public sealed class InvoiceLineInput
{
    public InvoiceLineInput(string description, int qty, decimal price)
    {
        Description = description;
        Qty = qty;
        Price = price;
    }

    public string Description { get; }
    public int Qty { get; }
    public decimal Price { get; }
}

/// <summary>Data access of a test app. Each app implements it with a different legacy-style technology.</summary>
public interface IInvoiceStore : IDisposable
{
    List<KeyValuePair<int, string>> CustomerNames();

    void CreateCustomer(string name, string city, decimal creditLimit);

    DataTable SearchCustomers(string text);

    void CreateInvoice(int customerId, DateTime date, bool taxable, IList<InvoiceLineInput> lines);

    /// <summary>Invoice id, number, customer, status and total, ordered by number.</summary>
    DataTable Invoices();

    void ChangeStatus(int invoiceId, string status);

    void DeleteDraft(int invoiceId);

    DataTable CustomersForExport();
}
