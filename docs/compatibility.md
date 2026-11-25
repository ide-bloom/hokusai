# Compatibility

## Tested automatically

| App | Data access | Capture | Status |
|---|---|---|---|
| `testapps/LegacyInvoices` (WinForms, .NET Framework 4.8, x86) | Classic ADO through late-bound COM (`ADODB.Connection`, SQLOLEDB), SQL built by string concatenation, SQL Server LocalDB | Extended Events | All six actions verified; generated API and MCP reproduce the app for new inputs. Runs in CI on `windows-latest`. |
| `testapps/AccessInvoices` (WinForms, .NET 10, x64) | ACE OLE DB with positional parameters, `.accdb` | State diffs | Five actions verified; *delete draft invoice* only after its guards are supplied (`Status = 'Draft'` for the invoice, unconditional for its lines and tax row); *search customers* is reported as having no data effect, as expected for diff-only capture. Runs locally only: the ACE provider is not installed on GitHub-hosted runners. |
| `testapps/ModernInvoices` (WinForms, .NET 10, x64) | ODBC with `?` parameters, SQLite through the SQLite ODBC driver | ODBC agent | All six actions verified; generated API and MCP reproduce the app for new inputs. Needs the 64-bit "SQLite3 ODBC Driver"; skipped where it is missing (see CONTRIBUTING.md for CI). |
| Windows PowerShell 5.1, 64-bit and 32-bit | System.Data.Odbc through the "SQL Server" ODBC driver | ODBC agent | Statements, parameter values and rows captured (agent smoke test). |

Each test app implements: create customer, create invoice with line items and a taxable checkbox, search customers,
change invoice status, delete draft invoice (destructive) and export customers to CSV (file effect).

## Manual test with a real VB6 or Delphi app

Use a sample you are allowed to inspect, with its database restored on a test server, never production.

1. **Prepare.** Note the app's bitness (32-bit for VB6 and older Delphi) and data access: ADO/OLE DB or ODBC against
   SQL Server, ODBC against another DBMS, BDE/ADO against Access, and so on. Run `hokusai doctor`.
2. **Connect.**
   - SQL Server (any client library): `hokusai connect --sqlserver "<admin connection to the test database>"`.
   - ODBC: `hokusai connect --odbc "DSN=<dsn>" --file <database file, if file based>`.
   - Access: `hokusai connect --access <file.mdb or .accdb>` (match ACE bitness to Hokusai's process for snapshots).
3. **Record** one action twice, varying every field: `hokusai record "<action>" -- <app.exe>`. VB6 controls are
   Win32 controls; ThunderRT6 text boxes expose values through MSAA. If a field's value is missing from
   `demonstration.json`, raise the poll interval with `--poll-ms 400` and type more slowly.
4. **Check capture** in the Studio's Recording view (live UI and data timelines) or in `demonstration.json`: every
   typed value, the button press, the statements (or row changes) and the expected file effects.
5. **Infer and verify**: `hokusai infer "<action>"`, review the evidence, `hokusai verify "<action>"`.
6. **Generate and test**: `hokusai generate …`, then `dotnet test` in the output folder.
7. **Report** the result in an issue with the app's technology, `hokusai doctor` output and any issues or mismatches
   (without customer data).

Known considerations:

- Delphi apps using BDE or dbExpress with SQL Server are captured by Extended Events; BDE with Paradox or dBase
  files is not supported.
- VB6 apps often format numbers and dates with the system locale; add the locale to `cultures` in
  `.hokusai/hokusai.json` if it differs from the recording machine's.
- Owner-drawn grids (for example third-party VB6 grid controls) may not expose cells to UI Automation; line items
  in such grids cannot be tied to loops.
