from __future__ import annotations

import json
from collections import Counter
from pathlib import Path
from typing import Any


class HarnessCompareError(RuntimeError):
    """Raised when harness runs cannot be compared."""


def read_json(path: Path) -> Any:
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except FileNotFoundError as exc:
        raise HarnessCompareError(f"File not found: {path}") from exc
    except json.JSONDecodeError as exc:
        raise HarnessCompareError(f"Invalid JSON in {path}: {exc}") from exc


def write_json(path: Path, value: Any) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(
        json.dumps(value, ensure_ascii=False, indent=2) + "\n",
        encoding="utf-8",
        newline="\n",
    )


def parse_run_specs(values: list[str]) -> list[tuple[str, Path]]:
    specs: list[tuple[str, Path]] = []
    seen: set[str] = set()
    for value in values:
        if "=" not in value:
            raise HarnessCompareError(
                f"Run spec must be label=path, got: {value}"
            )
        label, path_text = value.split("=", 1)
        label = label.strip()
        if not label or label in seen:
            raise HarnessCompareError(f"Invalid or duplicate run label: {label}")
        seen.add(label)
        specs.append((label, Path(path_text)))
    if not specs:
        raise HarnessCompareError("At least one --run label=path is required.")
    return specs


def compare_harness_runs(
    specs: list[tuple[str, Path]],
    baseline_label: str | None = None,
) -> dict[str, Any]:
    runs: dict[str, dict[str, Any]] = {}
    for label, path in specs:
        run = read_json(path)
        if run.get("schema") != "deepseek-vs.regression-run/1":
            raise HarnessCompareError(
                f"{label} is not a regression-run/1 document"
            )
        runs[label] = run

    first_label = specs[0][0]
    first = runs[first_label]
    for label, run in runs.items():
        for key in ("suite_id", "suite_version", "agent_models", "judge_models"):
            if run.get(key) != first.get(key):
                raise HarnessCompareError(
                    f"{key} differs for profile '{label}': "
                    f"{first.get(key)!r} != {run.get(key)!r}"
                )

    baseline = baseline_label or first_label
    if baseline not in runs:
        raise HarnessCompareError(f"Unknown baseline profile: {baseline}")

    case_ids = [str(case.get("id")) for case in first.get("cases") or []]
    case_maps = {
        label: {
            str(case.get("id")): case
            for case in run.get("cases") or []
        }
        for label, run in runs.items()
    }
    for label, mapping in case_maps.items():
        if set(mapping) != set(case_ids):
            raise HarnessCompareError(
                f"Profile '{label}' contains different case ids."
            )

    profile_summaries = {
        label: _profile_summary(run)
        for label, run in runs.items()
    }
    ranking = sorted(
        runs,
        key=lambda label: (
            -(profile_summaries[label].get("average_score") or 0.0),
            -profile_summaries[label].get("pass_count", 0),
            profile_summaries[label].get("critical_failure_count", 0),
        ),
    )
    cases = []
    for case_id in case_ids:
        baseline_case = case_maps[baseline][case_id]
        baseline_score = _number(baseline_case.get("score"))
        entry: dict[str, Any] = {
            "case_id": case_id,
            "dimension": baseline_case.get("dimension"),
            "profiles": {},
        }
        for label in runs:
            case = case_maps[label][case_id]
            score = _number(case.get("score"))
            entry["profiles"][label] = {
                "outcome": case.get("outcome"),
                "score": score,
                "verification_status": case.get("verification_status"),
                "critical_failures": case.get("critical_failures") or [],
                "profile": (case.get("harness_profile") or {}).get("id"),
            }
            if score is not None and baseline_score is not None:
                entry["profiles"][label]["score_delta_vs_baseline"] = round(
                    score - baseline_score, 4
                )
        cases.append(entry)

    return {
        "schema": "deepseek-vs.harness-comparison/1",
        "suite_id": first.get("suite_id"),
        "suite_version": first.get("suite_version"),
        "agent_models": first.get("agent_models") or [],
        "judge_models": first.get("judge_models") or [],
        "baseline": baseline,
        "profiles": profile_summaries,
        "ranking": ranking,
        "cases": cases,
    }


def _profile_summary(run: dict[str, Any]) -> dict[str, Any]:
    cases = list(run.get("cases") or [])
    outcomes = Counter(str(case.get("outcome") or "fail") for case in cases)
    verification = Counter(
        str(case.get("verification_status") or "not_run") for case in cases
    )
    profile_ids = Counter(
        str((case.get("harness_profile") or {}).get("id") or "unknown")
        for case in cases
    )
    summary = run.get("summary") or {}
    return {
        "profile_ids": sorted(profile_ids),
        "case_count": len(cases),
        "average_score": summary.get("average_score"),
        "pass_count": outcomes.get("pass", 0),
        "partial_count": outcomes.get("partial", 0),
        "fail_count": outcomes.get("fail", 0),
        "critical_failure_count": summary.get("critical_failure_count", 0),
        "verification_counts": {
            status: verification.get(status, 0)
            for status in ("pass", "fail", "not_available", "not_run")
        },
        "agent_usage": summary.get("agent_usage") or {},
        "judge_usage": summary.get("judge_usage") or {},
    }


def _number(value: Any) -> float | None:
    return float(value) if isinstance(value, (int, float)) else None
