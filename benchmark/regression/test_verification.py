from __future__ import annotations

import json
import sys
import tempfile
import unittest
import zipfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))

import verification


class VerificationTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.suite = json.loads(
            (HERE / "orderdesk.v1.json").read_text(encoding="utf-8")
        )
        cls.oracle_path = HERE / "orderdesk.oracle.v1.json"
        cls.oracles = json.loads(cls.oracle_path.read_text(encoding="utf-8"))

    def test_oracle_suite_is_valid(self) -> None:
        self.assertEqual(
            verification.validate_oracle_suite(
                self.oracles,
                self.suite,
                self.oracle_path.parent,
            ),
            [],
        )

    def test_read_only_workspace_passes_without_changes(self) -> None:
        result = verification.verify_workspace(
            self.oracles,
            self.oracle_path,
            "rgr-if-boundary",
            HERE / "fixtures" / "OrderDesk",
        )

        self.assertEqual(result["status"], "pass")
        self.assertEqual(result["workspace"]["changed_paths"], [])

    def test_hidden_dotnet_oracle_rejects_unfixed_fixture(self) -> None:
        result = verification.verify_workspace(
            self.oracles,
            self.oracle_path,
            "rgr-if-regular",
            HERE / "fixtures" / "OrderDesk",
        )

        self.assertEqual(result["status"], "fail")
        oracle_check = next(
            item
            for item in result["checks"]
            if item["name"] == "oracle_execution"
        )
        self.assertEqual(oracle_check["status"], "fail")

    def test_directory_rule_allows_modified_test_file(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            workspace = Path(temporary) / "workspace"
            source = HERE / "fixtures" / "OrderDesk"
            import shutil

            shutil.copytree(source, workspace)
            test_file = workspace / "tests" / "OrderDesk.Tests" / "Program.cs"
            test_file.write_text(
                test_file.read_text(encoding="utf-8") + "\n// changed\n",
                encoding="utf-8",
            )

            result = verification.verify_workspace(
                self.oracles,
                self.oracle_path,
                "rgr-if-regular",
                workspace,
            )

        allowed_check = next(
            item
            for item in result["checks"]
            if item["name"] == "allowed_changes"
        )
        self.assertEqual(allowed_check["status"], "pass")

    def test_read_only_workspace_rejects_generated_changes(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            workspace = Path(temporary) / "workspace"
            source = HERE / "fixtures" / "OrderDesk"
            import shutil

            shutil.copytree(source, workspace)
            (workspace / "unexpected.txt").write_text("change", encoding="utf-8")

            result = verification.verify_workspace(
                self.oracles,
                self.oracle_path,
                "rgr-if-boundary",
                workspace,
            )

        self.assertEqual(result["status"], "fail")
        self.assertIn("unexpected.txt", result["workspace"]["changed_paths"])

    def test_extract_workspace_snapshot(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            archive = root / "session.zip"
            with zipfile.ZipFile(archive, "w") as output:
                output.writestr("manifest.json", "{}")
                output.writestr("workspace/src/Order.cs", "class Order {}")

            destination = root / "workspace"
            destination.mkdir()
            self.assertTrue(
                verification.extract_workspace_snapshot(archive, destination)
            )
            self.assertEqual(
                (destination / "src" / "Order.cs").read_text(encoding="utf-8"),
                "class Order {}",
            )

    def test_archive_without_snapshot_is_not_available(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            archive = root / "session.zip"
            with zipfile.ZipFile(archive, "w") as output:
                output.writestr("manifest.json", "{}")

            result = verification.verify_archive(
                self.oracles,
                self.oracle_path,
                "rgr-if-boundary",
                archive,
            )

        self.assertEqual(result["status"], "not_available")

    def test_path_rules_support_directories_and_globs(self) -> None:
        self.assertTrue(
            verification._matches_rule(
                "tests/OrderDesk.Tests/NewTests.cs",
                "tests/OrderDesk.Tests/",
            )
        )
        self.assertTrue(
            verification._matches_rule("src/Feature.cs", "src/*.cs")
        )
        self.assertFalse(
            verification._matches_rule("src/Feature.txt", "src/*.cs")
        )


if __name__ == "__main__":
    unittest.main()
