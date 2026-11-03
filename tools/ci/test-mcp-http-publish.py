#!/usr/bin/env python3
"""Focused checks for actual MCP image inventory acceptance, without product builds."""
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

spec = importlib.util.spec_from_file_location("inventory", Path(__file__).with_name("inspect-mcp-http-publish.py"))
helper = importlib.util.module_from_spec(spec)
spec.loader.exec_module(helper)


class McpImageInventoryTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.candidate = self.publish("candidate", "linux-x64")
        self.control = self.publish("control", None)
        windows = self.control / "runtimes/win/lib/net10.0/WindowsOnly.dll"
        windows.parent.mkdir(parents=True)
        windows.write_bytes(b"runtime-only" * 100)

    def publish(self, name, runtime):
        root = self.root / name
        core = {"name": "Microsoft.NETCore.App", "version": "10.0.0"}
        for index, (role, (relative, assembly)) in enumerate(helper.DISTRIBUTIONS.items()):
            directory = root / relative
            directory.mkdir(parents=True, exist_ok=True)
            libraries = {"NetRatel.Shared/1.0.0": {"type": "project"}}
            if role != "cli":
                libraries.update({name + "/1.0.0": {"type": "package"} for name in (
                    "NetRatel.Mcp.Core", "ModelContextProtocol", "ModelContextProtocol.Core", "NJsonSchema", "Newtonsoft.Json")})
            deps = {"runtimeTarget": {"name": ".NETCoreApp,Version=v10.0" + ("/" + runtime if runtime else "")},
                    "libraries": libraries}
            (directory / (assembly + ".deps.json")).write_text(json.dumps(deps))
            frameworks = {"framework": core} if role != "http" else {
                "frameworks": [core, {"name": "Microsoft.AspNetCore.App", "version": "10.0.0"}]}
            (directory / (assembly + ".runtimeconfig.json")).write_text(json.dumps({"runtimeOptions": frameworks}))
            (directory / (assembly + ".pdb")).write_bytes(b"retained symbols")
            (directory / "THIRD-PARTY-NOTICES.txt").write_text("legal text")
            (directory / "NetRatel.Shared.dll").write_bytes(bytes([index]) * 16)
        (root / "http-mcp-windows.ps1").write_text("supported Windows launcher text")
        return root

    def compare(self):
        return helper.compare(helper.inventory(self.candidate), helper.inventory(self.control))

    def test_runtime_selection_preserves_windows_launcher_and_hashes_shared_assemblies_separately(self):
        candidate = helper.inventory(self.candidate)
        self.assertEqual(3, len({item["sha256"] for item in candidate["sharedAssemblies"].values()}))
        result = self.compare()
        self.assertGreater(result["expandedBytesSaved"], 0)
        self.assertNotIn("http-mcp-windows.ps1", result["removedFiles"])
        self.assertEqual(3, len(result["retainedSymbolPaths"]))

    def test_wrong_platform_runtime_files_fail_even_with_a_valid_linux_dependency_graph(self):
        path = self.candidate / "tooling/cli/runtimes/win-x64/native/wrong.dll"
        path.parent.mkdir(parents=True)
        path.write_bytes(b"wrong platform")
        with self.assertRaisesRegex(ValueError, "wrong-platform"):
            self.compare()

    def test_each_bundled_distribution_must_use_the_intended_runtime(self):
        path = self.candidate / "tooling/cli/netratel.deps.json"
        deps = json.loads(path.read_text())
        deps["runtimeTarget"]["name"] = ".NETCoreApp,Version=v10.0/win-x64"
        path.write_text(json.dumps(deps))
        with self.assertRaisesRegex(ValueError, "selected Linux RID"):
            self.compare()

    def test_mcp_schema_runtime_dependency_cannot_disappear(self):
        path = self.candidate / "NetRatel.Mcp.Http.deps.json"
        deps = json.loads(path.read_text())
        del deps["libraries"]["NJsonSchema/1.0.0"]
        path.write_text(json.dumps(deps))
        with self.assertRaisesRegex(ValueError, "hosting/schema"):
            helper.inventory(self.candidate)

    def test_supported_launcher_content_cannot_be_removed(self):
        (self.candidate / "http-mcp-windows.ps1").unlink()
        with self.assertRaisesRegex(ValueError, "launcher"):
            self.compare()

    def test_application_symbols_cannot_be_silently_removed(self):
        (self.candidate / "tooling/stdio/NetRatel.Mcp.pdb").unlink()
        with self.assertRaisesRegex(ValueError, "symbol file"):
            self.compare()


if __name__ == "__main__":
    unittest.main()
