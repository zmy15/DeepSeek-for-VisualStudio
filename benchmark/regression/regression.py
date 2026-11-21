#!/usr/bin/env python3
"""Fixture-based regression runner for exported Visual Studio Agent sessions."""

from __future__ import annotations

import argparse
from collections import Counter
import json
import shutil
import subprocess
import sys
from pathlib import Path
from typing import Any


HERE = Path(__file__).resolve().parent
DEFAULT_SUITE = HERE / "orderdesk.v1.json"
DEFAULT_REFERENCE_ANSWERS = HERE / "reference-answers.v1.json"
JUDGE_SCRIPT = HERE.parent / "judge" / "judge.py"

DEFAULT_AGENT_MODEL = "deepseek-flash"
DEFAULT_JUDGE_MODEL = "deepseek-flash"

DIMENSIONS = (
    "instruction_following",
    "context_grounding",
    "tool_use",
    "planning_execution",
    "code_change_quality",
    "safety_permission",
    "communication_ux",
    "efficiency_cost",
)

KINDS = ("regular", "boundary")
TAGS = ("regular", "boundary", "conditional", "read_only", "safety", "efficiency")
OUTCOME_RANK = {"fail": 0, "partial": 1, "pass": 2}


class RegressionError(RuntimeError):
    """Raised for predictable suite, result, or comparison failures."""


def read_json(path: Path) -> Any:
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except FileNotFoundError as exc:
        raise RegressionError(f"File not found: {path}") from exc
    except json.JSONDecodeError as exc:
        raise RegressionError(f"Invalid JSON in {path}: {exc}") from exc


def write_json(path: Path, value: Any) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(
        json.dumps(value, ensure_ascii=False, indent=2) + "\n",
        encoding="utf-8",
        newline="\n",
    )


def write_text(path: Path, value: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(value, encoding="utf-8", newline="\n")


def _resolve_inside(base: Path, relative: str) -> Path:
    candidate = (base / relative).resolve()
    try:
        candidate.relative_to(base.resolve())
    except ValueError as exc:
        raise RegressionError(
            f"Path escapes the suite directory: {relative}"
        ) from exc
    return candidate


def find_case(suite: dict[str, Any], case_id: str) -> dict[str, Any]:
    for case in suite.get("cases") or []:
        if str(case.get("id") or "") == case_id:
            return case
    available = ", ".join(
        str(case.get("id") or "") for case in suite.get("cases") or []
    )
    raise RegressionError(f"Unknown case '{case_id}'. Available cases: {available}")


def build_judge_case(
    suite: dict[str, Any],
    case: dict[str, Any],
    reference_answer: dict[str, Any] | None = None,
) -> dict[str, Any]:
    must = [str(item) for item in (case.get("checks") or {}).get("must") or []]
    must_not = [
        str(item) for item in (case.get("checks") or {}).get("must_not") or []
    ]
    prompts: list[str] = []
    turns: list[dict[str, Any]] = []
    for index, turn in enumerate(case.get("turns") or [], start=1):
        turn_number = int(turn.get("turn") or index)
        prompt = str(turn.get("prompt") or "").strip()
        prompts.append(f"Turn {turn_number}: {prompt}")
        checks = turn.get("checks") or {}
        must.extend(
            f"Turn {turn_number}: {item}" for item in checks.get("must") or []
        )
        must_not.extend(
            f"Turn {turn_number}: {item}" for item in checks.get("must_not") or []
        )
        turns.append(
            {
                "turn": turn_number,
                "user_prompt": prompt,
                "expected": turn.get("expected"),
                "checks": checks,
            }
        )

    return {
        "id": str(case["id"]),
        "dimension": "task_completion",
        "title": case.get("title"),
        "scenario": case.get("scenario"),
        "setup": (
            f"Use the isolated {suite.get('fixture')} workspace. "
            f"Verification commands: {', '.join(case.get('verification') or [])}"
        ),
        "user_prompt": "\n\n".join(prompts),
        "requires_workspace": True,
        "checks": {
            "must": must,
            "must_not": must_not,
        },
        "turns": turns,
        "reference_answer": reference_answer,
        "regression_suite": {
            "id": suite.get("id"),
            "version": suite.get("version"),
            "kind": case.get("kind"),
            "tags": case.get("tags") or [],
            "verification": case.get("verification") or [],
        },
    }


def validate_reference_answers(
    suite: dict[str, Any],
    reference_doc: dict[str, Any],
) -> list[str]:
    errors: list[str] = []
    if reference_doc.get("schema") != "deepseek-vs.reference-answers/1":
        errors.append("reference answers schema must be 'deepseek-vs.reference-answers/1'")
    if reference_doc.get("schema_version") != 1:
        errors.append("reference answers schema_version must be 1")
    if reference_doc.get("suite_id") != suite.get("id"):
        errors.append("reference answers suite_id must match the suite id")

    case_ids = {str(case.get("id")) for case in suite.get("cases") or []}
    seen: set[str] = set()
    for index, answer in enumerate(reference_doc.get("answers") or []):
        prefix = f"answers[{index}]"
        if not isinstance(answer, dict):
            errors.append(f"{prefix} must be an object")
            continue
        case_id = str(answer.get("case_id") or "").strip()
        if case_id not in case_ids:
            errors.append(f"{prefix} references unknown case '{case_id}'")
        elif case_id in seen:
            errors.append(f"duplicate reference answer for '{case_id}'")
        else:
            seen.add(case_id)
        if not str(answer.get("golden_answer") or "").strip():
            errors.append(f"{case_id or prefix} has an empty golden_answer")
        if not answer.get("required_facts"):
            errors.append(f"{case_id or prefix} has no required_facts")
        if not answer.get("forbidden_changes"):
            errors.append(f"{case_id or prefix} has no forbidden_changes")
        if not answer.get("verification"):
            errors.append(f"{case_id or prefix} has no verification commands")

    missing = sorted(case_ids - seen)
    if missing:
        errors.append("missing reference answers: " + ", ".join(missing))
    if not has_reference_criteria(reference_doc):
        errors.append("reference answers must define judging criteria")
    return errors


def has_reference_criteria(reference_doc: dict[str, Any]) -> bool:
    criteria = reference_doc.get("criteria") or {}
    return all(criteria.get(key) for key in ("deterministic", "semantic", "hard_failures"))


def load_reference_answers(
    suite: dict[str, Any],
    path: Path,
) -> tuple[dict[str, Any], dict[str, dict[str, Any]]]:
    reference_doc = read_json(path)
    if not isinstance(reference_doc, dict):
        raise RegressionError("Reference answers root must be a JSON object")
    errors = validate_reference_answers(suite, reference_doc)
    if errors:
        raise RegressionError("Invalid reference answers: " + "; ".join(errors))
    answers = {
        str(answer["case_id"]): answer
        for answer in reference_doc.get("answers") or []
    }
    return reference_doc, answers


def resolve_reference_answers_path(
    args: argparse.Namespace,
    suite: dict[str, Any],
    suite_path: Path,
) -> Path:
    cli_value = getattr(args, "reference_answers", None)
    if cli_value:
        return Path(cli_value)
    suite_value = str(suite.get("reference_answers") or "").strip()
    if suite_value:
        return _resolve_inside(suite_path.parent, suite_value)
    return DEFAULT_REFERENCE_ANSWERS


def validate_suite(
    suite: dict[str, Any],
    base_dir: Path = HERE,
) -> list[str]:
    errors: list[str] = []

    if suite.get("schema") != "deepseek-vs.regression-suite/1":
        errors.append("suite schema must be 'deepseek-vs.regression-suite/1'")
    if suite.get("schema_version") != 1:
        errors.append("suite schema_version must be 1")
    if not str(suite.get("id") or "").strip():
        errors.append("suite id is required")

    defaults = suite.get("defaults") or {}
    if defaults.get("agent_model") != DEFAULT_AGENT_MODEL:
        errors.append(f"defaults.agent_model must be '{DEFAULT_AGENT_MODEL}'")
    if defaults.get("judge_model") != DEFAULT_JUDGE_MODEL:
        errors.append(f"defaults.judge_model must be '{DEFAULT_JUDGE_MODEL}'")

    fixture = str(suite.get("fixture") or "").strip()
    solution = str(suite.get("solution") or "").strip()
    if not fixture:
        errors.append("fixture is required")
    if not solution:
        errors.append("solution is required")
    if fixture and solution:
        try:
            fixture_dir = _resolve_inside(base_dir, fixture)
            solution_path = _resolve_inside(fixture_dir, solution)
            if not fixture_dir.is_dir():
                errors.append(f"fixture directory does not exist: {fixture}")
            if not solution_path.is_file():
                errors.append(f"fixture solution does not exist: {solution}")
        except RegressionError as exc:
            errors.append(str(exc))

    cases = list(suite.get("cases") or [])
    if not cases:
        errors.append("suite must define at least one case")

    seen_ids: set[str] = set()
    policy = suite.get("coverage_policy") or {}
    minimum_cases = int(policy.get("minimum_cases_per_dimension", 2))
    require_per_dimension = bool(
        policy.get("require_regular_and_boundary_per_dimension", True)
    )
    require_suite_coverage = bool(
        policy.get("require_regular_and_boundary_in_suite", True)
    )
    require_conditional = bool(policy.get("require_conditional", True))

    coverage = {
        dimension: {"regular": 0, "boundary": 0}
        for dimension in DIMENSIONS
    }
    conditional_count = 0

    for index, case in enumerate(cases):
        prefix = f"cases[{index}]"
        case_id = str(case.get("id") or "").strip()
        if not case_id:
            errors.append(f"{prefix} has an empty id")
        elif case_id in seen_ids:
            errors.append(f"duplicate case id '{case_id}'")
        else:
            seen_ids.add(case_id)

        dimension = str(case.get("dimension") or "").strip()
        if dimension not in DIMENSIONS:
            errors.append(f"{case_id or prefix} uses unknown dimension '{dimension}'")

        kind = str(case.get("kind") or "").strip()
        if kind not in KINDS:
            errors.append(
                f"{case_id or prefix} kind must be one of: {', '.join(KINDS)}"
            )
        elif dimension in coverage:
            coverage[dimension][kind] += 1

        tags = [str(tag) for tag in case.get("tags") or []]
        unknown_tags = sorted(set(tags) - set(TAGS))
        if unknown_tags:
            errors.append(
                f"{case_id or prefix} has unknown tags: {', '.join(unknown_tags)}"
            )
        if "conditional" in tags:
            conditional_count += 1

        turns = list(case.get("turns") or [])
        if not turns:
            errors.append(f"{case_id or prefix} must define at least one turn")
        seen_turn_numbers: set[int] = set()
        for turn_index, turn in enumerate(turns, start=1):
            turn_number = int(turn.get("turn") or turn_index)
            if turn_number in seen_turn_numbers:
                errors.append(
                    f"{case_id or prefix} has duplicate turn number {turn_number}"
                )
            seen_turn_numbers.add(turn_number)
            if not str(turn.get("prompt") or "").strip():
                errors.append(
                    f"{case_id or prefix} turn {turn_number} has an empty prompt"
                )
            checks = turn.get("checks") or {}
            if not checks.get("must") and not checks.get("must_not"):
                errors.append(
                    f"{case_id or prefix} turn {turn_number} has no checks"
                )

        checks = case.get("checks") or {}
        if not checks.get("must") and not checks.get("must_not"):
            errors.append(f"{case_id or prefix} has no case-level checks")
        if not case.get("verification"):
            errors.append(f"{case_id or prefix} has no verification commands")

    for dimension in DIMENSIONS:
        regular = coverage[dimension]["regular"]
        boundary = coverage[dimension]["boundary"]
        if require_per_dimension and regular < 1:
            errors.append(f"dimension '{dimension}' needs at least one regular case")
        if require_per_dimension and boundary < 1:
            errors.append(f"dimension '{dimension}' needs at least one boundary case")
        if regular + boundary < minimum_cases:
            errors.append(
                f"dimension '{dimension}' needs at least {minimum_cases} cases"
            )

    if require_suite_coverage and (
        sum(item["regular"] for item in coverage.values()) < 1
        or sum(item["boundary"] for item in coverage.values()) < 1
    ):
        errors.append("suite must include at least one regular and one boundary case")
    if require_conditional and conditional_count < 1:
        errors.append("suite must include at least one case tagged 'conditional'")

    return errors


def _load_suite(path: Path) -> dict[str, Any]:
    suite = read_json(path)
    if not isinstance(suite, dict):
        raise RegressionError("Suite root must be a JSON object")
    errors = validate_suite(suite, path.parent)
    if errors:
        raise RegressionError("Invalid regression suite: " + "; ".join(errors))
    return suite


def render_case_guide(
    suite: dict[str, Any],
    case: dict[str, Any],
    output_dir: Path,
    reference_answer: dict[str, Any] | None = None,
) -> str:
    defaults = suite.get("defaults") or {}
    lines = [
        "# Agent Regression Case",
        "",
        f"- Suite: `{suite.get('id')}`",
        f"- Case: `{case.get('id')}`",
        f"- Dimension: `{case.get('dimension')}`",
        f"- Kind: `{case.get('kind')}`",
        f"- Agent model: `{defaults.get('agent_model')}`",
        f"- Judge model: `{defaults.get('judge_model')}`",
        f"- Workspace: `{output_dir}`",
        "",
        "## Scenario",
        "",
        str(case.get("scenario") or ""),
        "",
        "## Turns",
        "",
    ]
    for index, turn in enumerate(case.get("turns") or [], start=1):
        turn_number = int(turn.get("turn") or index)
        lines.extend(
            [
                f"### Turn {turn_number}",
                "",
                str(turn.get("prompt") or "").strip(),
                "",
                "Expected signal:",
                "",
                str(turn.get("expected") or "").strip(),
                "",
            ]
        )

    lines.extend(["## Case Checks", "", "Must:", ""])
    lines.extend(
        f"- {item}" for item in (case.get("checks") or {}).get("must") or []
    )
    lines.extend(["", "Must not:", ""])
    lines.extend(
        f"- {item}" for item in (case.get("checks") or {}).get("must_not") or []
    )
    lines.extend(["", "## Verification", ""])
    for command in case.get("verification") or []:
        lines.extend(["```powershell", str(command), "```", ""])

    if reference_answer:
        lines.extend(["## Reference Answer", "", str(reference_answer["golden_answer"]), ""])
        if reference_answer.get("reference_patch"):
            lines.extend(
                ["```csharp", str(reference_answer["reference_patch"]), "```", ""]
            )
        lines.extend(["Required facts:", ""])
        lines.extend(f"- {item}" for item in reference_answer["required_facts"])
        lines.extend(["", "Forbidden changes:", ""])
        lines.extend(
            f"- {item}" for item in reference_answer["forbidden_changes"]
        )
        lines.append("")

    lines.extend(
        [
            "## Export",
            "",
            "Run every turn above in one Agent session, then export the complete",
            f"session ZIP as `{case.get('id')}.zip`.",
            "",
        ]
    )
    return "\n".join(lines)


def run_prepare(args: argparse.Namespace) -> int:
    suite_path = Path(args.suite)
    suite = _load_suite(suite_path)
    reference_path = resolve_reference_answers_path(args, suite, suite_path)
    _, references = load_reference_answers(suite, reference_path)
    case = find_case(suite, args.case)

    fixture_dir = _resolve_inside(
        suite_path.parent,
        str(suite["fixture"]),
    )
    output_dir = Path(args.output).resolve()
    if output_dir.exists() and any(output_dir.iterdir()):
        raise RegressionError(f"Output directory is not empty: {output_dir}")

    shutil.copytree(
        fixture_dir,
        output_dir,
        dirs_exist_ok=True,
        ignore=shutil.ignore_patterns("bin", "obj", ".vs", "TestResults", "build"),
    )
    metadata_dir = (
        Path(args.metadata_dir).resolve()
        if getattr(args, "metadata_dir", None)
        else output_dir.parent / f"{output_dir.name}.benchmark-meta"
    )
    write_json(
        metadata_dir / "case.json",
        {
            "schema": "deepseek-vs.regression-case-instance/1",
            "suite_id": suite["id"],
            "suite_version": suite.get("version"),
            "case": case,
        },
    )
    guide = render_case_guide(
        suite,
        case,
        output_dir,
        references.get(str(case["id"])),
    )
    write_text(metadata_dir / "README.md", guide)

    print(f"Prepared case: {case['id']}")
    print(f"Workspace: {output_dir}")
    print(f"Solution: {output_dir / str(suite['solution'])}")
    print(f"Prompts: {metadata_dir / 'README.md'}")
    return 0


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

    return {
        "id": case["id"],
        "dimension": case["dimension"],
        "kind": case["kind"],
        "outcome": str(computed.get("outcome") or "fail"),
        "score": computed.get("weighted_score"),
        "dimension_scores": _session_score_map(session_result),
        "critical_failures": _critical_ids(session_result, rounds),
        "rounds": round_results,
        "agent_model": source.get("model"),
        "judge_model": judge.get("model"),
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


def _judge_command(
    args: argparse.Namespace,
    archive: Path,
    output: Path,
    case_path: Path,
) -> list[str]:
    command = [
        sys.executable,
        str(JUDGE_SCRIPT),
        "session",
        "--archive",
        str(archive),
        "--output",
        str(output),
        "--case-file",
        str(case_path),
        "--model",
        args.model,
        "--parallel",
        str(args.parallel),
    ]
    if args.base_url:
        command.extend(["--base-url", args.base_url])
    if args.api_key_env:
        command.extend(["--api-key-env", args.api_key_env])
    if args.no_json_mode:
        command.append("--no-json-mode")
    if args.verbose:
        command.append("--verbose")
    if args.dry_run:
        command.append("--dry-run")
    return command


def run_score(args: argparse.Namespace) -> int:
    suite_path = Path(args.suite)
    suite = _load_suite(suite_path)
    reference_path = resolve_reference_answers_path(args, suite, suite_path)
    _, references = load_reference_answers(suite, reference_path)
    archives_dir = Path(args.archives)
    results_dir = Path(args.results)
    results_dir.mkdir(parents=True, exist_ok=True)

    missing = [
        str(case["id"])
        for case in suite.get("cases") or []
        if not (archives_dir / f"{case['id']}.zip").is_file()
    ]
    if missing:
        raise RegressionError("Missing session archives: " + ", ".join(missing))

    for case in suite.get("cases") or []:
        case_id = str(case["id"])
        archive = archives_dir / f"{case_id}.zip"
        output = results_dir / f"{case_id}.judge.json"
        if getattr(args, "skip_existing", False) and output.is_file() and not args.dry_run:
            print(f"Skipping {case_id} (result exists)")
            continue
        case_path = results_dir / f"{case_id}.case.json"
        write_json(
            case_path,
            build_judge_case(suite, case, references.get(case_id)),
        )
        print(f"Scoring {case_id}...")
        completed = subprocess.run(
            _judge_command(args, archive, output, case_path),
            cwd=str(HERE.parent.parent),
            check=False,
        )
        if completed.returncode != 0:
            raise RegressionError(
                f"Judge failed for {case_id} with exit code {completed.returncode}"
            )

    if args.dry_run:
        print(f"Session scoring dry run passed for {len(suite['cases'])} cases")
        return 0

    output = collect_results(
        suite,
        results_dir,
        allow_model_mismatch=args.allow_model_mismatch,
    )
    output_path = results_dir / "regression-run.json"
    write_json(output_path, output)
    print(f"Run result: {output_path.resolve()}")
    return 0


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

    return {
        "schema": "deepseek-vs.regression-comparison/1",
        "suite_id": baseline.get("suite_id"),
        "suite_version": baseline.get("suite_version"),
        "policy": {
            "max_score_drop": max_score_drop,
            "max_dimension_drop": max_dimension_drop,
            "outcome_must_not_decrease": True,
            "critical_failures_must_not_increase": True,
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


def run_validate(args: argparse.Namespace) -> int:
    suite_path = Path(args.suite)
    suite = read_json(suite_path)
    if not isinstance(suite, dict):
        raise RegressionError("Suite root must be a JSON object")
    errors = validate_suite(suite, suite_path.parent)
    reference_doc = read_json(
        resolve_reference_answers_path(args, suite, suite_path)
    )
    if isinstance(reference_doc, dict):
        errors.extend(validate_reference_answers(suite, reference_doc))
    else:
        errors.append("reference answers root must be a JSON object")
    if errors:
        print(
            f"Regression suite validation failed with {len(errors)} error(s):",
            file=sys.stderr,
        )
        for error in errors:
            print(f"ERROR: {error}", file=sys.stderr)
        return 1

    cases = suite["cases"]
    kinds = Counter(str(case.get("kind")) for case in cases)
    print(
        f"Regression suite validation passed: {len(cases)} cases, "
        f"{len(DIMENSIONS)} dimensions, "
        f"regular={kinds.get('regular', 0)}, boundary={kinds.get('boundary', 0)}."
    )
    return 0


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description="Prepare, score, collect, and compare fixture-based Agent regressions."
    )
    subparsers = parser.add_subparsers(dest="command", required=True)

    validate_parser = subparsers.add_parser("validate", help="Validate a regression suite")
    validate_parser.add_argument("--suite", default=str(DEFAULT_SUITE))
    validate_parser.add_argument(
        "--reference-answers",
        default=None,
    )
    validate_parser.set_defaults(func=run_validate)

    prepare_parser = subparsers.add_parser(
        "prepare",
        help="Copy the fixture and render one case's Agent prompts",
    )
    prepare_parser.add_argument("--suite", default=str(DEFAULT_SUITE))
    prepare_parser.add_argument(
        "--reference-answers",
        default=None,
    )
    prepare_parser.add_argument("--case", required=True)
    prepare_parser.add_argument("--output", required=True)
    prepare_parser.add_argument(
        "--metadata-dir",
        help="Optional prompt/case metadata directory outside the Agent workspace",
    )
    prepare_parser.set_defaults(func=run_prepare)

    collect_parser = subparsers.add_parser(
        "collect",
        help="Collect <case-id>.judge.json files into one regression run",
    )
    collect_parser.add_argument("--suite", default=str(DEFAULT_SUITE))
    collect_parser.add_argument("--results", required=True)
    collect_parser.add_argument("--output", required=True)
    collect_parser.add_argument("--allow-model-mismatch", action="store_true")
    collect_parser.set_defaults(func=run_collect)

    score_parser = subparsers.add_parser(
        "score",
        help="Run the existing session Judge for every <case-id>.zip archive",
    )
    score_parser.add_argument("--suite", default=str(DEFAULT_SUITE))
    score_parser.add_argument(
        "--reference-answers",
        default=None,
    )
    score_parser.add_argument("--archives", required=True)
    score_parser.add_argument("--results", required=True)
    score_parser.add_argument("--model", default=DEFAULT_JUDGE_MODEL)
    score_parser.add_argument("--base-url")
    score_parser.add_argument("--api-key-env", default="JUDGE_API_KEY")
    score_parser.add_argument("--parallel", type=int, default=10)
    score_parser.add_argument("--no-json-mode", action="store_true")
    score_parser.add_argument("--dry-run", action="store_true")
    score_parser.add_argument("--skip-existing", action="store_true")
    score_parser.add_argument("--verbose", action="store_true")
    score_parser.add_argument("--allow-model-mismatch", action="store_true")
    score_parser.set_defaults(func=run_score)

    compare_parser = subparsers.add_parser(
        "compare",
        help="Fail when a candidate run regresses against a baseline run",
    )
    compare_parser.add_argument("--baseline", required=True)
    compare_parser.add_argument("--candidate", required=True)
    compare_parser.add_argument("--output")
    compare_parser.add_argument("--max-score-drop", type=float, default=0.1)
    compare_parser.add_argument("--max-dimension-drop", type=float, default=0.1)
    compare_parser.set_defaults(func=run_compare)

    return parser


def main() -> int:
    parser = build_parser()
    args = parser.parse_args()
    try:
        return int(args.func(args))
    except RegressionError as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        return 2
    except KeyboardInterrupt:
        print("Cancelled.", file=sys.stderr)
        return 130


if __name__ == "__main__":
    raise SystemExit(main())
