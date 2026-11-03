#!/usr/bin/env python3
"""Semantic regression checks for the API's platform-asset comparison."""
import copy
import hashlib
import importlib.util
import json
from pathlib import Path
import re
import tempfile
import unittest

spec = importlib.util.spec_from_file_location("payload", Path(__file__).with_name("compare-api-rid-payload.py"))
payload = importlib.util.module_from_spec(spec)
spec.loader.exec_module(payload)


class ApiRidPayloadTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.control = {path: {"bytes": 100, "sha256": hashlib.sha256(path.encode()).hexdigest()} for path in (
            "NetRatel.API.dll", "NetRatel.API.pdb", "NetRatel.API.deps.json", "runtime.spdx.json",
            "THIRD-PARTY-NOTICES.txt", "System.Drawing.Common.dll", *payload.MOVES.values(),
            "runtimes/win-x64/native/lmdb.dll", "web.config")}
        self.candidate = {key: value.copy() for key, value in self.control.items()
                          if not key.startswith("runtimes/") and key not in payload.LINUX_OMISSIONS}
        for destination, source in payload.MOVES.items():
            self.candidate[destination] = self.control[source].copy()
        self.documents = {}
        for arm, inventory in (("control", self.control), ("candidate", self.candidate)):
            directory = self.root / arm
            directory.mkdir()
            deps = {"runtimeTarget": {"name": "net10.0" + ("/linux-x64" if arm == "candidate" else "")},
                    "libraries": {"synthetic/1.0": {"type": "package"}}}
            (directory / "NetRatel.API.deps.json").write_text(json.dumps(deps))
            files = []
            for path, value in inventory.items():
                if path == "runtime.spdx.json":
                    continue
                name = "./publish/" + path
                files.append({"fileName": name, "SPDXID": "SPDXRef-File-" + re.sub(r"[^A-Za-z0-9.-]", "-", name).rstrip("-"),
                              "checksums": [{"algorithm": "SHA256", "checksumValue": value["sha256"]}],
                              "licenseConcluded": "NOASSERTION"})
            self.documents[arm] = {
                "name": "NetRatel.API", "creationInfo": {"created": "2026-11-03T20:00:00Z", "creators": ["Tool: synthetic"]},
                "packages": [{"name": "NetRatel.API", "SPDXID": "SPDXRef-Package-NetRatel.API"}],
                "files": files,
                "relationships": [{"spdxElementId": "SPDXRef-Package-NetRatel.API", "relationshipType": "CONTAINS",
                                   "relatedSpdxElement": item["SPDXID"]} for item in files]}
        self.write()

    def write(self):
        for arm, document in self.documents.items():
            digest = hashlib.sha256(json.dumps(document["files"], sort_keys=True).encode()).hexdigest()
            document["documentNamespace"] = "https://synthetic.invalid/sbom/9.8.7/" + digest
            (self.root / arm / "runtime.spdx.json").write_text(json.dumps(document))

    def compare(self):
        return payload.compare(self.control, self.candidate, self.root / "control", self.root / "candidate")

    def test_known_runtime_selection_retains_linux_native_symbols_and_metadata(self):
        result = self.compare()
        self.assertIn("runtimes/win-x64/native/lmdb.dll", result["removedRuntimePaths"])
        self.assertEqual(["NetRatel.API.pdb"], result["retainedPdbPaths"])
        self.assertEqual(["web.config"], result["omittedWindowsHostingFiles"])

    def test_missing_or_changed_linux_library_fails(self):
        for value in (None, {"bytes": 100, "sha256": "a" * 64}):
            with self.subTest(value=value):
                candidate = copy.deepcopy(self.candidate)
                if value is None:
                    del self.candidate["liblmdb.so"]
                else:
                    self.candidate["liblmdb.so"] = value
                with self.assertRaisesRegex(ValueError, "runtime asset"):
                    self.compare()
                self.candidate = candidate

    def test_generic_drawing_stub_cannot_substitute_for_selected_unix_asset(self):
        self.candidate["System.Drawing.Common.dll"] = self.control["System.Drawing.Common.dll"].copy()
        for item in self.documents["candidate"]["files"]:
            if item["fileName"] == "./publish/System.Drawing.Common.dll":
                item["checksums"][0]["checksumValue"] = self.candidate["System.Drawing.Common.dll"]["sha256"]
        self.write()
        with self.assertRaisesRegex(ValueError, "System.Drawing.Common"):
            self.compare()

    def test_unrelated_payload_or_symbol_change_fails(self):
        self.candidate["NetRatel.API.dll"] = {"bytes": 101, "sha256": "b" * 64}
        with self.assertRaisesRegex(ValueError, "Unexpected API"):
            self.compare()
        self.candidate["NetRatel.API.dll"] = self.control["NetRatel.API.dll"]
        del self.candidate["NetRatel.API.pdb"]
        with self.assertRaisesRegex(ValueError, "symbol"):
            self.compare()

    def test_package_creator_or_retained_file_metadata_change_fails(self):
        original = copy.deepcopy(self.documents["candidate"])
        for field in ("packages", "creationInfo", "files"):
            with self.subTest(field=field):
                self.documents["candidate"] = copy.deepcopy(original)
                if field == "packages":
                    self.documents["candidate"][field][0]["name"] = "unrelated"
                elif field == "creationInfo":
                    self.documents["candidate"][field]["creators"] = ["unrelated"]
                else:
                    self.documents["candidate"][field][0]["licenseConcluded"] = "unrelated"
                self.write()
                with self.assertRaises(ValueError):
                    self.compare()

    def test_forged_namespace_and_duplicate_relationship_fail(self):
        self.documents["candidate"]["relationships"].append(self.documents["candidate"]["relationships"][0])
        self.write()
        with self.assertRaisesRegex(ValueError, "relationships"):
            self.compare()
        self.documents["candidate"]["relationships"].pop()
        self.write()
        path = self.root / "candidate/runtime.spdx.json"
        path.write_text(path.read_text().replace("synthetic.invalid", "unrelated.invalid"))
        with self.assertRaisesRegex(ValueError, "namespace"):
            self.compare()


if __name__ == "__main__":
    unittest.main()
