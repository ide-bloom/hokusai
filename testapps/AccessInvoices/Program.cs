using System;
using System.Windows.Forms;

namespace Hokusai.TestApps;

internal static class Program
{
    /// <summary>
    /// Usage: AccessInvoices.exe &lt;file.accdb&gt;, or AccessInvoices.exe --create &lt;file.accdb&gt; &lt;seed.sql&gt; to build the database.
    /// </summary>
    [STAThread]
    private static int Main(string[] args)
    {
        if (args is ["--create", var path, var seed])
        {
            AccessStore.Create(path, seed);
            return 0;
        }

        if (args.Length != 1)
        {
            MessageBox.Show("Usage: AccessInvoices.exe <file.accdb> | --create <file.accdb> <seed.sql>", "Access Invoices");
            return 2;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm(new AccessStore(args[0]), "Access Invoices (ACE OLE DB)"));
        return 0;
    }
}
