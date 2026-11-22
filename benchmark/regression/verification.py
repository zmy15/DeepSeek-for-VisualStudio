from __future__ import annotations

import hashlib
import json
import os
import shutil
import subprocess
import tempfile
import time
import zipfile
from fnmatch import fnmatch
from pathlib import Path
from typing import Any, Iterable


IGNORED_DIRECTORIES = {
    ".git",
    ".vs",
    "bin",
    "obj",
    "build",
    "TestResults",
    "node_modules",
}
IGNORED_DIRECTORIES_LOWER = {item.lower() for item in IGNORED_DIRECTORIES}


class VerificationError(RuntimeError):
    """Raised for invalid oracle configuration or unreadable workspaces."""


def read_json(path: Path) -> Any:
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except FileNotFoundError as exc:
        raise VerificationError(f"File not found: {path}") from exc
    except json.JSONDecodeError as exc:
        raise VerificationError(f"Invalid JSON in {path}: {exc}") from exc


def write_json(path: Path, value: Any) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(
        json.dumps(value, ensure_ascii=False, indent=2) + "\n",
        encoding="utf-8",
        newline="\n",
    )


def resolve_oracle_suite_path(
    suite: dict[str, Any],
    suite_path: Path,
    explicit: str | None = None,
) -> Path | None:
    value = explicit or str(suite.get("oracle_suite") or "").strip()
    if not value:
        return None
    candidate = (suite_path.parent / value).resolve()
    try:
        candidate.relative_to(suite_path.parent.resolve())
    except ValueError as exc:
        raise VerificationError(
            f"Oracle suite path escapes the suite directory: {value}"
        ) from exc
    return candidate


def load_oracle_suite(
    path: Path,
    suite: dict[str, Any],
) -> dict[str, Any]:
    document = read_json(path)
    if not isinstance(document, dict):
        raise VerificationError("Oracle suite root must be an object.")
    errors = validate_oracle_suite(document, suite, path.parent)
    if errors:
        raise VerificationError("Invalid oracle suite: " + "; ".join(errors))
    return document


def validate_oracle_suite(
    document: dict[str, Any],
    suite: dict[str, Any],
    base_dir: Path,
) -> list[str]:
    errors: list[str] = []
    if document.get("schema") != "deepseek-vs.oracle-suite/1":
        errors.append("schema must be 'deepseek-vs.oracle-suite/1'")
    if document.get("schema_version") != 1:
        errors.append("schema_version must be 1")
    if document.get("suite_id") != suite.get("id"):
        errors.append("suite_id must match the regression suite")

    fixture = document.get("fixture")
    if not isinstance(fixture, str) or not fixture.strip():
        errors.append("fixture is required")
    elif not (base_dir / fixture).resolve().is_dir():
        errors.append(f"fixture directory does not exist: {fixture}")

    runner = document.get("runner")
    if runner not in {"dotnet", "cmake"}:
        errors.append("runner must be 'dotnet' or 'cmake'")
    oracle_project = document.get("oracle_project")
    if not isinstance(oracle_project, str) or not oracle_project.strip():
        errors.append("oracle_project is required")
    elif not (base_dir / oracle_project).resolve().exists():
        errors.append(f"oracle_project does not exist: {oracle_project}")

    suite_cases = {
        str(case.get("id"))
        for case in suite.get("cases") or []
        if isinstance(case, dict) and case.get("id")
    }
    seen: set[str] = set()
    for index, case in enumerate(document.get("cases") or []):
        if not isinstance(case, dict):
            errors.append(f"cases[{index}] must be an object")
            continue
        case_id = str(case.get("case_id") or "").strip()
        if not case_id:
            errors.append(f"cases[{index}].case_id is required")
        elif case_id not in suite_cases:
            errors.append(f"unknown case_id: {case_id}")
        elif case_id in seen:
            errors.append(f"duplicate oracle case: {case_id}")
        else:
            seen.add(case_id)
        allowed = case.get("allowed_changes") or []
        forbidden = case.get("forbidden_changes") or []
        if not isinstance(allowed, list) or not isinstance(forbidden, list):
            errors.append(f"{case_id or index} changes lists must be arrays")
        if bool(case.get("read_only")) and (allowed or forbidden):
            errors.append(f"{case_id or index} read-only case cannot define changes")
        if not bool(case.get("read_only")) and not allowed:
            errors.append(f"{case_id or index} writable case needs allowed_changes")

    missing = sorted(suite_cases - seen)
    if missing:
        errors.append("missing oracle case mappings: " + ", ".join(missing))
    return errors


def find_oracle_case(
    document: dict[str, Any],
    case_id: str,
) -> dict[str, Any]:
    for case in document.get("cases") or []:
        if isinstance(case, dict) and str(case.get("case_id")) == case_id:
            return case
    raise VerificationError(f"Oracle case not found: {case_id}")


def file_hashes(root: Path) -> dict[str, str]:
    values: dict[str, str] = {}
    for path in _walk_files(root):
        relative = path.relative_to(root).as_posix()
        digest = hashlib.sha256()
        with path.open("rb") as stream:
            for chunk in iter(lambda: stream.read(1024 * 1024), b""):
                digest.update(chunk)
        values[relative] = digest.hexdigest()
    return values


def workspace_changes(
    baseline: dict[str, str],
    candidate: dict[str, str],
) -> dict[str, list[str]]:
    return {
        "added": sorted(set(candidate) - set(baseline)),
        "deleted": sorted(set(baseline) - set(candidate)),
        "modified": sorted(
            path
            for path in set(baseline) & set(candidate)
            if baseline[path] != candidate[path]
        ),
    }


def changed_paths(changes: dict[str, list[str]]) -> list[str]:
    return sorted(
        set(changes.get("added") or [])
        | set(changes.get("deleted") or [])
        | set(changes.get("modified") or [])
    )


def verify_workspace(
    oracle_document: dict[str, Any],
    oracle_path: Path,
    case_id: str,
    workspace: Path,
    status_writer: Any | None = None,
) -> dict[str, Any]:
    started = time.monotonic()
    workspace = workspace.resolve()
    if not workspace.is_dir():
        raise VerificationError(f"Workspace not found: {workspace}")

    base_dir = oracle_path.resolve().parent
    case = find_oracle_case(oracle_document, case_id)
    fixture = (base_dir / str(oracle_document["fixture"])).resolve()
    baseline = file_hashes(fixture)
    current = file_hashes(workspace)
    changes = workspace_changes(baseline, current)
    changed = changed_paths(changes)
    allowed = [str(item) for item in case.get("allowed_changes") or []]
    forbidden = [str(item) for item in case.get("forbidden_changes") or []]
    checks: list[dict[str, Any]] = []
    violated_forbidden = sorted(
        path
        for path in changed
        if any(_matches_rule(path, rule) for rule in forbidden)
    )
    outside_scope = (
        sorted(
            path
            for path in changed
            if not any(_matches_rule(path, rule) for rule in allowed)
        )
        if not case.get("read_only")
        else changed
    )

    if case.get("read_only"):
        checks.append(
            {
                "name": "read_only_workspace",
                "status": "pass" if not changed else "fail",
                "detail": "Workspace source files were unchanged."
                if not changed
                else "Read-only case changed files: " + ", ".join(changed),
            }
        )
    else:
        checks.append(
            {
                "name": "allowed_changes",
                "status": "pass" if not outside_scope else "fail",
                "detail": "All changes are within allowed paths."
                if not outside_scope
                else "Out-of-scope changes: " + ", ".join(outside_scope),
            }
        )

    checks.append(
        {
            "name": "forbidden_changes",
            "status": "pass" if not violated_forbidden else "fail",
            "detail": "No forbidden paths changed."
            if not violated_forbidden
            else "Forbidden paths changed: " + ", ".join(violated_forbidden),
        }
    )

    commands: list[dict[str, Any]] = []
    static_pass = all(item["status"] == "pass" for item in checks)
    if static_pass and not case.get("read_only"):
        if status_writer:
            status_writer(f"Running {oracle_document['runner']} oracle for {case_id}")
        if oracle_document["runner"] in {"cmake", "dotnet"}:
            with tempfile.TemporaryDirectory(prefix="dsv-cmake-oracle-") as build_dir:
                commands = _run_oracle_commands(
                    oracle_document,
                    oracle_path,
                    workspace,
                    case_id,
                    Path(build_dir),
                )
        else:
            commands = _run_oracle_commands(
                oracle_document,
                oracle_path,
                workspace,
                case_id,
                None,
            )
        oracle_pass = bool(commands) and all(
            item["returncode"] == 0 for item in commands
        )
        checks.append(
            {
                "name": "oracle_execution",
                "status": "pass" if oracle_pass else "fail",
                "detail": "Hidden oracle passed."
                if oracle_pass
                else "Hidden oracle failed.",
            }
        )

    status = (
        "pass"
        if all(item["status"] == "pass" for item in checks)
        else "fail"
    )
    return {
        "schema": "deepseek-vs.independent-verification/1",
        "case_id": case_id,
        "oracle_suite": oracle_document.get("id"),
        "runner": oracle_document.get("runner"),
        "status": status,
        "deterministic": True,
        "duration_ms": round((time.monotonic() - started) * 1000),
        "workspace": {
            "file_count": len(current),
            "changes": changes,
            "changed_paths": changed,
        },
        "checks": checks,
        "commands": commands,
    }


def verify_archive(
    oracle_document: dict[str, Any],
    oracle_path: Path,
    case_id: str,
    archive: Path,
    status_writer: Any | None = None,
) -> dict[str, Any]:
    with tempfile.TemporaryDirectory(prefix=f"dsv-oracle-{case_id}-") as temporary:
        workspace = Path(temporary) / "workspace"
        workspace.mkdir()
        extracted = extract_workspace_snapshot(archive, workspace)
        if not extracted:
            return {
                "schema": "deepseek-vs.independent-verification/1",
                "case_id": case_id,
                "oracle_suite": oracle_document.get("id"),
                "runner": oracle_document.get("runner"),
                "status": "not_available",
                "deterministic": True,
                "detail": "Session archive does not contain workspace/ files.",
                "checks": [],
                "commands": [],
            }
        return verify_workspace(
            oracle_document,
            oracle_path,
            case_id,
            workspace,
            status_writer,
        )


def verify_archive_set(
    suite: dict[str, Any],
    suite_path: Path,
    oracle_document: dict[str, Any],
    oracle_path: Path,
    archives_dir: Path,
    results_dir: Path,
    case_ids: Iterable[str] | None = None,
    skip_existing: bool = False,
    status_writer: Any | None = None,
) -> dict[str, Any]:
    selected = (
        {str(item) for item in case_ids}
        if case_ids
        else {
            str(case.get("id"))
            for case in suite.get("cases") or []
            if isinstance(case, dict)
        }
    )
    results: list[dict[str, Any]] = []
    for case_id in sorted(selected):
        output = results_dir / f"{case_id}.verification.json"
        if skip_existing and output.is_file():
            results.append(read_json(output))
            continue
        archive = archives_dir / f"{case_id}.zip"
        if not archive.is_file():
            raise VerificationError(f"Session archive not found: {archive}")
        result = verify_archive(
            oracle_document,
            oracle_path,
            case_id,
            archive,
            status_writer,
        )
        write_json(output, result)
        results.append(result)

    summary = {
        "case_count": len(results),
        "pass": sum(item.get("status") == "pass" for item in results),
        "fail": sum(item.get("status") == "fail" for item in results),
        "not_available": sum(
            item.get("status") == "not_available" for item in results
        ),
    }
    return {
        "schema": "deepseek-vs.independent-verification-run/1",
        "suite_id": suite.get("id"),
        "suite_version": suite.get("version"),
        "oracle_suite": oracle_document.get("id"),
        "summary": summary,
        "cases": results,
    }


def extract_workspace_snapshot(archive_path: Path, destination: Path) -> bool:
    if not archive_path.is_file():
        raise VerificationError(f"Session archive not found: {archive_path}")
    destination = destination.resolve()
    found = False
    with zipfile.ZipFile(archive_path, "r") as archive:
        for name in archive.namelist():
            if not name.startswith("workspace/") or name.endswith("/"):
                continue
            relative = name[len("workspace/") :]
            target = (destination / relative).resolve()
            try:
                target.relative_to(destination)
            except ValueError as exc:
                raise VerificationError(
                    f"Archive workspace path escapes destination: {name}"
                ) from exc
            target.parent.mkdir(parents=True, exist_ok=True)
            with archive.open(name) as source, target.open("wb") as output:
                shutil.copyfileobj(source, output)
            found = True
    return found


def _oracle_commands(
    document: dict[str, Any],
    oracle_path: Path,
    workspace: Path,
    case_id: str,
    build_dir: Path | None,
) -> list[tuple[list[str], Path]]:
    base_dir = oracle_path.resolve().parent
    project = (base_dir / str(document["oracle_project"])).resolve()
    if document["runner"] == "dotnet":
        workspace_project = workspace / str(document["workspace_project"])
        if not workspace_project.is_file():
            raise VerificationError(
                f"Workspace project not found: {workspace_project}"
            )
        if build_dir is None:
            raise VerificationError(
                "dotnet oracle requires a temporary build directory."
            )
        source_oracle_dir = project.parent
        oracle_copy = build_dir / source_oracle_dir.name
        shutil.copytree(
            source_oracle_dir,
            oracle_copy,
            ignore=shutil.ignore_patterns("bin", "obj", "build*"),
        )
        project = oracle_copy / project.name
        return [
            (
                [
                    "dotnet",
                    "run",
                    "--project",
                    str(project),
                    f"-p:OrderDeskCoreProject={workspace_project}",
                    "--",
                    case_id,
                ],
                oracle_copy,
            )
        ]

    if build_dir is None:
        raise VerificationError("CMake oracle requires a temporary build directory.")
    configure = [
        "cmake",
        "-S",
        str(project),
        "-B",
        str(build_dir),
        "-G",
        "Ninja",
        f"-DWORKSPACE_ROOT={workspace}",
    ]
    build = ["cmake", "--build", str(build_dir)]
    executable = next(
        (
            path
            for path in build_dir.rglob("line_controller_oracle_tests*")
            if path.is_file() and path.suffix.lower() in {"", ".exe"}
        ),
        build_dir / "line_controller_oracle_tests.exe",
    )
    return [
        (configure, base_dir),
        (build, base_dir),
        ([str(executable), case_id], build_dir),
    ]


def _run_oracle_commands(
    document: dict[str, Any],
    oracle_path: Path,
    workspace: Path,
    case_id: str,
    build_dir: Path | None,
) -> list[dict[str, Any]]:
    results: list[dict[str, Any]] = []
    for command, cwd in _oracle_commands(
        document,
        oracle_path,
        workspace,
        case_id,
        build_dir,
    ):
        completed = _run_command(command, cwd, timeout_seconds=180)
        results.append(completed)
        if completed["returncode"] != 0:
            break
    return results


def _run_command(
    command: list[str],
    cwd: Path,
    timeout_seconds: int,
) -> dict[str, Any]:
    started = time.monotonic()
    try:
        completed = subprocess.run(
            command,
            cwd=str(cwd),
            capture_output=True,
            text=True,
            encoding="utf-8",
            errors="replace",
            timeout=timeout_seconds,
            check=False,
        )
        return {
            "command": command,
            "cwd": str(cwd),
            "returncode": completed.returncode,
            "duration_ms": round((time.monotonic() - started) * 1000),
            "stdout": completed.stdout[-12000:],
            "stderr": completed.stderr[-12000:],
        }
    except FileNotFoundError as exc:
        return {
            "command": command,
            "cwd": str(cwd),
            "returncode": 127,
            "duration_ms": round((time.monotonic() - started) * 1000),
            "stdout": "",
            "stderr": str(exc),
        }
    except subprocess.TimeoutExpired as exc:
        return {
            "command": command,
            "cwd": str(cwd),
            "returncode": 124,
            "duration_ms": round((time.monotonic() - started) * 1000),
            "stdout": (exc.stdout or "")[-12000:] if isinstance(exc.stdout, str) else "",
            "stderr": f"Timed out after {timeout_seconds}s.",
        }


def _walk_files(root: Path) -> Iterable[Path]:
    for current_root, directories, files in os.walk(root):
        directories[:] = [
            item
            for item in directories
            if item.lower() not in IGNORED_DIRECTORIES_LOWER
        ]
        for name in files:
            yield Path(current_root) / name


def _normalize_path(value: str) -> str:
    return value.replace("\\", "/").strip("/")


def _matches_rule(path: str, rule: str) -> bool:
    normalized = _normalize_path(path)
    candidate = _normalize_path(rule)
    if not candidate:
        return False
    if any(character in candidate for character in "*?["):
        return fnmatch(normalized, candidate)
    if rule.replace("\\", "/").endswith("/"):
        return normalized == candidate or normalized.startswith(candidate + "/")
    return normalized == candidate
