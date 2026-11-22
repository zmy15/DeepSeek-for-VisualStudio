#!/usr/bin/env python3
"""Shared constants and file helpers for benchmark regression commands."""

from __future__ import annotations

import json
from pathlib import Path
from typing import Any


HERE = Path(__file__).resolve().parent
DEFAULT_SUITE = HERE / "orderdesk.v1.json"
DEFAULT_REFERENCE_ANSWERS = HERE / "reference-answers.v1.json"
DEFAULT_JUDGE_CACHE = HERE / "artifacts" / "judge-cache"
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


def resolve_inside(base: Path, relative: str) -> Path:
    candidate = (base / relative).resolve()
    try:
        candidate.relative_to(base.resolve())
    except ValueError as exc:
        raise RegressionError(
            f"Path escapes the suite directory: {relative}"
        ) from exc
    return candidate
