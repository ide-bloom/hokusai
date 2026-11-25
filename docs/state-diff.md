# State diffs

State diffs record what changed in the database during a demonstration, row by row. They are the only data capture
for file databases without the ODBC agent (Access through ACE, SQLite through Microsoft.Data.Sqlite), and they are
recorded for every source because verification compares against them.

## Snapshots

`Runtime.StateDiffer.SnapshotAsync` reads every column of the chosen tables (except row-version columns, which
change on every write) and canonicalizes values: invariant numbers with trailing zeros removed, `yyyy-MM-dd HH:mm:ss[.fffffff]`
timestamps, `1`/`0` booleans, lowercase GUIDs, `0x…` binary.

Which tables:

- When the whole database holds at most 200,000 rows, every table is snapshotted at the start mark (and saved as
  `before.json`, which inference also uses for lookups) and again at the end mark.
- Otherwise only the tables the statements touched are snapshotted at the end mark, and their "before" state is read
  from the demonstration's baseline copy.

## Diffs

`Runtime.StateDiffer.Diff` matches rows by primary key and reports inserts, updates (with the changed columns) and
deletes. Tables without a key are compared as multisets by row hash, so an update appears as a delete plus an insert.

## Comparison during verification

`Runtime.DiffComparer.Compare` compares the app's diff with the replay's:

- Columns holding generated values (timestamps, GUIDs, columns set by `GETDATE()`/`NEWID()`/`datetime('now')`,
  and columns with time- or GUID-based defaults) are excluded.
- When such a column is part of the key, rows are matched on their remaining content.
- Numbers compare by value, timestamps by instant.
- Every difference names the table, the row key, the column, and both values.

## Statements from diffs

For diff-only sources, `Infer.DiffSynthesizer` rebuilds the statements that produce the observed changes, so
alignment and correlation work as for captured statements:

1. Updates of rows that existed before, first. A numeric change gets the read the app must have done
   (`SELECT NextValue FROM Counter WHERE Name = ?`), so a number used by a later insert, and the counter written back
   incremented, are explained as dependent values.
2. Inserts in foreign-key order (referenced tables first), each followed by the identity read the provider offers
   (`SELECT @@IDENTITY`, `last_insert_rowid()`, `SCOPE_IDENTITY()`), so child rows referencing the new key are
   dependent.
3. Deletes, children first. Child rows deleted together with their parent are deleted through the foreign key
   (`DELETE FROM InvoiceLine WHERE InvoiceId = ?`), as the app did.

Limits of diff-only capture: reads are invisible (a search action is reported as having no data effect), and
conditions in the app's SQL that did not change the outcome (`AND Status = 'Draft'`) cannot be seen.

## Guards for destructive statements

Because the condition behind a delete is invisible in a diff, every DELETE synthesized from a state diff is marked
`GuardRequired`, and the action gets an issue: it is not verified and is generated disabled with that reason, even if
the replay matches. The user supplies the guard:

```
hokusai guard "delete draft invoice"                                   # lists steps needing a guard
hokusai guard "delete draft invoice" --step 3 --where "Status = 'Draft'"
hokusai guard "delete draft invoice" --step 1 --unconditional          # the app deletes these rows without a condition
hokusai verify "delete draft invoice"
```

A guard is a single SQL condition (no `;` or comments) appended to the statement as `AND (<condition>)`. Guards are
stored in `recordings/<action>/guards.json` by statement shape, so they survive re-inference. The action is enabled
only when verification passes with the guards in place, which shows the guard is consistent with what the app did in
every demonstration. Deletes captured as statements (Extended Events, ODBC agent) carry the app's own condition and
need no guard.

CSV exports from diff-only sources are explained by finding a table whose rows, projected and ordered by one of its
columns, produce the file (`FileOutputInference.SynthesizeExportQuery`).
