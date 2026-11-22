from __future__ import annotations

import sys
import tempfile
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))

import harness_compare


class HarnessCompareTests(unittest.TestCase):
    def test_profile_catalog_points_to_existing_profiles(self) -> None:
        catalog = harness_compare.read_json(HERE / "harness-profiles.v1.json")
        for relative in catalog["profiles"]:
            self.assertTrue((HERE / relative).is_file(), relative)

    def test_compares_profiles_with_fixed_models(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            first = root / "ask.json"
            second = root / "edit.json"
            self._write(first, "ask-auto", "partial", 3.0)
            self._write(second, "edit-direct", "pass", 4.5)

            report = harness_compare.compare_harness_runs(
                [("ask", first), ("edit", second)],
                baseline_label="ask",
            )

        self.assertEqual(report["ranking"][0], "edit")
        case = report["cases"][0]
        self.assertEqual(case["profiles"]["ask"]["outcome"], "partial")
        self.assertEqual(
            case["profiles"]["edit"]["score_delta_vs_baseline"],
            1.5,
        )

    def test_rejects_model_mismatch(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            first = root / "first.json"
            second = root / "second.json"
            self._write(first, "ask-auto", "pass", 4.0)
            self._write(
                second,
                "edit-direct",
                "pass",
                4.0,
                agent_models=["other-model"],
            )
            with self.assertRaises(harness_compare.HarnessCompareError):
                harness_compare.compare_harness_runs(
                    [("ask", first), ("edit", second)]
                )

    def _write(
        self,
        path: Path,
        profile: str,
        outcome: str,
        score: float,
        agent_models: list[str] | None = None,
    ) -> None:
        harness_compare.write_json(
            path,
            {
                "schema": "deepseek-vs.regression-run/1",
                "suite_id": "suite",
                "suite_version": "1",
                "agent_models": agent_models or ["deepseek-flash"],
                "judge_models": ["deepseek-flash"],
                "cases": [
                    {
                        "id": "case-1",
                        "dimension": "instruction_following",
                        "outcome": outcome,
                        "score": score,
                        "verification_status": "pass",
                        "critical_failures": [],
                        "harness_profile": {"id": profile},
                    }
                ],
                "summary": {
                    "average_score": score,
                    "critical_failure_count": 0,
                    "agent_usage": {},
                    "judge_usage": {},
                },
            },
        )


if __name__ == "__main__":
    unittest.main()
