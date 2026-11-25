# The ODBC agent

For ODBC data sources, Hokusai loads `hokusai_agent32.dll` or `hokusai_agent64.dll` into the app and hooks the ODBC
API with [Microsoft Detours](https://github.com/microsoft/Detours). Source: `native/agent`.

## Loading

`hokusai_launcher32/64.exe` matches the target's bitness (Hokusai reads the PE header and the CLR header, so AnyCPU
.NET programs that prefer 32-bit are handled):

- `launch <agent.dll> <program> [args]` starts the program with the agent in its import table
  (`DetourCreateProcessWithDlls`); console programs get their own console so they do not depend on the launcher's
  standard handles.
- `launch-wait …` does the same and waits, printing the program's exit code (for diagnosing a program that will not
  start with the agent).
- `attach <agent.dll> <pid>` loads the agent into a running process with `CreateRemoteThread(LoadLibraryW)`. The CLI
  asks for confirmation unless `--yes` is given; the Studio asks too.

The agent links `odbc32.dll` statically, so the Driver Manager is loaded before `DllMain` and hooking there is safe.
It hooks the exports applications call; driver-internal calls are not affected.

## What is captured

| Hooked function | Why |
|---|---|
| `SQLExecDirect`, `SQLExecDirectW` | statement text and execution |
| `SQLPrepare`, `SQLPrepareW`, `SQLExecute` | prepared statements: text at prepare, execution later |
| `SQLBindParameter` | parameter bindings; values are read at execute time because ODBC binds by reference |
| `SQLParamData`, `SQLPutData` | data-at-execution parameters, reassembled from their chunks |
| `SQLBindCol`, `SQLFetch`, `SQLFetchScroll`, `SQLExtendedFetch` | bound column values of each fetched row |
| `SQLGetData` | unbound column values, including long values read in chunks |
| `SQLNumResultCols`, `SQLDescribeColW` (called, not hooked) | column names of the result |
| `SQLRowCount` | rows affected |
| `SQLMoreResults`, `SQLCloseCursor`, `SQLFreeStmt`, `SQLFreeHandle` | end of a result set; reset or drop of statement state |
| `SQLEndTran`, `SQLTransact` | commits and rollbacks |

ANSI applications call the unsuffixed exports (`SQLExecDirect`), Unicode applications the `W` exports; both are
hooked. ANSI text is converted from the process code page.

Values are rendered as Hokusai's canonical text: invariant numbers, ISO dates and timestamps, lowercase GUIDs,
`0x…` for binary. A value the app never reads (an unfetched column) is not captured: the agent only observes.

Failed executions (negative return codes) are dropped, because they change nothing.

Not supported: parameter arrays (`SQL_ATTR_PARAMSET_SIZE` greater than 1) and multi-row block fetches.

## The event ring

Events are JSON objects written to a shared-memory ring named `Local\hokusai_ring_<pid>` (4,096 slots of 8 KB):

- The writer reserves a sequence number with an interlocked increment, writes the slot, then publishes
  `committed = sequence + 1`.
- The reader (`Capture.Odbc.EventRing`) consumes slots in order, counts slots overwritten before it read them
  (`LostEvents`), and the header counts events truncated to the slot size.

`Capture.Odbc.OdbcAgent` turns events into statements: rows and row counts attach to the last execution on the same
statement handle, even after the statement was handed to the recording, because the reader keeps the object.

## Health

`hokusai doctor` reports whether the four agent files are present. The agent's first event reports whether all hooks
attached (a non-zero Detours error stops the recording with a message). The end-to-end test
`OdbcAgentTests` loads the agent into 64- and 32-bit Windows PowerShell running parameterized ODBC queries against
LocalDB through the ODBC driver that ships with Windows, and checks statements, parameters and rows.
