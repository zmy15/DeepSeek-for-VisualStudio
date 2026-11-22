from __future__ import annotations

import contextlib
import io
import json
import sys
import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))

import regression


class RegressionTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.suite = json.loads(
            (HERE / "orderdesk.v1.json").read_text(encoding="utf-8")
        )
        cls.references = json.loads(
            (HERE / "reference-answers.v1.json").read_text(encoding="utf-8")
        )
        cls.cpp_suite = json.loads(
            (HERE / "line-controller.v1.json").read_text(encoding="utf-8")
        )
        cls.cpp_references = json.loads(
            (HERE / "line-controller-reference-answers.v1.json").read_text(
                encoding="utf-8"
            )
        )

    def test_suite_is_valid(self) -> None:
        self.assertEqual(regression.validate_suite(self.suite, HERE), [])

    def test_reference_answers_are_valid(self) -> None:
        self.assertEqual(
            regression.validate_reference_answers(self.suite, self.references),
            [],
        )

    def test_cpp_suite_and_reference_answers_are_valid(self) -> None:
        self.assertEqual(
            regression.validate_suite(self.cpp_suite, HERE),
            [],
        )
        self.assertEqual(
            regression.validate_reference_answers(
                self.cpp_suite,
                self.cpp_references,
            ),
            [],
        )
        oracle_path = HERE / "line-controller.oracle.v1.json"
        oracle_doc = json.loads(oracle_path.read_text(encoding="utf-8"))
        import verification

        self.assertEqual(
            verification.validate_oracle_suite(
                oracle_doc,
                self.cpp_suite,
                HERE,
            ),
            [],
        )

    def test_underspecified_suite_is_valid_with_dynamic_user_simulation(self) -> None:
        suite = json.loads(
            (HERE / "underspecified.v1.json").read_text(encoding="utf-8")
        )
        references = json.loads(
            (HERE / "underspecified-reference-answers.v1.json").read_text(
                encoding="utf-8"
            )
        )
        self.assertEqual(regression.validate_suite(suite, HERE), [])
        self.assertEqual(
            regression.validate_reference_answers(suite, references),
            [],
        )
        for case in suite["cases"]:
            self.assertGreaterEqual(case["user_simulation"]["max_turns"], 4)

    def test_cpp_prepare_uses_cmake_and_external_metadata(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            output = Path(tmp) / "line-controller"
            args = SimpleNamespace(
                suite=str(HERE / "line-controller.v1.json"),
                case="cpp-cq-regular",
                output=str(output),
            )
            with contextlib.redirect_stdout(io.StringIO()):
                self.assertEqual(regression.run_prepare(args), 0)

            metadata_dir = output.parent / f"{output.name}.benchmark-meta"
            self.assertTrue((output / "CMakeLists.txt").is_file())
            self.assertTrue((output / ".git").is_dir())
            self.assertTrue((metadata_dir / "case.json").is_file())
            self.assertFalse((output / "_benchmark").exists())
            guide = (metadata_dir / "README.md").read_text(encoding="utf-8")
            self.assertIn("std::numeric_limits<int>::max()", guide)

    def test_suite_defaults_to_deepseek_flash(self) -> None:
        defaults = self.suite["defaults"]
        self.assertEqual(defaults["agent_model"], "deepseek-flash")
        self.assertEqual(defaults["judge_model"], "deepseek-flash")

    def test_suite_covers_every_dimension_with_regular_and_boundary_cases(self) -> None:
        cases = self.suite["cases"]
        for dimension in regression.DIMENSIONS:
            dimension_cases = [
                case for case in cases if case["dimension"] == dimension
            ]
            self.assertGreaterEqual(len(dimension_cases), 2, dimension)
            self.assertTrue(
                any(case["kind"] == "regular" for case in dimension_cases),
                dimension,
            )
            self.assertTrue(
                any(case["kind"] == "boundary" for case in dimension_cases),
                dimension,
            )

    def test_judge_case_preserves_per_turn_checks(self) -> None:
        case = next(
            item for item in self.suite["cases"] if item["id"] == "rgr-if-boundary"
        )
        reference = next(
            item
            for item in self.references["answers"]
            if item["case_id"] == "rgr-if-boundary"
        )
        judge_case = regression.build_judge_case(self.suite, case, reference)

        self.assertEqual(judge_case["id"], "rgr-if-boundary")
        self.assertEqual(len(judge_case["turns"]), 2)
        self.assertIn(
            "引用 CalculateTotal",
            judge_case["turns"][0]["checks"]["must"][0],
        )
        self.assertNotIn(
            "表格",
            " ".join(judge_case["turns"][0]["checks"]["must"]),
        )
        self.assertIn("90", judge_case["reference_answer"]["golden_answer"])

    def test_judge_case_uses_test_first_dedicated_tool_policy(self) -> None:
        case = next(
            item for item in self.suite["cases"] if item["id"] == "rgr-cq-regular"
        )
        judge_case = regression.build_judge_case(self.suite, case)

        self.assertTrue(judge_case["verification_policy"]["test_first"])
        self.assertEqual(
            judge_case["verification_policy"]["preferred_test_tool"],
            "run_tests",
        )
        self.assertEqual(
            judge_case["verification_policy"]["terminal_build_or_test"],
            "forbidden",
        )
        self.assertTrue(
            any("run_tests first" in item for item in judge_case["checks"]["must"])
        )
        self.assertTrue(
            any(
                "run_in_terminal" in item
                for item in judge_case["checks"]["must_not"]
            )
        )

    def test_read_only_judge_case_forbids_tests_and_builds(self) -> None:
        case = next(
            item for item in self.suite["cases"] if item["id"] == "rgr-cg-regular"
        )
        judge_case = regression.build_judge_case(self.suite, case)

        self.assertFalse(judge_case["verification_policy"]["test_first"])
        self.assertTrue(
            any("must not run build" in item for item in judge_case["checks"]["must"])
        )

    def test_prepare_copies_fixture_and_writes_prompts(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            output = Path(tmp) / "case-workspace"
            args = SimpleNamespace(
                suite=str(HERE / "orderdesk.v1.json"),
                case="rgr-cq-regular",
                output=str(output),
            )
            with contextlib.redirect_stdout(io.StringIO()):
                self.assertEqual(regression.run_prepare(args), 0)

            self.assertTrue((output / "OrderDesk.slnx").is_file())
            self.assertTrue(
                (output / "src" / "OrderDesk.Core" / "PricingPolicy.cs").is_file()
            )
            self.assertFalse((output / "src" / "OrderDesk.Core" / "bin").exists())
            self.assertFalse((output / "src" / "OrderDesk.Core" / "obj").exists())
            self.assertTrue((output / ".git").is_dir())
            git_root = regression.subprocess.run(
                ["git", "rev-parse", "--show-toplevel"],
                cwd=str(output),
                capture_output=True,
                text=True,
                encoding="utf-8",
                check=True,
            ).stdout.strip()
            self.assertEqual(Path(git_root).resolve(), output.resolve())
            metadata_dir = output.parent / f"{output.name}.benchmark-meta"
            guide = (metadata_dir / "README.md").read_text(
                encoding="utf-8"
            )
            self.assertIn("deepseek-flash", guide)
            self.assertIn("修复 CustomerFormatter.FormatName", guide)
            self.assertIn("Reference Answer", guide)
            metadata = json.loads(
                (metadata_dir / "case.json").read_text(encoding="utf-8")
            )
            self.assertEqual(metadata["case"]["id"], "rgr-cq-regular")
            self.assertFalse((output / "_benchmark").exists())

    def test_score_dry_run_uses_flash_model_for_every_archive(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            archives_dir = Path(tmp) / "archives"
            results_dir = Path(tmp) / "results"
            archives_dir.mkdir()
            for case in self.suite["cases"]:
                (archives_dir / f"{case['id']}.zip").write_bytes(b"placeholder")

            args = SimpleNamespace(
                suite=str(HERE / "orderdesk.v1.json"),
                archives=str(archives_dir),
                results=str(results_dir),
                model="deepseek-flash",
                base_url=None,
                api_key_env="JUDGE_API_KEY",
                parallel=10,
                no_json_mode=False,
                dry_run=True,
                verbose=False,
                allow_model_mismatch=False,
            )
            completed = SimpleNamespace(returncode=0)
            with patch.object(
                regression.subprocess,
                "run",
                return_value=completed,
            ) as run_mock, contextlib.redirect_stdout(io.StringIO()):
                self.assertEqual(regression.run_score(args), 0)

            self.assertEqual(run_mock.call_count, len(self.suite["cases"]))
            expected_case_files = {
                f"{case['id']}.case.json" for case in self.suite["cases"]
            }
            actual_case_files: set[str] = set()
            for call in run_mock.call_args_list:
                command = call.args[0]
                self.assertIn("--model", command)
                self.assertEqual(
                    command[command.index("--model") + 1],
                    "deepseek-flash",
                )
                case_path = Path(command[command.index("--case-file") + 1])
                self.assertTrue(case_path.is_file())
                actual_case_files.add(case_path.name)
                self.assertIn("--dry-run", command)
            self.assertEqual(actual_case_files, expected_case_files)

    def test_judge_cache_key_tracks_archive_case_and_model(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            archive = Path(tmp) / "case.zip"
            case_file = Path(tmp) / "case.json"
            archive.write_bytes(b"archive-v1")
            case_file.write_text('{"id":"case"}', encoding="utf-8")

            first = regression.judge_cache_key(
                archive,
                case_file,
                "deepseek-flash",
            )
            second = regression.judge_cache_key(
                archive,
                case_file,
                "deepseek-flash",
            )
            other_model = regression.judge_cache_key(
                archive,
                case_file,
                "other-model",
            )
            other_effort = regression.judge_cache_key(
                archive,
                case_file,
                "deepseek-flash",
                thinking_effort="high",
            )
            archive.write_bytes(b"archive-v2")
            changed_archive = regression.judge_cache_key(
                archive,
                case_file,
                "deepseek-flash",
            )

            self.assertEqual(first, second)
            self.assertNotEqual(first, other_model)
            self.assertNotEqual(first, other_effort)
            self.assertNotEqual(first, changed_archive)

    def test_collect_aggregates_judge_results(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            results_dir = Path(tmp)
            self._write_results(results_dir, score=4)

            run = regression.collect_results(self.suite, results_dir)

            self.assertEqual(run["schema"], "deepseek-vs.regression-run/1")
            self.assertEqual(run["summary"]["case_count"], len(self.suite["cases"]))
            self.assertEqual(run["summary"]["outcome_counts"]["pass"], 16)
            self.assertEqual(run["summary"]["average_score"], 4.0)
            self.assertEqual(run["agent_models"], ["deepseek-flash"])
            self.assertEqual(run["judge_models"], ["deepseek-flash"])
            self.assertGreater(run["summary"]["judge_usage"]["prompt_tokens"], 0)
            self.assertEqual(
                run["summary"]["judge_usage"]["cache_hit_rate"],
                0.6,
            )

    def test_collect_rejects_model_mismatch(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            results_dir = Path(tmp)
            self._write_results(results_dir, score=4, agent_model="other-model")

            with self.assertRaises(regression.RegressionError):
                regression.collect_results(self.suite, results_dir)

    def test_collect_supports_case_subset(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            results_dir = Path(tmp)
            self._write_results(results_dir, score=4)
            selected = self.suite["cases"][0]["id"]

            run = regression.collect_results(
                self.suite,
                results_dir,
                case_ids=[selected],
            )

        self.assertEqual(run["summary"]["case_count"], 1)
        self.assertEqual(run["cases"][0]["id"], selected)

    def test_compare_passes_identical_runs(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            results_dir = Path(tmp)
            self._write_results(results_dir, score=4)
            run = regression.collect_results(self.suite, results_dir)

            comparison = regression.compare_runs(run, run)

            self.assertFalse(comparison["regression"])
            self.assertEqual(comparison["failure_count"], 0)
            self.assertEqual(comparison["summary"]["average_score_delta"], 0.0)

    def test_compare_detects_outcome_and_score_regression(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            baseline_dir = Path(tmp) / "baseline"
            candidate_dir = Path(tmp) / "candidate"
            baseline_dir.mkdir()
            candidate_dir.mkdir()
            self._write_results(baseline_dir, score=5)
            self._write_results(candidate_dir, score=3)
            baseline = regression.collect_results(self.suite, baseline_dir)
            candidate = regression.collect_results(
                self.suite,
                candidate_dir,
                allow_model_mismatch=True,
            )
            candidate["cases"][0]["outcome"] = "fail"

            comparison = regression.compare_runs(baseline, candidate)

            self.assertTrue(comparison["regression"])
            rules = {item["rule"] for item in comparison["failures"]}
            self.assertIn("outcome_regression", rules)
            self.assertIn("average_score_regression", rules)

    def test_independent_verification_failure_overrides_judge_pass(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            results_dir = Path(tmp)
            self._write_results(results_dir, score=5)
            case_id = self.suite["cases"][0]["id"]
            (results_dir / f"{case_id}.verification.json").write_text(
                json.dumps(
                    {
                        "schema": "deepseek-vs.independent-verification/1",
                        "case_id": case_id,
                        "status": "fail",
                        "checks": [],
                    }
                ),
                encoding="utf-8",
            )

            run = regression.collect_results(self.suite, results_dir)

        case = next(item for item in run["cases"] if item["id"] == case_id)
        self.assertEqual(case["judge_outcome"], "pass")
        self.assertEqual(case["outcome"], "fail")
        self.assertIn(
            "independent_verification_failure",
            case["critical_failures"],
        )
        self.assertEqual(run["summary"]["verification_counts"]["fail"], 1)

    def _write_results(
        self,
        results_dir: Path,
        score: int,
        agent_model: str = "deepseek-flash",
    ) -> None:
        for case in self.suite["cases"]:
            rounds = []
            for turn in case["turns"]:
                rounds.append(
                    {
                        "turn_index": turn["turn"],
                        "result": {"critical_failures": []},
                        "computed": {
                            "outcome": "pass",
                            "weighted_score": score,
                        },
                        "judge": {
                            "usage": {
                                "prompt_tokens": 100,
                                "completion_tokens": 20,
                                "prompt_cache_hit_tokens": 60,
                                "prompt_cache_miss_tokens": 40,
                            }
                        },
                    }
                )
            result = {
                "schema": "deepseek-vs.session-judge-run/1",
                "case_id": case["id"],
                "manifest": {
                    "source": {
                        "model": agent_model,
                    }
                },
                "rounds": rounds,
                "session_result": {
                    "critical_failures": [],
                    "scores": [
                        {
                            "dimension": dimension,
                            "score": score,
                        }
                        for dimension in (
                            "task_completion",
                            "cross_turn_consistency",
                            "instruction_following",
                            "context_continuity",
                            "tool_handoff_quality",
                            "safety_permission",
                            "loop_control",
                            "efficiency_cache",
                            "final_communication",
                        )
                    ],
                },
                "computed": {
                    "outcome": "pass",
                    "weighted_score": score,
                },
                "judge": {
                    "model": "deepseek-flash",
                    "usage": {
                        "prompt_tokens": 100,
                        "completion_tokens": 20,
                        "prompt_cache_hit_tokens": 60,
                        "prompt_cache_miss_tokens": 40,
                    },
                },
            }
            path = results_dir / f"{case['id']}.judge.json"
            path.write_text(
                json.dumps(result, ensure_ascii=False),
                encoding="utf-8",
            )


if __name__ == "__main__":
    unittest.main()
