#!/usr/bin/env python3
"""Compatibility entry point for the modular regression benchmark."""

from __future__ import annotations

import subprocess

from regression_cli import (
    build_parser,
    main,
    run_harness_compare,
    run_stability,
    run_trials_command,
    run_validate,
    run_verify,
)
from regression_common import (
    DEFAULT_AGENT_MODEL,
    DEFAULT_JUDGE_CACHE,
    DEFAULT_JUDGE_MODEL,
    DEFAULT_REFERENCE_ANSWERS,
    DEFAULT_SUITE,
    DIMENSIONS,
    HERE,
    JUDGE_SCRIPT,
    KINDS,
    OUTCOME_RANK,
    TAGS,
    RegressionError,
    read_json,
    resolve_inside,
    write_json,
    write_text,
)
from regression_compare import compare_runs, run_compare
from regression_results import (
    _critical_ids,
    _session_score_map,
    _sum_usage,
    _summary,
    collect_results,
    extract_case_result,
    run_collect,
)
from regression_scoring import (
    _judge_command,
    judge_cache_key,
    run_judge_cache_warmup,
    run_score,
)
from regression_suite import (
    _load_suite,
    build_judge_case,
    find_case,
    has_reference_criteria,
    load_reference_answers,
    render_case_guide,
    resolve_reference_answers_path,
    run_prepare,
    validate_reference_answers,
    validate_suite,
)

_resolve_inside = resolve_inside


__all__ = [
    "DEFAULT_AGENT_MODEL",
    "DEFAULT_JUDGE_CACHE",
    "DEFAULT_JUDGE_MODEL",
    "DEFAULT_REFERENCE_ANSWERS",
    "DEFAULT_SUITE",
    "DIMENSIONS",
    "HERE",
    "JUDGE_SCRIPT",
    "KINDS",
    "OUTCOME_RANK",
    "RegressionError",
    "TAGS",
    "_critical_ids",
    "_judge_command",
    "_load_suite",
    "_resolve_inside",
    "_session_score_map",
    "_sum_usage",
    "_summary",
    "build_judge_case",
    "build_parser",
    "collect_results",
    "compare_runs",
    "extract_case_result",
    "find_case",
    "has_reference_criteria",
    "judge_cache_key",
    "load_reference_answers",
    "main",
    "read_json",
    "render_case_guide",
    "resolve_reference_answers_path",
    "run_collect",
    "run_compare",
    "run_harness_compare",
    "run_judge_cache_warmup",
    "run_prepare",
    "run_score",
    "run_stability",
    "run_trials_command",
    "run_validate",
    "run_verify",
    "subprocess",
    "validate_reference_answers",
    "validate_suite",
    "write_json",
    "write_text",
]


if __name__ == "__main__":
    raise SystemExit(main())
