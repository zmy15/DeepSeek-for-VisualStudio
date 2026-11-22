"""Judge invocation, result caching, and aggregate scoring."""

from __future__ import annotations

import argparse
import hashlib
import shutil
import subprocess
import sys
from pathlib import Path
from typing import Any

import verification
from regression_common import (
    DEFAULT_JUDGE_CACHE,
    HERE,
    JUDGE_SCRIPT,
    RegressionError,
    write_json,
)
from regression_results import collect_results
from regression_suite import (
    _load_suite,
    build_judge_case,
    load_reference_answers,
    resolve_reference_answers_path,
)


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
    command.extend(["--max-tokens", str(getattr(args, "max_tokens", 6144))])
    if not getattr(args, "thinking_enabled", True):
        command.append("--no-thinking")
    command.extend(
        [
            "--thinking-effort",
            getattr(args, "thinking_effort", "low"),
        ]
    )
    if args.no_json_mode:
        command.append("--no-json-mode")
    if args.verbose:
        command.append("--verbose")
    if args.dry_run:
        command.append("--dry-run")
    return command


def judge_cache_key(
    archive: Path,
    case_path: Path,
    model: str,
    thinking_enabled: bool = True,
    thinking_effort: str = "low",
    max_tokens: int = 6144,
    temperature: float = 0.0,
) -> str:
    digest = hashlib.sha256()
    for path in (
        archive,
        case_path,
        JUDGE_SCRIPT,
        HERE.parent / "judge" / "general-prompt.md",
        HERE.parent / "judge" / "general-rubric.v1.json",
        HERE.parent / "judge" / "session-prompt.md",
        HERE.parent / "judge" / "session-rubric.v1.json",
    ):
        digest.update(path.name.encode("utf-8"))
        digest.update(path.read_bytes())
    digest.update(model.encode("utf-8"))
    digest.update(str(thinking_enabled).encode("utf-8"))
    digest.update(str(thinking_effort).encode("utf-8"))
    digest.update(str(max_tokens).encode("utf-8"))
    digest.update(str(temperature).encode("utf-8"))
    return digest.hexdigest()


def run_judge_cache_warmup(args: argparse.Namespace) -> None:
    warmup_command = [
        sys.executable,
        str(JUDGE_SCRIPT),
        "warmup",
        "--calls",
        "2",
        "--model",
        args.model,
        "--max-tokens",
        "32",
    ]
    if args.base_url:
        warmup_command.extend(["--base-url", args.base_url])
    if args.api_key_env:
        warmup_command.extend(["--api-key-env", args.api_key_env])
    if not getattr(args, "thinking_enabled", True):
        warmup_command.append("--no-thinking")
    warmup_command.extend(
        [
            "--thinking-effort",
            getattr(args, "thinking_effort", "low"),
        ]
    )
    print("Priming Judge prefix cache...")
    warmup = subprocess.run(
        warmup_command,
        cwd=str(HERE.parent.parent),
        check=False,
    )
    if warmup.returncode != 0:
        print("Warning: Judge cache warmup failed; continuing with cold requests.")


def run_score(args: argparse.Namespace) -> int:
    suite_path = Path(args.suite)
    suite = _load_suite(suite_path)
    reference_path = resolve_reference_answers_path(args, suite, suite_path)
    _, references = load_reference_answers(suite, reference_path)
    archives_dir = Path(args.archives)
    results_dir = Path(args.results)
    results_dir.mkdir(parents=True, exist_ok=True)
    cache_dir = Path(
        getattr(args, "judge_cache_dir", None) or DEFAULT_JUDGE_CACHE
    )

    missing = [
        str(case["id"])
        for case in suite.get("cases") or []
        if not getattr(args, "case", None)
        or str(case["id"]) in set(args.case)
        if not (archives_dir / f"{case['id']}.zip").is_file()
    ]
    if missing:
        raise RegressionError("Missing session archives: " + ", ".join(missing))

    if getattr(args, "verify", True) and not args.dry_run:
        try:
            oracle_path = verification.resolve_oracle_suite_path(
                suite,
                suite_path,
                getattr(args, "oracle_suite", None),
            )
        except verification.VerificationError as exc:
            raise RegressionError(str(exc)) from exc
        if oracle_path is not None:
            try:
                oracle_document = verification.load_oracle_suite(oracle_path, suite)
                verification_run = verification.verify_archive_set(
                    suite,
                    suite_path,
                    oracle_document,
                    oracle_path,
                    archives_dir,
                    results_dir,
                    case_ids=getattr(args, "case", None),
                    skip_existing=bool(getattr(args, "skip_existing", False)),
                    status_writer=print,
                )
                write_json(results_dir / "verification-run.json", verification_run)
            except verification.VerificationError as exc:
                raise RegressionError(str(exc)) from exc

    warmup_done = False
    suite_cases = [
        case for case in suite.get("cases") or []
        if not getattr(args, "case", None)
        or str(case["id"]) in set(args.case)
    ]
    for case in suite_cases:
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
        cache_path = None
        if getattr(args, "judge_cache", True) and not args.dry_run:
            key = judge_cache_key(
                archive,
                case_path,
                args.model,
                thinking_enabled=getattr(args, "thinking_enabled", True),
                thinking_effort=getattr(args, "thinking_effort", "low"),
                max_tokens=getattr(args, "max_tokens", 6144),
                temperature=0.0,
            )
            cache_path = cache_dir / f"{key}.json"
            if cache_path.is_file():
                output.parent.mkdir(parents=True, exist_ok=True)
                shutil.copyfile(cache_path, output)
                print(f"Cached {case_id}")
                continue
        if (
            getattr(args, "cache_warmup", True)
            and not args.dry_run
            and not warmup_done
        ):
            run_judge_cache_warmup(args)
            warmup_done = True
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
        if cache_path is not None and output.is_file():
            cache_path.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(output, cache_path)

    if args.dry_run:
        print(f"Session scoring dry run passed for {len(suite_cases)} cases")
        return 0

    output = collect_results(
        suite,
        results_dir,
        allow_model_mismatch=args.allow_model_mismatch,
        case_ids=getattr(args, "case", None),
    )
    output_path = results_dir / "regression-run.json"
    write_json(output_path, output)
    print(f"Run result: {output_path.resolve()}")
    return 0

