# Architecture

```
src/
  Hokusai.Runtime/    data layer shared by verification and generated code (copied into generated projects as source)
  Hokusai.Core/       recording model, templates, workspace and DPAPI credentials, template interpreter
  Hokusai.Sql/        T-SQL normalization through ScriptDom; tokenizer for SQLite, Access and generic ODBC SQL
  Hokusai.Capture/    UI Automation recorder, Extended Events sessions, ODBC agent reader, file effect capture
  Hokusai.Infer/      alignment, value correlation, dependents, conditions, loops, diff synthesis, CSV exports
  Hokusai.Verify/     baselines, database copies, replay and comparison
  Hokusai.Generate/   C# emitter and Scriban templates for the API, MCP server and test projects
  Hokusai.Engine/     the workflow used by both front ends: recording sessions, infer, verify, generate, checks
  Hokusai.Cli/        hokusai.exe (System.CommandLine)
  Hokusai.Studio/     Hokusai Studio (WPF, Fluent theme)
native/agent/         hokusai_agent{32,64}.dll and hokusai_launcher{32,64}.exe (C++20, Microsoft Detours, vcpkg)
testapps/             legacy-style apps and their seed scripts
tests/                unit and golden tests (any OS); end-to-end tests (Windows)
```

`Hokusai.Engine` is the one project beyond the planned list: the CLI and the Studio need the same orchestration, and
it depends on every other library, so it sits above them instead of inside any of them.

## Data flow

1. **Recording** (`Engine.RecordingSession`): starts or attaches to the app, then runs the UI recorder, the data
   capture for the configured source (Extended Events session or ODBC agent) and file capture side by side. At each
   start mark it saves a baseline copy (`baseline.bak`, `.db` or `.accdb`) and, for databases up to 200,000 rows, a
   snapshot of every table (`before.json`). At the end mark it assigns events to the demonstration by timestamp,
   computes the row-level state diff and the file effects, and saves `demonstration.json`.
2. **Inference** (`Infer.TemplateInferrer`): recovers result sets missing from Extended Events by replaying the
   demonstration on its baseline (`Engine.HokusaiEngine.RecoverResultsAsync`), normalizes statements, aligns
   demonstrations, classifies every value and writes `template.json` with evidence.
3. **Verification** (`Verify.Verifier`): restores each baseline into a fresh copy, runs the template through
   `Core.TemplateExecutor` with the demonstration's inputs, and compares diffs, files and read results.
4. **Generation** (`Generate.ProjectGenerator`): emits straight-line C# for each action on top of the same runtime
   primitives (`HkRun`, `HkEncode`, `HkCondition`, `HkLookup`), plus the API, MCP and test projects.

## Why the runtime is shared as source

Verification proves that a template reproduces the app. That proof only carries over to the generated API if the
API executes statements exactly as verification did. `Hokusai.Runtime` holds every primitive that touches the
database (placeholder rendering, parameter binding, value encodings, lookups, snapshots, diffs, CSV output) and the
generator embeds its source files into each generated project, so both run the same code. Golden tests pin the
generated output; the generated regression tests and the end-to-end tests check the behavior.

## Workspace layout

```
.hokusai/hokusai.json        data source (without secrets), cultures, watch folders
.hokusai/secrets.json        connection string encrypted with DPAPI for the current Windows user
recordings/<action>/demo-001/demonstration.json, before.json, baseline.*
recordings/<action>/template.json
recordings/<action>/verification.json
```

`recordings/` and `.hokusai/` are in `.gitignore`.
