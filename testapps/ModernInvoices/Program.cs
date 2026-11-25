using System;
using System.Windows.Forms;

namespace Hokusai.TestApps;

internal static class Program
{
    /// <summary>Usage: ModernInvoices.exe "Driver={SQLite3 ODBC Driver};Database=&lt;file&gt;"</summary>
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 1)
        {
            MessageBox.Show("Usage: ModernInvoices.exe \"<ODBC connection string>\"", "Modern Invoices");
            return 2;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm(new OdbcStore(args[0]), "Modern Invoices (ODBC)"));
        return 0;
    }
}
