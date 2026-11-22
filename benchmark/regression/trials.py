from __future__ import annotations

import json
import subprocess
import sys
from pathlib import Path
from typing import Any, Iterable

import stability


HERE = Path(__file__).resolve().parent
REGRESSION_SCRIPT = HERE / "regression.py"
DEFAULT_RUNNER = HERE.parent / "headless" / "bin" / "Debug" / "net472" / "BenchmarkRunner.exe"


class TrialError(RuntimeError):
    """Raised when a repeated trial cannot be prepared or executed."""


def run_trials(args: Any) -> dict[str, Any]:
    suite_path = Path(args.suite).resolve()
    suite = _read_json(suite_path)
    selected = _selected_cases(suite, args.case)
    output_root = Path(args.output).resolve()
    output_root.mkdir(parents=True, exist_ok=True)
    runner = Path(args.runner).resolve()
    if not args.dry_run and not runner.is_file():
        raise TrialError(f"Headless runner not found: {runner}")

    run_results: list[Path] = []
    for run_number in range(1, args.runs + 1):
        run_root = output_root / f"run-{run_number:03d}"
        archives = run_root / "archives"
        results = run_root / "results"
        archives.mkdir(parents=True, exist_ok=True)
        results.mkdir(parents=True, exist_ok=True)

        for case in selected:
            case_id = str(case["id"])
            case_dir = run_root / "cases" / case_id
            metadata_dir = case_dir.parent / f"{case_id}.benchmark-meta"
            archive = archives / f"{case_id}.zip"
            transcript = run_root / "transcripts" / f"{case_id}.json"
            prepare = _prepare_command(
                suite_path,
                case_id,
                case_dir,
                metadata_dir,
            )
            execute = _runner_command(
                runner,
                case_dir,
                archive,
                transcript,
                args,
            )
            if args.dry_run:
                _print_commands(run_number, case_id, [prepare, execute])
                continue
            _run(prepare, f"prepare {case_id} run {run_number}")
            _run(execute, f"agent {case_id} run {run_number}")

        if args.dry_run:
            continue
        score = _score_command(suite_path, archives, results, args)
        _run(score, f"score run {run_number}")
        run_results.append(results / "regression-run.json")

    if args.dry_run:
        return {
            "schema": "deepseek-vs.trial-plan/1",
            "runs": args.runs,
            "case_count": len(selected),
            "output": str(output_root),
        }

    runs = stability.load_runs(run_results)
    report = stability.summarize(runs)
    report["trial_output"] = str(output_root)
    report["headless_runner"] = str(runner)
    report["harness_profile"] = str(Path(args.profile).resolve())
    stability.write_json(output_root / "stability.json", report)
    return report


def _prepare_command(
    suite_path: Path,
    case_id: str,
    case_dir: Path,
    metadata_dir: Path,
) -> list[str]:
    return [
        sys.executable,
        str(REGRESSION_SCRIPT),
        "prepare",
        "--suite",
        str(suite_path),
        "--case",
        case_id,
        "--output",
        str(case_dir),
        "--metadata-dir",
        str(metadata_dir),
    ]


def _runner_command(
    runner: Path,
    case_dir: Path,
    archive: Path,
    transcript: Path,
    args: Any,
) -> list[str]:
    command = [
        str(runner),
        "--case-dir",
        str(case_dir),
        "--archive",
        str(archive),
        "--transcript",
        str(transcript),
        "--model",
        str(args.agent_model),
        "--base-url",
        str(args.base_url),
        "--harness-profile",
        str(Path(args.profile).resolve()),
    ]
    if args.user_sim_model:
        command.extend(["--user-sim-model", str(args.user_sim_model)])
    if args.max_turns:
        command.extend(["--max-turns", str(args.max_turns)])
    return command


def _score_command(
    suite_path: Path,
    archives: Path,
    results: Path,
    args: Any,
) -> list[str]:
    command = [
        sys.executable,
        str(REGRESSION_SCRIPT),
        "score",
        "--suite",
        str(suite_path),
        "--archives",
        str(archives),
        "--results",
        str(results),
        "--model",
        str(args.judge_model),
        "--parallel",
        str(args.judge_parallel),
        "--api-key-env",
        str(args.judge_api_key_env),
    ]
    if args.judge_base_url:
        command.extend(["--base-url", str(args.judge_base_url)])
    if args.no_verify:
        command.append("--no-verify")
    for case_id in args.case or []:
        command.extend(["--case", str(case_id)])
    if args.no_thinking:
        command.append("--no-thinking")
    if args.thinking_effort:
        command.extend(["--thinking-effort", str(args.thinking_effort)])
    return command


def _run(command: list[str], label: str) -> None:
    print(f"[trials] {label}")
    completed = subprocess.run(command, cwd=str(HERE.parent.parent), check=False)
    if completed.returncode != 0:
        raise TrialError(
            f"{label} failed with exit code {completed.returncode}: "
            + " ".join(command)
        )


def _selected_cases(
    suite: dict[str, Any],
    selected_ids: Iterable[str] | None,
) -> list[dict[str, Any]]:
    cases = [
        case for case in suite.get("cases") or []
        if isinstance(case, dict)
    ]
    ids = {str(item) for item in selected_ids or []}
    if not ids:
        return cases
    selected = [case for case in cases if str(case.get("id")) in ids]
    missing = sorted(ids - {str(case.get("id")) for case in selected})
    if missing:
        raise TrialError("Unknown cases: " + ", ".join(missing))
    return selected


def _print_commands(
    run_number: int,
    case_id: str,
    commands: list[list[str]],
) -> None:
    for command in commands:
        print(
            f"[dry-run run={run_number} case={case_id}] "
            + subprocess.list2cmdline(command)
        )


def _read_json(path: Path) -> Any:
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except FileNotFoundError as exc:
        raise TrialError(f"File not found: {path}") from exc
    except json.JSONDecodeError as exc:
        raise TrialError(f"Invalid JSON in {path}: {exc}") from exc
