# Harness Comparison

Harness configuration is evaluated as an independent variable while the Agent
model, Judge model, suite, and case set stay fixed.

## Profiles

The reusable profiles are under `benchmark/headless/profiles/`:

- `ask-auto`
- `ask-no-handoff`
- `edit-direct`
- `ask-shallow-handoff`

Run each profile with the `trials` command, then compare representative runs:

```powershell
.\benchmark\.venv\Scripts\python.exe .\benchmark\regression\regression.py harness-compare `
  --run ask=.\benchmark\regression\artifacts\harness\ask\run-001\results\regression-run.json `
  --run edit=.\benchmark\regression\artifacts\harness\edit\run-001\results\regression-run.json `
  --baseline ask `
  --output .\benchmark\regression\artifacts\harness\comparison.json
```

The report contains:

- profile-level score, pass count, critical failures, verification status;
- case-by-case score deltas against the baseline profile;
- stable ranking by average score and pass count;
- a compatibility check that rejects mixed models, suite versions, or case sets.

The comparison module does not execute models. Agent execution belongs to
`trials.py`; comparison accepts only completed `regression-run.json` files.
