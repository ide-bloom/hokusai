using System;
using System.Windows.Forms;

namespace Hokusai.TestApps;

internal static class Program
{
    /// <summary>Usage: LegacyInvoices.exe "&lt;ADO connection string&gt;" (for LocalDB, SQLOLEDB with the instance pipe name).</summary>
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 1)
        {
            MessageBox.Show("Usage: LegacyInvoices.exe \"<ADO connection string>\"", "Legacy Invoices");
            return 2;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new MainForm(new AdoStore(args[0]), "Legacy Invoices (ADO)"));
        return 0;
    }
}
