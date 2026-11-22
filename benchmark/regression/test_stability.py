from __future__ import annotations

import copy
import sys
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))

import stability


class StabilityTests(unittest.TestCase):
    def test_pass_at_k_and_variance(self) -> None:
        runs = [
            self._run("pass", 4.0),
            self._run("fail", 2.0),
            self._run("pass", 3.0),
        ]

        report = stability.summarize(copy.deepcopy(runs))
        case = report["cases"][0]

        self.assertEqual(report["run_count"], 3)
        self.assertEqual(case["outcome_counts"]["pass"], 2)
        self.assertEqual(case["pass_at_1"], 0.6667)
        self.assertEqual(case["pass_at_k"]["3"], 1.0)
        self.assertTrue(case["outcome_variance"])
        self.assertTrue(case["score_variance"])
        self.assertEqual(case["score"]["min"], 2.0)
        self.assertEqual(case["score"]["max"], 4.0)

    def test_rejects_model_mismatch(self) -> None:
        first = self._run("pass", 4.0)
        second = self._run("pass", 4.0)
        second["agent_models"] = ["other-model"]

        with self.assertRaises(stability.StabilityError):
            stability.summarize([first, second])

    def _run(self, outcome: str, score: float) -> dict:
        return {
            "schema": "deepseek-vs.regression-run/1",
            "suite_id": "suite",
            "suite_version": "1",
            "agent_models": ["deepseek-flash"],
            "judge_models": ["deepseek-flash"],
            "cases": [
                {
                    "id": "case-1",
                    "dimension": "instruction_following",
                    "outcome": outcome,
                    "score": score,
                    "verification_status": "pass",
                }
            ],
            "summary": {
                "average_score": score,
                "critical_failure_count": 0,
            },
        }


if __name__ == "__main__":
    unittest.main()
