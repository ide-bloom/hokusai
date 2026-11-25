# Verification

`hokusai verify <action> [--copy-from recorded|<file>]` decides whether an action is verified.

For each demonstration:

1. **Fresh copy.** By default (`recorded`), the baseline captured at the demonstration's start mark is restored:
   a SQL Server backup restored under a new database name (`RESTORE … WITH MOVE`), or a copied SQLite or Access file
   (SQLite baselines are taken with the online backup API, so they are consistent while the app holds the file).
   `--copy-from` takes a `.bak` or `.mdf` (attached with `FOR ATTACH_REBUILD_LOG`), a `.db`/`.accdb` file, or a `.sql`
   seed script (split on `GO` lines) for small test databases. A single source suits demonstrations that do not
   depend on one another; the recorded baselines always match the state each demonstration started from.
2. **Replay.** The template runs with the demonstration's inputs, rebuilt from its UI events as typed values, inside
   a transaction, through `Core.TemplateExecutor`, which uses the same runtime primitives as generated code.
3. **Compare.** The replay's row-level diff is compared with the app's (see [state-diff.md](state-diff.md)), with
   generated columns excluded. If the template writes a CSV file, its content is compared with the file the app
   wrote, line by line. For read actions, the result rows are compared with the rows the app received.

The copy is rolled back and dropped afterwards. Verification never writes to the configured database.

An action is **verified** when inference reported no issues and every demonstration matched. A delete synthesized
from a state diff is an issue until its guard is supplied with `hokusai guard` (see [state-diff.md](state-diff.md#guards-for-destructive-statements)). Otherwise the report
(`recordings/<action>/verification.json`, `hokusai verify` output and the Studio) lists the issues and, per
demonstration, the replay error or each mismatch:

```
'create invoice': NOT VERIFIED (1/2 demonstrations match)
  demonstration 1:
    Invoice [InvoiceId=12].Status: expected 'Draft', got 'DRAFT' (Insert)
```

The template's status is saved with it; `hokusai generate` generates unverified actions disabled, with the first
issue or mismatch as the reason.
