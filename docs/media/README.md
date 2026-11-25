# Recording the demo GIF

`demo.gif` is a placeholder until the demo is recorded. The demo shows, in about 60 seconds:

1. `hokusai record "create invoice" -- LegacyInvoices.exe "<ADO connection string>"` and two demonstrations with
   different customers, dates, the Taxable checkbox on and off, and two versus one line items.
2. `hokusai infer "create invoice"`, showing the lookup, the loop over line items and the Taxable condition.
3. `hokusai verify "create invoice"` reporting VERIFIED.
4. `hokusai generate --actions "create invoice" --out demo-api --name Invoicing`.
5. In Claude (Claude Code or Claude Desktop) with the generated MCP server registered: "Create an invoice for
   Initech with 10 bolts at 0.25", the `confirm` prompt, and the new invoice appearing in the legacy app.

## How to record

- Use the legacy test app on a fresh LocalDB database seeded from `testapps/seed/sqlserver.sql`
  (the end-to-end tests show the exact setup in `tests/Hokusai.EndToEnd.Tests/Scenarios.cs`).
- Record at 1280×720 with ScreenToGif (or any recorder that exports GIF), 10–12 frames per second, terminal font
  size 16 or larger.
- Keep the file under 10 MB so it renders on GitHub; trim pauses rather than speeding up typing.
- Do not show real customer data or connection strings with credentials.
- Replace `docs/media/demo.gif` and remove the "not recorded yet" note from the top of `README.md`.
