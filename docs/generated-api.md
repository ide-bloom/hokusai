# Generated REST API

`hokusai generate --actions <list> --out <dir> --name <Name>` writes:

```
<Name>.slnx, Directory.Build.props, global.json, README.md
src/<Name>.Actions/   action classes, input records, validation, ActionRunner, schema and database guards,
                      and the Hokusai runtime as source (Runtime/*.cs)
src/<Name>.Api/       ASP.NET Core minimal API
src/<Name>.Mcp/       MCP server (see mcp.md)
tests/<Name>.Tests/   regression tests with each demonstration's inputs, expected diff and baseline
```

## Endpoints

- `POST /actions/<action>` per action (for example `/actions/create-invoice`), with a JSON body matching the input
  record (camelCase names; line items as arrays). Responses: `{ action, dryRun, rowsAffected, statements, rows, file }`.
- `GET /actions` lists the actions with their status.
- `GET /openapi/v1.json` serves the OpenAPI document, with each action's summary and description.

## Behavior

- **Validation**: JSON types; required fields (from NOT NULL columns without defaults); maximum lengths; foreign-key
  existence for key inputs; lookups that must match exactly one row. Invalid input returns `400` with the errors.
- **Execution**: in a transaction with the same provider type the app used (SqlClient, ODBC, Microsoft.Data.Sqlite or
  OLE DB), with every value bound as a parameter. (Through the SQLite ODBC driver, which rejects `SQL_NUMERIC`
  parameters, decimals are bound as invariant text.) Statement text is fixed at generation time; identifiers come from
  schema metadata.
- **Dry run**: `?dryRun=true` returns the planned statements with their argument values, with lookups and dependent
  values described instead of resolved. Nothing is executed.
- **Idempotency**: write requests with an `Idempotency-Key` header are executed once; a retry with the same key and
  body returns the stored response (`Idempotent-Replay: true`), a different body returns `422`. Keys are kept in
  `idempotency.jsonl` (`Hokusai:Idempotency:Path`).
- **Authentication**: `X-Api-Key` checked against SHA-256 hashes in `Hokusai:Auth:ApiKeyHashes` (create a hash with
  `dotnet run --project src/<Name>.Api -- hash-key <key>`), or Windows authentication with
  `Hokusai:Auth:Mode=Windows`. The API refuses to start in API-key mode without a configured key.
- **Audit log**: every call (action, user, dry run, outcome, rows affected, error, idempotency key, inputs) is appended
  to `audit.jsonl` (`Hokusai:Audit:Path`).
- **Schema drift**: at startup the API fingerprints the tables of every enabled action and refuses to start if any
  changed since inference.
- **Disabled actions**: unverified actions return `403` with the reason unless `Hokusai:EnableUnverified=true`.
- **Binding**: `http://127.0.0.1:5080` by default (`Urls` in `appsettings.json`).

## Configuration

| Key | Default | Meaning |
|---|---|---|
| `ConnectionStrings:Hokusai` | none (required) | From user secrets or `ConnectionStrings__Hokusai`; never in the project files. |
| `Hokusai:Provider` | provider the app used | `SqlServer`, `Odbc`, `Sqlite` or `Access`. |
| `Hokusai:AllowNonLocalDatabase` | `false` | Allow a database on another machine. |
| `Hokusai:Auth:Mode` | `ApiKey` | `ApiKey` or `Windows`. |
| `Hokusai:Auth:ApiKeyHashes:<n>` | none | SHA-256 hex of each accepted key. |
| `Hokusai:EnableUnverified` | `false` | Serve unverified actions. |
| `Hokusai:Audit:Path`, `Hokusai:Idempotency:Path` | content root | Log files. |

Use a least-privilege login for write actions and a read-only login when serving only read actions.

## Regression tests

`tests/<Name>.Tests` contains one test per demonstration. Each restores the demonstration's baseline into a fresh copy
(LocalDB for SQL Server sources, override with `HOKUSAI_TEST_SQLSERVER`; a file copy for file databases, override the
connection template with `HOKUSAI_TEST_CONNECTION_TEMPLATE`), calls the action through the API with
`WebApplicationFactory`, and compares the row changes with the recorded diff, the CSV content with the recorded file,
and read results with the recorded rows. Tests of disabled actions are skipped with the reason.
