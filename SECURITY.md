# Security policy

## Reporting a vulnerability

Please report vulnerabilities privately through
[GitHub security advisories](https://github.com/ide-bloom/hokusai/security/advisories/new), not as public issues.
Include the affected component (CLI, Studio, ODBC agent, generated API or MCP server), a description and, if
possible, a minimal reproduction. You will get an acknowledgement within five working days.

## Supported versions

Only the latest release receives security fixes.

## Security model

Hokusai observes applications and databases you own or are allowed to inspect. Its design keeps that observation
contained:

- **Consent for in-process capture.** The ODBC agent is loaded only into a program Hokusai starts, or into a
  running process you name with `--attach` and confirm (or pass `--yes`). It never injects anywhere else.
- **Read-only observation.** The agent's hooks report what the app sends to ODBC; they never change arguments or
  results. Extended Events sessions are created filtered to the app's client process ID and host, and dropped when
  recording ends (`hokusai doctor --clean` removes leftovers).
- **Verification on copies.** `hokusai verify` replays templates only on fresh copies (restored backups, copied
  files or seed scripts), never on the configured database.
- **Credentials.** Connection strings are encrypted with DPAPI for the current Windows user
  (`.hokusai/secrets.json`) and never written to `hokusai.json`, recordings or generated projects. Generated
  projects read their connection string from user secrets or environment variables, and the generated tests refuse
  to write a connection template that contains a password.
- **Generated services.** Generated APIs bind to `127.0.0.1` by default, require an API key (stored as a SHA-256
  hash) or Windows authentication, refuse databases on other machines unless `Hokusai:AllowNonLocalDatabase=true`,
  refuse to start after schema drift, and bind every value as a parameter. MCP write tools require `confirm: true`.
- **No telemetry.** Hokusai makes no network connections other than to the databases you configure.

## Sensitive data in recordings

Recordings contain every value typed during a demonstration, the statements the app sent with their parameter
values, database snapshots and baseline copies. Treat the `recordings/` folder like the database itself; it is
excluded from Git by the repository's `.gitignore`.
