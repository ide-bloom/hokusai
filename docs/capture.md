# Capture

A recording session attaches four observers to the app's process. Everything between a start mark and an end mark
belongs to that demonstration; events are ordered by timestamp, so each data event is linked to the button press
that preceded it (`StatementStep.Trigger`).

## UI events (`Capture.Ui.UiRecorder`)

UI Automation (FlaUI, UIA3) records:

- **Invoke**: button presses, from the UIA Invoked event and, because legacy Win32 buttons do not always raise it,
  from low-level mouse (click on a button) and keyboard (Enter or Space on a focused button) hooks. Duplicates within
  400 ms are merged.
- **Value changes**: text in edit controls, spinners and controls that expose only the Value pattern (date pickers),
  with the control's label and automation ID. Password fields are never recorded.
- **Selections**: combo boxes (selected item text), lists, and selected grid rows with the text of every cell.
- **Toggles**: check boxes and radio buttons.
- **Grid snapshots**: every row and column of each grid, read through the Table pattern, row and cell children, the
  Value pattern or MSAA values (WinForms on .NET Framework).

Values are polled every 200 ms (`--poll-ms`) in addition to events, because legacy controls raise UIA change events
inconsistently, and every control's current value is emitted at each start mark so unchanged fields are known. A
failing element (some providers do not even report a control type) is skipped without stopping the scan.

The **label** of a control is its accessible name when that is not just its content, otherwise `LabeledBy`,
otherwise the nearest text element to its left or above it.

Inference reads each control as it was at the **commit time**: the first write statement (or the last statement of
a read-only demonstration, or the last button press when only state diffs are available). Forms that clear
themselves after saving are therefore read before they clear.

## SQL Server

See [extended-events.md](extended-events.md).

## ODBC

See [odbc-agent.md](odbc-agent.md).

## State diffs

See [state-diff.md](state-diff.md).

## File effects (`Capture.Files`)

With an elevated Hokusai, a kernel ETW session (TraceEvent, `FileIO` and `FileIOInit` keywords) attributes file
writes to the app and to processes it starts. Structured text files in the watch folders (the app's own folder
plus folders added with `hokusai connect --watch`) are snapshotted at each start mark, so modifications show exactly
what changed; files elsewhere are reported with their new content. Without elevation, writes are found by comparing
watch-folder snapshots only. Database, journal, log and temporary files are ignored (databases are covered by state
diffs).

Content diffs (`StructuredDiff`):

| Format | Detected by | Diff |
|---|---|---|
| INI | `.ini`, `.cfg`, `.inf` | `section.key` values |
| JSON | `.json` | JSON pointer paths |
| XML | `.xml`, `.config` | element paths with positions, `@attribute` values |
| CSV | `.csv`, `.tsv` | rows as a multiset |
| Text | anything else | lines (LCS) |

Files up to 2 MB are diffed. CSV exports are explained as a formatting of a query result and generated (see
[inference.md](inference.md#csv-exports)); other file effects are recorded and diffed but make the action
unverified, because only CSV output is generated.
