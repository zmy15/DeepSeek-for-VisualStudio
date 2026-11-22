# Independent Verification

Judge scores are not the final oracle. Each regression suite can reference an
`oracle_suite`, implemented outside the Agent workspace.

## Data flow

```text
headless session
  -> session ZIP
  -> workspace/ snapshot
  -> deterministic path-scope checks
  -> hidden dotnet/CMake oracle
  -> <case>.verification.json
  -> merged into regression-run.json
```

An independent verification failure forces the case outcome to `fail`, even if
the LLM Judge returned `pass`.

## Commands

```powershell
.\benchmark\.venv\Scripts\python.exe .\benchmark\regression\regression.py validate

.\benchmark\.venv\Scripts\python.exe .\benchmark\regression\regression.py verify `
  --suite .\benchmark\regression\orderdesk.v1.json `
  --archives .\benchmark\regression\artifacts\candidate\archives `
  --results .\benchmark\regression\artifacts\candidate\results
```

`regression.py score` runs verification automatically before LLM scoring.
Use `--no-verify` only for legacy archives that do not contain `workspace/`.

## Safety boundary

The oracle project is never copied into the Agent workspace. Its path,
`allowed_changes`, and `forbidden_changes` are loaded from:

- `orderdesk.oracle.v1.json`
- `line-controller.oracle.v1.json`
- `underspecified.oracle.v1.json`
