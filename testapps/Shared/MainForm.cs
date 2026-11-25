using System;
using System.Collections.Generic;
using System.Data;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace Hokusai.TestApps;

/// <summary>
/// The invoicing UI shared by the three test apps. It is deliberately plain WinForms built in code, with the
/// habits of legacy line-of-business apps: values parsed in the user's culture, labels next to controls,
/// combo boxes mapping display text to keys, and a form that clears itself after saving.
/// </summary>
public sealed class MainForm : Form
{
    private readonly IInvoiceStore _store;
    private readonly TextBox _name = Box("txtName");
    private readonly TextBox _city = Box("txtCity");
    private readonly TextBox _creditLimit = Box("txtCreditLimit");
    private readonly TextBox _search = Box("txtSearch");
    private readonly DataGridView _customers = Grid("grdCustomers");
    private readonly ComboBox _customer = Combo("cboCustomer");
    private readonly TextBox _date = Box("txtDate");
    private readonly CheckBox _taxable = new() { Name = "chkTaxable", Text = "Taxable", AutoSize = true };
    private readonly TextBox _lineDescription = Box("txtLineDescription");
    private readonly TextBox _lineQty = Box("txtLineQty");
    private readonly TextBox _linePrice = Box("txtLinePrice");
    private readonly DataGridView _lines = Grid("grdLines");
    private readonly ComboBox _invoice = Combo("cboInvoice");
    private readonly ComboBox _status = Combo("cboStatus");
    private readonly DataGridView _invoices = Grid("grdInvoices");
    private readonly TextBox _exportPath = Box("txtExportPath");
    private readonly Label _message = new() { Name = "lblMessage", Text = "Ready.", AutoSize = false, Height = 24, Dock = DockStyle.Bottom };
    private readonly List<InvoiceLineInput> _pendingLines = new();
    private List<KeyValuePair<int, string>> _customerItems = new();
    private DataTable _invoiceTable = new();
    private int _actions;

    public MainForm(IInvoiceStore store, string title)
    {
        _store = store;
        Text = title;
        Name = "MainForm";
        ClientSize = new Size(760, 520);
        StartPosition = FormStartPosition.CenterScreen;
        var tabs = new TabControl { Name = "tabs", Dock = DockStyle.Fill };
        tabs.TabPages.Add(CustomersPage());
        tabs.TabPages.Add(InvoicePage());
        tabs.TabPages.Add(InvoicesPage());
        tabs.TabPages.Add(ExportPage());
        Controls.Add(tabs);
        Controls.Add(_message);
        Load += (_, _) => RefreshLists();
    }

    private static TextBox Box(string name) => new() { Name = name, Width = 220 };

    private static ComboBox Combo(string name) => new() { Name = name, Width = 220, DropDownStyle = ComboBoxStyle.DropDownList };

    private static DataGridView Grid(string name) => new()
    {
        Name = name,
        ReadOnly = true,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
        RowHeadersVisible = false,
        Width = 700,
        Height = 180,
    };

    private static void Row(Control parent, string label, Control control, ref int y)
    {
        parent.Controls.Add(new Label { Text = label, Left = 12, Top = y + 3, Width = 120 });
        control.Left = 140;
        control.Top = y;
        parent.Controls.Add(control);
        y += 32;
    }

    private static Button Button(string name, string text, EventHandler click)
    {
        var b = new Button { Name = name, Text = text, Width = 140, Height = 26 };
        b.Click += click;
        return b;
    }

    private void Run(Action action, string done)
    {
        // Status messages are numbered so a repeated action is visibly a new event.
        _actions++;
        try
        {
            action();
            _message.Text = done + " (" + _actions + ")";
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or ArgumentException or System.Runtime.InteropServices.COMException or System.Data.Common.DbException)
        {
            _message.Text = "Error: " + ex.Message + " (" + _actions + ")";
        }
    }

    private TabPage CustomersPage()
    {
        var page = new TabPage("Customers") { Name = "pageCustomers" };
        var y = 12;
        Row(page, "Name:", _name, ref y);
        Row(page, "City:", _city, ref y);
        Row(page, "Credit limit:", _creditLimit, ref y);
        var save = Button("btnSaveCustomer", "Save customer", (_, _) => Run(SaveCustomer, "Customer saved."));
        save.Left = 140;
        save.Top = y;
        page.Controls.Add(save);
        y += 44;
        Row(page, "Search:", _search, ref y);
        var search = Button("btnSearch", "Search", (_, _) => Run(() => _customers.DataSource = _store.SearchCustomers(_search.Text), "Search done."));
        search.Left = 370;
        search.Top = y - 32;
        page.Controls.Add(search);
        _customers.Left = 12;
        _customers.Top = y;
        page.Controls.Add(_customers);
        return page;
    }

    private void SaveCustomer()
    {
        // Like many legacy forms: trimmed name, culture-aware number, form cleared afterwards.
        var limit = decimal.Parse(_creditLimit.Text, NumberStyles.Number, CultureInfo.CurrentCulture);
        _store.CreateCustomer(_name.Text.Trim(), _city.Text.Trim(), limit);
        _name.Text = _city.Text = _creditLimit.Text = "";
        RefreshLists();
    }

    private TabPage InvoicePage()
    {
        var page = new TabPage("New invoice") { Name = "pageInvoice" };
        var y = 12;
        Row(page, "Customer:", _customer, ref y);
        Row(page, "Date:", _date, ref y);
        _taxable.Left = 140;
        _taxable.Top = y;
        page.Controls.Add(_taxable);
        y += 32;
        Row(page, "Item:", _lineDescription, ref y);
        Row(page, "Quantity:", _lineQty, ref y);
        Row(page, "Unit price:", _linePrice, ref y);
        var add = Button("btnAddLine", "Add line", (_, _) => Run(AddLine, "Line added."));
        add.Left = 370;
        add.Top = y - 32;
        page.Controls.Add(add);
        _lines.Left = 12;
        _lines.Top = y;
        _lines.Height = 120;
        page.Controls.Add(_lines);
        var save = Button("btnSaveInvoice", "Save invoice", (_, _) => Run(SaveInvoice, "Invoice saved."));
        save.Left = 12;
        save.Top = y + 128;
        page.Controls.Add(save);
        _date.Text = DateTime.Today.ToString("d", CultureInfo.CurrentCulture);
        ShowLines();
        return page;
    }

    private void AddLine()
    {
        _pendingLines.Add(new InvoiceLineInput(
            _lineDescription.Text.Trim(),
            int.Parse(_lineQty.Text, NumberStyles.Integer, CultureInfo.CurrentCulture),
            decimal.Parse(_linePrice.Text, NumberStyles.Number, CultureInfo.CurrentCulture)));
        _lineDescription.Text = _lineQty.Text = _linePrice.Text = "";
        ShowLines();
    }

    private void ShowLines()
    {
        var table = new DataTable();
        table.Columns.Add("Item");
        table.Columns.Add("Quantity");
        table.Columns.Add("Unit price");
        foreach (var l in _pendingLines)
        {
            table.Rows.Add(l.Description, l.Qty.ToString(CultureInfo.CurrentCulture), l.Price.ToString(CultureInfo.CurrentCulture));
        }

        _lines.DataSource = table;
    }

    private void SaveInvoice()
    {
        if (_customer.SelectedIndex < 0)
        {
            throw new InvalidOperationException("Choose a customer.");
        }

        var customerId = _customerItems[_customer.SelectedIndex].Key;
        var date = DateTime.Parse(_date.Text, CultureInfo.CurrentCulture);
        _store.CreateInvoice(customerId, date, _taxable.Checked, _pendingLines);
        _pendingLines.Clear();
        _taxable.Checked = false;
        ShowLines();
        RefreshLists();
    }

    private TabPage InvoicesPage()
    {
        var page = new TabPage("Invoices") { Name = "pageInvoices" };
        var y = 12;
        Row(page, "Invoice:", _invoice, ref y);
        Row(page, "Status:", _status, ref y);
        _status.Items.AddRange(new object[] { "Draft", "Sent", "Paid" });
        var apply = Button("btnApplyStatus", "Apply status", (_, _) => Run(ApplyStatus, "Status changed."));
        apply.Left = 140;
        apply.Top = y;
        page.Controls.Add(apply);
        var delete = Button("btnDeleteDraft", "Delete draft", (_, _) => Run(DeleteDraft, "Draft deleted."));
        delete.Left = 300;
        delete.Top = y;
        page.Controls.Add(delete);
        _invoices.Left = 12;
        _invoices.Top = y + 40;
        page.Controls.Add(_invoices);
        return page;
    }

    private int SelectedInvoiceId()
    {
        if (_invoice.SelectedIndex < 0)
        {
            throw new InvalidOperationException("Choose an invoice.");
        }

        return Convert.ToInt32(_invoiceTable.Rows[_invoice.SelectedIndex]["InvoiceId"], CultureInfo.InvariantCulture);
    }

    private void ApplyStatus()
    {
        if (_status.SelectedItem is not string status)
        {
            throw new InvalidOperationException("Choose a status.");
        }

        _store.ChangeStatus(SelectedInvoiceId(), status);
        RefreshLists();
    }

    private void DeleteDraft()
    {
        _store.DeleteDraft(SelectedInvoiceId());
        RefreshLists();
    }

    private TabPage ExportPage()
    {
        var page = new TabPage("Export") { Name = "pageExport" };
        var y = 12;
        Row(page, "Export to file:", _exportPath, ref y);
        _exportPath.Width = 400;
        var export = Button("btnExport", "Export customers", (_, _) => Run(Export, "Exported."));
        export.Left = 140;
        export.Top = y;
        page.Controls.Add(export);
        return page;
    }

    private void Export()
    {
        // Semicolon-separated with a header and two-decimal amounts, as the accounting package expects.
        var table = _store.CustomersForExport();
        var sb = new StringBuilder("Id;Name;City;Credit limit\r\n");
        foreach (DataRow r in table.Rows)
        {
            sb.Append(Convert.ToString(r["CustomerId"], CultureInfo.InvariantCulture)).Append(';')
              .Append(Quote(Convert.ToString(r["Name"], CultureInfo.InvariantCulture) ?? "")).Append(';')
              .Append(Quote(r["City"] is DBNull ? "" : Convert.ToString(r["City"], CultureInfo.InvariantCulture) ?? "")).Append(';')
              .Append(Convert.ToDecimal(r["CreditLimit"], CultureInfo.InvariantCulture).ToString("0.00", CultureInfo.InvariantCulture)).Append("\r\n");
        }

        File.WriteAllText(_exportPath.Text, sb.ToString());
    }

    private static readonly char[] QuoteTriggers = { ';', '"', '\r', '\n' };

#pragma warning disable CA1870 // SearchValues does not exist on .NET Framework 4.8, which shares this file.
    private static string Quote(string value) =>
        value.IndexOfAny(QuoteTriggers) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
#pragma warning restore CA1870

    private void RefreshLists()
    {
        _customerItems = _store.CustomerNames();
        _customer.Items.Clear();
        _customer.Items.AddRange(_customerItems.Select(c => (object)c.Value).ToArray());
        _invoiceTable = _store.Invoices();
        _invoice.Items.Clear();
        _invoice.Items.AddRange(_invoiceTable.Rows.Cast<DataRow>().Select(r => (object)Convert.ToString(r["InvoiceNo"], CultureInfo.InvariantCulture)!).ToArray());
        _invoices.DataSource = _invoiceTable;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _store.Dispose();
        }

        base.Dispose(disposing);
    }
}
