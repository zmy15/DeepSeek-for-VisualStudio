# Repeated Trials and Stability

One run is not enough to describe Coding Agent behavior. The `trials` command
prepares a clean workspace for every case and run, executes the headless Agent,
performs independent verification, runs the Judge, and aggregates `pass@k` and
score variance.

```powershell
$env:DEEPSEEK_API_KEY = "<agent-key>"
$env:JUDGE_API_KEY = "<judge-key>"

.\benchmark\.venv\Scripts\python.exe .\benchmark\regression\regression.py trials `
  --suite .\benchmark\regression\orderdesk.v1.json `
  --runs 3 `
  --profile .\benchmark\headless\profiles\ask-auto.json `
  --output .\benchmark\regression\artifacts\stability\orderdesk-ask-auto
```

Output:

```text
run-001/results/regression-run.json
run-002/results/regression-run.json
run-003/results/regression-run.json
stability.json
```

`stability.json` reports:

- `pass@1` and unbiased `pass@k`;
- mean, median, min, max, and standard deviation;
- cases whose outcome or score changed across runs;
- deterministic verification counts;
- exact source paths for every repeated run.

For an existing set of repeated results:

```powershell
.\benchmark\.venv\Scripts\python.exe .\benchmark\regression\regression.py stability `
  --runs-dir .\benchmark\regression\artifacts\stability\orderdesk-ask-auto `
  --output .\benchmark\regression\artifacts\stability\orderdesk-ask-auto\stability.json
```
