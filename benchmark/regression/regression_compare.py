"""Baseline/candidate regression comparison and gate evaluation."""

from __future__ import annotations

import argparse
import json
from pathlib import Path
from typing import Any

from regression_common import (
    DIMENSIONS,
    OUTCOME_RANK,
    RegressionError,
    read_json,
    write_json,
)


def _as_score(value: Any) -> float | None:
    return float(value) if isinstance(value, (int, float)) else None


def compare_runs(
    baseline: dict[str, Any],
    candidate: dict[str, Any],
    max_score_drop: float = 0.1,
    max_dimension_drop: float = 0.1,
) -> dict[str, Any]:
    for label, run in (("baseline", baseline), ("candidate", candidate)):
        if run.get("schema") != "deepseek-vs.regression-run/1":
            raise RegressionError(f"{label} is not a regression-run/1 document")
    for key in ("suite_id", "suite_version"):
        if baseline.get(key) != candidate.get(key):
            raise RegressionError(
                f"{key} mismatch: {baseline.get(key)!r} != {candidate.get(key)!r}"
            )
    if baseline.get("agent_models") != candidate.get("agent_models"):
        raise RegressionError("Agent models differ between baseline and candidate")
    if baseline.get("judge_models") != candidate.get("judge_models"):
        raise RegressionError("Judge models differ between baseline and candidate")

    baseline_cases = {
        str(case.get("id")): case for case in baseline.get("cases") or []
    }
    candidate_cases = {
        str(case.get("id")): case for case in candidate.get("cases") or []
    }
    if set(baseline_cases) != set(candidate_cases):
        raise RegressionError("Baseline and candidate contain different case ids")

    failures: list[dict[str, Any]] = []
    comparisons: list[dict[str, Any]] = []
    for case_id in baseline_cases:
        before = baseline_cases[case_id]
        after = candidate_cases[case_id]
        before_outcome = str(before.get("outcome") or "fail")
        after_outcome = str(after.get("outcome") or "fail")
        before_rank = OUTCOME_RANK.get(before_outcome, 0)
        after_rank = OUTCOME_RANK.get(after_outcome, 0)
        if after_rank < before_rank:
            failures.append(
                {
                    "scope": "case",
                    "case_id": case_id,
                    "rule": "outcome_regression",
                    "baseline": before_outcome,
                    "candidate": after_outcome,
                }
            )

        before_score = _as_score(before.get("score"))
        after_score = _as_score(after.get("score"))
        score_delta = (
            round(after_score - before_score, 4)
            if before_score is not None and after_score is not None
            else None
        )
        if score_delta is not None and score_delta < -max_score_drop:
            failures.append(
                {
                    "scope": "case",
                    "case_id": case_id,
                    "rule": "score_regression",
                    "baseline": before_score,
                    "candidate": after_score,
                    "delta": score_delta,
                    "allowed_drop": max_score_drop,
                }
            )

        new_critical = sorted(
            set(after.get("critical_failures") or [])
            - set(before.get("critical_failures") or [])
        )
        if new_critical:
            failures.append(
                {
                    "scope": "case",
                    "case_id": case_id,
                    "rule": "new_critical_failure",
                    "failures": new_critical,
                }
            )

        before_verification = str(before.get("verification_status") or "not_run")
        after_verification = str(after.get("verification_status") or "not_run")
        if (
            before_verification == "pass"
            and after_verification != "pass"
        ) or (
            after_verification == "fail"
            and before_verification != "fail"
        ):
            failures.append(
                {
                    "scope": "case",
                    "case_id": case_id,
                    "rule": "deterministic_verification_regression",
                    "baseline": before_verification,
                    "candidate": after_verification,
                }
            )

        dimension_deltas: dict[str, float | None] = {}
        for dimension in DIMENSIONS:
            before_dimension = _as_score(
                (before.get("dimension_scores") or {}).get(dimension)
            )
            after_dimension = _as_score(
                (after.get("dimension_scores") or {}).get(dimension)
            )
            delta = (
                round(after_dimension - before_dimension, 4)
                if before_dimension is not None and after_dimension is not None
                else None
            )
            dimension_deltas[dimension] = delta
            if delta is not None and delta < -max_dimension_drop:
                failures.append(
                    {
                        "scope": "dimension",
                        "case_id": case_id,
                        "dimension": dimension,
                        "rule": "dimension_regression",
                        "baseline": before_dimension,
                        "candidate": after_dimension,
                        "delta": delta,
                        "allowed_drop": max_dimension_drop,
                    }
                )

        before_rounds = {
            int(item.get("turn") or index): item
            for index, item in enumerate(before.get("rounds") or [], start=1)
        }
        after_rounds = {
            int(item.get("turn") or index): item
            for index, item in enumerate(after.get("rounds") or [], start=1)
        }
        round_deltas: dict[str, float | None] = {}
        for turn in sorted(set(before_rounds) | set(after_rounds)):
            before_round = before_rounds.get(turn) or {}
            after_round = after_rounds.get(turn) or {}
            before_round_outcome = str(before_round.get("outcome") or "fail")
            after_round_outcome = str(after_round.get("outcome") or "fail")
            if OUTCOME_RANK.get(after_round_outcome, 0) < OUTCOME_RANK.get(
                before_round_outcome, 0
            ):
                failures.append(
                    {
                        "scope": "turn",
                        "case_id": case_id,
                        "turn": turn,
                        "rule": "turn_outcome_regression",
                        "baseline": before_round_outcome,
                        "candidate": after_round_outcome,
                    }
                )
            before_round_score = _as_score(before_round.get("score"))
            after_round_score = _as_score(after_round.get("score"))
            round_delta = (
                round(after_round_score - before_round_score, 4)
                if before_round_score is not None and after_round_score is not None
                else None
            )
            round_deltas[str(turn)] = round_delta
            if round_delta is not None and round_delta < -max_score_drop:
                failures.append(
                    {
                        "scope": "turn",
                        "case_id": case_id,
                        "turn": turn,
                        "rule": "turn_score_regression",
                        "baseline": before_round_score,
                        "candidate": after_round_score,
                        "delta": round_delta,
                        "allowed_drop": max_score_drop,
                    }
                )

        comparisons.append(
            {
                "case_id": case_id,
                "dimension": before.get("dimension"),
                "baseline_outcome": before_outcome,
                "candidate_outcome": after_outcome,
                "baseline_verification": before_verification,
                "candidate_verification": after_verification,
                "score_delta": score_delta,
                "dimension_deltas": dimension_deltas,
                "turn_deltas": round_deltas,
            }
        )

    baseline_summary = baseline.get("summary") or {}
    candidate_summary = candidate.get("summary") or {}
    baseline_pass = int(
        (baseline_summary.get("outcome_counts") or {}).get("pass") or 0
    )
    candidate_pass = int(
        (candidate_summary.get("outcome_counts") or {}).get("pass") or 0
    )
    if candidate_pass < baseline_pass:
        failures.append(
            {
                "scope": "suite",
                "rule": "pass_count_regression",
                "baseline": baseline_pass,
                "candidate": candidate_pass,
            }
        )

    baseline_average = _as_score(baseline_summary.get("average_score"))
    candidate_average = _as_score(candidate_summary.get("average_score"))
    average_delta = (
        round(candidate_average - baseline_average, 4)
        if baseline_average is not None and candidate_average is not None
        else None
    )
    if average_delta is not None and average_delta < -max_score_drop:
        failures.append(
            {
                "scope": "suite",
                "rule": "average_score_regression",
                "baseline": baseline_average,
                "candidate": candidate_average,
                "delta": average_delta,
                "allowed_drop": max_score_drop,
            }
        )

    dimension_average_deltas: dict[str, float | None] = {}
    baseline_dimensions = baseline_summary.get("dimension_averages") or {}
    candidate_dimensions = candidate_summary.get("dimension_averages") or {}
    for dimension in DIMENSIONS:
        before = _as_score(baseline_dimensions.get(dimension))
        after = _as_score(candidate_dimensions.get(dimension))
        delta = (
            round(after - before, 4)
            if before is not None and after is not None
            else None
        )
        dimension_average_deltas[dimension] = delta
        if delta is not None and delta < -max_dimension_drop:
            failures.append(
                {
                    "scope": "suite",
                    "dimension": dimension,
                    "rule": "average_dimension_regression",
                    "baseline": before,
                    "candidate": after,
                    "delta": delta,
                    "allowed_drop": max_dimension_drop,
                }
            )

    baseline_verification = baseline_summary.get("verification_counts") or {}
    candidate_verification = candidate_summary.get("verification_counts") or {}
    if int(candidate_verification.get("fail") or 0) > int(
        baseline_verification.get("fail") or 0
    ):
        failures.append(
            {
                "scope": "suite",
                "rule": "deterministic_verification_failures_increased",
                "baseline": baseline_verification,
                "candidate": candidate_verification,
            }
        )

    return {
        "schema": "deepseek-vs.regression-comparison/1",
        "suite_id": baseline.get("suite_id"),
        "suite_version": baseline.get("suite_version"),
        "policy": {
            "max_score_drop": max_score_drop,
            "max_dimension_drop": max_dimension_drop,
            "outcome_must_not_decrease": True,
            "critical_failures_must_not_increase": True,
            "deterministic_verification_must_not_regress": True,
        },
        "regression": bool(failures),
        "failure_count": len(failures),
        "failures": failures,
        "summary": {
            "baseline": baseline_summary,
            "candidate": candidate_summary,
            "average_score_delta": average_delta,
            "dimension_average_deltas": dimension_average_deltas,
        },
        "cases": comparisons,
    }


def run_compare(args: argparse.Namespace) -> int:
    report = compare_runs(
        read_json(Path(args.baseline)),
        read_json(Path(args.candidate)),
        max_score_drop=args.max_score_drop,
        max_dimension_drop=args.max_dimension_drop,
    )
    if args.output:
        write_json(Path(args.output), report)

    print(f"Regression: {'YES' if report['regression'] else 'NO'}")
    print(f"Failures: {report['failure_count']}")
    average_delta = report["summary"]["average_score_delta"]
    print(f"Average score delta: {average_delta if average_delta is not None else 'n/a'}")
    for failure in report["failures"]:
        print(f"  - {json.dumps(failure, ensure_ascii=False)}")
    if args.output:
        print(f"Output: {Path(args.output).resolve()}")
    return 3 if report["regression"] else 0

