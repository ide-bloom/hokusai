# SQL Server capture with Extended Events

For SQL Server sources (any client library, including classic ADO/OLE DB, ODBC and SqlClient), Hokusai captures
on the server, with no change to the app (`Capture.SqlServer.ExtendedEventsSession`).

## The session

When recording starts, Hokusai creates and starts a session named `hokusai_<random>`:

```sql
CREATE EVENT SESSION [hokusai_…] ON SERVER
ADD EVENT sqlserver.sql_batch_completed(ACTION(sqlserver.client_pid, sqlserver.client_hostname, sqlserver.session_id, sqlserver.database_name)
    WHERE (sqlserver.client_pid = <app pid> AND sqlserver.client_hostname = N'<machine>')),
ADD EVENT sqlserver.rpc_completed(... same actions and predicate ...)
ADD TARGET package0.event_file(SET filename = N'<server log folder>\hokusai_….xel', max_file_size = 64)
WITH (MAX_DISPATCH_LATENCY = 1 SECONDS, EVENT_RETENTION_MODE = NO_EVENT_LOSS, STARTUP_STATE = OFF);
```

- The predicate keeps only the app's own connections: its client process ID and host.
- `sql_batch_completed` carries the batch text and row count; `rpc_completed` carries the RPC statement with its
  parameter values (for example `exec sp_executesql N'…', N'@P1 int', @P1=5`) and row count. `sp_reset_connection`
  calls are ignored.
- The event file lives in the server's log folder (from `SERVERPROPERTY('ErrorLogFileName')`), which the server can
  always write. Hokusai reads it incrementally with `sys.fn_xe_file_target_read_file`, resuming after the last
  buffer offset read.
- The session is dropped when recording ends. A session left behind by a crash is stopped and harmless;
  `hokusai doctor` lists it and `hokusai doctor --clean` drops it.

## Result sets

Extended Events do not record result sets. Inference needs them for dependent values (a next number read before an
insert, `SCOPE_IDENTITY()`) and lookups, so `hokusai infer` **replays** each demonstration: it restores the
demonstration's baseline into a throwaway database and executes the captured statements, in order, inside a
transaction that is rolled back, keeping the results of the statements that return rows. The replayed results are
marked `ResultRecovered` and cited in the evidence.

## Required permissions

| SQL Server version | Manage the session | Read the event file |
|---|---|---|
| 2019 and earlier | `ALTER ANY EVENT SESSION` | `VIEW SERVER STATE` |
| 2022 and later | `CREATE ANY EVENT SESSION`, `ALTER ANY EVENT SESSION`, `DROP ANY EVENT SESSION` (or `ALTER ANY EVENT SESSION`) | `VIEW SERVER PERFORMANCE STATE` |

Baselines additionally need `BACKUP DATABASE` on the observed database, and verification and result recovery need
`CREATE DATABASE` (for `RESTORE`) on the server used for copies. The LocalDB instance owner has all of these.
`hokusai doctor` checks the event-session permissions.

Grant example for a dedicated recording login on SQL Server 2022:

```sql
GRANT CREATE ANY EVENT SESSION, ALTER ANY EVENT SESSION, DROP ANY EVENT SESSION TO [hokusai_recorder];
GRANT VIEW SERVER PERFORMANCE STATE TO [hokusai_recorder];
```

Record against a test copy of the database, not production: demonstrations write real rows.
