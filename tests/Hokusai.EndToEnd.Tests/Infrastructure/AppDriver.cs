using System.Globalization;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;

namespace Hokusai.EndToEnd.Tests.Infrastructure;

internal sealed record CustomerInput(string Name, string City, decimal CreditLimit);

internal sealed record LineInput(string Item, int Quantity, decimal UnitPrice);

internal sealed record InvoiceInput(string Customer, DateTime Date, bool Taxable, LineInput[] Lines);

internal sealed record StatusInput(string InvoiceNo, string Status);

/// <summary>
/// Performs the test apps' actions through UI Automation the way a user would: typing, choosing from combo boxes
/// and clicking buttons with the mouse. Pauses between steps give the recorder's polling time to see each value.
/// </summary>
internal sealed class AppDriver : IDisposable
{
    private static readonly TimeSpan Pace = TimeSpan.FromMilliseconds(450);
    private readonly UIA3Automation _automation = new();
    private readonly Application _app;
    private readonly Window _window;
    private readonly int _processId;

    public AppDriver(int processId)
    {
        _processId = processId;
        _app = Application.Attach(processId);
        _window = _app.GetMainWindow(_automation, TimeSpan.FromSeconds(60)) ?? throw new InvalidOperationException("The app window did not appear.");

        // The form loads its lists after the window appears; wait for its first status message.
        _app.WaitWhileBusy(TimeSpan.FromSeconds(30));
        Find("lblMessage");
        Thread.Sleep(Pace);
    }

    private AutomationElement Find(string automationId) =>
        FlaUI.Core.Tools.Retry.WhileNull(() => _window.FindFirstDescendant(cf => cf.ByAutomationId(automationId)), TimeSpan.FromSeconds(10)).Result
        ?? throw new InvalidOperationException($"Control {automationId} not found. Visible: {string.Join(", ", _window.FindAllDescendants().Select(e => e.Properties.AutomationId.TryGetValue(out var a) ? a : "?"))}");

    private void Tab(string name)
    {
        var tab = _window.FindFirstDescendant(cf => cf.ByControlType(ControlType.TabItem).And(cf.ByName(name)))?.AsTabItem()
            ?? throw new InvalidOperationException($"Tab {name} not found.");
        tab.Select();
        Thread.Sleep(Pace);
    }

    private void Type(string id, string text)
    {
        // A freshly started app may still be settling; confirm the text arrived and retry if it did not.
        var box = Find(id).AsTextBox();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            box.Text = text;
            Thread.Sleep(150);
            if (box.Text == text)
            {
                break;
            }
        }

        if (box.Text != text)
        {
            throw new InvalidOperationException($"{id} shows '{box.Text}' instead of '{text}'.");
        }

        Thread.Sleep(Pace);
    }

    private void Choose(string id, string text)
    {
        // Selecting through the SelectionItem pattern does not change a Win32 drop-down list, and typed prefixes
        // time out between keystrokes; step through the items with the arrow keys like a keyboard user.
        var combo = Find(id).AsComboBox();
        _window.SetForeground();
        combo.Click(moveMouse: true);
        Thread.Sleep(200);
        FlaUI.Core.Input.Keyboard.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.HOME);
        for (var i = 0; i < 200 && Selected(combo) != text; i++)
        {
            FlaUI.Core.Input.Keyboard.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.DOWN);
            Thread.Sleep(30);
        }

        FlaUI.Core.Input.Keyboard.Press(FlaUI.Core.WindowsAPI.VirtualKeyShort.RETURN);
        Thread.Sleep(Pace);
        if (Selected(combo) != text)
        {
            throw new InvalidOperationException($"{id} shows '{Selected(combo)}' instead of '{text}'.");
        }
    }

    private static string? Selected(ComboBox combo) =>
        combo.Patterns.Value.TryGetPattern(out var v) ? v.Value.ValueOrDefault : combo.SelectedItem?.Text;

    private void Check(string id, bool value)
    {
        Find(id).AsCheckBox().IsChecked = value;
        Thread.Sleep(Pace);
    }

    /// <summary>Clicks a button with the mouse and waits for the app's status message.</summary>
    private void Press(string id, string expected)
    {
        var before = Find("lblMessage").Name;
        _window.SetForeground();
        Find(id).AsButton().Click(moveMouse: true);

        // The app numbers its status messages, so a repeated action still produces a new message. A synthesized
        // click is occasionally lost (focus or window activation races); then press the button through UIA.
        var result = FlaUI.Core.Tools.Retry.WhileFalse(() => Find("lblMessage").Name != before, TimeSpan.FromSeconds(6));
        if (!result.Success)
        {
            Find(id).AsButton().Invoke();
            result = FlaUI.Core.Tools.Retry.WhileFalse(() => Find("lblMessage").Name != before, TimeSpan.FromSeconds(30));
        }

        var text = Find("lblMessage").Name;
        if (!result.Success || !text.StartsWith(expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"After {id}: '{text}'.");
        }

        Thread.Sleep(Pace);
    }

    private static string Number(decimal value) => value.ToString(CultureInfo.CurrentCulture);

    public void CreateCustomer(CustomerInput c)
    {
        Tab("Customers");
        Type("txtName", c.Name);
        Type("txtCity", c.City);
        Type("txtCreditLimit", Number(c.CreditLimit));
        Press("btnSaveCustomer", "Customer saved.");
    }

    public void Search(string text)
    {
        Tab("Customers");
        Type("txtSearch", text);
        Press("btnSearch", "Search done.");
    }

    public void CreateInvoice(InvoiceInput i)
    {
        Tab("New invoice");
        Choose("cboCustomer", i.Customer);
        Type("txtDate", i.Date.ToString("d", CultureInfo.CurrentCulture));
        Check("chkTaxable", i.Taxable);
        foreach (var line in i.Lines)
        {
            Type("txtLineDescription", line.Item);
            Type("txtLineQty", line.Quantity.ToString(CultureInfo.CurrentCulture));
            Type("txtLinePrice", Number(line.UnitPrice));
            Press("btnAddLine", "Line added.");
        }

        Press("btnSaveInvoice", "Invoice saved.");
    }

    public void ChangeStatus(StatusInput s)
    {
        Tab("Invoices");
        Choose("cboInvoice", s.InvoiceNo);
        Choose("cboStatus", s.Status);
        Press("btnApplyStatus", "Status changed.");
    }

    public void DeleteDraft(string invoiceNo)
    {
        Tab("Invoices");
        Choose("cboInvoice", invoiceNo);
        Press("btnDeleteDraft", "Draft deleted.");
    }

    public void Export(string path)
    {
        Tab("Export");
        Type("txtExportPath", path);
        Press("btnExport", "Exported.");
    }

    /// <summary>Names in the customer search result grid (WinForms grid rows may not even report a control type).</summary>
    public List<string> SearchResultNames()
    {
        Thread.Sleep(Pace);
        var names = new List<string>();
        foreach (var row in Find("grdCustomers").FindAllChildren())
        {
            foreach (var cell in row.FindAllChildren())
            {
                var name = cell.Properties.Name.TryGetValue(out var n) ? n ?? "" : "";
                if (name.StartsWith("Name Row", StringComparison.Ordinal))
                {
                    names.Add(cell.Patterns.Value.TryGetPattern(out var v) ? v.Value.ValueOrDefault ?? "" : cell.Patterns.LegacyIAccessible.Pattern.Value.ValueOrDefault ?? "");
                }
            }
        }

        return names;
    }

    public void Dispose()
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(_processId);
            process.CloseMainWindow();
            if (!process.WaitForExit(5000))
            {
                process.Kill();
            }
        }
        catch (ArgumentException)
        {
            // Already exited.
        }

        _app.Dispose();
        _automation.Dispose();
    }
}
