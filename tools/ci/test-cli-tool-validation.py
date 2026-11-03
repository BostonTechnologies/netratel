#!/usr/bin/env python3
"""Focused negative checks for optional portable-tool artifact provenance."""

import hashlib
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch
import zipfile

spec = importlib.util.spec_from_file_location("cli_tool_validation", Path(__file__).with_name("validate-cli-tool.py"))
tool = importlib.util.module_from_spec(spec)
spec.loader.exec_module(tool)


class PortableToolInputTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.revision = "a" * 40
        self.version = "1.2.3"
        self.run = {"id": 100, "run_attempt": 2, "path": tool.WORKFLOW, "head_sha": self.revision,
                    "status": "completed", "conclusion": "success", "event": "workflow_dispatch",
                    "head_repository": {"full_name": tool.REPOSITORY}}

    def test_only_exact_successful_source_and_trusted_workflow_can_select_a_package(self):
        for changes in ({"head_sha": "b" * 40}, {"path": ".github/workflows/other.yml"},
                        {"conclusion": "cancelled"}, {"status": "in_progress"},
                        {"event": "pull_request"}, {"head_repository": {"full_name": "example/fork"}}):
            with self.subTest(changes=changes), self.assertRaisesRegex(ValueError, "exactly one"):
                tool.select_build([{**self.run, **changes}], self.revision)
        self.assertEqual(self.run, tool.select_build([self.run, {**self.run, "head_sha": "b" * 40}], self.revision))

    def test_multiple_successful_builds_for_the_same_source_are_ambiguous(self):
        with self.assertRaisesRegex(ValueError, "ambiguous"):
            tool.select_build([self.run, {**self.run, "id": 101}], self.revision)

    def test_run_selection_includes_later_api_pages(self):
        output = json.dumps([self.run]) + "\n" + json.dumps([{**self.run, "id": 101}])
        with patch.object(tool.subprocess, "check_output", return_value=output):
            runs = tool.api("actions/workflows/release-build.yml/runs", "workflow_runs")
        with self.assertRaisesRegex(ValueError, "ambiguous"):
            tool.select_build(runs, self.revision)

    def candidate(self, wrong_attempt=False, duplicate_artifact=False, corrupt_download=False,
                  wrong_checksum=False, wrong_artifact_source=False):
        payload = b"synthetic exact candidate package"
        name = "NetRatel.Cli.1.2.3.nupkg"
        archive = self.root / "trusted.zip"
        with zipfile.ZipFile(archive, "w") as output:
            output.writestr(name, payload)
            output.writestr("SHA256SUMS", ("0" * 64 if wrong_checksum else hashlib.sha256(payload).hexdigest()) + "  " + name + "\n")
        artifact = {"id": 200, "name": "netratel-1.2.3-linux-x64", "expired": False,
                    "digest": "sha256:" + hashlib.sha256(archive.read_bytes()).hexdigest(),
                    "workflow_run": {"id": 100, "head_sha": "b" * 40 if wrong_artifact_source else self.revision}}

        def api(path, field=None):
            if field == "workflow_runs":
                return [self.run]
            if field == "artifacts":
                return [artifact, artifact] if duplicate_artifact else [artifact]
            return {**self.run, "head_sha": "b" * 40 if wrong_attempt else self.revision}

        def download(identity, destination):
            self.assertEqual(200, identity)
            destination.write_bytes(b"modified download" if corrupt_download else archive.read_bytes())

        with patch.object(tool, "api", side_effect=api), patch.object(tool.promotion, "download_artifact", side_effect=download) as downloaded:
            result = tool.candidate_package(self.root, self.version, self.revision)
            self.assertEqual(1, downloaded.call_count)
            return result

    def test_original_authenticated_bytes_and_hosted_checksums_are_both_required(self):
        package, receipt = self.candidate()
        self.assertEqual(b"synthetic exact candidate package", package.read_bytes())
        self.assertEqual((100, 2, self.revision), (receipt["runId"], receipt["attempt"], receipt["sourceSha"]))

    def test_wrong_attempt_artifact_source_and_duplicate_artifacts_fail_closed(self):
        for option in ("wrong_attempt", "wrong_artifact_source", "duplicate_artifact"):
            with self.subTest(option=option), self.assertRaises(ValueError):
                self.candidate(**{option: True})

    def test_modified_download_and_checksum_mismatch_fail_closed(self):
        for option in ("corrupt_download", "wrong_checksum"):
            with self.subTest(option=option), self.assertRaisesRegex(ValueError, "digest differs|SHA256SUMS"):
                self.candidate(**{option: True})

    def test_installed_file_hash_must_match_the_original_package(self):
        package_root = self.root / self.version
        assembly = package_root / "tools/net10.0/any/netratel.dll"
        assembly.parent.mkdir(parents=True)
        assembly.write_bytes(b"authentic assembly")
        inventory = {"files": {"tools/net10.0/any/netratel.dll": hashlib.sha256(assembly.read_bytes()).hexdigest()},
                     "toolPayloadBytes": assembly.stat().st_size}
        result = tool.installed_inventory(self.root, self.version, inventory)
        self.assertEqual(1, result["matchedPackageFiles"])
        assembly.write_bytes(b"different assembly")
        with self.assertRaisesRegex(ValueError, "differs"):
            tool.installed_inventory(self.root, self.version, inventory)

    def test_feed_configuration_clears_external_sources_and_fallback_folders(self):
        config = Path(tool.feed_config(self.root, self.root / "verified-feed", "candidate"))
        source = tool.ET.parse(config).getroot()
        self.assertIsNotNone(source.find("packageSources/clear"))
        self.assertIsNotNone(source.find("fallbackPackageFolders/clear"))
        self.assertEqual(str(self.root / "verified-feed"), source.find("packageSources/add").get("value"))


if __name__ == "__main__":
    unittest.main()
