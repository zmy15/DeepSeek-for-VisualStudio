from __future__ import annotations

import json
import math
import statistics
from collections import Counter
from pathlib import Path
from typing import Any, Iterable


class StabilityError(RuntimeError):
    """Raised when repeated runs cannot be compared."""


def read_json(path: Path) -> Any:
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except FileNotFoundError as exc:
        raise StabilityError(f"File not found: {path}") from exc
    except json.JSONDecodeError as exc:
        raise StabilityError(f"Invalid JSON in {path}: {exc}") from exc


def write_json(path: Path, value: Any) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(
        json.dumps(value, ensure_ascii=False, indent=2) + "\n",
        encoding="utf-8",
        newline="\n",
    )


def discover_runs(directory: Path) -> list[Path]:
    return sorted(directory.glob("run-*/results/regression-run.json"))


def load_runs(paths: Iterable[Path]) -> list[dict[str, Any]]:
    runs = []
    for path in paths:
        value = read_json(path)
        if value.get("schema") != "deepseek-vs.regression-run/1":
            raise StabilityError(f"{path} is not a regression-run/1 document")
        value["_source_path"] = str(path.resolve())
        runs.append(value)
    if not runs:
        raise StabilityError("At least one regression run is required.")
    return runs


def summarize(runs: list[dict[str, Any]]) -> dict[str, Any]:
    _validate_compatible(runs)
    first = runs[0]
    case_ids = [str(case.get("id")) for case in first.get("cases") or []]
    case_runs: dict[str, list[dict[str, Any]]] = {case_id: [] for case_id in case_ids}
    for run in runs:
        for case in run.get("cases") or []:
            case_runs[str(case.get("id"))].append(case)

    case_reports = [
        _case_stability(case_id, case_runs[case_id], len(runs))
        for case_id in case_ids
    ]
    suite_scores = [
        float(run["summary"]["average_score"])
        for run in runs
        if isinstance((run.get("summary") or {}).get("average_score"), (int, float))
    ]
    per_case_pass_at_1 = [
        float(case["pass_at_1"])
        for case in case_reports
        if case.get("pass_at_1") is not None
    ]
    per_case_pass_at_k = [
        value
        for case in case_reports
        for k, value in (case.get("pass_at_k") or {}).items()
        if k == str(len(runs)) and value is not None
    ]
    verification_counts = Counter(
        str(case.get("verification_status") or "not_run")
        for case in case_reports
        for _ in range(int(case.get("runs") or 0))
    )
    return {
        "schema": "deepseek-vs.stability-report/1",
        "suite_id": first.get("suite_id"),
        "suite_version": first.get("suite_version"),
        "run_count": len(runs),
        "agent_models": first.get("agent_models") or [],
        "judge_models": first.get("judge_models") or [],
        "run_sources": [run.get("_source_path") for run in runs],
        "summary": {
            "pass_at_1": _round(
                statistics.fmean(per_case_pass_at_1)
                if per_case_pass_at_1
                else None
            ),
            "pass_at_k": _round(
                statistics.fmean(per_case_pass_at_k)
                if per_case_pass_at_k
                else None
            ),
            "k": len(runs),
            "suite_average_score": _stats(suite_scores),
            "cases_with_outcome_variance": sum(
                bool(case["outcome_variance"]) for case in case_reports
            ),
            "cases_with_score_variance": sum(
                bool(case["score_variance"]) for case in case_reports
            ),
            "verification_counts": {
                status: verification_counts.get(status, 0)
                for status in ("pass", "fail", "not_available", "not_run")
            },
        },
        "cases": case_reports,
    }


def _case_stability(
    case_id: str,
    cases: list[dict[str, Any]],
    run_count: int,
) -> dict[str, Any]:
    outcomes = [
        str(case.get("outcome") or "fail")
        for case in cases
    ]
    scores = [
        float(case["score"])
        for case in cases
        if isinstance(case.get("score"), (int, float))
    ]
    pass_count = outcomes.count("pass")
    outcome_counts = Counter(outcomes)
    verification_statuses = [
        str(case.get("verification_status") or "not_run")
        for case in cases
    ]
    return {
        "case_id": case_id,
        "dimension": cases[0].get("dimension") if cases else None,
        "runs": run_count,
        "outcome_counts": {
            item: outcome_counts.get(item, 0)
            for item in ("pass", "partial", "fail")
        },
        "pass_at_1": _round(pass_count / run_count if run_count else None),
        "pass_at_k": {
            str(k): _round(_pass_at_k(run_count, pass_count, k))
            for k in range(1, run_count + 1)
        },
        "score": _stats(scores),
        "outcome_variance": len(set(outcomes)) > 1,
        "score_variance": len(set(round(value, 4) for value in scores)) > 1,
        "verification_counts": {
            status: verification_statuses.count(status)
            for status in ("pass", "fail", "not_available", "not_run")
        },
    }


def _pass_at_k(run_count: int, pass_count: int, k: int) -> float | None:
    if k < 1 or k > run_count:
        return None
    if pass_count == 0:
        return 0.0
    if run_count - pass_count < k:
        return 1.0
    return 1.0 - (
        math.comb(run_count - pass_count, k) / math.comb(run_count, k)
    )


def _stats(values: list[float]) -> dict[str, float | None]:
    if not values:
        return {
            "count": 0,
            "mean": None,
            "median": None,
            "min": None,
            "max": None,
            "stdev": None,
        }
    return {
        "count": len(values),
        "mean": _round(statistics.fmean(values)),
        "median": _round(statistics.median(values)),
        "min": _round(min(values)),
        "max": _round(max(values)),
        "stdev": _round(
            statistics.stdev(values) if len(values) > 1 else 0.0
        ),
    }


def _validate_compatible(runs: list[dict[str, Any]]) -> None:
    first = runs[0]
    for run in runs[1:]:
        for key in ("suite_id", "suite_version", "agent_models", "judge_models"):
            if run.get(key) != first.get(key):
                raise StabilityError(
                    f"{key} differs across repeated runs: "
                    f"{first.get(key)!r} != {run.get(key)!r}"
                )
    case_sets = [
        {str(case.get("id")) for case in run.get("cases") or []}
        for run in runs
    ]
    if any(case_set != case_sets[0] for case_set in case_sets[1:]):
        raise StabilityError("Repeated runs contain different case ids.")


def _round(value: float | None) -> float | None:
    return round(value, 4) if value is not None else None
