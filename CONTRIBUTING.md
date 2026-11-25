# Contributing to Hokusai

Thank you for helping. Hokusai's promise is narrow and strict: a generated action is marked verified only when
replaying it on a fresh database copy reproduces exactly what the app did. Contributions should keep that promise.

## Build

Requirements (Windows 11 x64): the .NET 10 SDK, the .NET Framework 4.8 developer pack, Visual Studio Build Tools
with the C++ workload (x86 and x64), CMake, vcpkg (`VCPKG_ROOT` set), SQL Server Express LocalDB and Git. The ACE
OLE DB provider and the SQLite ODBC driver are needed only for the Access and ODBC end-to-end scenarios.

```
cd native/agent
cmake --preset x64 && cmake --build --preset x64
cmake --preset x86 && cmake --build --preset x86
cd ../..
dotnet build hokusai.slnx
```

## Test

```
dotnet test --project tests/Hokusai.Tests                       # unit and golden tests, any OS
dotnet test --project tests/Hokusai.EndToEnd.Tests -- --explicit on   # drives the test apps with the mouse
```

The end-to-end scenarios are marked explicit because they take over the mouse and keyboard for a few minutes;
they skip themselves when LocalDB, the ACE provider or the SQLite ODBC driver is missing.

After an intended change to generated code, refresh the golden files and review the diff:

```
HOKUSAI_UPDATE_GOLDEN=1 dotnet test --project tests/Hokusai.Tests
```

### CI and the SQLite ODBC driver

The driver's download host serves a TLS certificate issued for a different domain, so CI installs it only when the
installer matches a checksum stored in the repository variable `SQLITEODBC_SHA256`. Maintainers set that variable
after verifying the installer out of band; without it the ODBC scenario is skipped (the agent itself is still
tested against SQL Server through the ODBC driver that ships with Windows).

## Rules

- Format: `dotnet format hokusai.slnx` and `clang-format -i native/agent/src/*`.
- Warnings are errors; nullable reference types and analyzers are on. Pass `CancellationToken`s through.
- Every SQL statement executed by generated code or verification is parameterized; identifiers come from schema
  metadata and are quoted. Never concatenate input into SQL.
- Every inference rule has a unit test and a section in `docs/inference.md`.
- Comments explain why, not what.
- Never commit secrets, recordings, databases (`*.db`, `*.mdf`, `*.accdb`, backups) or build output. The small seed
  scripts in `testapps/seed` are the only database artifacts in the repository.
- Keep `README.md` and `docs/` describing only what exists.

## Pull requests

Describe the behavior change and how you verified it. For inference changes, include the demonstrations (as a unit
test built with `DemoBuilder`) that motivated it.
