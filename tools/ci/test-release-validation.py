#!/usr/bin/env python3
"""Negative release-gate tests using isolated source fixtures."""
import importlib.util
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest
import hashlib
import tarfile
import zipfile


def module(name):
    spec = importlib.util.spec_from_file_location(name, ROOT / "tools/ci" / (name + ".py"))
    loaded = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(loaded)
    return loaded

ROOT = Path(__file__).resolve().parents[2]
SCANNER = ROOT / "tools/ci/verify-product-version.py"


class ProductVersionTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="netratel-version-tests-")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        (self.root / "tools/ci").mkdir(parents=True)
        (self.root / "release").mkdir()
        (self.root / "src/NetRatel/Fixture").mkdir(parents=True)
        for name in ("verify-product-version.py", "verify-product-version.sh"):
            shutil.copy2(ROOT / "tools/ci" / name, self.root / "tools/ci" / name)
        shutil.copy2(ROOT / "Directory.Build.props", self.root / "Directory.Build.props")
        self.project = self.root / "src/NetRatel/Fixture/Fixture.csproj"
        self.project.write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>')
        (self.root / "release/release-manifest.json").write_text(json.dumps({"version": "0.1.0-rc.2"}))

    def run_gate(self, **environment):
        return subprocess.run(["/bin/bash", str(self.root / "tools/ci/verify-product-version.sh")],
                              env={**os.environ, **environment}, capture_output=True, text=True)

    def test_conditional_nested_and_malformed_overrides_fail(self):
        for contents in ('<Project><PropertyGroup><Version Condition="true">9.0.0</Version></PropertyGroup></Project>',
                         '<Project><PropertyGroup><PackageVersion>9.0.0</PackageVersion></PropertyGroup></Project>',
                         '<Project>'):
            with self.subTest(contents=contents):
                (self.project.parent / "nested.targets").write_text(contents)
                result = self.run_gate()
                self.assertNotEqual(result.returncode, 0, result.stdout)
                self.assertRegex(result.stderr, "local product version|Cannot parse")

    def test_missing_and_broken_scanner_fail(self):
        result = self.run_gate(PATH="/nonexistent")
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("unavailable", result.stderr)
        (self.root / "tools/ci/verify-product-version.py").write_text("raise RuntimeError('broken scanner')")
        result = self.run_gate()
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("broken scanner", result.stderr)

    def test_tag_mismatch_fails_before_msbuild(self):
        result = self.run_gate(GITHUB_REF_TYPE="tag", GITHUB_REF_NAME="v9.0.0")
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("does not match", result.stderr)

    def test_stable_and_candidate_evaluated_composition(self):
        for suffix in ("rc.1", "rc.2", ""):
            with self.subTest(suffix=suffix):
                result = subprocess.run(["dotnet", "msbuild", str(self.project), "-nologo",
                    f"-p:VersionSuffix={suffix}",
                    "-getProperty:Version,PackageVersion,InformationalVersion,AssemblyVersion,FileVersion"],
                    capture_output=True, text=True)
                self.assertEqual(result.returncode, 0, result.stderr)
                properties = json.loads(result.stdout)["Properties"]
                version = "0.1.0" + ("-" + suffix if suffix else "")
                for name in ("Version", "PackageVersion", "InformationalVersion"):
                    self.assertEqual(properties[name], version)
                self.assertEqual(properties["AssemblyVersion"], "0.1.0.0")
                self.assertEqual(properties["FileVersion"], "0.1.0.0")


class DistributionTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="netratel-distribution-tests-")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.promotion = module("promote-release")
        self.verifier = module("verify-runtime-sbom")

    def receipt(self, revision="b" * 40):
        version = "0.1.0-rc.2"
        return {
            "repository": self.promotion.REPOSITORY,
            "workflow": ".github/workflows/release-build.yml",
            "runId": 1,
            "attempt": 1,
            "headSha": revision,
            "productVersion": version,
            "files": {
                name: {"artifact": "release-artifacts", "artifactId": 11,
                       "artifactDigest": "sha256:" + "c" * 64, "path": name, "sha256": "a" * 64}
                for name in self.promotion.required_artifacts(version)
            }
        }

    def receipt_for_directory(self, directory, revision="b" * 40):
        receipt = self.receipt(revision)
        for name, item in receipt["files"].items():
            item["sha256"] = hashlib.sha256((directory / name).read_bytes()).hexdigest()
        return receipt

    def populate_flat_output(self, directory, version="0.1.0-rc.2"):
        directory.mkdir(parents=True, exist_ok=True)
        for name in self.promotion.required_artifacts(version):
            (directory / name).write_bytes(name.encode())
        self.promotion.checksums(directory)

    def test_flat_staging_rejects_missing_files_wrong_checksums_and_nested_names(self):
        inputs = self.root / "inputs"
        inputs.mkdir()
        sums = inputs / "SHA256SUMS"
        sums.write_text("")
        with self.assertRaisesRegex(ValueError, "Missing required"):
            self.promotion.stage(inputs, self.root / "output", "0.1.0-rc.2")
        sums.write_text("0" * 64 + "  missing.tar.gz\n")
        with self.assertRaisesRegex(ValueError, "Missing/corrupt"):
            self.promotion.stage(inputs, self.root / "output", "0.1.0-rc.2")
        sums.write_text("0" * 64 + "  cli/nested.nupkg\n")
        with self.assertRaisesRegex(ValueError, "flat safe"):
            self.promotion.stage(inputs, self.root / "output", "0.1.0-rc.2")

    def test_flat_staging_verifies_all_required_downloads_without_rearrangement(self):
        inputs = self.root / "inputs"
        inputs.mkdir()
        lines = []
        for name in self.promotion.required_artifacts("0.1.0-rc.2"):
            (inputs / name).write_bytes(name.encode())
            lines.append(f"{hashlib.sha256(name.encode()).hexdigest()}  {name}\n")
        (inputs / "SHA256SUMS").write_text("".join(lines))
        output = self.root / "flat"
        self.promotion.stage(inputs, output, "0.1.0-rc.2")
        self.promotion.verify_staged(output, "0.1.0-rc.2")
        self.assertEqual(subprocess.run(["sha256sum", "-c", "SHA256SUMS"], cwd=output, capture_output=True).returncode, 0)
        with self.assertRaisesRegex(ValueError, "new directory"):
            self.promotion.stage(inputs, output, "0.1.0-rc.2")
        (output / self.promotion.required_artifacts("0.1.0-rc.2")[0]).write_bytes(b"corrupted after staging")
        with self.assertRaisesRegex(ValueError, "changed or missing"):
            self.promotion.verify_staged(output, "0.1.0-rc.2")

    def test_staging_rejects_unexpected_output_files(self):
        output = self.root / "output"
        self.populate_flat_output(output)
        (output / "unreviewed-upload.txt").write_text("must not be published")
        with self.assertRaisesRegex(ValueError, "unexpected files"):
            self.promotion.verify_staged(output, "0.1.0-rc.2")

    def test_partial_resume_preserves_completed_digest_and_rejects_wrong_source_or_digest(self):
        path = self.root / "journal.json"
        receipt = self.receipt()
        state = self.promotion.resume_state(path, "0.1.0-rc.2", "b" * 40, "public-candidate", receipt)
        reference = "ghcr.io/bostontechnologies/public-candidate-api@sha256:" + "a" * 64
        state["images"]["api"] = reference
        path.write_text(json.dumps(state))
        self.assertEqual(self.promotion.resume_state(path, "0.1.0-rc.2", "b" * 40, "public-candidate", receipt)["images"], {"api": reference})
        with self.assertRaisesRegex(ValueError, "different approved"):
            self.promotion.resume_state(path, "0.1.0-rc.2", "c" * 40, "public-candidate", self.receipt("c" * 40))
        changed_receipt = self.receipt()
        changed_receipt["files"][next(iter(changed_receipt["files"]))]["sha256"] = "c" * 64
        with self.assertRaisesRegex(ValueError, "different approved"):
            self.promotion.resume_state(path, "0.1.0-rc.2", "b" * 40, "public-candidate", changed_receipt)
        state["images"]["api"] = reference[:-64] + "REPLACE_AFTER_APPROVED_PUBLIC_RELEASE"
        path.write_text(json.dumps(state))
        with self.assertRaisesRegex(ValueError, "invalid digest"):
            self.promotion.resume_state(path, "0.1.0-rc.2", "b" * 40, "public-candidate", receipt)

    def test_partial_or_placeholder_image_sets_cannot_finalize_a_bundle(self):
        with self.assertRaisesRegex(ValueError, "All five"):
            self.promotion.validate_digests({"api": "missing"})
        with self.assertRaisesRegex(ValueError, "Invalid/unresolved"):
            self.promotion.validate_digests({name: "ghcr.io/example/image@sha256:REPLACE_AFTER_APPROVED_PUBLIC_RELEASE"
                                            for name in self.promotion.COMPONENTS})

    def test_promotion_uses_outputs_and_embeds_instructions_without_source_checkout(self):
        version = "0.1.0-rc.2"
        for name in self.promotion.required_artifacts(version):
            (self.root / name).write_bytes(name.encode())
        archive = self.root / f"netratel-compose-{version}.tar.gz"
        with tarfile.open(archive, "w:gz") as target:
            for name in ("compose.images.yaml", "compose.mcp-http.yaml", ".env.images.example", "release-manifest.json", "INSTALL.md"):
                target.add(ROOT / "release" / name, arcname=name)
            for name in ("LICENSE", "NOTICE"):
                target.add(ROOT / name, arcname=name)
        images = {name: f"ghcr.io/example/{name}@sha256:" + "a" * 64 for name in self.promotion.COMPONENTS}
        self.promotion.checksums(self.root)
        receipt = self.receipt_for_directory(self.root)
        self.promotion.finalize_bundle(self.root, version, "b" * 40, images, receipt)
        with tarfile.open(archive) as source:
            text = source.extractfile(".env.images.example").read().decode()
            self.assertNotIn("REPLACE_AFTER_APPROVED_PUBLIC_RELEASE", text)
            self.assertIn(images["api"], text)
            self.assertIn("INSTALL.md", source.getnames())
        self.assertFalse((self.root / "publication.json").exists())
        self.assertEqual(json.loads((self.root / "publication.candidate.json").read_text())["publicCommit"], "b" * 40)
        self.promotion.complete_bundle(self.root, version, "b" * 40, images, receipt)
        self.assertEqual(json.loads((self.root / "publication.json").read_text())["verification"]["state"], "complete")

    def test_receipt_identity_rejects_wrong_source_missing_files_and_invalid_digest(self):
        receipt = self.receipt()
        self.promotion.validate_input_receipt_identity(receipt, "0.1.0-rc.2", "b" * 40)
        receipt["headSha"] = "c" * 40
        with self.assertRaisesRegex(ValueError, "approved repository"):
            self.promotion.validate_input_receipt_identity(receipt, "0.1.0-rc.2", "b" * 40)
        receipt = self.receipt()
        receipt["files"].pop(next(iter(receipt["files"])))
        with self.assertRaisesRegex(ValueError, "every required"):
            self.promotion.validate_input_receipt_identity(receipt, "0.1.0-rc.2", "b" * 40)
        receipt = self.receipt()
        receipt["files"][next(iter(receipt["files"]))]["sha256"] = "not-a-digest"
        with self.assertRaisesRegex(ValueError, "invalid identity"):
            self.promotion.validate_input_receipt_identity(receipt, "0.1.0-rc.2", "b" * 40)

    def test_authenticated_receipt_accepts_explicit_root_path_with_duplicate_nested_package(self):
        version = "0.1.0-rc.2"
        inputs = self.root / "inputs"
        inputs.mkdir()
        receipt = self.receipt()
        lines = []
        for name in self.promotion.required_artifacts(version):
            content = name.encode()
            (inputs / name).write_bytes(content)
            digest = hashlib.sha256(content).hexdigest()
            receipt["files"][name]["sha256"] = digest
            lines.append(f"{digest}  {name}\n")
        (inputs / "SHA256SUMS").write_text("".join(lines))
        producer = self.root / "producer"
        producer.mkdir()
        for source in inputs.iterdir():
            if source.is_file() and source.name != "SHA256SUMS":
                shutil.copy2(source, producer / source.name)
        nested = producer / "cli"
        nested.mkdir()
        package = f"NetRatel.Cli.{version}.nupkg"
        shutil.copy2(producer / package, nested / package)
        artifact_zip = self.root / "release-artifacts.zip"
        with zipfile.ZipFile(artifact_zip, "w") as archive:
            for path in producer.rglob("*"):
                if path.is_file():
                    archive.write(path, path.relative_to(producer).as_posix())
        receipt["files"][package]["path"] = package
        receipt["files"][package]["artifactDigest"] = "sha256:" + hashlib.sha256(artifact_zip.read_bytes()).hexdigest()
        for name, item in receipt["files"].items():
            item["artifactDigest"] = receipt["files"][package]["artifactDigest"]
        receipt_path = self.root / "release-receipt.json"
        receipt_path.write_text(json.dumps(receipt))

        original_run = self.promotion.run
        commands = []
        def run_success(*command, env=None):
            commands.append(command)
            if command[:2] == ("gh", "api") and "/artifacts?" not in command[2]:
                if "/attempts/2" in command[2]:
                    return json.dumps({"conclusion": "failure", "head_sha": "b" * 40,
                                       "path": ".github/workflows/release-build.yml"})
                return json.dumps({"conclusion": "success", "head_sha": "b" * 40,
                                   "path": ".github/workflows/release-build.yml"})
            if command[:2] == ("gh", "api"):
                return json.dumps({"artifacts": [{"id": 11, "name": "release-artifacts", "expired": False,
                                                   "digest": receipt["files"][package]["artifactDigest"]}]})
            raise AssertionError(command)

        self.promotion.run = run_success
        original_download = self.promotion.download_artifact
        try:
            self.promotion.download_artifact = lambda artifact_id, destination: shutil.copy2(artifact_zip, destination)
            identity = self.promotion.verified_input_receipt(inputs, receipt_path, version, "b" * 40)
            self.assertEqual(identity["files"], receipt["files"])
            self.assertIn(("gh", "api", "repos/BostonTechnologies/netratel/actions/runs/1/artifacts?per_page=100"), commands)
            self.assertFalse(any("/attempts/1/artifacts" in command[2] for command in commands if len(command) > 2))
            self.promotion.download_artifact = lambda artifact_id, destination: Path(destination).write_bytes(b"modified ZIP")
            with self.assertRaisesRegex(ValueError, "download digest"):
                self.promotion.verified_input_receipt(inputs, receipt_path, version, "b" * 40)
            self.promotion.download_artifact = lambda artifact_id, destination: shutil.copy2(artifact_zip, destination)
            receipt["files"][package]["path"] = "not-the-approved-root/" + package
            receipt_path.write_text(json.dumps(receipt))
            with self.assertRaisesRegex(ValueError, "differs"):
                self.promotion.verified_input_receipt(inputs, receipt_path, version, "b" * 40)
            receipt["files"][package]["path"] = package
            receipt["attempt"] = 2
            receipt_path.write_text(json.dumps(receipt))
            with self.assertRaisesRegex(ValueError, "not successful"):
                self.promotion.verified_input_receipt(inputs, receipt_path, version, "b" * 40)
            self.promotion.run = lambda *command, **kwargs: json.dumps({
                "conclusion": "failure", "head_sha": "b" * 40,
                "path": ".github/workflows/release-build.yml"})
            with self.assertRaisesRegex(ValueError, "not successful"):
                self.promotion.verified_input_receipt(inputs, receipt_path, version, "b" * 40)
        finally:
            self.promotion.run = original_run
            self.promotion.download_artifact = original_download

    def test_artifact_zip_download_streams_binary_stdout_without_unsupported_output_flag(self):
        destination = self.root / "artifact.zip"
        original_run = self.promotion.subprocess.run
        calls = []
        def binary_download(command, **kwargs):
            calls.append((command, kwargs))
            self.assertEqual(command, ("gh", "api", "repos/BostonTechnologies/netratel/actions/artifacts/77/zip"))
            self.assertNotIn("text", kwargs)
            self.assertNotIn("capture_output", kwargs)
            self.assertNotIn("--output", command)
            kwargs["stdout"].write(b"ZIP bytes")
            return subprocess.CompletedProcess(command, 0)
        self.promotion.subprocess.run = binary_download
        try:
            self.promotion.download_artifact(77, destination)
        finally:
            self.promotion.subprocess.run = original_run
        self.assertEqual(destination.read_bytes(), b"ZIP bytes")
        self.assertEqual(len(calls), 1)

    def test_staged_bytes_must_match_receipt_before_and_after_candidate(self):
        version = "0.1.0-rc.2"
        output = self.root / "output"
        self.populate_flat_output(output, version)
        receipt = self.receipt_for_directory(output)
        self.promotion.verify_pristine_staged(output, version, receipt)
        for name in self.promotion.required_artifacts(version):
            with self.subTest(name=name):
                original = (output / name).read_bytes()
                (output / name).write_bytes(b"substituted-" + name.encode())
                self.promotion.checksums(output)
                with self.assertRaisesRegex(ValueError, "artifact map|authenticated input receipt"):
                    self.promotion.verify_pristine_staged(output, version, receipt)
                (output / name).write_bytes(original)
        self.promotion.checksums(output)

    def test_candidate_binds_every_non_derived_artifact(self):
        version = "0.1.0-rc.2"
        archive = self.root / f"netratel-compose-{version}.tar.gz"
        with tarfile.open(archive, "w:gz") as target:
            for name in ("compose.images.yaml", "compose.mcp-http.yaml", ".env.images.example", "release-manifest.json", "INSTALL.md"):
                target.add(ROOT / "release" / name, arcname=name)
            for name in ("LICENSE", "NOTICE"):
                target.add(ROOT / name, arcname=name)
        for name in self.promotion.required_artifacts(version):
            path = self.root / name
            if not path.exists():
                path.write_bytes(name.encode())
        self.promotion.checksums(self.root)
        receipt = self.receipt_for_directory(self.root)
        images = {name: f"ghcr.io/example/{name}@sha256:" + "a" * 64 for name in self.promotion.COMPONENTS}
        self.promotion.finalize_bundle(self.root, version, "b" * 40, images, receipt)
        self.promotion.validate_candidate_bundle(self.root, version, "b" * 40, images, receipt)
        for name in self.promotion.required_artifacts(version):
            if name == archive.name:
                continue
            with self.subTest(name=name):
                original = (self.root / name).read_bytes()
                (self.root / name).write_bytes(b"substituted")
                self.promotion.checksums(self.root)
                with self.assertRaisesRegex(ValueError, "authenticated input receipt"):
                    self.promotion.validate_candidate_bundle(self.root, version, "b" * 40, images, receipt)
                (self.root / name).write_bytes(original)
        self.promotion.checksums(self.root)

    def test_directory_only_inventory_and_wrong_file_digests_are_rejected(self):
        with self.assertRaisesRegex(ValueError, "first-party"):
            self.verifier.verify({"packages": [{"name": "artifacts"}]}, self.root)
        (self.root / "file").write_bytes(b"actual bytes")
        document = {
            "packages": [{"name": name, "SPDXID": f"SPDXRef-{index}", "externalRefs": [{"referenceType": "purl"}]}
                         for index, name in enumerate(("NetRatel.Client", "Akka", "runtime"))],
            "files": [{"fileName": "file", "SPDXID": "SPDXRef-file",
                       "checksums": [{"algorithm": "SHA256", "checksumValue": "0" * 64}]}],
            "relationships": []
        }
        with self.assertRaisesRegex(ValueError, "placeholder"):
            self.verifier.verify(document, self.root)
        document["files"][0]["checksums"][0]["checksumValue"] = "1" * 64
        with self.assertRaisesRegex(ValueError, "Incorrect"):
            self.verifier.verify(document, self.root)
        document["files"][0]["checksums"][0]["checksumValue"] = hashlib.sha256(b"actual bytes").hexdigest()
        self.verifier.verify(document, self.root)


if __name__ == "__main__":
    unittest.main()
