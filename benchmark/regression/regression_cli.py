#!/usr/bin/env python3
"""Command-line dispatch for the fixture-based regression benchmark."""

from __future__ import annotations

import argparse
from collections import Counter
import sys
from pathlib import Path

import harness_compare
import stability
import trials
import verification
from regression_common import (
    DEFAULT_AGENT_MODEL,
    DEFAULT_JUDGE_CACHE,
    DEFAULT_JUDGE_MODEL,
    DEFAULT_SUITE,
    DIMENSIONS,
    HERE,
    RegressionError,
    read_json,
    write_json,
)
from regression_compare import run_compare
from regression_results import run_collect
from regression_scoring import run_score
from regression_suite import (
    _load_suite,
    resolve_reference_answers_path,
    run_prepare,
    validate_reference_answers,
    validate_suite,
)


def run_verify(args: argparse.Namespace) -> int:
    suite_path = Path(args.suite)
    suite = _load_suite(suite_path)
    try:
        oracle_path = verification.resolve_oracle_suite_path(
            suite,
            suite_path,
            args.oracle_suite,
        )
        if oracle_path is None:
            raise RegressionError(
                "No oracle_suite configured; pass --oracle-suite explicitly."
            )
        oracle_document = verification.load_oracle_suite(oracle_path, suite)
        report = verification.verify_archive_set(
            suite,
            suite_path,
            oracle_document,
            oracle_path,
            Path(args.archives),
            Path(args.results),
            case_ids=args.case or None,
            skip_existing=args.skip_existing,
            status_writer=print,
        )
    except verification.VerificationError as exc:
        raise RegressionError(str(exc)) from exc

    output = Path(args.output) if args.output else (
        Path(args.results) / "verification-run.json"
    )
    write_json(output, report)
    summary = report["summary"]
    print(
        "Independent verification: "
        f"pass={summary['pass']} fail={summary['fail']} "
        f"not_available={summary['not_available']}"
    )
    print(f"Output: {output.resolve()}")
    return 3 if summary["fail"] else 0


def run_stability(args: argparse.Namespace) -> int:
    paths = [Path(item) for item in (args.runs or [])]
    if args.runs_dir:
        paths.extend(stability.discover_runs(Path(args.runs_dir)))
    if not paths:
        raise RegressionError("Pass --runs or --runs-dir with repeated run results.")
    try:
        runs = stability.load_runs(paths)
        report = stability.summarize(runs)
        stability.write_json(Path(args.output), report)
    except stability.StabilityError as exc:
        raise RegressionError(str(exc)) from exc

    summary = report["summary"]
    print(
        f"Runs: {report['run_count']} | "
        f"pass@1={summary['pass_at_1']} | "
        f"pass@{summary['k']}={summary['pass_at_k']}"
    )
    print(
        "Cases with variance: "
        f"outcome={summary['cases_with_outcome_variance']} "
        f"score={summary['cases_with_score_variance']}"
    )
    print(f"Output: {Path(args.output).resolve()}")
    return 0


def run_harness_compare(args: argparse.Namespace) -> int:
    try:
        specs = harness_compare.parse_run_specs(args.run)
        report = harness_compare.compare_harness_runs(
            specs,
            baseline_label=args.baseline,
        )
        harness_compare.write_json(Path(args.output), report)
    except harness_compare.HarnessCompareError as exc:
        raise RegressionError(str(exc)) from exc

    print("Profiles: " + ", ".join(report["ranking"]))
    for label in report["ranking"]:
        summary = report["profiles"][label]
        print(
            f"  {label}: avg={summary.get('average_score')} "
            f"pass={summary.get('pass_count')}/{summary.get('case_count')} "
            f"critical={summary.get('critical_failure_count')}"
        )
    print(f"Output: {Path(args.output).resolve()}")
    return 0


def run_trials_command(args: argparse.Namespace) -> int:
    try:
        report = trials.run_trials(args)
    except trials.TrialError as exc:
        raise RegressionError(str(exc)) from exc
    if report.get("schema") == "deepseek-vs.trial-plan/1":
        print(
            f"Trial plan: runs={report['runs']} "
            f"cases={report['case_count']} output={report['output']}"
        )
        return 0

    summary = report["summary"]
    print(
        f"Trials complete: pass@1={summary['pass_at_1']} "
        f"pass@{summary['k']}={summary['pass_at_k']} "
        f"outcome-variance-cases={summary['cases_with_outcome_variance']}"
    )
    print(f"Output: {Path(report['trial_output']) / 'stability.json'}")
    return 0


def run_validate(args: argparse.Namespace) -> int:
    suite_path = Path(args.suite)
    suite = read_json(suite_path)
    if not isinstance(suite, dict):
        raise RegressionError("Suite root must be a JSON object")
    errors = validate_suite(suite, suite_path.parent)
    try:
        oracle_path = verification.resolve_oracle_suite_path(
            suite,
            suite_path,
            getattr(args, "oracle_suite", None),
        )
        if oracle_path is not None:
            oracle_doc = verification.read_json(oracle_path)
            if isinstance(oracle_doc, dict):
                errors.extend(
                    verification.validate_oracle_suite(
                        oracle_doc,
                        suite,
                        oracle_path.parent,
                    )
                )
            else:
                errors.append("oracle suite root must be an object")
    except verification.VerificationError as exc:
        errors.append(str(exc))
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
    validate_parser.add_argument("--oracle-suite")
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
    score_parser.add_argument("--parallel", type=int, default=1)
    score_parser.add_argument("--max-tokens", type=int, default=6144)
    score_parser.add_argument(
        "--no-thinking",
        dest="thinking_enabled",
        action="store_false",
    )
    score_parser.add_argument(
        "--thinking-effort",
        choices=("low", "high", "max"),
        default="low",
    )
    score_parser.add_argument(
        "--no-cache-warmup",
        dest="cache_warmup",
        action="store_false",
        help="Skip the two-call Judge prefix warmup",
    )
    score_parser.add_argument(
        "--judge-cache-dir",
        default=str(DEFAULT_JUDGE_CACHE),
        help="Content-addressed Judge result cache directory",
    )
    score_parser.add_argument(
        "--no-judge-cache",
        dest="judge_cache",
        action="store_false",
        help="Disable reuse of content-addressed Judge results",
    )
    score_parser.add_argument("--no-json-mode", action="store_true")
    score_parser.add_argument("--dry-run", action="store_true")
    score_parser.add_argument("--skip-existing", action="store_true")
    score_parser.add_argument("--verbose", action="store_true")
    score_parser.add_argument("--allow-model-mismatch", action="store_true")
    score_parser.add_argument(
        "--oracle-suite",
        help="Override the suite's oracle_suite path",
    )
    score_parser.add_argument(
        "--no-verify",
        dest="verify",
        action="store_false",
        help="Skip deterministic independent verification",
    )
    score_parser.set_defaults(verify=True)
    score_parser.set_defaults(func=run_score)

    verify_parser = subparsers.add_parser(
        "verify",
        help="Run hidden oracles against archived workspace snapshots",
    )
    verify_parser.add_argument("--suite", default=str(DEFAULT_SUITE))
    verify_parser.add_argument("--oracle-suite")
    verify_parser.add_argument("--archives", required=True)
    verify_parser.add_argument("--results", required=True)
    verify_parser.add_argument("--output")
    verify_parser.add_argument("--case", action="append")
    verify_parser.add_argument("--skip-existing", action="store_true")
    verify_parser.set_defaults(func=run_verify)

    stability_parser = subparsers.add_parser(
        "stability",
        help="Summarize pass@k and score variance across repeated regression runs",
    )
    stability_parser.add_argument("--runs", nargs="*")
    stability_parser.add_argument(
        "--runs-dir",
        help="Directory containing run-*/results/regression-run.json",
    )
    stability_parser.add_argument("--output", required=True)
    stability_parser.set_defaults(func=run_stability)

    harness_parser = subparsers.add_parser(
        "harness-compare",
        help="Compare the same suite under multiple Harness profiles",
    )
    harness_parser.add_argument(
        "--run",
        action="append",
        required=True,
        metavar="LABEL=PATH",
    )
    harness_parser.add_argument("--baseline")
    harness_parser.add_argument("--output", required=True)
    harness_parser.set_defaults(func=run_harness_compare)

    trials_parser = subparsers.add_parser(
        "trials",
        help="Prepare, run, verify, score, and aggregate repeated trials",
    )
    trials_parser.add_argument("--suite", default=str(DEFAULT_SUITE))
    trials_parser.add_argument("--case", action="append")
    trials_parser.add_argument("--runs", type=int, required=True)
    trials_parser.add_argument("--output", required=True)
    trials_parser.add_argument("--runner", default=str(trials.DEFAULT_RUNNER))
    trials_parser.add_argument(
        "--profile",
        default=str(HERE.parent / "headless" / "profiles" / "ask-auto.json"),
    )
    trials_parser.add_argument("--agent-model", default=DEFAULT_AGENT_MODEL)
    trials_parser.add_argument("--judge-model", default=DEFAULT_JUDGE_MODEL)
    trials_parser.add_argument("--base-url", default="https://api.deepseek.com")
    trials_parser.add_argument("--judge-base-url")
    trials_parser.add_argument("--user-sim-model")
    trials_parser.add_argument("--max-turns", type=int)
    trials_parser.add_argument("--judge-parallel", type=int, default=1)
    trials_parser.add_argument(
        "--judge-api-key-env",
        default="JUDGE_API_KEY",
        help="Environment variable containing the Judge API key",
    )
    trials_parser.add_argument("--no-verify", action="store_true")
    trials_parser.add_argument("--no-thinking", action="store_true")
    trials_parser.add_argument(
        "--thinking-effort",
        choices=("low", "high", "max"),
        default="low",
    )
    trials_parser.add_argument("--dry-run", action="store_true")
    trials_parser.set_defaults(func=run_trials_command)

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
