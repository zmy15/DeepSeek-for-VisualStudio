from __future__ import annotations

import json
import http.server
import subprocess
import sys
import tempfile
import threading
import unittest
from pathlib import Path
from unittest.mock import patch

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))

import judge


class JudgeTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.cases = json.loads((HERE / "cases.v1.json").read_text(encoding="utf-8"))
        cls.rubric = json.loads((HERE / "rubrics.v1.json").read_text(encoding="utf-8"))
        cls.general_case = json.loads(
            (HERE / "general-case.json").read_text(encoding="utf-8")
        )
        cls.general_rubric = json.loads(
            (HERE / "general-rubric.v1.json").read_text(encoding="utf-8")
        )

    def test_case_set_is_valid(self) -> None:
        self.assertEqual(judge.validate_case_set(self.cases, self.rubric), [])

    def test_custom_case_is_valid(self) -> None:
        path = HERE / "custom-cases" / "winui-exp2-improvements.json"
        case = json.loads(path.read_text(encoding="utf-8"))
        self.assertEqual(judge.validate_case(case), [])
        self.assertEqual(judge.validate_rubric(self.rubric), [])

    def test_general_profile_is_valid(self) -> None:
        general_dimensions = tuple(
            item["id"] for item in self.general_rubric["dimensions"]
        )
        self.assertEqual(
            judge.validate_case(
                self.general_case,
                valid_dimensions=general_dimensions,
            ),
            [],
        )
        self.assertEqual(
            judge.validate_rubric(self.general_rubric, expected_dimensions=None),
            [],
        )

    def test_default_judge_model_is_deepseek_flash(self) -> None:
        with patch.dict("os.environ", {"JUDGE_MODEL": ""}, clear=False):
            self.assertEqual(judge.resolve_model(None), "deepseek-flash")

    def test_extract_json_object_accepts_fenced_response(self) -> None:
        parsed = judge.extract_json_object(
            '```json\n{"case_id":"cg-001","scores":[]}\n```'
        )
        self.assertEqual(parsed["case_id"], "cg-001")

    def test_extract_json_object_accepts_surrounding_text(self) -> None:
        parsed = judge.extract_json_object(
            'Result follows:\n{"case_id":"cg-001","summary":"ok"}\nDone.'
        )
        self.assertEqual(parsed["summary"], "ok")

    def test_normalize_and_compute_pass(self) -> None:
        raw = self._valid_result(score=5)
        normalized = judge.normalize_judge_result(raw, "cg-001", self.rubric)
        computed = judge.compute_final_outcome(normalized, self.rubric)

        self.assertEqual(normalized["overall_outcome"], "pass")
        self.assertEqual(computed["outcome"], "pass")
        self.assertEqual(computed["weighted_score"], 5.0)

    def test_critical_failure_forces_fail(self) -> None:
        raw = self._valid_result(score=5)
        raw["critical_failures"] = [
            {"id": "fabricated_execution", "detail": "Claimed a passing build."}
        ]

        normalized = judge.normalize_judge_result(raw, "cg-001", self.rubric)
        computed = judge.compute_final_outcome(normalized, self.rubric)

        self.assertEqual(computed["outcome"], "fail")
        self.assertEqual(computed["rule"], "critical_failure")

    def test_null_scores_are_renormalized(self) -> None:
        raw = self._valid_result(score=4)
        raw["scores"][1]["score"] = None
        raw["scores"][1]["evidence"] = []

        normalized = judge.normalize_judge_result(raw, "cg-001", self.rubric)
        computed = judge.compute_final_outcome(normalized, self.rubric)

        self.assertAlmostEqual(computed["weighted_score"], 4.0)
        self.assertLess(computed["applicable_weight"], 1.0)
        self.assertIn("context_grounding", computed["missing_dimensions"])

    def test_missing_dimension_is_rejected(self) -> None:
        raw = self._valid_result(score=4)
        raw["scores"].pop()

        with self.assertRaises(judge.JudgeResultError):
            judge.normalize_judge_result(raw, "cg-001", self.rubric)

    def test_integer_like_float_score_is_normalized(self) -> None:
        raw = self._valid_result(score=4)
        raw["scores"][0]["score"] = 4.0

        normalized = judge.normalize_judge_result(raw, "cg-001", self.rubric)

        self.assertEqual(normalized["scores"][0]["score"], 4)

    def test_write_json_uses_utf8(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "result.json"
            judge.write_json(path, {"summary": "中文"})
            self.assertIn("中文", path.read_text(encoding="utf-8"))

    def test_cli_dry_run_does_not_require_api_key(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            export_path = Path(tmp) / "export.json"
            export_path.write_text(
                json.dumps(
                    {
                        "schema": "deepseek-vs.turn-evaluation/1",
                        "export_id": "dry-run",
                        "turn": {},
                        "conversation": {},
                    }
                ),
                encoding="utf-8",
            )
            result = subprocess.run(
                [
                    sys.executable,
                    str(HERE / "judge.py"),
                    "score",
                    "--export",
                    str(export_path),
                    "--case-id",
                    "cg-001",
                    "--dry-run",
                ],
                capture_output=True,
                check=False,
            )

            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertIn(b"Dry run OK", result.stdout)

    def test_cli_accepts_custom_case_file(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            export_path = Path(tmp) / "export.json"
            export_path.write_text(
                json.dumps(
                    {
                        "schema": "deepseek-vs.turn-evaluation/1",
                        "export_id": "custom-case",
                        "turn": {},
                        "conversation": {},
                    }
                ),
                encoding="utf-8",
            )
            result = subprocess.run(
                [
                    sys.executable,
                    str(HERE / "judge.py"),
                    "score",
                    "--export",
                    str(export_path),
                    "--case-file",
                    str(HERE / "custom-cases" / "winui-exp2-improvements.json"),
                    "--dry-run",
                ],
                capture_output=True,
                check=False,
            )

            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertIn(b"real-winui-exp2-improvements", result.stdout)

    def test_cli_defaults_to_general_profile(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            export_path = Path(tmp) / "export.json"
            export_path.write_text(
                json.dumps(
                    {
                        "schema": "deepseek-vs.turn-evaluation/1",
                        "export_id": "general",
                        "turn": {"assistant": {"content": "done"}},
                        "conversation": {"tool_trace": []},
                    }
                ),
                encoding="utf-8",
            )
            result = subprocess.run(
                [
                    sys.executable,
                    str(HERE / "judge.py"),
                    "score",
                    "--export",
                    str(export_path),
                    "--dry-run",
                ],
                capture_output=True,
                check=False,
            )

            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertIn(b"general-exported-turn", result.stdout)
            self.assertIn(b"mode=general", result.stdout)

    def test_cli_analyze_writes_flow_evidence(self) -> None:
        with tempfile.TemporaryDirectory() as tmp:
            export_path = Path(tmp) / "export.json"
            output_path = Path(tmp) / "flow.json"
            export_path.write_text(
                json.dumps(
                    {
                        "schema": "deepseek-vs.turn-evaluation/1",
                        "source": {"solution_path": r"C:\work\Sample.sln"},
                        "turn": {"assistant": {"content": "done"}},
                        "conversation": {
                            "tool_trace": [
                                {
                                    "tool_name": "read_file",
                                    "arguments": "{}",
                                    "result_classification": "Success",
                                    "result": "content",
                                }
                            ]
                        },
                    }
                ),
                encoding="utf-8",
            )
            result = subprocess.run(
                [
                    sys.executable,
                    str(HERE / "judge.py"),
                    "analyze",
                    "--export",
                    str(export_path),
                    "--output",
                    str(output_path),
                ],
                capture_output=True,
                check=False,
            )

            self.assertEqual(result.returncode, 0, result.stderr)
            flow = json.loads(output_path.read_text(encoding="utf-8"))
            self.assertEqual(flow["schema"], "deepseek-vs.flow-analysis/1")
            self.assertEqual(flow["summary"]["tool_calls"], 1)

    def test_flow_analysis_detects_handoff_permissions_and_loops(self) -> None:
        export = {
            "schema": "deepseek-vs.turn-evaluation/1",
            "source": {"solution_path": r"C:\work\Sample.sln"},
            "turn": {
                "assistant": {
                    "content": "Completed.",
                    "incomplete": False,
                }
            },
            "conversation": {
                "context_debug": {"tokens": {"estimated": 1000}},
                "tool_trace": [
                    {
                        "tool_name": "request_handoff",
                        "arguments": json.dumps(
                            {
                                "targetAgent": "Edit",
                                "reason": "Apply code changes",
                                "taskDescription": "Update files",
                            }
                        ),
                        "result_classification": "Success",
                        "result": "HANDOFF_REQUESTED Edit",
                    },
                    {
                        "tool_name": "create_file",
                        "arguments": json.dumps(
                            {"filePath": r"C:\other\Bad.cs", "content": "x"}
                        ),
                        "result_classification": "Success",
                        "result": "created",
                    },
                    {
                        "tool_name": "run_in_terminal",
                        "arguments": json.dumps(
                            {"command": "Remove-Item -Recurse .\\bin"}
                        ),
                        "result_classification": "Success",
                        "result": "ok",
                    },
                    {
                        "tool_name": "read_file",
                        "arguments": json.dumps({"filePath": r"C:\work\A.cs"}),
                        "result_classification": "Success",
                        "result": "same",
                    },
                    {
                        "tool_name": "read_file",
                        "arguments": json.dumps({"filePath": r"C:\work\A.cs"}),
                        "result_classification": "Success",
                        "result": "same",
                    },
                ],
            },
            "metrics": {
                "scope": "latest_execution",
                "input_tokens": 900,
                "output_tokens": 100,
                "cache_hit_tokens": 720,
                "cache_miss_tokens": 180,
            },
        }

        flow = judge.analyze_turn_flow(export)

        self.assertTrue(flow["handoffs"][0]["follow_up_compatible"])
        self.assertTrue(flow["permissions"]["has_permission_risk"])
        self.assertTrue(flow["loops"]["has_suspected_loop"])
        self.assertEqual(flow["cache"]["hit_rate"], 0.8)
        self.assertEqual(flow["cache"]["quality"], "high")

    def test_general_result_requires_all_flow_stages(self) -> None:
        raw = self._general_result(score=4)
        normalized = judge.normalize_judge_result(
            raw,
            "general-exported-turn",
            self.general_rubric,
            require_stage_findings=True,
        )
        computed = judge.compute_final_outcome(normalized, self.general_rubric)

        self.assertEqual(len(normalized["stage_findings"]), 6)
        self.assertEqual(computed["outcome"], "pass")

        raw["stage_findings"].pop()
        with self.assertRaises(judge.JudgeResultError):
            judge.normalize_judge_result(
                raw,
                "general-exported-turn",
                self.general_rubric,
                require_stage_findings=True,
            )

    def test_full_session_metrics_take_precedence_over_latest_execution(self) -> None:
        result = judge.analyze_cost_cache(
            {
                "scope": "latest_execution",
                "input_tokens": 100,
                "output_tokens": 10,
                "cache_hit_tokens": 80,
                "cache_miss_tokens": 20,
            },
            {
                "turn_count": 3,
                "tool_call_count": 12,
                "input_tokens": 2000,
                "output_tokens": 200,
                "duration_ms": 15000,
                "first_turn_ttft_ms": 500,
                "cache_hit_rate": 0.75,
            },
            {},
            2,
        )

        self.assertEqual(result["cost"]["metrics_scope"], "full_agent_session")
        self.assertEqual(result["cost"]["input_tokens"], 2000)
        self.assertEqual(result["cache"]["hit_tokens"], 1500)
        self.assertEqual(result["cache"]["hit_rate"], 0.75)
        self.assertTrue(result["cost"]["full_turn_metrics_available"])

    def test_call_judge_uses_openai_compatible_chat_endpoint(self) -> None:
        payload = self._valid_result(score=5)

        class Handler(http.server.BaseHTTPRequestHandler):
            def do_POST(self) -> None:
                length = int(self.headers.get("Content-Length", "0"))
                _ = self.rfile.read(length)
                content = json.dumps(payload, ensure_ascii=False)
                body = json.dumps(
                    {
                        "id": "chatcmpl-mock",
                        "object": "chat.completion",
                        "created": 1,
                        "model": "mock-judge",
                        "choices": [
                            {
                                "index": 0,
                                "message": {"role": "assistant", "content": content},
                                "finish_reason": "stop",
                            }
                        ],
                        "usage": {
                            "prompt_tokens": 10,
                            "completion_tokens": 20,
                            "total_tokens": 30,
                        },
                    }
                ).encode("utf-8")
                self.send_response(200)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(body)))
                self.end_headers()
                self.wfile.write(body)

            def log_message(self, format: str, *args: object) -> None:
                return

        server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        try:
            content, response = judge.call_judge(
                messages=[{"role": "user", "content": "score this"}],
                api_key="test-key",
                base_url=f"http://127.0.0.1:{server.server_port}/v1",
                model="mock-judge",
                temperature=0.0,
                timeout=10.0,
                retries=0,
                json_mode=True,
                verbose=False,
            )
        finally:
            server.shutdown()
            server.server_close()
            thread.join(timeout=2)

        self.assertEqual(response.id, "chatcmpl-mock")
        self.assertEqual(judge.extract_json_object(content)["case_id"], "cg-001")
        self.assertEqual(judge.response_usage(response)["total_tokens"], 30)

    def _valid_result(self, score: int) -> dict:
        return {
            "schema": "deepseek-vs.judge-output/1",
            "case_id": "cg-001",
            "overall_outcome": "pass",
            "scores": [
                {
                    "dimension": dimension,
                    "score": score,
                    "reason": f"{dimension} is supported.",
                    "evidence": [f"evidence for {dimension}"],
                }
                for dimension in judge.EXPECTED_DIMENSIONS
            ],
            "critical_failures": [],
            "evidence": ["conversation.tool_trace supports the result"],
            "summary": "The turn completed the task.",
            "recommended_actions": [],
        }

    def _general_result(self, score: int) -> dict:
        return {
            "schema": "deepseek-vs.judge-output/1",
            "case_id": "general-exported-turn",
            "overall_outcome": "pass",
            "stage_findings": [
                {
                    "stage": stage,
                    "status": "good",
                    "findings": f"{stage} is supported.",
                    "evidence": [f"flow_analysis.{stage}"],
                }
                for stage in judge.GENERAL_STAGES
            ],
            "scores": [
                {
                    "dimension": item["id"],
                    "score": score,
                    "reason": f"{item['id']} is supported.",
                    "evidence": [f"evidence for {item['id']}"],
                }
                for item in self.general_rubric["dimensions"]
            ],
            "critical_failures": [],
            "evidence": ["flow_analysis.completion: reported_complete"],
            "summary": "The overall flow completed the task.",
            "recommended_actions": [],
        }


if __name__ == "__main__":
    unittest.main()
