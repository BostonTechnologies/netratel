#!/usr/bin/env python3
"""Semantic regression checks for the API's platform-asset comparison."""
import copy
import hashlib
import importlib.util
import json
from pathlib import Path
import re
import shutil
import subprocess
import tempfile
import unittest
from unittest.mock import patch

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

    def test_control_must_retain_the_portable_runtime_target(self):
        path = self.root / "control/NetRatel.API.deps.json"
        document = json.loads(path.read_text())
        document["runtimeTarget"]["name"] += "/linux-x64"
        path.write_text(json.dumps(document))
        with self.assertRaisesRegex(ValueError, "runtime dependency"):
            self.compare()

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


class ApiIdentityWebPayloadTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        projects = ["NetRatel.API", "NetRatel.Infrastructure", "NetRatel.Application", "NetRatel.Shared",
                    "NetRatel.ServiceDefaults", "NetRatel.AgentGateway.Contracts", "NetRatel.Akka"]
        target_name = ".NETCoreApp,Version=v10.0/linux-x64"
        packages = {name: "4.16.1" if name in payload.IDENTITY_ROOTS else "1.2.3" for name in payload.IDENTITY_PACKAGES}
        packages["Duende.IdentityModel"] = "8.1.0"
        packages["Microsoft.IdentityModel.Tokens"] = "8.23.0"
        packages["Microsoft.IdentityModel.Protocols.OpenIdConnect"] = "8.23.0"
        packages["Microsoft.IdentityModel.Protocols"] = "8.23.0"
        target = {name + "/9.8.7": {"runtime": {name + ".dll": {}}} for name in projects}
        for name, version in packages.items():
            target[name + "/" + version] = {"runtime": {name + ".dll": {}}}
        target["NetRatel.API/9.8.7"]["dependencies"] = dict.fromkeys(payload.IDENTITY_ROOTS, "4.16.1") | {
            "Duende.IdentityModel": "8.1.0", "Microsoft.IdentityModel.Tokens": "8.23.0",
            "Microsoft.IdentityModel.Protocols.OpenIdConnect": "8.23.0"}
        target["Microsoft.IdentityModel.Protocols.OpenIdConnect/8.23.0"]["dependencies"] = {
            "Microsoft.IdentityModel.Protocols": "8.23.0", "Microsoft.IdentityModel.Tokens": "8.23.0"}
        target["Microsoft.Identity.Web/4.16.1"]["dependencies"] = {
            name: version for name, version in packages.items() if name in payload.IDENTITY_PACKAGES - payload.IDENTITY_ROOTS}
        libraries = {key: {"type": "project" if key.startswith("NetRatel.") else "package", "sha512": "synthetic"} for key in target}
        control_deps = {"runtimeTarget": {"name": target_name}, "compilationOptions": {},
                        "targets": {".NETCoreApp,Version=v10.0": {}, target_name: target}, "libraries": libraries}
        self.deps = {"control": control_deps, "candidate": copy.deepcopy(control_deps)}
        for key in list(self.deps["candidate"]["libraries"]):
            if key.rsplit("/", 1)[0] in payload.IDENTITY_PACKAGES:
                del self.deps["candidate"]["libraries"][key]
                del self.deps["candidate"]["targets"][target_name][key]
        for name in payload.IDENTITY_ROOTS:
            del self.deps["candidate"]["targets"][target_name]["NetRatel.API/9.8.7"]["dependencies"][name]
        paths = {name + extension for name in projects for extension in (".dll", ".pdb")} | {
            name + ".dll" for name in packages} | {
            "NetRatel.API.deps.json", "NetRatel.API.runtimeconfig.json", "runtime.spdx.json", "THIRD-PARTY-NOTICES.txt",
            "liblmdb.so", "System.Drawing.Common.dll", "docker-entrypoint.sh", "licenses/LICENSE", "licenses/NOTICE"}
        self.inventories, self.documents = {}, {}
        for arm in ("control", "candidate"):
            directory = self.root / arm
            directory.mkdir()
            notice = "Inventory\n\nDeclared upstream metadata\n\nRuntime graph inventory\n\n" + "\n\n".join(
                name + " " + version + "\nlicense: synthetic\n\nA complete retained license paragraph."
                for name, version in sorted(packages.items(), key=lambda item: item[0] + "/" + item[1])
                if arm == "control" or name not in payload.IDENTITY_PACKAGES) + "\n"
            inventory = {}
            for path in sorted(paths - (payload.IDENTITY_FILES if arm == "candidate" else set())):
                value = path.encode()
                if path in ("NetRatel.API.dll", "NetRatel.API.pdb"):
                    value += arm.encode()
                elif path == "NetRatel.API.deps.json":
                    value = json.dumps(self.deps[arm]).encode()
                elif path == "THIRD-PARTY-NOTICES.txt":
                    value = notice.encode()
                destination = directory / path
                destination.parent.mkdir(parents=True, exist_ok=True)
                destination.write_bytes(value)
                inventory[path] = {"bytes": len(value), "sha256": hashlib.sha256(value).hexdigest()}
            self.inventories[arm] = inventory
            spdx_packages = [{"name": key.rsplit("/", 1)[0], "versionInfo": key.rsplit("/", 1)[1],
                              "SPDXID": "SPDXRef-Package-" + key.replace("/", "-"), "licenseDeclared": "MIT"}
                             for key in self.deps[arm]["libraries"]]
            files = []
            for path, value in inventory.items():
                if path in {"runtime.spdx.json", "docker-entrypoint.sh", "licenses/LICENSE", "licenses/NOTICE"}:
                    continue
                name = "./publish/" + path
                files.append({"fileName": name, "SPDXID": "SPDXRef-File-" + re.sub(r"[^A-Za-z0-9.-]", "-", name).rstrip("-"),
                              "checksums": [{"algorithm": "SHA256", "checksumValue": value["sha256"]}],
                              "licenseConcluded": "NOASSERTION"})
            self.documents[arm] = {
                "name": "NetRatel.API", "creationInfo": {"created": "2026-11-03T20:0" + ("0" if arm == "control" else "1") + ":00Z",
                                                          "creators": ["Tool: synthetic"]},
                "packages": spdx_packages, "files": files,
                "relationships": [{"spdxElementId": spdx_packages[0]["SPDXID"], "relationshipType": "CONTAINS", "relatedSpdxElement": item["SPDXID"]} for item in files]
                    + [{"spdxElementId": spdx_packages[0]["SPDXID"], "relationshipType": "DEPENDS_ON", "relatedSpdxElement": item["SPDXID"]} for item in spdx_packages[1:]]}
        self.write()

    def write(self):
        for arm, document in self.documents.items():
            for item in document["files"]:
                path = item["fileName"].removeprefix("./publish/")
                if path in self.inventories[arm]:
                    item["checksums"] = [{"algorithm": "SHA256", "checksumValue": self.inventories[arm][path]["sha256"]}]
            digest = hashlib.sha256(json.dumps(document["files"], sort_keys=True).encode()).hexdigest()
            document["documentNamespace"] = "https://synthetic.invalid/sbom/9.8.7/" + digest
            (self.root / arm / "runtime.spdx.json").write_text(json.dumps(document))
            (self.root / arm / "NetRatel.API.deps.json").write_text(json.dumps(self.deps[arm]))

    def compare(self):
        return payload.compare_identity_web(self.inventories["control"], self.inventories["candidate"], self.root / "control", self.root / "candidate")

    def test_exact_unused_closure_retains_native_symbols_auth_dependencies_and_notices(self):
        result = self.compare()
        self.assertEqual(19, len(result["removedFiles"]))
        self.assertEqual(19, len(result["removedPackages"]))
        self.assertEqual(7, len(result["retainedPdbPaths"]))
        self.assertEqual(4, result["retainedNoticeSectionsIdentical"])

    def test_missing_symbol_unrelated_dll_change_or_extra_file_fails(self):
        original = copy.deepcopy(self.inventories["candidate"])
        for operation in ("symbol", "retained", "native", "extra"):
            with self.subTest(operation=operation):
                self.inventories["candidate"] = copy.deepcopy(original)
                if operation == "symbol":
                    del self.inventories["candidate"]["NetRatel.Shared.pdb"]
                elif operation == "retained":
                    self.inventories["candidate"]["Duende.IdentityModel.dll"]["sha256"] = "a" * 64
                elif operation == "native":
                    self.inventories["candidate"]["liblmdb.so"]["sha256"] = "c" * 64
                else:
                    self.inventories["candidate"]["unrelated.txt"] = {"bytes": 1, "sha256": "b" * 64}
                with self.assertRaises(ValueError):
                    self.compare()

    def test_retained_dependency_version_edge_or_runtime_asset_change_fails(self):
        original = copy.deepcopy(self.deps["candidate"])
        for operation in ("library", "version", "edge", "asset"):
            with self.subTest(operation=operation):
                self.deps["candidate"] = copy.deepcopy(original)
                target = self.deps["candidate"]["targets"][self.deps["candidate"]["runtimeTarget"]["name"]]
                if operation == "library":
                    self.deps["candidate"]["libraries"]["Duende.IdentityModel/8.1.0"]["sha512"] = "changed"
                elif operation == "version":
                    # Real first-attempt regression: the obsolete package had
                    # also supplied the higher active OIDC protocol version.
                    for name in ("Microsoft.IdentityModel.Protocols.OpenIdConnect", "Microsoft.IdentityModel.Protocols"):
                        libraries = self.deps["candidate"]["libraries"]
                        libraries[name + "/8.19.2"] = libraries.pop(name + "/8.23.0")
                        target[name + "/8.19.2"] = target.pop(name + "/8.23.0")
                    target["NetRatel.API/9.8.7"]["dependencies"]["Microsoft.IdentityModel.Protocols.OpenIdConnect"] = "8.19.2"
                    target["Microsoft.IdentityModel.Protocols.OpenIdConnect/8.19.2"]["dependencies"]["Microsoft.IdentityModel.Protocols"] = "8.19.2"
                elif operation == "edge":
                    del target["NetRatel.API/9.8.7"]["dependencies"]["Duende.IdentityModel"]
                else:
                    target["Duende.IdentityModel/8.1.0"]["runtime"] = {"other.dll": {}}
                self.write()
                with self.assertRaisesRegex(ValueError, "dependency|runtime asset"):
                    self.compare()

    def test_retained_spdx_package_document_creator_file_or_relationship_change_fails(self):
        original = copy.deepcopy(self.documents["candidate"])
        for operation in ("package", "document", "creator", "file", "relationship"):
            with self.subTest(operation=operation):
                self.documents["candidate"] = copy.deepcopy(original)
                document = self.documents["candidate"]
                if operation == "package":
                    document["packages"][0]["licenseDeclared"] = "changed"
                elif operation == "document":
                    document["name"] = "changed"
                elif operation == "creator":
                    document["creationInfo"]["creators"] = ["changed"]
                elif operation == "file":
                    document["files"][0]["licenseConcluded"] = "changed"
                else:
                    document["relationships"].append(document["relationships"][0])
                self.write()
                with self.assertRaises(ValueError):
                    self.compare()

    def test_forged_namespace_or_incomplete_spdx_coverage_fails(self):
        path = self.root / "candidate/runtime.spdx.json"
        path.write_text(path.read_text().replace("synthetic.invalid", "unrelated.invalid"))
        with self.assertRaisesRegex(ValueError, "namespace"):
            self.compare()
        self.documents["candidate"]["files"].pop()
        self.write()
        with self.assertRaisesRegex(ValueError, "every actual"):
            self.compare()

    def test_removed_spdx_package_version_must_match_original_runtime_graph(self):
        package = next(item for item in self.documents["control"]["packages"] if item["name"] == "Azure.Core")
        package["versionInfo"] = "99.0.0"
        self.write()
        with self.assertRaisesRegex(ValueError, "SPDX package identity"):
            self.compare()

    def test_retained_license_text_cannot_change_even_with_matching_spdx_hash(self):
        path = self.root / "candidate/THIRD-PARTY-NOTICES.txt"
        content = path.read_bytes().replace(b"A complete retained", b"An altered retained")
        path.write_bytes(content)
        self.inventories["candidate"][path.name] = {"bytes": len(content), "sha256": hashlib.sha256(content).hexdigest()}
        self.write()
        with self.assertRaisesRegex(ValueError, "upstream notice"):
            self.compare()

    def test_reconstruction_changes_only_original_roots_imports_and_shared_diagnostic_metadata(self):
        repository = Path(__file__).resolve().parents[2]
        source = self.root / "source"
        results = self.root / "results"
        results.mkdir()
        paths = ("docker/api/Dockerfile", "Directory.Build.targets", "src/NetRatel/NetRatel.API/NetRatel.API.csproj", "src/NetRatel/NetRatel.API/Program.cs")
        for path in paths:
            destination = source / path
            destination.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(repository / path, destination)
        script = (repository / "tools/ci/test-api-image.sh").read_text()
        code = re.search(r'python3 - "\$temporary/source" "\$results" "\$comparison" <<\'PY\'\n(.*?)\nPY', script, re.S).group(1)
        def inspect(command, **kwargs):
            reference = command[-1]
            return json.dumps([{"RepoDigests": [reference.split(":")[0] + "@sha256:" + "a" * 64]}])
        with patch("sys.argv", ["-", str(source), str(results), "identity-web"]), patch.object(subprocess, "run") as run, patch.object(subprocess, "check_output", side_effect=inspect):
            exec(compile(code, "API controlled source preparation", "exec"), {})
        self.assertEqual(2, run.call_count)
        control = self.root / "control-source"
        project = Path(paths[2])
        restored = (control / project).read_text()
        for name in sorted(payload.IDENTITY_ROOTS):
            line = '    <PackageReference Include="' + name + '" Version="4.16.1" />\n'
            self.assertEqual(1, restored.count(line))
            restored = restored.replace(line, "")
        self.assertEqual((source / project).read_text(), restored)
        program = Path(paths[3])
        restored = (control / program).read_text()
        for namespace in ("Microsoft.Identity.Abstractions", "Microsoft.Identity.Web", "Microsoft.Identity.Web.Resource"):
            line = "using " + namespace + ";\n"
            self.assertEqual(1, restored.count(line))
            restored = restored.replace(line, "")
        self.assertEqual((source / program).read_text(), restored)
        for path in paths[:2]:
            self.assertEqual((source / path).read_bytes(), (control / path).read_bytes())
        receipt = json.loads((results / "controlled-build.json").read_text())
        self.assertEqual("identity-web", receipt["comparison"])
        self.assertNotIn("diagnosticCompilerProperties", receipt)
        self.assertEqual({"Microsoft.IdentityModel.Protocols.OpenIdConnect": "8.23.0"}, receipt["sharedPreservationPin"])
        for arm, directory in (("candidate", source), ("control", control)):
            for path in (project, program):
                self.assertEqual(hashlib.sha256((directory / path).read_bytes()).hexdigest(), receipt["sourceInputSha256"][arm][str(path)])


if __name__ == "__main__":
    unittest.main()
