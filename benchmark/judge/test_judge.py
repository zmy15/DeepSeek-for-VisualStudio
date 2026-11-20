from __future__ import annotations

import json
import http.server
import subprocess
import sys
import tempfile
import threading
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))

import judge


class JudgeTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.cases = json.loads((HERE / "cases.v1.json").read_text(encoding="utf-8"))
        cls.rubric = json.loads((HERE / "rubrics.v1.json").read_text(encoding="utf-8"))

    def test_case_set_is_valid(self) -> None:
        self.assertEqual(judge.validate_case_set(self.cases, self.rubric), [])

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


if __name__ == "__main__":
    unittest.main()
