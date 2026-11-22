"""Regression suite validation, reference answers, and case preparation."""

from __future__ import annotations

import argparse
import os
import shutil
import subprocess
from pathlib import Path
from typing import Any

from regression_common import (
    DEFAULT_AGENT_MODEL,
    DEFAULT_JUDGE_MODEL,
    DEFAULT_REFERENCE_ANSWERS,
    DIMENSIONS,
    HERE,
    KINDS,
    TAGS,
    RegressionError,
    read_json,
    resolve_inside,
    write_json,
    write_text,
)


def _case_verification_commands(case: dict[str, Any]) -> list[str]:
    commands = [str(command) for command in case.get("verification") or []]
    read_only = "read_only" in {
        str(tag) for tag in case.get("tags") or []
    }
    if read_only:
        commands = [
            command
            for command in commands
            if command.strip() != "git status --short"
        ]
    return commands


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
    read_only = "read_only" in {str(tag) for tag in case.get("tags") or []}
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

    if read_only:
        must.append("Read-only tasks must not run build or test commands.")
        must_not.append(
            "Run run_tests, build_solution, or terminal build/test commands "
            "for a read-only task."
        )
    else:
        must.append(
            "When runnable tests exist, run run_tests first after code changes; "
            "use build_solution only when no test project is available."
        )
        must_not.append(
            "Use run_in_terminal for build or test commands, including dotnet "
            "build/test, msbuild, vstest, ctest, or cmake configure/build."
        )

    return {
        "id": str(case["id"]),
        "dimension": "task_completion",
        "title": case.get("title"),
        "scenario": case.get("scenario"),
        "setup": (
            f"Use the isolated {suite.get('fixture')} workspace. "
            "Independent oracle/reference verification is listed in regression_suite. "
            "Agent verification must follow verification_policy."
        ),
        "user_prompt": "\n\n".join(prompts),
        "requires_workspace": True,
        "checks": {
            "must": must,
            "must_not": must_not,
        },
        "turns": turns,
        "verification_policy": {
            "test_first": not read_only,
            "preferred_test_tool": "run_tests",
            "fallback_build_tool": "build_solution",
            "test_pass_condition": "total > 0 and failed = 0",
            "terminal_build_or_test": "forbidden",
            "terminal_allowed_uses": [
                "read-only inspection",
                "scripts",
                "dependency installation",
                "environment queries",
            ],
        },
        "user_simulation": case.get("user_simulation"),
        "reference_answer": reference_answer,
        "regression_suite": {
            "id": suite.get("id"),
            "version": suite.get("version"),
            "kind": case.get("kind"),
            "tags": case.get("tags") or [],
            "verification": _case_verification_commands(case),
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

    cases_by_id = {
        str(case.get("id")): case
        for case in suite.get("cases") or []
        if isinstance(case, dict) and case.get("id")
    }
    case_ids = set(cases_by_id)
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
        case = cases_by_id.get(case_id) or {}
        read_only = "read_only" in {
            str(tag) for tag in case.get("tags") or []
        }
        if not answer.get("verification") and not read_only:
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
        return resolve_inside(suite_path.parent, suite_value)
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
            fixture_dir = resolve_inside(base_dir, fixture)
            solution_path = resolve_inside(fixture_dir, solution)
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
        read_only = "read_only" in {
            str(tag) for tag in case.get("tags") or []
        }
        if not case.get("verification") and not read_only:
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
    lines.extend(
        [
            "",
            "## Oracle Verification Reference",
            "",
            "The Agent must prefer `run_tests` over building when runnable tests exist.",
            "Use `build_solution` only when no test project exists.",
            "Never run build or test commands through `run_in_terminal`.",
            "",
        ]
    )
    for command in _case_verification_commands(case):
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

    fixture_dir = resolve_inside(
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
        ignore=shutil.ignore_patterns(
            ".git",
            "bin",
            "obj",
            ".vs",
            "TestResults",
            "build",
        ),
    )
    initialize_case_repository(output_dir, str(case["id"]))
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
    print(f"Git repository: {output_dir / '.git'}")
    return 0


def initialize_case_repository(workspace: Path, case_id: str) -> None:
    """Create a deterministic standalone repository for one prepared case."""
    workspace = workspace.resolve()
    env = dict(os.environ)
    env.update(
        {
            "GIT_AUTHOR_NAME": "DeepSeek VS Benchmark",
            "GIT_AUTHOR_EMAIL": "benchmark@local.invalid",
            "GIT_AUTHOR_DATE": "2000-01-01T00:00:00Z",
            "GIT_COMMITTER_NAME": "DeepSeek VS Benchmark",
            "GIT_COMMITTER_EMAIL": "benchmark@local.invalid",
            "GIT_COMMITTER_DATE": "2000-01-01T00:00:00Z",
        }
    )

    def run(*args: str) -> None:
        completed = subprocess.run(
            ["git", *args],
            cwd=str(workspace),
            capture_output=True,
            text=True,
            encoding="utf-8",
            errors="replace",
            env=env,
            check=False,
        )
        if completed.returncode != 0:
            raise RegressionError(
                "Failed to initialize benchmark Git repository: "
                + (completed.stderr.strip() or completed.stdout.strip() or "git failed")
            )

    run("init")
    exclude_path = workspace / ".git" / "info" / "exclude"
    exclude_path.write_text(
        "\n".join(
            [
                "bin/",
                "obj/",
                "build/",
                ".vs/",
                "TestResults/",
                "",
            ]
        ),
        encoding="utf-8",
    )
    run("add", "-A")
    run("commit", "-m", f"benchmark fixture: {case_id}")

    completed = subprocess.run(
        ["git", "rev-parse", "--show-toplevel"],
        cwd=str(workspace),
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        check=False,
    )
    actual_root = completed.stdout.strip() if completed.returncode == 0 else ""
    if Path(actual_root).resolve() != workspace:
        raise RegressionError(
            "Prepared case is not an independent Git repository: "
            f"expected {workspace}, got {actual_root or '<none>'}"
        )

