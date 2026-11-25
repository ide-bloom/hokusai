## What and why

## Checklist

- [ ] `dotnet format hokusai.slnx --verify-no-changes` and the clang-format check pass
- [ ] Unit tests pass (`dotnet test --project tests/Hokusai.Tests`)
- [ ] New or changed inference rules have a test and are described in `docs/inference.md`
- [ ] Generated code changes are reflected in the golden files (`HOKUSAI_UPDATE_GOLDEN=1`)
- [ ] Generated SQL stays parameterized; no statement is built by concatenating inputs
- [ ] Documentation describes only what exists
