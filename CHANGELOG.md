# Changelog

All notable changes to this project are documented in this file. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- `hokusai guard`: deletes rebuilt from state diffs (Access, SQLite without the agent) are no longer verified on their
  own, because the app's condition cannot be observed; the action stays disabled until the user supplies the guard
  (or confirms it is unconditional) and verification passes with it.

### Fixed

- Decimal parameters through the SQLite ODBC driver are bound as invariant text; the driver rejects `SQL_NUMERIC`.
- Button presses seen by the input hooks are timestamped when they happen, not when they are resolved, so forms that
  clear themselves after saving are read correctly in state-diff mode.
- Verification reports a useful message for provider exceptions that carry none.

## [0.1.0] - 2026-11-24

### Added

- `hokusai` CLI with `connect`, `record`, `infer`, `verify`, `generate`, `schema-check`, `doctor` and `actions`.
- Hokusai Studio (WPF, Fluent theme) with data source setup, recording with live UI and data timelines,
  demonstration lists, the inferred template color-coded by value class with evidence, conditions and loops,
  verification results with row-level diffs, and generation.
- Capture: UI Automation recorder (events, input hooks and polling); SQL Server Extended Events sessions filtered to
  the app's process; the native ODBC agent (`hokusai_agent32/64.dll`, Microsoft Detours) with a shared-memory event
  ring; row-level state diffs for any source; ETW kernel file events with INI, CSV, XML, JSON and text diffs.
- SQL normalization through ScriptDom for T-SQL (including `sp_executesql` and `sp_prepexec` unwrapping) and a
  dialect-aware tokenizer for SQLite, Access and generic ODBC SQL.
- Inference: demonstration alignment with conditional statements and loops, value classification (input with
  identity, trim, case, locale-aware number and date, boolean and LIKE transforms; lookups; dependent values
  including counters; generated timestamps and GUIDs; constants), single-input conditions, parameter typing from the
  schema, read actions with typed results, CSV exports, and statement synthesis from state diffs for file
  databases.
- Verification by replay on fresh copies (LocalDB backup and restore, attach, file copy or seed script), with
  exact mismatching rows and columns.
- Generation of an ASP.NET Core minimal API (OpenAPI, dry runs, idempotency keys, API-key or Windows
  authentication, audit log, startup schema-drift check, localhost binding), an MCP server on the official C# SDK
  (confirmation and dry runs for writes) and an xUnit regression test project.
- Test apps: legacy WinForms on .NET Framework 4.8 with classic ADO, modern WinForms with ODBC on SQLite, and an
  Access variant through ACE OLE DB.

[Unreleased]: https://github.com/ide-bloom/hokusai/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/ide-bloom/hokusai/releases/tag/v0.1.0
