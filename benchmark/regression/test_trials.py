from __future__ import annotations

import contextlib
import io
import sys
import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))

import trials


class TrialTests(unittest.TestCase):
    def test_dry_run_builds_repeated_plan(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            args = SimpleNamespace(
                suite=str(HERE / "underspecified.v1.json"),
                case=["mt-if-regular"],
                runs=3,
                output=str(Path(temporary) / "out"),
                runner=str(trials.DEFAULT_RUNNER),
                profile=str(
                    HERE.parent / "headless" / "profiles" / "ask-auto.json"
                ),
                agent_model="deepseek-flash",
                judge_model="deepseek-flash",
                base_url="https://api.deepseek.com",
                judge_base_url=None,
                user_sim_model="deepseek-flash",
                max_turns=4,
                judge_parallel=1,
                judge_api_key_env="JUDGE_API_KEY",
                no_verify=False,
                no_thinking=False,
                thinking_effort="low",
                dry_run=True,
            )

            with contextlib.redirect_stdout(io.StringIO()):
                report = trials.run_trials(args)

        self.assertEqual(report["schema"], "deepseek-vs.trial-plan/1")
        self.assertEqual(report["runs"], 3)
        self.assertEqual(report["case_count"], 1)


if __name__ == "__main__":
    unittest.main()
