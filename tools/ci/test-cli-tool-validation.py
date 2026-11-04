#!/usr/bin/env python3
"""Focused negative checks for optional portable-tool artifact provenance."""

import hashlib
import importlib.util
import json
from pathlib import Path
import tempfile
import tarfile
import unittest
from unittest.mock import patch
import sys
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
            output.writestr("cli/" + name, b"raw pack output must not select the distributable")
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
        self.assertEqual(str((self.root / "verified-feed").resolve()), source.find("packageSources/add").get("value"))

    def test_prior_release_json_uses_utf8_instead_of_the_windows_console_encoding(self):
        original = tool.prior_releases.subprocess.run

        def emit_utf8(arguments, **options):
            self.assertEqual("gh", arguments[0])
            self.assertEqual("utf-8", options.get("encoding"))
            return original([sys.executable, "-c", "import sys; sys.stdout.buffer.write('release \\u201d'.encode('utf-8'))"], **options)

        with patch.object(tool.prior_releases.subprocess, "run", side_effect=emit_utf8):
            self.assertEqual("release \u201d", tool.prior_releases.gh("api", "synthetic-release-json"))


class ArtifactSetupInputTests(unittest.TestCase):
    def test_terminal_guard_rejects_visibly_complete_credentials_split_by_ANSI_or_cursor_moves(self):
        credential = "synthetic-private"
        for raw in (b"synthetic-\x1b[0mprivate", b"\x1b[1;1Hsynthetic-\x1b[1;11Hprivate"):
            with self.subTest(raw=raw):
                self.assertNotIn(credential.encode(), raw)
                self.assertIn(credential, tool.setup_fixture.screen(raw))
                with self.assertRaisesRegex(ValueError, "rendered"):
                    tool.setup_fixture.checked_terminal_screen(raw, credential)

    def test_terminal_cursor_diffs_restore_spaces_and_replace_stale_prompts(self):
        text = "Use existing authentication"
        raw = b"\x1b[2J" + b"".join(f"\x1b[2;{column}H{letter}".encode()
                                    for column, letter in enumerate(text, 1) if letter != " ")
        self.assertIn(text, tool.setup_fixture.screen(raw))
        replaced = raw + b"\x1b[2J\x1b[1;1HInstance Web URL"
        self.assertIn("Instance Web URL", tool.setup_fixture.screen(replaced))
        self.assertNotIn(text, tool.setup_fixture.screen(replaced))

    def test_successful_exit_cannot_hide_changed_protected_files_modes_or_credential_output(self):
        before = {name: {"sha256": "a" * 64, "bytes": 10, "mode": 0o600}
                  for name in ("profile.json", "credential.secret")}
        requests = [{"method": "GET", "path": "/api/integration-pairing/v1/capabilities", "authenticated": False, "tls": True},
                    {"method": "GET", "path": "/api/v1/auth/ai-agent/status", "authenticated": True, "tls": True}]
        tool.setup_fixture.validate_preservation(before, before, requests, b"saved", "synthetic-private")
        for changes in ({"sha256": "b" * 64}, {"mode": 0o644}):
            after = {**before, "profile.json": {**before["profile.json"], **changes}}
            with self.subTest(changes=changes), self.assertRaisesRegex(ValueError, "protected"):
                tool.setup_fixture.validate_preservation(before, after, requests, b"saved", "synthetic-private")
        with self.assertRaisesRegex(ValueError, "disclosed"):
            tool.setup_fixture.validate_preservation(before, before, requests, b"synthetic-private", "synthetic-private")
        with self.assertRaisesRegex(ValueError, "unexpected"):
            tool.setup_fixture.validate_preservation(before, before, requests + [{"method": "POST"}], b"saved", "synthetic-private")
        with self.assertRaisesRegex(ValueError, "unexpected"):
            tool.setup_fixture.validate_preservation(before, before, [{**item, "tls": False} for item in requests], b"saved", "synthetic-private")

    def test_runtime_selection_requires_unique_actual_hostfxr_framework_and_root(self):
        trace = "Using dotnet root path [C:\\dotnet]\nResolved fxr [C:\\dotnet\\host\\fxr\\10.0.14\\hostfxr.dll]...\nChose FX version [C:\\dotnet\\shared\\Microsoft.NETCore.App\\10.0.14]\n"
        self.assertEqual("C:\\dotnet\\host\\fxr\\10.0.14\\hostfxr.dll", tool.selected_runtime_paths(trace)["hostfxr"])
        for modified in (trace.replace("Resolved fxr", "not a selection"),
                         trace + "Chose FX version [C:\\dotnet\\shared\\Microsoft.NETCore.App\\10.0.15]\n"):
            with self.assertRaisesRegex(ValueError, "missing/ambiguous"):
                tool.selected_runtime_paths(modified)

    def test_archive_extraction_rejects_traversal_duplicate_paths_and_links(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            for variation in ("traversal", "duplicate", "link"):
                with self.subTest(variation=variation):
                    path = root / (variation + ".tar.gz")
                    with tarfile.open(path, "w:gz") as archive:
                        member = tarfile.TarInfo("../outside" if variation == "traversal" else "payload")
                        if variation == "link":
                            member.type = tarfile.SYMTYPE
                            member.linkname = "/outside"
                        archive.addfile(member)
                        if variation == "duplicate":
                            archive.addfile(member)
                    with self.assertRaisesRegex(ValueError, "unsafe, duplicate or special"):
                        tool.safe_tar_extract(path, root / "expanded")


if __name__ == "__main__":
    unittest.main()
