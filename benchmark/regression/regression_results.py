"""Case result extraction, aggregation, and regression-run collection."""

from __future__ import annotations

import argparse
from collections import Counter
from pathlib import Path
from typing import Any

import verification
from regression_common import (
    DIMENSIONS,
    RegressionError,
    read_json,
    write_json,
)
from regression_suite import _load_suite


def _session_score_map(session_result: dict[str, Any]) -> dict[str, int | None]:
    scores: dict[str, int | None] = {}
    for item in session_result.get("scores") or []:
        if not isinstance(item, dict):
            continue
        dimension = str(item.get("dimension") or "")
        value = item.get("score")
        scores[dimension] = int(value) if isinstance(value, (int, float)) else None
    return scores


def _critical_ids(session_result: dict[str, Any], rounds: list[dict[str, Any]]) -> list[str]:
    ids = {
        str(item.get("id") or "")
        for item in session_result.get("critical_failures") or []
        if isinstance(item, dict) and item.get("id")
    }
    for round_result in rounds:
        for item in (round_result.get("result") or {}).get("critical_failures") or []:
            if isinstance(item, dict) and item.get("id"):
                ids.add(str(item["id"]))
    return sorted(ids)


def _sum_usage(usages: list[dict[str, Any]]) -> dict[str, Any]:
    prompt = 0
    completion = 0
    hit = 0
    miss = 0
    for usage in usages:
        if not isinstance(usage, dict):
            continue
        prompt += int(usage.get("prompt_tokens") or 0)
        completion += int(usage.get("completion_tokens") or 0)
        raw_hit = int(usage.get("prompt_cache_hit_tokens") or 0)
        details = usage.get("prompt_tokens_details") or {}
        details_hit = (
            int(details.get("cached_tokens") or 0)
            if isinstance(details, dict)
            else 0
        )
        hit += raw_hit if raw_hit > 0 else details_hit
        miss += int(usage.get("prompt_cache_miss_tokens") or 0)
    total_cacheable = hit + miss
    return {
        "prompt_tokens": prompt,
        "completion_tokens": completion,
        "cache_hit_tokens": hit,
        "cache_miss_tokens": miss,
        "cache_hit_rate": round(hit / total_cacheable, 4) if total_cacheable else None,
    }


def extract_case_result(
    suite: dict[str, Any],
    case: dict[str, Any],
    result_doc: dict[str, Any],
    source_path: Path,
) -> dict[str, Any]:
    if result_doc.get("schema") != "deepseek-vs.session-judge-run/1":
        raise RegressionError(
            f"{source_path} is not a deepseek-vs.session-judge-run/1 result"
        )
    result_case_id = str(result_doc.get("case_id") or "").strip()
    if result_case_id != str(case["id"]):
        raise RegressionError(
            f"{source_path} has case_id '{result_case_id}', expected '{case['id']}'"
        )

    computed = result_doc.get("computed") or {}
    session_result = result_doc.get("session_result") or {}
    rounds = list(result_doc.get("rounds") or [])
    manifest = result_doc.get("manifest") or {}
    source = manifest.get("source") or {}
    judge = result_doc.get("judge") or {}
    telemetry = manifest.get("session_telemetry") or {}
    verification_path = source_path.with_name(
        source_path.name.replace(".judge.json", ".verification.json")
    )
    verification_result = (
        verification.read_json(verification_path)
        if verification_path.is_file()
        else None
    )

    round_results = []
    for index, item in enumerate(rounds, start=1):
        item_computed = item.get("computed") or {}
        round_results.append(
            {
                "turn": int(item.get("turn_index") or index),
                "outcome": str(item_computed.get("outcome") or "fail"),
                "score": item_computed.get("weighted_score"),
                "critical_failures": sorted(
                    {
                        str(failure.get("id") or "")
                        for failure in (item.get("result") or {}).get(
                            "critical_failures"
                        )
                        or []
                        if isinstance(failure, dict) and failure.get("id")
                    }
                ),
            }
        )

    outcome = str(computed.get("outcome") or "fail")
    critical_failures = _critical_ids(session_result, rounds)
    verification_status = (
        str(verification_result.get("status") or "unknown")
        if verification_result
        else "not_run"
    )
    if verification_status == "fail":
        outcome = "fail"
        critical_failures = sorted(
            set(critical_failures) | {"independent_verification_failure"}
        )

    return {
        "id": case["id"],
        "dimension": case["dimension"],
        "kind": case["kind"],
        "outcome": outcome,
        "judge_outcome": str(computed.get("outcome") or "fail"),
        "score": computed.get("weighted_score"),
        "dimension_scores": _session_score_map(session_result),
        "critical_failures": critical_failures,
        "verification_status": verification_status,
        "verification": verification_result,
        "harness_profile": manifest.get("harness_profile") or {},
        "execution_environment": manifest.get("execution_environment") or {},
        "user_simulation": manifest.get("user_simulation") or {},
        "rounds": round_results,
        "agent_model": source.get("model"),
        "judge_model": judge.get("model"),
        "agent_usage": {
            "prompt_tokens": int(telemetry.get("input_tokens") or 0),
            "completion_tokens": int(telemetry.get("output_tokens") or 0),
            "cache_hit_tokens": int(telemetry.get("cache_hit_tokens") or 0),
            "cache_miss_tokens": int(telemetry.get("cache_miss_tokens") or 0),
            "cache_hit_rate": telemetry.get("cache_hit_rate"),
            "duration_ms": telemetry.get("duration_ms"),
        },
        "judge_usage": _sum_usage(
            [
                item.get("judge", {}).get("usage") or {}
                for item in rounds
            ]
            + [judge.get("usage") or {}]
        ),
        "simulation_usage": telemetry.get("user_simulation_usage") or {},
        "source_result": str(source_path.resolve()),
    }


def _summary(cases: list[dict[str, Any]]) -> dict[str, Any]:
    outcomes = Counter(str(case.get("outcome") or "fail") for case in cases)
    weighted_scores = [
        float(case["score"])
        for case in cases
        if isinstance(case.get("score"), (int, float))
    ]
    dimension_values: dict[str, list[float]] = {dimension: [] for dimension in DIMENSIONS}
    session_dimension_values: dict[str, list[float]] = {}
    round_outcomes = Counter()
    verification_counts = Counter()
    for case in cases:
        case_dimension = str(case.get("dimension") or "")
        case_score = case.get("score")
        if case_dimension in dimension_values and isinstance(case_score, (int, float)):
            dimension_values[case_dimension].append(float(case_score))
        for dimension, score in (case.get("dimension_scores") or {}).items():
            if isinstance(score, (int, float)):
                session_dimension_values.setdefault(dimension, []).append(float(score))
        for round_result in case.get("rounds") or []:
            round_outcomes[str(round_result.get("outcome") or "fail")] += 1
        verification_counts[str(case.get("verification_status") or "not_run")] += 1

    dimension_averages = {}
    for dimension, values in dimension_values.items():
        dimension_averages[dimension] = (
            round(sum(values) / len(values), 4) if values else None
        )
    session_dimension_averages = {
        dimension: round(sum(values) / len(values), 4)
        for dimension, values in session_dimension_values.items()
        if values
    }
    agent_prompt = sum(
        int((case.get("agent_usage") or {}).get("prompt_tokens") or 0)
        for case in cases
    )
    agent_completion = sum(
        int((case.get("agent_usage") or {}).get("completion_tokens") or 0)
        for case in cases
    )
    agent_hit = sum(
        int((case.get("agent_usage") or {}).get("cache_hit_tokens") or 0)
        for case in cases
    )
    agent_miss = sum(
        int((case.get("agent_usage") or {}).get("cache_miss_tokens") or 0)
        for case in cases
    )
    judge_prompt = sum(
        int((case.get("judge_usage") or {}).get("prompt_tokens") or 0)
        for case in cases
    )
    judge_completion = sum(
        int((case.get("judge_usage") or {}).get("completion_tokens") or 0)
        for case in cases
    )
    judge_hit = sum(
        int((case.get("judge_usage") or {}).get("cache_hit_tokens") or 0)
        for case in cases
    )
    judge_miss = sum(
        int((case.get("judge_usage") or {}).get("cache_miss_tokens") or 0)
        for case in cases
    )
    simulation_prompt = sum(
        int((case.get("simulation_usage") or {}).get("prompt_tokens") or 0)
        for case in cases
    )
    simulation_completion = sum(
        int((case.get("simulation_usage") or {}).get("completion_tokens") or 0)
        for case in cases
    )

    return {
        "case_count": len(cases),
        "outcome_counts": {
            outcome: outcomes.get(outcome, 0)
            for outcome in ("pass", "partial", "fail")
        },
        "round_outcome_counts": {
            outcome: round_outcomes.get(outcome, 0)
            for outcome in ("pass", "partial", "fail")
        },
        "average_score": (
            round(sum(weighted_scores) / len(weighted_scores), 4)
            if weighted_scores
            else None
        ),
        "dimension_averages": dimension_averages,
        "session_dimension_averages": session_dimension_averages,
        "critical_failure_count": sum(
            len(case.get("critical_failures") or []) for case in cases
        ),
        "verification_counts": {
            status: verification_counts.get(status, 0)
            for status in ("pass", "fail", "not_available", "not_run", "unknown")
        },
        "agent_usage": {
            "prompt_tokens": agent_prompt,
            "completion_tokens": agent_completion,
            "cache_hit_tokens": agent_hit,
            "cache_miss_tokens": agent_miss,
            "cache_hit_rate": (
                round(agent_hit / (agent_hit + agent_miss), 4)
                if agent_hit + agent_miss
                else None
            ),
        },
        "judge_usage": {
            "prompt_tokens": judge_prompt,
            "completion_tokens": judge_completion,
            "cache_hit_tokens": judge_hit,
            "cache_miss_tokens": judge_miss,
            "cache_hit_rate": (
                round(judge_hit / (judge_hit + judge_miss), 4)
                if judge_hit + judge_miss
                else None
            ),
        },
        "simulation_usage": {
            "prompt_tokens": simulation_prompt,
            "completion_tokens": simulation_completion,
        },
    }


def collect_results(
    suite: dict[str, Any],
    results_dir: Path,
    allow_model_mismatch: bool = False,
) -> dict[str, Any]:
    defaults = suite.get("defaults") or {}
    missing: list[str] = []
    cases: list[dict[str, Any]] = []
    for case in suite.get("cases") or []:
        case_id = str(case["id"])
        result_path = results_dir / f"{case_id}.judge.json"
        if not result_path.is_file():
            missing.append(case_id)
            continue
        result_doc = read_json(result_path)
        if not isinstance(result_doc, dict):
            raise RegressionError(f"Result root must be an object: {result_path}")
        cases.append(
            extract_case_result(suite, case, result_doc, result_path)
        )

    if missing:
        raise RegressionError(
            "Missing case result files: " + ", ".join(missing)
        )

    agent_models = {
        str(case.get("agent_model") or "unknown") for case in cases
    }
    judge_models = {
        str(case.get("judge_model") or "unknown") for case in cases
    }
    if not allow_model_mismatch:
        expected_agent = str(defaults.get("agent_model"))
        expected_judge = str(defaults.get("judge_model"))
        if agent_models != {expected_agent}:
            raise RegressionError(
                f"Agent model mismatch: expected {expected_agent}, got "
                + ", ".join(sorted(agent_models))
            )
        if judge_models != {expected_judge}:
            raise RegressionError(
                f"Judge model mismatch: expected {expected_judge}, got "
                + ", ".join(sorted(judge_models))
            )

    return {
        "schema": "deepseek-vs.regression-run/1",
        "suite_id": suite["id"],
        "suite_version": suite.get("version"),
        "fixture": suite.get("fixture"),
        "agent_models": sorted(agent_models),
        "judge_models": sorted(judge_models),
        "cases": cases,
        "summary": _summary(cases),
    }


def run_collect(args: argparse.Namespace) -> int:
    suite = _load_suite(Path(args.suite))
    output = collect_results(
        suite,
        Path(args.results),
        allow_model_mismatch=args.allow_model_mismatch,
    )
    output_path = Path(args.output)
    write_json(output_path, output)

    summary = output["summary"]
    print(f"Collected {summary['case_count']} cases")
    print(f"Outcomes: {summary['outcome_counts']}")
    print(f"Average score: {summary['average_score']}")
    print(f"Output: {output_path.resolve()}")
    return 0

