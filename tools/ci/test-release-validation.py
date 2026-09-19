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
        self.assertEqual(subprocess.run(["sha256sum", "-c", "SHA256SUMS"], cwd=output, capture_output=True).returncode, 0)
        with self.assertRaisesRegex(ValueError, "new directory"):
            self.promotion.stage(inputs, output, "0.1.0-rc.2")

    def test_partial_or_placeholder_image_sets_cannot_finalize_a_bundle(self):
        with self.assertRaisesRegex(ValueError, "All five"):
            self.promotion.validate_digests({"api": "missing"})
        with self.assertRaisesRegex(ValueError, "Invalid/unresolved"):
            self.promotion.validate_digests({name: "ghcr.io/example/image@sha256:REPLACE_AFTER_APPROVED_PUBLIC_RELEASE"
                                            for name in self.promotion.COMPONENTS})

    def test_promotion_uses_outputs_and_embeds_instructions_without_source_checkout(self):
        version = "0.1.0-rc.2"
        archive = self.root / f"netratel-compose-{version}.tar.gz"
        with tarfile.open(archive, "w:gz") as target:
            for name in ("compose.images.yaml", "compose.mcp-http.yaml", ".env.images.example", "release-manifest.json", "INSTALL.md"):
                target.add(ROOT / "release" / name, arcname=name)
            for name in ("LICENSE", "NOTICE"):
                target.add(ROOT / name, arcname=name)
        images = {name: f"ghcr.io/example/{name}@sha256:" + "a" * 64 for name in self.promotion.COMPONENTS}
        self.promotion.finalize_bundle(self.root, version, "b" * 40, images)
        with tarfile.open(archive) as source:
            text = source.extractfile(".env.images.example").read().decode()
            self.assertNotIn("REPLACE_AFTER_APPROVED_PUBLIC_RELEASE", text)
            self.assertIn(images["api"], text)
            self.assertIn("INSTALL.md", source.getnames())
        self.assertEqual(json.loads((self.root / "publication.json").read_text())["publicCommit"], "b" * 40)

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
