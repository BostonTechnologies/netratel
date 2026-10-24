#!/usr/bin/env python3
"""Negative release-gate tests using isolated source fixtures."""
import base64
import contextlib
import copy
from datetime import datetime, timezone
import runpy
import stat
from types import SimpleNamespace
import importlib.util
import io
import json
import os
import re
import shlex
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import time
import unittest
from unittest.mock import patch
from urllib.error import HTTPError
import hashlib
import tarfile
import zipfile
import xml.etree.ElementTree as ET


def module(name):
    spec = importlib.util.spec_from_file_location(name, ROOT / "tools/ci" / (name + ".py"))
    loaded = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(loaded)
    return loaded

ROOT = Path(__file__).resolve().parents[2]
SCANNER = ROOT / "tools/ci/verify-product-version.py"


class WindowsClientRecoveryPackagingTests(unittest.TestCase):
    def test_entrypoint_is_generated_from_validated_manifest_and_canonical_installer(self):
        generator = module("prepare-windows-client-repair")
        manifest = {"schema": "netratel.client.manifest.v1", "product": "NetRatel.Client",
                    "runtimeId": "win-x64", "version": "1.2.3-beta.10", "commitSha": "a" * 40,
                    "executable": "NetRatel.Client.exe"}
        content = generator.render(manifest)
        self.assertNotIn("@@", content)
        self.assertIn("$offlineRepair = $true", content)
        self.assertIn("$repairSourceRevision = '" + "a" * 40 + "'", content)
        self.assertIn("[ValidateSet('Preview','Apply')]", content)
        self.assertIn("function Save-RepairUpdateEvidence", content)
        with tempfile.TemporaryDirectory() as directory:
            distribution = Path(directory)
            (distribution / "netratel-client-manifest.json").write_text(json.dumps(manifest))
            command = [sys.executable, str(ROOT / "tools/ci/prepare-windows-client-repair.py"),
                       "--distribution", directory]
            subprocess.run(command, check=True, capture_output=True)
            subprocess.run(command + ["--check"], check=True, capture_output=True)
            entrypoint = distribution / "updater/repair-windows-client.ps1"
            entrypoint.write_text(entrypoint.read_text().replace("$offlineRepair = $true", "$offlineRepair = $false"))
            changed = subprocess.run(command + ["--check"], capture_output=True, text=True)
            self.assertNotEqual(0, changed.returncode)
            self.assertIn("differs", changed.stderr)

    def test_manifest_cannot_inject_powershell_or_an_unpublished_source(self):
        generator = module("prepare-windows-client-repair")
        manifest = {"schema": "netratel.client.manifest.v1", "product": "NetRatel.Client",
                    "runtimeId": "win-x64", "version": "1.2.3", "commitSha": "a" * 40,
                    "executable": "NetRatel.Client.exe"}
        for name, value in (("version", "1.2.3'; unexpected"), ("commitSha", "unknown"),
                            ("runtimeId", "linux-x64"), ("executable", "another.exe")):
            with self.subTest(name=name), self.assertRaises(ValueError):
                generator.render({**manifest, name: value})


class CampaignPreflightTests(unittest.TestCase):
    def setUp(self):
        spec = importlib.util.spec_from_file_location("campaign_preflight", ROOT / "tools/testing/mcp-campaign-preflight.py")
        self.checker = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(self.checker)
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.directory = Path(temporary.name)
        receipt = self.directory / "receipt.json"
        receipt.write_text('{"syntheticPreparationRecord": true}')
        self.paths = {name: self.directory / (name + ".json")
                      for name in ("register", "baseline", "history", "context_map", "remaining_ledger")}
        context_gates = {
            "beta27/qa-durable-client": ["QA", "DISPOSABLE-CLIENT"],
            "beta27/qa-durable-job": ["QA", "DISPOSABLE-CLIENT"],
            "beta27/qa-start-preview": ["QA", "DISPOSABLE-CLIENT"],
            "beta27/wsl-recovery": ["QA", "DISPOSABLE-CLIENT"],
            "beta26-continuation/B26-linux-paging-continuation": [],
            "beta26-continuation/B26-windows-paging-continuation": [],
            "synthetic/already-completed": ["LOG-FIXTURE"],
            "synthetic/excluded": [],
        }
        self.baseline = {"contexts": [{"contextId": key, "origin": "synthetic", "currentDisposition": "Untested",
                                      "invoked": False, "requiredGateIds": gates, "actualReceiptIds": []}
                                     for key, gates in context_gates.items()]}
        self.history = copy.deepcopy(self.baseline)
        self.paths["baseline"].write_text(json.dumps(self.baseline))
        self.paths["history"].write_text(json.dumps(self.history))
        self.mapping = {"contexts": [{"contextId": key, "origin": "synthetic", "disposition": "Untested",
                                      "invoked": False, "requiredGateIds": gates, "receiptIds": [],
                                      "prospectiveRequiredGateIds": gates, "completionBatch": "work",
                                      "planningPopulation": "excluded" if key.endswith("excluded") else "outstanding"}
                                     for key, gates in context_gates.items()],
                        "baselineFieldNames": {"disposition": "disposition", "invoked": "invoked",
                                               "requiredGateIds": "requiredGateIds", "receiptIds": "receiptIds"},
                        "retainedContextCount": len(context_gates), "batches": {"work": {}},
                        "sourceEvidenceSha256": self.checker.digest(self.paths["baseline"]),
                        "historyEvidenceSha256": self.checker.digest(self.paths["history"]),
                        "protectedOriginalAgentIds": ["00000000-0000-0000-0000-000000000001"],
                        "protectedPrimaryExpiresAtUtc": "2030-01-01T00:00:00Z"}
        self.register = {}

        def put(field, value):
            target = self.register
            parts = field.split(".")
            for part in parts[:-1]:
                target = target.setdefault(part, {})
            target[parts[-1]] = value

        for fields in self.checker.RECEIPTS.values():
            for field in fields:
                put(field, receipt.name)
        for field in ("consumer.aiConsumerNameAndVersion", "consumer.connectionReference", "consumer.bridgeArchiveSha256",
                      "review.eligibleDistinctReviewerReference", "actors.primary.protectedReference", "actors.noReveal.protectedReference",
                      "receiver.connectorReference", "receiver.syntheticDestinationReference", "receiver.allowedDeliveryAndCleanup"):
            put(field, "synthetic-label")
        for actor in ("primary", "noReveal", "additionalPermissionTestCredential"):
            put("actors." + actor + ".expiry", "2030-01-01T00:00:00Z")
        put("release.tag", "v1.2.3")
        put("release.sourceSha", "a" * 40)
        put("consumer.consumerLoadedSchemaObserved", True)
        put("consumer.promptMessageExecutionMode", "native-menu")
        put("consumer.protocolControlMode", "ai-host")
        put("consumer.enduranceMode", "ai-host-native")
        for index, os_name in enumerate(("windows", "linux"), 2):
            prefix = "fixtures." + os_name + "."
            for field in ("independentDeviceIdentityReceipt", "runningVersionSourceAndExecutableHashReceipt"):
                put(prefix + field, receipt.name)
            for field in ("protectedConnectionProfileReference", "inertServiceName", "inertServiceInitialState", "cleanupOwnerAndScope"):
                put(prefix + field, "synthetic-label")
            put(prefix + "tenantId", index)
            put(prefix + "agentId", "00000000-0000-0000-0000-" + str(index).zfill(12))
            put(prefix + "writableRoots", ["synthetic-root"])
            put(prefix + "allowedOperations", ["synthetic-operation"])
        for field in ("originalQaClientReceipt", "originalQaJobReceipt", "originalQaRuntimeReceipt"):
            put("operational." + field, receipt.name)
        self.remaining = {"requiredContexts": len(context_gates), "unresolvedOriginalContexts": 6,
                          "remainingOriginalContexts": [{"contextId": key} for key in list(context_gates)[:6]],
                          "plannedExclusions": [{"contextId": "synthetic/excluded"}]}
        self.paths["remaining_ledger"].write_text(json.dumps(self.remaining))
        self.remaining_sha256 = self.checker.digest(self.paths["remaining_ledger"])

    def inspect(self, use_remaining=True):
        self.paths["remaining_ledger"].write_text(json.dumps(self.remaining))
        return self.checker.inspect(self.register, self.baseline, self.history, self.mapping, self.paths,
                                    "v1.2.3", "a" * 40, datetime(2026, 1, 1, tzinfo=timezone.utc), 360,
                                    self.remaining if use_remaining else None, self.remaining_sha256 if use_remaining else None)

    def test_current_residuals_select_work_without_changing_baseline_or_claiming_live_acceptance(self):
        before = {key: self.paths[key].read_bytes() for key in ("baseline", "history")}
        report = self.inspect()
        self.assertEqual("complete-for-declared-plan", report["recordCheck"])
        self.assertEqual(8, report["retainedContextCount"])
        self.assertEqual(6, report["selectedOutstandingContexts"])
        self.assertEqual(6, report["batches"][0]["assignedOutstandingContexts"])
        self.assertFalse(report["liveAcceptanceEstablished"])
        self.assertEqual(before, {key: self.paths[key].read_bytes() for key in before})
        self.assertEqual(self.checker.digest(self.paths["remaining_ledger"]), report["residualLedgerSha256"])

    def test_absent_runtime_and_windows_do_not_block_durable_qa_or_linux_rows(self):
        del self.register["operational"]["originalQaRuntimeReceipt"]
        del self.register["fixtures"]["windows"]["runningVersionSourceAndExecutableHashReceipt"]
        report = self.inspect()
        rows = {row["contextId"]: row["missingGateIds"] for row in report["batches"][0]["contexts"]}
        self.assertEqual([], rows["beta27/qa-durable-client"])
        self.assertEqual([], rows["beta27/qa-durable-job"])
        self.assertEqual([], rows["beta26-continuation/B26-linux-paging-continuation"])
        self.assertEqual(["QA-RUNTIME"], rows["beta27/qa-start-preview"])
        self.assertEqual(["QA-RUNTIME"], rows["beta27/wsl-recovery"])
        self.assertEqual(["EXECUTORS-WINDOWS"], rows["beta26-continuation/B26-windows-paging-continuation"])

    def test_invalid_residual_scope_rejects_every_selected_context(self):
        original = copy.deepcopy(self.remaining)
        for change in ("unknown", "duplicate", "excluded", "count", "universe"):
            with self.subTest(change=change):
                self.remaining = copy.deepcopy(original)
                if change in {"unknown", "duplicate", "excluded"}:
                    self.remaining["remainingOriginalContexts"][0]["contextId"] = {
                        "unknown": "synthetic/unknown", "duplicate": "beta27/qa-durable-job",
                        "excluded": "synthetic/excluded"}[change]
                else:
                    self.remaining["unresolvedOriginalContexts" if change == "count" else "requiredContexts"] += 1
                report = self.inspect()
                self.assertEqual("missing-or-inconsistent", report["recordCheck"])
                self.assertTrue(any(f["gate"] == "HISTORY" for f in report["findings"]))
                self.assertTrue(all("HISTORY" in row["missingGateIds"] for row in report["batches"][0]["contexts"]))

    def test_owner_decision_does_not_require_an_independent_reviewer_but_keeps_applied_evidence(self):
        del self.register["review"]["eligibleDistinctReviewerReference"]
        self.assertTrue(any(f["gate"] == "REVIEW" for f in self.inspect()["findings"]))
        for mode in ("owner_direct", "owner_self_approval"):
            self.register["review"]["approvalMode"] = mode
            self.assertFalse(any(f["gate"] == "REVIEW" for f in self.inspect()["findings"]))
        del self.register["review"]["appliedReceipt"]
        self.assertTrue(any(f["gate"] == "REVIEW" for f in self.inspect()["findings"]))

    def test_pinned_current_ledger_rejects_removed_or_added_exclusions_and_changed_requirements(self):
        original = copy.deepcopy(self.remaining)
        for change in ("removed-exclusion", "added-exclusion", "changed-requirement", "wrong-hash"):
            with self.subTest(change=change):
                self.remaining = copy.deepcopy(original)
                if change == "removed-exclusion":
                    self.remaining["plannedExclusions"] = []
                elif change == "added-exclusion":
                    self.remaining["plannedExclusions"].append({"contextId": "synthetic/already-completed"})
                elif change == "changed-requirement":
                    self.remaining["remainingOriginalContexts"][0]["requirement"] = "Different assertion"
                else:
                    self.remaining_sha256 = "0" * 64
                report = self.inspect()
                self.assertEqual("missing-or-inconsistent", report["recordCheck"])
                self.assertTrue(any(record["field"] == "remaining-ledger-sha256"
                                    for finding in report["findings"] for record in finding["missingRecords"]))
                self.assertTrue(all("HISTORY" in row["missingGateIds"] for row in report["batches"][0]["contexts"]))

    def test_legacy_input_still_checks_all_outstanding_rows_and_original_qa_record(self):
        report = self.inspect(use_remaining=False)
        self.assertEqual(7, report["selectedOutstandingContexts"])
        self.assertEqual("complete-for-declared-plan", report["recordCheck"])
        del self.register["operational"]["originalQaDisposition"]
        self.assertTrue(any(f["gate"] == "QA" for f in self.inspect(use_remaining=False)["findings"]))

    def test_unrepresentable_expiry_and_session_window_are_rejected_without_a_traceback(self):
        for name, value in (("baseline", self.baseline), ("history", self.history), ("context_map", self.mapping),
                            ("remaining_ledger", self.remaining)):
            self.paths[name].write_text(json.dumps(value))
        command = [sys.executable, str(ROOT / "tools/testing/mcp-campaign-preflight.py")]
        for name, path in self.paths.items():
            command += ["--" + name.replace("_", "-"), str(path)]
        command += ["--expected-tag", "v1.2.3", "--expected-source", "a" * 40,
                    "--remaining-ledger-sha256", self.remaining_sha256]
        for expiry, minutes in (("0001-01-01T00:00:00+01:00", "360"), ("2030-01-01T00:00:00Z", str(10 ** 30))):
            with self.subTest(expiry=expiry, minutes=minutes):
                self.register["actors"]["primary"]["expiry"] = expiry
                self.paths["register"].write_text(json.dumps(self.register))
                result = subprocess.run(command + ["--session-minutes", minutes], capture_output=True, text=True)
                self.assertEqual(2, result.returncode)
                self.assertNotIn("Traceback", result.stdout + result.stderr)
                self.assertNotIn('"recordCheck": "complete-for-declared-plan"', result.stdout)
                if result.stdout:
                    self.assertFalse(json.loads(result.stdout)["liveAcceptanceEstablished"])


class PublicGatewayRouteTests(unittest.TestCase):
    def test_public_ingress_routes_every_protobuf_method_to_the_api_gateway_listener(self):
        proto_path = ROOT / "src/NetRatel/NetRatel.AgentGateway.Contracts/Protos/agent_gateway.proto"
        proto = proto_path.read_text()
        package = re.search(r"(?m)^package\s+([A-Za-z_][A-Za-z_0-9.]*)\s*;", proto)
        self.assertIsNotNone(package, "The gateway protobuf package declaration is required.")

        method_paths = []
        for service in re.finditer(
                r"(?ms)^[ \t]*service\s+([A-Za-z_][A-Za-z_0-9]*)\s*\{(.*?)^[ \t]*\}", proto):
            methods = re.findall(r"(?m)^[ \t]*rpc\s+([A-Za-z_][A-Za-z_0-9]*)\s*\(", service.group(2))
            method_paths.extend(
                f"/{package.group(1)}.{service.group(1)}/{method}" for method in methods)

        self.assertTrue(method_paths, "The gateway protobuf must declare at least one RPC method.")
        ingress = (ROOT / "release/nginx.public-https.conf").read_text()
        grpc_locations = [
            (match.group(1), match.group(2))
            for match in re.finditer(
                r"(?ms)^[ \t]*location\s+\^~\s+(\S+)\s*\{(.*?)^[ \t]*\}", ingress)
            if re.search(r"(?m)^[ \t]*grpc_pass\s+", match.group(2))
        ]
        self.assertEqual(len(grpc_locations), 1, "The public ingress must define one dedicated gRPC route.")
        prefix, location_body = grpc_locations[0]
        self.assertEqual(prefix, f"/{package.group(1)}.")
        self.assertRegex(location_body, r"(?m)^[ \t]*grpc_pass\s+grpc://api:9223\s*;")
        self.assertRegex(location_body, r"(?m)^[ \t]*client_body_timeout\s+3600s\s*;")
        for method_path in method_paths:
            with self.subTest(method_path=method_path):
                self.assertTrue(method_path.startswith(prefix), f"{method_path} misses the public gRPC location.")


class TraefikGatewayRouteFixtureTests(unittest.TestCase):
    def test_fixture_routes_the_full_dotted_rpc_prefix_to_h2c_and_keeps_rest_as_fallback(self):
        dynamic = (ROOT / "tools/ci/tests/traefik-gateway-dynamic.yaml").read_text()
        self.assertIn("PathPrefix(`/netratel.gateway.v1.`)", dynamic)
        self.assertIn("priority: 100", dynamic)
        self.assertIn("h2c://gateway-fixture:9223", dynamic)
        self.assertIn("PathPrefix(`/`)", dynamic)
        self.assertIn("priority: 10", dynamic)
        self.assertIn("http://gateway-fixture:9222", dynamic)
        self.assertNotIn("StripPrefix", dynamic)

        probe = (ROOT / "tools/ci/tests/traefik-upstream.js").read_text()
        self.assertIn('"/netratel.gateway.v1.AgentGateway/Connect"', probe)
        self.assertIn('"grpc-status": "7"', probe)
        self.assertIn('grpc-status", "7"', probe)
        self.assertIn('response.status !== 403', probe)
        self.assertIn("x-netratel-ingress-route", probe)
        self.assertIn("x-correlation-id", probe)


class MtpTestReceiptTests(unittest.TestCase):
    def setUp(self):
        self.verifier = module("verify-mtp-trx")
        self.directory = tempfile.TemporaryDirectory()
        self.report = Path(self.directory.name) / "receipt.trx"

    def tearDown(self):
        self.directory.cleanup()

    def write_report(self, outcome="Completed", counters=None, run_info=""):
        values = counters or {
            "total": 2, "executed": 2, "passed": 2, "failed": 0,
            "error": 0, "timeout": 0, "aborted": 0, "inconclusive": 0,
            "passedButRunAborted": 0, "notRunnable": 0, "notExecuted": 0,
            "disconnected": 0, "warning": 0, "completed": 0,
            "inProgress": 0, "pending": 0,
        }
        attributes = " ".join(f'{name}="{value}"' for name, value in values.items())
        self.report.write_text(
            f'<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">'
            f'<ResultSummary outcome="{outcome}"><Counters {attributes}/></ResultSummary>'
            f"{run_info}</TestRun>",
            encoding="utf-8",
        )

    def test_completed_report_requires_exact_positive_pass_count(self):
        self.write_report()
        receipt = self.verifier.validate_report(self.report, expected_executed=2)
        self.assertEqual((receipt["total"], receipt["executed"], receipt["passed"]), (2, 2, 2))

    def test_empty_or_incomplete_report_is_rejected(self):
        self.write_report(counters={
            "total": 0, "executed": 0, "passed": 0, "failed": 0,
            "error": 0, "timeout": 0, "aborted": 0, "inconclusive": 0,
            "passedButRunAborted": 0, "notRunnable": 0, "notExecuted": 0,
            "disconnected": 0, "warning": 0, "completed": 0,
            "inProgress": 0, "pending": 0,
        })
        with self.assertRaisesRegex(ValueError, "selected zero test cases"):
            self.verifier.validate_report(self.report, expected_executed=2)

        self.write_report(outcome="Failed", run_info="<RunInfo />")
        with self.assertRaisesRegex(ValueError, "outcome='Failed'"):
            self.verifier.validate_report(self.report, expected_executed=2)

    def test_every_non_success_counter_must_be_zero(self):
        for counter in self.verifier.NON_SUCCESS_COUNTERS:
            with self.subTest(counter=counter):
                counters = {
                    name: 0 for name in self.verifier.COUNTER_NAMES
                }
                counters.update(total=2, executed=2, passed=2)
                counters[counter] = 1
                self.write_report(counters=counters)
                with self.assertRaisesRegex(ValueError, "non-success test counters"):
                    self.verifier.validate_success_report(self.report)

    def test_passed_executed_and_total_counters_must_agree(self):
        for counter, value in (("total", 3), ("executed", 1), ("passed", 1)):
            with self.subTest(counter=counter):
                counters = {
                    name: 0 for name in self.verifier.COUNTER_NAMES
                }
                counters.update(total=2, executed=2, passed=2)
                counters[counter] = value
                self.write_report(counters=counters)
                with self.assertRaisesRegex(ValueError, "must contain only passed executed cases"):
                    self.verifier.validate_success_report(self.report)

    def test_negative_counters_are_rejected(self):
        counters = {name: 0 for name in self.verifier.COUNTER_NAMES}
        counters.update(total=2, executed=2, passed=2, warning=-1)
        self.write_report(counters=counters)
        with self.assertRaisesRegex(ValueError, "negative test counter"):
            self.verifier.validate_success_report(self.report)

    def test_missing_or_invalid_standard_counters_are_rejected(self):
        counters = {
            name: 0 for name in self.verifier.COUNTER_NAMES
        }
        counters.update(total=2, executed=2, passed=2)
        del counters["timeout"]
        self.write_report(counters=counters)
        with self.assertRaisesRegex(ValueError, "invalid or missing test counters"):
            self.verifier.validate_success_report(self.report)

        counters["timeout"] = "unknown"
        self.write_report(counters=counters)
        with self.assertRaisesRegex(ValueError, "invalid or missing test counters"):
            self.verifier.validate_success_report(self.report)


class FastTestContainerPreparationTests(unittest.TestCase):
    images = (
        "postgres:16-alpine",
        "testcontainers/ryuk:0.14.0@sha256:7c1a8a9a47c780ed0f983770a662f80deb115d95cce3e2daa3d12115b8cd28f0",
    )

    def prepare(self, scenario, deadline=None):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            fake_bin = directory / "bin"
            fake_bin.mkdir()
            fake_docker = fake_bin / "docker"
            fake_docker.write_text(
                "#!/usr/bin/env python3\n"
                "import json, os, pathlib, signal, subprocess, sys, time\n"
                "args = sys.argv[1:]\n"
                "root = pathlib.Path(os.environ['NETRATEL_CONTAINER_TEST_ROOT'])\n"
                "mode = os.environ['NETRATEL_CONTAINER_TEST_SCENARIO']\n"
                "with (root / 'commands.jsonl').open('a') as log:\n"
                "    log.write(json.dumps(args) + '\\n')\n"
                "if args[0] == 'info':\n"
                "    sys.exit(8 if mode == 'daemon' else 0)\n"
                "state_path = root / 'state.json'\n"
                "state = json.loads(state_path.read_text()) if state_path.exists() else {}\n"
                "image = args[-1]\n"
                "entry = state.setdefault(image, {'cached': mode == 'cached' or (mode in ('kill', 'mixed') and image.startswith('postgres:')), 'inspects': 0})\n"
                "if args[:2] == ['image', 'inspect']:\n"
                "    entry['inspects'] += 1\n"
                "    state_path.write_text(json.dumps(state))\n"
                "    if not entry['cached']: sys.exit(1)\n"
                "    if mode == 'inspect' and entry['inspects'] == 2: sys.exit(9)\n"
                "    print('invalid' if mode == 'identity' else 'sha256:' + 'a' * 64)\n"
                "elif args[:2] == ['pull', '--quiet']:\n"
                "    if mode == 'pull': sys.exit(7)\n"
                "    if mode == 'deadline' and image.startswith('postgres:'): time.sleep(1.1)\n"
                "    if mode in ('deadline', 'kill', 'mixed') and image.startswith('testcontainers/'):\n"
                "        child_code = 'import signal, time; '\n"
                "        if mode == 'kill':\n"
                "            signal.signal(signal.SIGTERM, signal.SIG_IGN)\n"
                "        if mode in ('kill', 'mixed'):\n"
                "            child_code += 'signal.signal(signal.SIGTERM, signal.SIG_IGN); '\n"
                "        child = subprocess.Popen([sys.executable, '-c', child_code + 'time.sleep(60)'])\n"
                "        (root / 'pids.json').write_text(json.dumps([os.getpid(), child.pid]))\n"
                "        time.sleep(60)\n"
                "    entry['cached'] = True\n"
                "    state_path.write_text(json.dumps(state))\n"
                "else:\n"
                "    sys.exit(10)\n",
                encoding="utf-8",
            )
            fake_docker.chmod(0o755)
            fake_dotnet = fake_bin / "dotnet"
            fake_dotnet.write_text(
                "#!/usr/bin/env python3\n"
                "import os, pathlib\n"
                "(pathlib.Path(os.environ['NETRATEL_CONTAINER_TEST_ROOT']) / 'tests-started').touch()\n",
                encoding="utf-8",
            )
            fake_dotnet.chmod(0o755)
            environment = {
                **os.environ,
                "PATH": str(fake_bin) + os.pathsep + os.environ.get("PATH", ""),
                "NETRATEL_CONTAINER_TEST_ROOT": str(directory),
                "NETRATEL_CONTAINER_TEST_SCENARIO": scenario,
                "GITHUB_STEP_SUMMARY": str(directory / "summary.md"),
            }
            arguments = [] if deadline is None else [str(value) for value in deadline] if isinstance(deadline, tuple) else [str(deadline)]
            started = time.monotonic()
            live_children = []
            try:
                completed = subprocess.run(
                    ["bash", "-c", 'bash tools/ci/prepare-fast-test-containers.sh "$@" && dotnet test', "prepare", *arguments],
                    cwd=ROOT, env=environment, check=False, capture_output=True, text=True, timeout=10,
                )
            finally:
                elapsed = time.monotonic() - started
                pids_path = directory / "pids.json"
                if pids_path.exists():
                    for pid in json.loads(pids_path.read_text()):
                        process_stat = Path(f"/proc/{pid}/stat")
                        # timeout can reap its supervisor before the kernel has
                        # scheduled a SIGKILLed descendant's final exit. Observe
                        # that bounded transition; a still-running child fails.
                        observation_deadline = time.monotonic() + 0.5
                        def still_running():
                            try:
                                return process_stat.read_text().rsplit(")", 1)[1].split()[0] != "Z"
                            except FileNotFoundError:
                                return False
                        while still_running() and time.monotonic() < observation_deadline:
                            time.sleep(0.01)
                        if still_running():
                            live_children.append(pid)
                            try:
                                os.kill(pid, 9)  # Do not leak a failing fixture's sleep process.
                            except ProcessLookupError:
                                pass
            commands_path = directory / "commands.jsonl"
            commands = [json.loads(line) for line in commands_path.read_text().splitlines()] if commands_path.exists() else []
            return completed, commands, elapsed, live_children, (directory / "tests-started").exists()

    def test_cold_and_cached_images_are_ready_before_tests_without_repeated_pulls(self):
        for scenario in ("cold", "cached"):
            with self.subTest(scenario=scenario):
                completed, commands, _, children, tests_started = self.prepare(scenario)
                self.assertEqual(0, completed.returncode, completed.stdout + completed.stderr)
                self.assertTrue(tests_started)
                self.assertEqual([], children)
                pulls = [args for args in commands if args[0] == "pull"]
                self.assertEqual([["pull", "--quiet", image] for image in self.images] if scenario == "cold" else [], pulls)
                self.assertEqual("info", commands[0][0])
                for image in self.images:
                    self.assertEqual(2, sum(args[:2] == ["image", "inspect"] and args[-1] == image for args in commands))
                    self.assertIn(f"Fast regression container ready: {image} id=sha256:", completed.stdout)
                self.assertIn("status=ready", completed.stdout)

    def test_daemon_pull_and_readiness_failures_stop_before_tests_and_do_not_retry(self):
        for scenario, expected_status in (("daemon", 8), ("pull", 7), ("inspect", 9), ("identity", 1)):
            with self.subTest(scenario=scenario):
                completed, commands, _, children, tests_started = self.prepare(scenario)
                self.assertEqual(expected_status, completed.returncode, completed.stdout + completed.stderr)
                self.assertFalse(tests_started)
                self.assertEqual([], children)
                self.assertNotIn(self.images[1], [args[-1] for args in commands])
                self.assertLessEqual(sum(args[0] == "pull" for args in commands), 1)
                self.assertIn(f"status=failed({expected_status})", completed.stdout)

    def test_one_deadline_covers_both_images_and_terminates_docker_descendants(self):
        completed, commands, elapsed, children, tests_started = self.prepare("deadline", 2)
        self.assertEqual(137, completed.returncode, completed.stdout + completed.stderr)
        self.assertEqual([["pull", "--quiet", image] for image in self.images], [args for args in commands if args[0] == "pull"])
        self.assertLess(elapsed, 9)
        self.assertFalse(tests_started)
        self.assertEqual([], children, "The timed-out Docker process and its child must stop.")

    def test_kill_grace_stops_term_ignoring_docker_descendants(self):
        for scenario in ("kill", "mixed"):
            with self.subTest(scenario=scenario):
                completed, _, elapsed, children, tests_started = self.prepare(scenario, 1)
                self.assertEqual(137, completed.returncode, completed.stdout + completed.stderr)
                self.assertLess(elapsed, 8)
                self.assertFalse(tests_started)
                self.assertEqual([], children, "Docker descendants that ignore TERM must stop after the kill grace even if their parent exits.")

    def test_invalid_deadlines_fail_before_docker_and_workflows_keep_required_stage_order(self):
        for deadline in (0, 121, "invalid", (1, 2)):
            with self.subTest(deadline=deadline):
                completed, commands, _, _, tests_started = self.prepare("cold", deadline)
                self.assertEqual(2, completed.returncode)
                self.assertEqual([], commands)
                self.assertFalse(tests_started)
        helper = ROOT / "tools/ci/prepare-fast-test-containers.sh"
        self.assertIn('--signal=TERM --kill-after=5s "${deadline}s"', helper.read_text())
        self.assertNotIn("--foreground", helper.read_text())
        for relative in (".github/workflows/public-pr-validation.yml", ".github/workflows/release-build.yml"):
            workflow = (ROOT / relative).read_text()
            with self.subTest(workflow=relative):
                prepare = workflow.index("- name: Prepare fast regression containers")
                self.assertEqual(1, workflow.count("bash tools/ci/prepare-fast-test-containers.sh"))
                self.assertLess(workflow.index("- name: Build Release solution"), prepare)
                self.assertLess(prepare, workflow.index("- name: Run fast regressions (Core, API, Components)"))
                self.assertIn("timeout-minutes: 20", workflow[:prepare])
                self.assertNotIn("continue-on-error", workflow[prepare:workflow.index("- name: Retain fast regression receipts")])


class MtpCiRunnerSelectionTests(unittest.TestCase):
    def test_independent_contracts_are_required_before_pr_success_and_release_packaging(self):
        for relative, gate in ((".github/workflows/public-pr-validation.yml", "pr-validation"),
                               (".github/workflows/release-build.yml", "release-rehearsal")):
            workflow = (ROOT / relative).read_text(encoding="utf-8")
            jobs = dict(re.findall(r"(?ms)^  ([a-z-]+):\n(.*?)(?=^  [a-z-]+:|\Z)",
                                   workflow.split("\njobs:\n", 1)[1]))
            with self.subTest(workflow=relative):
                contracts = jobs["contracts"]
                self.assertIn("runs-on: ubuntu-latest", contracts)
                self.assertIn("timeout-minutes: 20", contracts)
                self.assertIn("actions/setup-dotnet@", contracts)
                self.assertIn("global-json-file: global.json", contracts)
                command = "python3 tools/ci/test-release-validation.py"
                self.assertEqual(1, workflow.count(command))
                self.assertIn(command, contracts)
                self.assertNotRegex(contracts, r"(?m)^    (?:needs|if|continue-on-error):")
                self.assertNotIn("continue-on-error", contracts)
                self.assertIn("if: always()", jobs[gate])
                self.assertRegex(jobs[gate], r"(?m)^    needs: \[[^\n]*\bcontracts\b[^\n]*\]$")
                self.assertIn("CONTRACTS_RESULT: ${{ needs.contracts.result }}", jobs[gate])
                self.assertIn('[[ "$CONTRACTS_RESULT" == success ]]', jobs[gate])
                if gate == "release-rehearsal":
                    for package in ("package", "mcp-windows", "client-packages"):
                        self.assertIn("needs: [validate, contracts]", jobs[package])

    def test_ci_uses_sdk10_mtp_selectors_and_requires_each_generic_test_assembly(self):
        helper = ROOT / "tools/ci/run-fast-regressions.sh"
        source = helper.read_text(encoding="utf-8")
        required_assemblies = (
            "NetRatel.Tests",
            "NetRatel.API.IntegrationTests",
            "NetRatel.Web.ComponentTests",
        )
        assemblies = re.search(r"(?ms)^assemblies=\(\n(.*?)^\)", source)
        self.assertIsNotNone(assemblies)
        self.assertEqual(required_assemblies, tuple(assemblies.group(1).split()))
        self.assertIn("--configuration Release --no-build --max-parallel-test-modules 1", source)
        self.assertIn("--filter-not-trait category=compose category=hosted category=manual-integration", source)
        self.assertIn('--report-trx-filename "fast-$assembly.trx"', source)
        self.assertIn('python3 tools/ci/verify-mtp-trx.py "$report"', source)
        self.assertNotIn("--expected-executed", source, "New default regressions must remain discoverable.")
        self.assertNotRegex(source, r"--filter(?:\s|=)", "VSTest filter syntax is invalid under the selected MTP runner.")
        self.assertNotIn("-- --report-trx", source, "SDK 10 MTP options are passed directly without a legacy separator.")
        self.assertNotIn("--filter-class", source)
        self.assertNotIn("NetRatel.Web.PlaywrightTests", source)
        self.assertNotIn("setsid", source)

        for relative in (".github/workflows/public-pr-validation.yml", ".github/workflows/release-build.yml"):
            workflow = (ROOT / relative).read_text(encoding="utf-8")
            with self.subTest(workflow=relative):
                self.assertEqual(1, workflow.count("bash tools/ci/run-fast-regressions.sh"))
                for manual_command in (
                    "run-physical-disk-incident-proof.sh", "run-pairing-acceptance.sh",
                    "run-gateway-native-proof.sh", "smoke-local-first-compose.sh",
                    "smoke-oidc-compose.sh", "smoke-postgresql-local-upgrade.sh",
                    "smoke-postgresql-oidc-upgrade.sh", "smoke-release-images.sh",
                    "playwright.ps1 install", "playwright-browser-evidence",
                ):
                    self.assertNotIn(manual_command, workflow)

        manual = (ROOT / ".github/workflows/integration-validation.yml").read_text(encoding="utf-8")
        triggers = re.search(r"(?ms)^on:\n(.*?)(?=^[A-Za-z_][A-Za-z_0-9-]*:)", manual)
        self.assertIsNotNone(triggers, "The optional workflow must declare its manual trigger.")
        self.assertEqual(["workflow_dispatch"], re.findall(r"(?m)^  ([A-Za-z_][A-Za-z_0-9-]*):", triggers.group(1)))
        jobs = manual.split("\njobs:\n", 1)
        self.assertEqual(2, len(jobs))
        self.assertEqual(["selected-suite", "windows-mcp", "client-measurement"],
                         re.findall(r"(?m)^  ([A-Za-z_][A-Za-z_0-9-]*):", jobs[1]))
        selected, measurement = jobs[1].split("\n  client-measurement:", 1)
        self.assertNotRegex(selected, r"(?m)^\s+matrix:", "A functional suite selection must not fan out into other functional suites.")
        self.assertIn("if: inputs.suite != 'client-measurement'", selected)
        self.assertIn("if: inputs.suite == 'client-measurement'", measurement)
        self.assertIn("inputs.assessment_only && '[{\"runtime\":\"linux-x64\",\"runner\":\"ubuntu-latest\"}]'", measurement,
                      "The first trim assessment must permit a single Linux job without repeating A/B.")
        self.assertIn("if: ${{ !inputs.assessment_only }}", measurement)
        self.assertIn("!cancelled() && !inputs.assessment_only", measurement)
        self.assertIn("--source client-baseline", measurement)
        self.assertIn("--source .", measurement)
        self.assertNotIn("dotnet build", measurement)
        self.assertNotIn("build-public-image", measurement)
        self.assertNotIn("continue-on-error", measurement)
        self.assertIn("name: client-measurement-evidence-${{ matrix.runtime }}", measurement)
        self.assertIn("!TestResults/client-measurement/**/artifacts/**", measurement,
                      "Small evidence uploads must exclude the native archive bytes.")
        self.assertIn("name: client-measurement-archives-${{ matrix.runtime }}", measurement)
        self.assertIn("path: TestResults/client-measurement/**/artifacts/**", measurement)
        self.assertEqual(2, measurement.count("uses: actions/upload-artifact@"))
        self.assertEqual(2, measurement.count("if-no-files-found: error"))
        self.assertEqual(2, measurement.count("retention-days: 14"))
        self.assertIn("inputs.measurement_verify_run != '' && '[{\"runtime\":\"win-x64\",\"runner\":\"windows-latest\"}]'", measurement)
        self.assertEqual(2, measurement.count("uses: actions/download-artifact@"))
        self.assertEqual(2, measurement.count("run-id: ${{ inputs.measurement_verify_run }}"))
        self.assertRegex(measurement, r"permissions:\s+contents: read\s+actions: read")
        self.assertIn("ref: ${{ github.workflow_sha }}", measurement)
        self.assertIn("--verify-existing downloaded-client-measurement/baseline/win-x64", measurement)
        self.assertIn("--verify-existing downloaded-client-measurement/head/win-x64", measurement)
        for step in ("A - baseline", "B - cleanup", "C - bounded"):
            block = re.search(r"(?ms)^      - name: " + re.escape(step) + r".*?(?=^      - |\Z)", measurement)
            self.assertIsNotNone(block)
            self.assertIn("inputs.measurement_verify_run == ''", block.group(0), "Verification reuse must skip every publish step.")
        self.assertNotRegex(manual, r"(?m)^\s+needs:", "Manual suites must not depend on automatic validation.")
        self.assertIn('source_sha="$(git rev-parse HEAD)"', manual)
        self.assertIn('"sourceSha": sys.argv[1]', manual)
        self.assertIn('Path("TestResults/selected-source.json")', manual)
        self.assertIn("NETRATEL_PLAYWRIGHT_ARTIFACT_ROOT:", manual)
        self.assertIn("src/NetRatel/NetRatel.Web.PlaywrightTests/bin/Release/net10.0/TestResults/playwright", manual)
        self.assertRegex(manual, r"(?s)name: Retain selected TRX, screenshots and sanitized receipts\s+if: always\(\).*?path: \|\s+TestResults")

        # Retain cheap selector/source checks for opt-in Compose helpers.
        for relative in (
            "tools/ci/smoke-oidc-compose.sh",
            "tools/ci/smoke-postgresql-oidc-upgrade.sh",
            "tools/ci/smoke-local-first-compose.sh",
        ):
            optional_source = (ROOT / relative).read_text(encoding="utf-8")
            with self.subTest(optional_helper=relative):
                self.assertNotRegex(optional_source, r"--filter(?:\s|=)", "VSTest filter syntax is invalid under the selected MTP runner.")
                self.assertNotIn("-- --report-trx", optional_source, "SDK 10 MTP options are passed directly without a legacy separator.")
                self.assertIn("--report-trx-filename", optional_source)

        browser_test = (ROOT / "src/NetRatel/NetRatel.Web.PlaywrightTests/ClientsManagementResponsiveTests.cs").read_text(encoding="utf-8")
        self.assertIn("AbortedCriticalStartupScript_FailsAndWritesDiagnosticsBeforeContextDisposal", browser_test)
        self.assertIn('"startup-diagnostics.json"', browser_test)
        self.assertIn('"startup-failure.png"', browser_test)

        api_root = ROOT / "src/NetRatel/NetRatel.API.IntegrationTests"
        declaration = (api_root / "PairingLifecycleTests.cs").read_text(encoding="utf-8")
        self.assertIn('[Trait("category", "manual-integration")]', declaration)
        self.assertIn('public sealed class PairingLifecycleTests', declaration)
        self.assertIn("selected zero test cases", (ROOT / "tools/ci/verify-mtp-trx.py").read_text())

    def test_generic_test_modules_are_serialized_and_browser_failure_evidence_is_collected(self):
        # Preserve this existing test ID while verifying the amended fast-only
        # runner and immediate command/receipt failure propagation.
        helper = ROOT / "tools/ci/run-fast-regressions.sh"
        verifier = module("verify-mtp-trx")
        expected_projects = [
            f"src/NetRatel/{assembly}/{assembly}.csproj"
            for assembly in ("NetRatel.Tests", "NetRatel.API.IntegrationTests", "NetRatel.Web.ComponentTests")
        ]
        for failure in ("none", "command", "zero", "summary"):
            with self.subTest(failure=failure), tempfile.TemporaryDirectory() as temporary:
                directory = Path(temporary)
                fake_bin = directory / "bin"
                fake_bin.mkdir()
                command_log = directory / "commands.jsonl"
                fake_dotnet = fake_bin / "dotnet"
                fake_dotnet.write_text(
                    "#!/usr/bin/env python3\n"
                    "import json, os, pathlib, sys\n"
                    "args = sys.argv[1:]\n"
                    "with open(os.environ['NETRATEL_FAST_TEST_COMMAND_LOG'], 'a') as log:\n"
                    "    log.write(json.dumps(args) + '\\n')\n"
                    "failure = os.environ['NETRATEL_FAST_TEST_FAILURE']\n"
                    "if failure in ('command', 'summary'):\n"
                    "    sys.exit(7)\n"
                    "count = 0 if failure == 'zero' else 2\n"
                    f"counters = dict.fromkeys({verifier.COUNTER_NAMES!r}, 0)\n"
                    "counters.update(total=count, executed=count, passed=count)\n"
                    "attributes = ' '.join(f'{name}=\"{value}\"' for name, value in counters.items())\n"
                    "report = pathlib.Path(args[args.index('--results-directory') + 1]) / args[args.index('--report-trx-filename') + 1]\n"
                    "report.write_text('<TestRun><ResultSummary outcome=\"Completed\"><Counters ' + attributes + '/></ResultSummary></TestRun>')\n",
                    encoding="utf-8",
                )
                fake_dotnet.chmod(0o755)
                results = directory / "results"
                # A stale successful report must not rescue a failed command.
                results.mkdir()
                stale_report = results / "fast-NetRatel.Tests.trx"
                stale_report.write_text("stale report", encoding="utf-8")
                environment = {
                    **os.environ,
                    "PATH": str(fake_bin) + os.pathsep + os.environ.get("PATH", ""),
                    "NETRATEL_FAST_TEST_COMMAND_LOG": str(command_log),
                    "NETRATEL_FAST_TEST_FAILURE": failure,
                    "GITHUB_STEP_SUMMARY": str(directory if failure == "summary" else directory / "summary.md"),
                }
                completed = subprocess.run(
                    ["bash", str(helper), str(results)], cwd=ROOT,
                    env=environment, check=False, capture_output=True, text=True,
                )
                commands = [json.loads(line) for line in command_log.read_text().splitlines()]
                expected_count = 3 if failure == "none" else 1
                self.assertEqual(expected_count, len(commands), completed.stdout + completed.stderr)
                self.assertEqual(expected_projects[:expected_count], [args[args.index("--project") + 1] for args in commands])
                for args in commands:
                    self.assertEqual("test", args[0])
                    self.assertIn("--no-build", args)
                    self.assertEqual("Release", args[args.index("--configuration") + 1])
                    filter_start = args.index("--filter-not-trait") + 1
                    self.assertEqual(["category=compose", "category=hosted", "category=manual-integration"], args[filter_start:filter_start + 3])
                if failure == "none":
                    self.assertEqual(0, completed.returncode, completed.stdout + completed.stderr)
                    for assembly in ("NetRatel.Tests", "NetRatel.API.IntegrationTests", "NetRatel.Web.ComponentTests"):
                        self.assertEqual(2, verifier.validate_success_report(results / f"fast-{assembly}.trx")["executed"])
                elif failure in ("command", "summary"):
                    self.assertEqual(7, completed.returncode)
                    self.assertFalse(stale_report.exists())
                else:
                    self.assertNotEqual(0, completed.returncode)
                    self.assertIn("selected zero test cases", completed.stderr)
                if failure != "summary":
                    self.assertIn("NetRatel.Tests:", (directory / "summary.md").read_text())
                else:
                    self.assertIn("failed(7)", completed.stdout)

    def test_release_compose_oidc_checks_current_presence_and_bounds_command_conflict_diagnostics(self):
        source = (ROOT / "tools/ci/smoke-oidc-compose.sh").read_text(encoding="utf-8")
        current_presence = re.search(r"read_current_gateway_presence\(\) \{([\s\S]*?)\n\}", source)
        self.assertIsNotNone(current_presence)
        self.assertIn("/api/v2/client-presence/", current_presence.group(1))
        self.assertIn(".online == true", current_presence.group(1))
        self.assertIn('.source == "gateway"', current_presence.group(1))
        self.assertIn('.authority == "akka"', current_presence.group(1))
        self.assertIn(".isAuthoritative == true", current_presence.group(1))

        browser_stage = source.index('stage="running browser OIDC rehearsal"')
        telemetry_stage = source.index('stage="waiting for Client telemetry"', browser_stage)
        presence_wait = source.index('wait_for_current_gateway_presence "$operator_access_token"', telemetry_stage)
        first_dispatch = source.index('command_response="$(dispatch_disposable_command', presence_wait)
        self.assertLess(browser_stage, telemetry_stage)
        self.assertLess(telemetry_stage, presence_wait)
        self.assertLess(presence_wait, first_dispatch)

        dispatch_start = source.index("dispatch_disposable_command() {")
        dispatch_end = source.index("\n}\n", dispatch_start) + 3
        dispatch = source[dispatch_start:dispatch_end]
        self.assertIn("--write-out '%{http_code}'", dispatch)
        self.assertIn('.code | select(. == "agent_command_session_unavailable")', dispatch)
        failure_diagnostics = dispatch[dispatch.index('echo "Disposable command dispatch failed'):]
        self.assertIn("current Client presence HTTP=%s state=%s; container state=%s", failure_diagnostics)
        self.assertNotIn('cat "$command_response_path"', failure_diagnostics)



class GatewayDiagnosticTests(unittest.TestCase):
    def test_diagnostics_do_not_disclose_log_payloads(self):
        summarizer = module("summarize-gateway-log")
        lines = [
            "Bearer synthetic-sensitive-token https://private.example.invalid/secret\n",
            "Command gateway admitted. authority=akka. tenantId=123\n",
            'Command gateway session failed: RpcException: Status(StatusCode="Unavailable", Detail="synthetic-sensitive-token")\n',
            "Gateway extension 'command' failed without ending presence: InvalidOperationException: private-path\n",
        ]
        self.assertEqual(summarizer.summarize(lines), [
            "command_admitted", "command_transport_failed RpcException Unavailable",
            "command_extension_failed InvalidOperationException",
        ])

    def test_diagnostics_retain_only_twenty_latest_events(self):
        summarizer = module("summarize-gateway-log")
        lines = ["Presence admitted.\n"] * 30 + ["Command gateway admitted. authority=akka.\n"]
        events = summarizer.summarize(lines)
        self.assertEqual(len(events), 20)
        self.assertEqual(events[-1], "command_admitted")

    def test_metrics_deduplicate_primary_and_correlate_secondary_without_inventing_origin(self):
        summarizer = module("summarize-gateway-log")
        owner = "11111111-2222-3333-4444-555555555555"
        refresh_owner = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"
        failure = f"Gateway session failed: utc=2026-10-03T18:07:53Z, correlation={owner}, reason=unknown. suppressedRepeatedFailures=3."
        metrics = summarizer.summarize_metrics([
            "2026-10-03T18:00:00+00:00 Service started. secret-token\n",
            f'File gateway session failed: RpcException: StatusCode="Cancelled", correlation={owner}, private-payload\n',
            failure, failure,
            f"Refreshing the gateway session before the agent token expires. correlation={refresh_owner},\n",
            f"Terminal gateway session stopped with its presence owner. correlation={refresh_owner},\n",
            "Gateway extension 'file' stopped with its presence owner.\n",
            "Presence authentication renewed. utc=2026-10-03T19:00:00Z,\n",
            "Presence authentication renewed. utc=2026-10-03T19:30:00Z,\n",
            "2026-10-03T20:00:00+00:00 Service observation ended.\n",
        ])
        self.assertEqual(metrics["primary_presence_failures"], 1)
        self.assertEqual(metrics["suppressed_primary_failures_reported"], 3)
        self.assertEqual(metrics["observed_seconds"], 7200)
        self.assertEqual(metrics["primary_failures_per_observed_hour"], 0.5)
        self.assertEqual(metrics["refresh_reasons"], {"token-expiry-reconnect": 1, "authenticated-renewal": 2})
        self.assertEqual(metrics["secondary_events"], {
            "correlated_presence_failure": 1, "correlated_planned_refresh": 1, "uncorrelated": 1,
        })
        self.assertEqual(metrics["secondary_statuses"], {"Cancelled": 1, "unknown": 2})
        self.assertEqual(metrics["secondary_reasons"], {"RpcException": 1, "presence-owner-stopped": 2})
        self.assertEqual(metrics["reset_origin"], "unknown")
        output = json.dumps(metrics)
        for secret in (owner, refresh_owner, "secret-token", "private-payload"):
            self.assertNotIn(secret, output)

    def test_metrics_retain_unknown_reasons_missing_times_and_missing_owner(self):
        summarizer = module("summarize-gateway-log")
        metrics = summarizer.summarize_metrics([
            "Gateway session failed: reason=synthetic-secret-token. edge=secret-host\n",
            "Gateway session failed: reason=invalid heartbeat acknowledgement.\n",
            "Gateway session failed: reason=renewal_io_blocked.\n",
            "Gateway session failed: reason=presence_io_timeout.\n",
        ])
        self.assertEqual(metrics["primary_presence_failures"], 4)
        self.assertEqual(metrics["primary_failures_without_identity"], 4)
        self.assertEqual(metrics["primary_reasons"], {"invalid heartbeat acknowledgement": 1, "unknown": 1,
                                                 "renewal_io_blocked": 1, "presence_io_timeout": 1})
        self.assertIsNone(metrics["observed_seconds"])
        self.assertIsNone(metrics["primary_failures_per_observed_hour"])
        self.assertNotIn("synthetic-secret-token", json.dumps(metrics))

    def test_metrics_bound_correlation_storage_and_report_incomplete_deduplication(self):
        summarizer = module("summarize-gateway-log")
        metrics = summarizer.summarize_metrics([
            "Gateway session failed: correlation=11111111-2222-3333-4444-555555555555, reason=unknown.\n",
            "Gateway session failed: correlation=aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee, reason=unknown.\n",
        ], identity_limit=1)
        self.assertFalse(metrics["correlation_complete"])
        self.assertEqual(metrics["primary_presence_failures"], 2)

    def test_safe_supervisor_diagnostics_correlate_by_server_owner_and_preserve_typed_reason(self):
        summarizer = module("summarize-gateway-log")
        owner = "11111111-2222-3333-4444-555555555555"
        line = ("Gateway extension 'file' transport failed without ending presence: IOException; "
                f"retrying in 1s. serverConnection={owner}, connectionEpoch=3.")
        self.assertEqual(summarizer.summarize([line]), ["file_extension_transport_failed IOException"])
        metrics = summarizer.summarize_metrics([
            line, f"Gateway session failed: correlation=aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee, serverConnection={owner}, reason=unknown.",
        ])
        self.assertEqual(metrics["primary_presence_failures"], 1)
        self.assertEqual(metrics["secondary_events"]["correlated_presence_failure"], 1)
        self.assertEqual(metrics["secondary_reasons"], {"IOException": 1})
        self.assertNotIn(owner, json.dumps(metrics))


class PublishedClientPackFetchTests(unittest.TestCase):
    def setUp(self):
        self.fetcher = module("fetch-published-client-pack")
        self.directory = tempfile.TemporaryDirectory()
        self.fetch_count = 0
        self.source_version = "1.2.0"
        self.source_tag = "v" + self.source_version
        self.source_commit = "a" * 40
        self.archive_bytes = {
            f"netratel-client-{self.source_version}-linux-x64.tar.gz": b"linux pack",
            f"netratel-client-{self.source_version}-win-x64.zip": b"windows pack",
            f"netratel-client-{self.source_version}-osx-arm64.tar.gz": b"mac pack",
        }

    def tearDown(self):
        self.directory.cleanup()

    def run_fetch(self, *, selected_runtime=None, missing_assets=(), verification=None):
        self.fetch_count += 1
        output = Path(self.directory.name) / f"published-pack-{self.fetch_count}"
        output.mkdir()
        files = {name: {"sha256": hashlib.sha256(data).hexdigest()}
                 for name, data in self.archive_bytes.items()}
        publication = {
            "productVersion": self.source_version,
            "verification": verification if verification is not None else {"state": "complete"},
            "publicCommit": self.source_commit,
            "inputReceipt": {"repository": "BostonTechnologies/netratel",
                             "headSha": self.source_commit, "files": files},
        }
        publication_data = json.dumps(publication).encode()
        asset_bytes = {
            "publication.json": publication_data,
            "SHA256SUMS": b"release checksums\n",
            **self.archive_bytes,
        }
        assets = [
            {"name": name, "state": "uploaded", "size": len(data),
             "digest": "sha256:" + hashlib.sha256(data).hexdigest()}
            for name, data in asset_bytes.items() if name not in missing_assets
        ]
        release_data = json.dumps({"draft": False, "tag_name": self.source_tag, "assets": assets})
        self.downloaded = []

        def download(args, **_kwargs):
            args = tuple(args)
            name = args[args.index("--pattern") + 1]
            target = Path(args[args.index("--dir") + 1]) / name
            target.write_bytes(asset_bytes[name])
            self.downloaded.append(name)
            return subprocess.CompletedProcess(args, 0, "", "")

        def command(*args):
            if args[-1] == "tools/ci/product-version.py":
                return "0.1.0-rc.13"
            if args[-1] == "tools/ci/select-prior-release.py":
                return json.dumps({"tag": self.source_tag})
            raise AssertionError(f"Unexpected command: {args}")

        def gh(*args):
            if args[1].endswith("/releases/tags/" + self.source_tag):
                return release_data
            if args[1].endswith("/commits/" + self.source_tag):
                return json.dumps({"sha": self.source_commit})
            raise AssertionError(f"Unexpected GitHub API request: {args}")

        argv = ["fetch-published-client-pack.py", "--output", str(output)]
        if selected_runtime is not None:
            argv.extend(("--runtime", selected_runtime))
        with patch.object(self.fetcher, "command", side_effect=command), \
                patch.object(self.fetcher, "gh", side_effect=gh), \
                patch.object(self.fetcher.subprocess, "run", side_effect=download), \
                patch.object(sys, "argv", argv):
            self.fetcher.main()
        return output

    def test_runtime_filter_downloads_only_requested_archive_and_keeps_metadata(self):
        selected = f"netratel-client-{self.source_version}-win-x64.zip"
        for verification in (
            {"state": "complete"},
            {
                "state": "complete",
                "scope": "build-distribution-integrity",
                "requiredSmokes": [],
                "functionalAcceptance": {"state": "pending-owner-testing", "executedSmokes": []},
            },
        ):
            with self.subTest(verification=verification):
                output = self.run_fetch(selected_runtime="win-x64", verification=verification)
                self.assertCountEqual(self.downloaded, ["publication.json", "SHA256SUMS", selected])
                self.assertTrue((output / selected).is_file())
                self.assertFalse((output / f"netratel-client-{self.source_version}-linux-x64.tar.gz").exists())

    def test_runtime_filter_still_requires_every_inventory_asset_to_be_published(self):
        linux_archive = f"netratel-client-{self.source_version}-linux-x64.tar.gz"
        with self.assertRaisesRegex(ValueError, "Publication inventory asset is absent"):
            self.run_fetch(selected_runtime="win-x64", missing_assets=(linux_archive,))
        self.assertCountEqual(self.downloaded, ["publication.json", "SHA256SUMS"])

    def test_runtime_filter_rejects_duplicate_runtime_inventory_entries(self):
        windows_tarball = f"netratel-client-{self.source_version}-win-x64.tar.gz"
        self.archive_bytes[windows_tarball] = b"duplicate windows pack"
        with self.assertRaisesRegex(ValueError, "Duplicate published client runtime: win-x64"):
            self.run_fetch(selected_runtime="win-x64")
        self.assertCountEqual(self.downloaded, ["publication.json", "SHA256SUMS"])

    def test_missing_requested_runtime_is_rejected_and_default_fetch_remains_complete(self):
        with self.assertRaisesRegex(ValueError, "no client archive for runtime: freebsd-x64"):
            self.run_fetch(selected_runtime="freebsd-x64")
        self.assertCountEqual(self.downloaded, ["publication.json", "SHA256SUMS"])

        self.run_fetch()
        self.assertCountEqual(self.downloaded, [
            "publication.json", "SHA256SUMS", *self.archive_bytes.keys(),
        ])


class ChromiumNssSmokeTests(unittest.TestCase):
    def test_nss_helper_creates_and_removes_only_its_private_store_and_rejects_legacy_precedence(self):
        helper = ROOT / "tools/ci/chromium-nss-trust.sh"
        with tempfile.TemporaryDirectory(prefix="netratel-nss-guard-") as temporary:
            root = Path(temporary)
            fake_bin = root / "bin"
            fake_bin.mkdir()
            fake_certutil = fake_bin / "certutil"
            fake_certutil.write_text(
                """#!/usr/bin/env bash
printf '%s\\n' "$*" >> "$CERTUTIL_CALLS"
if [[ "${CERTUTIL_FAIL:-}" == "$1" ]]; then
  exit 42
fi
if [[ "$1" == -N ]]; then
  shift
  while (($#)); do
    if [[ "$1" == -d ]]; then
      database="${2#sql:}"
      mkdir -p "$database"
      : > "$database/cert9.db"
      exit 0
    fi
    shift
  done
  exit 2
fi
""",
                encoding="utf-8",
            )
            fake_certutil.chmod(0o755)
            temporary_root = root / "tmp"
            temporary_root.mkdir()
            certificate = root / "smoke-ca.crt"
            certificate.write_text("synthetic test certificate\n", encoding="ascii")
            call_log = root / "certutil-calls.txt"
            environment = os.environ.copy()
            environment.update(
                TMPDIR=str(temporary_root),
                CERTUTIL_CALLS=str(call_log),
                PATH=f"{fake_bin}{os.pathsep}{environment['PATH']}",
            )
            legacy_store = root / "fixture-home" / ".pki" / "nssdb"
            prepare = subprocess.run(
                [
                    "bash", "-c",
                    'source "$1"; require_chromium_nss_legacy_store_absent "$2" || exit 11; '
                    'prepare_chromium_nss_database "$3" "$4" || exit 12; '
                    'chromium_nss_xdg_data_home="$3"; '
                    'printf "%s\\n" "$chromium_nss_xdg_data_home"; '
                    'test -f "$chromium_nss_xdg_data_home/pki/nssdb/cert9.db" || exit 13; '
                    'cleanup_chromium_nss_trust',
                    "nss-helper-test", str(helper), str(legacy_store),
                    str(tempfile.mkdtemp(prefix="netratel-chromium-nss.", dir=temporary_root)),
                    str(certificate),
                ],
                env=environment,
                check=False,
                text=True,
                capture_output=True,
            )
            self.assertEqual(prepare.returncode, 0, prepare.stderr)
            owned_store = Path(prepare.stdout.strip())
            self.assertEqual(owned_store.parent, temporary_root)
            self.assertFalse(owned_store.exists(), "The helper must remove its exact private XDG store.")
            certutil_calls = call_log.read_text(encoding="utf-8")
            self.assertIn("-A -d sql:", certutil_calls)
            self.assertIn("-n netratel-smoke-root -t C,,", certutil_calls)

            for failed_phase in ("-N", "-A", "-L"):
                with self.subTest(certutil_phase=failed_phase):
                    failing_store = Path(tempfile.mkdtemp(
                        prefix="netratel-chromium-nss.", dir=temporary_root
                    ))
                    failing_environment = environment.copy()
                    failing_environment["CERTUTIL_FAIL"] = failed_phase
                    failed_setup = subprocess.run(
                        [
                            "bash", "-c",
                            'source "$1"; chromium_nss_xdg_data_home="$2"; '
                            'prepare_chromium_nss_database "$2" "$3" || { '
                            'cleanup_chromium_nss_trust; exit 12; }; exit 0',
                            "nss-helper-test", str(helper), str(failing_store), str(certificate),
                        ],
                        env=failing_environment,
                        check=False,
                        text=True,
                        capture_output=True,
                    )
                    self.assertEqual(failed_setup.returncode, 12, failed_setup.stderr)
                    self.assertFalse(failing_store.exists(), "Failed NSS setup must clean its exact task-owned directory.")

            legacy_store.mkdir(parents=True)
            before = set(temporary_root.iterdir())
            refused = subprocess.run(
                [
                    "bash", "-c",
                    'source "$1"; require_chromium_nss_legacy_store_absent "$2"',
                    "nss-helper-test", str(helper), str(legacy_store),
                ],
                env=environment,
                check=False,
                text=True,
                capture_output=True,
            )
            self.assertNotEqual(refused.returncode, 0)
            self.assertIn("pre-existing ~/.pki/nssdb", refused.stderr)
            self.assertEqual(before, set(temporary_root.iterdir()))

    def test_strict_oidc_browser_and_api_request_share_the_private_ca_trust_path(self):
        browser_test = (ROOT / "src/NetRatel/NetRatel.Web.PlaywrightTests/OidcComposeBrowserSmokeTests.cs").read_text()
        self.assertEqual(browser_test.count("IgnoreHTTPSErrors = false"), 2)
        self.assertNotIn("IgnoreHTTPSErrors = true", browser_test)
        self.assertIn("context.APIRequest.GetAsync", browser_test)
        self.assertIn("executableVersion.Major >= 146", browser_test)
        self.assertIn('browserEnvironment["XDG_DATA_HOME"] = nssDataHome', browser_test)
        self.assertIsNotNone(re.search(
            r"if \(expectCurrentShell\)\s*\{\s*var verifyDirectoryAuthorizationBoundaries.*?VerifyClientDirectoryCircuitAsync",
            browser_test,
            re.DOTALL,
        ))

        helper = (ROOT / "tools/ci/chromium-nss-trust.sh").read_text()
        self.assertIn('legacy_database="$home_directory/.pki/nssdb"', helper)
        self.assertIn('local database_path="$xdg_data_home/pki/nssdb"', helper)
        self.assertIn("certutil -A", helper)

        for relative in (
            "tools/ci/smoke-oidc-compose.sh",
            "tools/ci/smoke-postgresql-oidc-upgrade.sh",
        ):
            source = (ROOT / relative).read_text()
            with self.subTest(script=relative):
                self.assertIn("chromium-nss-trust.sh", source)
                self.assertIn('XDG_DATA_HOME="$chromium_nss_xdg_data_home"', source)
                self.assertIn('NODE_EXTRA_CA_CERTS="$smoke_ca_certificate_path"', source)
                self.assertIn("NETRATEL_BROWSER_SMOKE_NSS_DATA_HOME=", source)
                self.assertIn("basicConstraints=critical,CA:FALSE", source)
                self.assertIn("subjectAltName=DNS:gateway,DNS:localhost,IP:127.0.0.1", source)

        oidc_smoke = (ROOT / "tools/ci/smoke-oidc-compose.sh").read_text()
        self.assertIn('subjectAltName=DNS:host.docker.internal,IP:127.0.0.1', oidc_smoke)
        self.assertIn('if [[ "${GITHUB_ACTIONS:-false}" == true ]]; then', oidc_smoke)
        production_overlay = (ROOT / "tests/compose/oidc-smoke.production.compose.yaml").read_text()
        self.assertIn('Authentication__Oidc__RequireHttpsMetadata: "true"', production_overlay)
        upgrade_smoke = (ROOT / "tools/ci/smoke-postgresql-oidc-upgrade.sh").read_text()
        self.assertIn('run_browser_oidc_smoke "$legacy_version" false', upgrade_smoke)
        self.assertIn('run_browser_oidc_smoke "v$(python3 tools/ci/product-version.py)" true', upgrade_smoke)

        manual_workflow = ROOT / ".github/workflows/integration-validation.yml"
        self.assertIn("libnss3-tools", manual_workflow.read_text())
        for workflow in (
            ROOT / ".github/workflows/public-pr-validation.yml",
            ROOT / ".github/workflows/release-build.yml",
        ):
            with self.subTest(workflow=workflow.name):
                self.assertNotIn("libnss3-tools", workflow.read_text())

    def test_pinned_oidc_compose_uses_one_subject_profile_for_browser_code_and_refresh_tokens(self):
        page = (ROOT / "tests/compose/oidc-smoke-login.html").read_text()
        profile_block = re.search(
            r'<script id="oidc-claim-profiles" type="application/json">([\s\S]*?)</script>',
            page,
        )
        self.assertIsNotNone(profile_block)
        profiles = json.loads(profile_block.group(1))
        self.assertEqual(
            set(profiles),
            {
                "netratel-test-operator",
                "netratel-test-tenant-admin",
                "netratel-test-unprivileged",
            },
        )
        for subject, claims in profiles.items():
            with self.subTest(subject=subject):
                self.assertEqual(claims["sub"], subject)
                self.assertEqual(claims["aud"], ["netratel-smoke-client", "netratel.api"])
                self.assertEqual(
                    claims.get("roles", []),
                    ["Operator"] if subject == "netratel-test-operator" else [],
                )
                self.assertTrue(claims["preferred_username"].endswith("@example.test"))

        compose_files = (
            ROOT / "tests/compose/oidc-smoke.compose.yaml",
            ROOT / "tests/compose/oidc-smoke.production.compose.yaml",
        )
        for compose_file in compose_files:
            source = compose_file.read_text()
            config_block = re.search(r"(?m)^\s+JSON_CONFIG:\s*>-\s*\n\s+(\{[^\n]+\})\s*$", source)
            self.assertIsNotNone(config_block, str(compose_file))
            provider_config = json.loads(config_block.group(1))
            with self.subTest(compose_file=compose_file.name):
                self.assertEqual(provider_config["loginPagePath"], "/run/netratel-smoke/oidc-login.html")
                callbacks = provider_config["tokenCallbacks"]
                self.assertEqual(len(callbacks), 1)
                self.assertEqual(
                    [mapping["match"] for mapping in callbacks[0]["requestMappings"]],
                    ["netratel-cli-smoke-client"],
                )

        base_compose = compose_files[0].read_text()
        self.assertIn("file: ${PWD}/tests/compose/oidc-smoke-login.html", base_compose)
        self.assertIn("target: /run/netratel-smoke/oidc-login.html", base_compose)
        self.assertIn('usernameInput.addEventListener("input", updateClaims)', page)
        self.assertIn('loginForm.addEventListener("submit", updateClaims)', page)

        smoke = (ROOT / "tools/ci/smoke-oidc-compose.sh").read_text()
        self.assertIn('source "$root/tools/ci/oidc-smoke-claims.sh"', smoke)
        self.assertIn('claims=${claims_json}', smoke)
        self.assertIn('claims=${operator_claims_json}', smoke)
        self.assertIn(
            'operator_claims_json="$(oidc_smoke_claims_for_subject '
            '"$root/tests/compose/oidc-smoke-login.html" netratel-test-operator)"',
            smoke,
        )
        request_helper = re.search(
            r"request_oidc_access_token\(\) \(([\s\S]*?)\n\)",
            smoke,
        )
        self.assertIsNotNone(request_helper)
        for token_variable in (
            "$access_token", "$id_token", "$refreshed_access_token", "$refreshed_id_token",
        ):
            self.assertRegex(
                request_helper.group(1),
                re.escape(f'verify_oidc_smoke_token_claims "{token_variable}"')
                + r'\s+"\$claims_json".*"\$oidc_authority"\s+\|\|\s*return 1',
            )
        self.assertIn("grant_type=refresh_token", smoke)

        upgrade = (ROOT / "tools/ci/smoke-postgresql-oidc-upgrade.sh").read_text()
        self.assertIn('source "$root/tools/ci/oidc-smoke-claims.sh"', upgrade)
        upgrade_helper = re.search(
            r"request_operator_access_token\(\) \(([\s\S]*?)\n\)",
            upgrade,
        )
        self.assertIsNotNone(upgrade_helper)
        self.assertIn(
            'expected_issuer="http://host.docker.internal:${NETRATEL_OIDC_TEST_PORT}/default"',
            upgrade_helper.group(1),
        )
        self.assertIn('--data-urlencode "claims=${claims_json}"', upgrade_helper.group(1))
        self.assertIn('token_cookie_jar="$(mktemp)"', upgrade_helper.group(1))
        self.assertIn('trap \'unlink "$token_cookie_jar" 2>/dev/null || true\' EXIT',
                      upgrade_helper.group(1))
        self.assertNotIn('--cookie "$cookie_jar"', upgrade_helper.group(1))
        self.assertIn('"access token" "$expected_issuer" || return 1', upgrade_helper.group(1))
        self.assertLess(
            upgrade_helper.group(1).index('"access token" "$expected_issuer" || return 1'),
            upgrade_helper.group(1).index('printf \'%s\' "$access_token"'),
        )

    def test_oidc_claims_shell_helpers_extract_and_reject_fixture_claims(self):
        helper = ROOT / "tools/ci/oidc-smoke-claims.sh"
        page = ROOT / "tests/compose/oidc-smoke-login.html"
        username = "netratel-test-operator"
        extracted = subprocess.run(
            [
                "bash", "-c",
                'source "$1"; oidc_smoke_claims_for_subject "$2" "$3"',
                "oidc-smoke-claims-test", str(helper), str(page), username,
            ],
            check=True,
            capture_output=True,
            text=True,
        )
        claims_profile = json.loads(extracted.stdout)
        self.assertEqual(claims_profile["sub"], username)
        self.assertEqual(claims_profile["preferred_username"], f"{username}@example.test")
        self.assertEqual(claims_profile["roles"], ["Operator"])
        self.assertEqual(claims_profile["aud"], ["netratel-smoke-client", "netratel.api"])

        def encoded_token(payload):
            encoded = base64.urlsafe_b64encode(json.dumps(payload).encode()).rstrip(b"=").decode()
            return f"e30.{encoded}.signature"

        expected_issuer = "http://host.docker.internal:18085/default"
        valid_claims = {
            **claims_profile,
            "iss": expected_issuer,
            "exp": int(time.time()) + 120,
        }

        def verify(payload):
            token = encoded_token(payload)
            result = subprocess.run(
                [
                    "bash", "-c",
                    'source "$1"; verify_oidc_smoke_token_claims "$2" "$3" "$4" "$5" "$6"',
                    "oidc-smoke-claims-test", str(helper), token, json.dumps(claims_profile),
                    username, "access token", expected_issuer,
                ],
                capture_output=True,
                text=True,
            )
            self.assertNotIn(token, result.stderr)
            return result

        self.assertEqual(verify(valid_claims).returncode, 0)
        invalid_claims = {
            "missing operator role": {
                key: value for key, value in valid_claims.items() if key != "roles"
            },
            "wrong issuer": {**valid_claims, "iss": "http://wrong.example/default"},
            "wrong audience": {**valid_claims, "aud": ["netratel-smoke-client"]},
            "expired token": {**valid_claims, "exp": int(time.time()) - 1},
        }
        for reason, payload in invalid_claims.items():
            with self.subTest(reason=reason):
                self.assertNotEqual(verify(payload).returncode, 0)


class RuntimeSelectorRetirementTests(unittest.TestCase):
    api_retired_properties = (
        "PresenceEnabled", "GatewayEnabled", "ClientUpdatesEnabled",
        "ControlGatewayEnabled", "FileGatewayEnabled", "LogGatewayEnabled", "RemoteSupportGatewayEnabled",
        "RemoteSupportV2InventoryEnabled", "RemoteSupportV2LifecycleAuthorityEnabled",
        "RemoteSupportV2ReplicaSafeEdgeEnabled", "RemoteSupportV2MediaEnabled",
        "RemoteSupportLegacyGatewayRollbackEnabled", "PrimaryCardGatewayReadsEnabled",
        "PrimaryCardGatewayActionsEnabled", "TerminalGatewayEnabled", "TerminalGatewayPrimaryCardEnabled",
        "PresenceReadModelEnabled", "TelemetryShadowEnabled", "CommandShadowEnabled",
        "CommandPersistenceEnabled", "JobShadowEnabled", "TerminalShadowEnabled", "SignalRShadowEnabled",
        "SignalRShadowLocalCanaryEnabled", "PresenceAuthorityEnabled", "PingAuthorityEnabled",
        "TelemetryAuthorityEnabled", "FileBrowseAuthorityEnabled", "LogAuthorityEnabled",
        "RemoteSupportAuthorityEnabled", "CommandAuthorityEnabled", "JobAuthorityEnabled",
        "TerminalAuthorityEnabled", "SignalRAuthorityEnabled", "RemoteSupportShadowEnabled",
        "AuthorityMode",
    )
    client_retired_gateway_properties = (
        "RequiredPresenceAuthority", "TelemetryShadowEnabled", "TelemetryAuthorityEnabled",
        "ControlAuthorityEnabled", "CommandAuthorityEnabled", "FileAuthorityEnabled",
        "JobAuthorityEnabled", "LogAuthorityEnabled", "RemoteSupportAuthorityEnabled",
        "TerminalAuthorityEnabled", "RemoteSupportV1Enabled", "ControlGatewayEnabled",
        "FileGatewayEnabled", "LogGatewayEnabled", "RemoteSupportGatewayEnabled",
        "TerminalGatewayEnabled", "RemoteSupportV2InventoryEnabled", "RemoteSupportV2MediaEnabled",
    )
    selector_names = tuple(sorted(set(api_retired_properties + client_retired_gateway_properties)
                                  - {"PresenceEnabled", "GatewayEnabled", "ClientUpdatesEnabled"}))
    client_selector_names = tuple(sorted(client_retired_gateway_properties))
    retired_selector_pattern = re.compile(
        r"\bNetRatelAkkaMigration\b"
        r"|(?<![A-Za-z0-9])LegacyQueueWorker(?![A-Za-z0-9])"
        r"|(?<![A-Za-z0-9])Transport(?::|__|\.)Mode(?![A-Za-z0-9])"
        r"|(?<![A-Za-z0-9])Gateway(?::|__|\.)Enabled(?![A-Za-z0-9])"
        r"|[\"']Transport[\"']\s*:\s*\{\s*[\"']Mode[\"']"
        r"|[\"']Gateway[\"']\s*:\s*\{\s*[\"']Enabled[\"']"
        r"|\b(?:PrimaryCardGateway(?:Reads|Actions)Enabled|TerminalGatewayPrimaryCardEnabled)\b"
        r"|(?<![A-Za-z0-9])Gateway(?::|__|\.)(?:" +
        "|".join(re.escape(name) for name in client_selector_names) + r")(?![A-Za-z0-9])"
        r"|(?<![A-Za-z0-9])(?:" + "|".join(re.escape(name) for name in selector_names) + r")(?![A-Za-z0-9])",
        re.IGNORECASE,
    )
    scanned_roots = (
        ROOT / "src/NetRatel",
        ROOT / "release",
        ROOT / "docker",
        ROOT / "tests",
        ROOT / ".github/workflows",
        ROOT / "tools/ci",
        ROOT / "docs",
    )
    scanned_suffixes = {
        ".cs", ".csproj", ".json", ".yaml", ".yml", ".sh", ".py", ".md", ".conf",
        ".properties", ".env", ".props", ".targets", ".xml", ".proto", ".ps1", ".psm1",
        ".psd1", ".cmd", ".bat", ".toml", ".ini", ".sln", ".slnx", ".slnf",
    }

    @staticmethod
    def _marker_region(content, start_marker, end_marker, start_at=0):
        start = content.index(start_marker, start_at)
        end = content.index(end_marker, start + len(start_marker))
        return start, end

    @staticmethod
    def _csharp_member_region(content, member_marker):
        start = content.index(member_marker)
        following = re.search(r"(?m)^ {4}(?:\[(?:Fact|Theory)\]|(?:public|private|protected)\s+)", content[start + len(member_marker):])
        end = len(content) if following is None else start + len(member_marker) + following.start()
        return start, end

    @classmethod
    def _allowed_retirement_regions(cls):
        regions = {}

        def add(path, content, label, start, end):
            for match in cls.retired_selector_pattern.finditer(content, start, end):
                regions.setdefault(path, []).append((label, match.start(), match.end()))

        def marker(path, content, label, start_marker, end_marker, start_at=0):
            start, end = cls._marker_region(content, start_marker, end_marker, start_at)
            add(path, content, label, start, end)

        def member(path, content, label, marker_text):
            start, end = cls._csharp_member_region(content, marker_text)
            add(path, content, label, start, end)

        relative = "src/NetRatel/NetRatel.Infrastructure/Artifacts/ScriptTemplateService.cs"
        content = (ROOT / relative).read_text(encoding="utf-8")
        # Windows and Linux are resource templates with inert historical keys;
        # only the retained macOS flow still cleans exact legacy properties.
        marker(relative, content, "macOS service-environment cleanup inventory",
               "            retired = {", "            retired_json_gateway = {")
        marker(relative, content, "macOS nested JSON cleanup inventory",
               "            retired_json_gateway = {", "            environment = {")

        relative = "src/NetRatel/NetRatel.Client/tools/netratel-update.ps1"
        content = (ROOT / relative).read_text(encoding="utf-8")
        marker(relative, content, "Windows updater JSON property cleanup",
               "$name -in @(", ")) {")

        relative = "src/NetRatel/NetRatel.Tests/API/NetRatelAkkaRuntimeRegistrationTests.cs"
        content = (ROOT / relative).read_text(encoding="utf-8")
        marker(relative, content, "retired API key test inventory", "RetiredBooleanKeys =\n    [", "    ];")
        member(relative, content, "retired API keys are inert in either boolean state",
               "public void RetiredMigrationKeys_AreInertWhenAbsentEnabledOrDisabled(")

        relative = "src/NetRatel/NetRatel.API.IntegrationTests/RuntimeInventoryHostIntegrationTests.cs"
        content = (ROOT / relative).read_text(encoding="utf-8")
        marker(relative, content, "runtime host retired selector fixture inventory",
               "RetiredBooleanKeys =\n    [", "    ];")
        member(relative, content, "initialized Local host ignores supplied retired selectors",
               "public async Task InitializedLocalHostsIgnoreRetiredSelectorsAndRunTheSameAkkaGateway(")
        member(relative, content, "pre-ready host rejects database failure with retired keys present",
               "public async Task DatabaseUnavailableDuringBootstrapKeepsTheOperationalRuntimeOutAndReadinessFalse(")
        member(relative, content, "unconfigured host remains non-ready when retired selectors are true",
               "public async Task UnconfiguredLocalBootstrapDoesNotStartAkkaEvenWhenRetiredSelectorsAreTrue(")

        relative = "src/NetRatel/NetRatel.API.IntegrationTests/ApiFactory.cs"
        content = (ROOT / relative).read_text(encoding="utf-8")
        marker(relative, content, "API integration fixture environment snapshot keys",
               "RetiredSelectorConfigurationKeys { get; } = Array.AsReadOnly<string>(\n    [", "    ]);")

        relative = "src/NetRatel/NetRatel.Tests/API/ApiEndpointRegistrationSourceTests.cs"
        content = (ROOT / relative).read_text(encoding="utf-8")
        member(relative, content, "API registration does not bind the retired options section",
               "public void Program_Uses_Dedicated_H2c_Listener_For_The_Agent_Gateway(")

        relative = "src/NetRatel/NetRatel.Tests/Client/AgentGatewayPresenceClientTests.cs"
        content = (ROOT / relative).read_text(encoding="utf-8")
        member(relative, content, "retired client settings do not disable gateway extensions",
               "public async Task RunAsync_LegacyGatewaySelectorsCannotDisableAcceptedExtensions(")

        relative = "src/NetRatel/NetRatel.Tests/Client/ClientConfigurationLoaderTests.cs"
        content = (ROOT / relative).read_text(encoding="utf-8")
        marker(relative, content, "retired client key test inventory",
               "RetiredGatewayBooleanSettings =\n    [", "    ];")
        test_start = content.index("public void Load_RetiredGatewaySelectorsAreInertWhileSupportedClientTunablesRemainActive(")
        for label, literal in (
            ("RequiredPresenceAuthority is tested as inert", 'gatewayEntries.Append(",\\"RequiredPresenceAuthority\\"'),
            ("Transport.Mode legacy JSON shape is tested as inert", '"Transport": { "Mode":'),
        ):
            start = content.index(literal, test_start)
            end = content.index("\n", start)
            add(relative, content, label, start, end)

        relative = "src/NetRatel/NetRatel.Tests/Client/AkkaGatewayCanarySourceTests.cs"
        content = (ROOT / relative).read_text(encoding="utf-8")
        member(relative, content, "client runtime does not select a transport mode",
               "public void Client_Runtime_Uses_Akka_Without_Transport_Selection(")

        relative = "src/NetRatel/NetRatel.Tests/Infrastructure/ScriptTemplateServiceTests.cs"
        content = (ROOT / relative).read_text(encoding="utf-8")
        member(relative, content, "macOS installer retires stale values while preserving ordinary settings",
               "public async Task Build_MacOS_Service_Preserves_Only_ExplicitSplitGateway(")
        member(relative, content, "generated installer has no retired client defaults",
               "private static void AssertNoRetiredDefaults(")

        relative = "docs/implementation/RC11_AKKA_RUNTIME_REFACTOR.md"
        content = (ROOT / relative).read_text(encoding="utf-8")
        marker(relative, content, "bounded report-only refactor inventory",
               "<!-- RETIRED_SELECTOR_INVENTORY_BEGIN -->", "<!-- RETIRED_SELECTOR_INVENTORY_END -->")

        return regions

    def test_retired_activation_selectors_are_absent_outside_the_exact_cleanup_and_regression_allowlist(self):
        allowed_regions = self._allowed_retirement_regions()
        unexpected = []
        seen_regions = set()
        for base in self.scanned_roots:
            paths = (base,) if base.is_file() else base.rglob("*")
            for path in paths:
                if not path.is_file() or path.suffix not in self.scanned_suffixes:
                    continue
                if {"bin", "obj", "__pycache__"}.intersection(path.parts):
                    continue
                relative = path.relative_to(ROOT).as_posix()
                if relative == "tools/ci/test-release-validation.py":
                    continue
                content = path.read_bytes().decode("utf-8", errors="ignore")
                regions = allowed_regions.get(relative, ())
                for match in self.retired_selector_pattern.finditer(content):
                    allowed = next((region for region in regions if region[1] <= match.start() < region[2]), None)
                    if allowed is None:
                        line = content.count("\n", 0, match.start()) + 1
                        unexpected.append(f"{relative}:{line}: {match.group(0)}")
                    else:
                        seen_regions.add((relative, allowed[0]))

        expected_regions = {
            (path, label)
            for path, regions in allowed_regions.items()
            for label, _start, _end in regions
        }
        self.assertEqual(expected_regions, seen_regions,
                         "Every allowlisted region must contain a retired selector for the documented cleanup or regression assertion.")
        self.assertEqual([], unexpected,
                         "Retired activation selectors may appear only inside exact updater cleanup inventories and inert-key behavior assertions:\n"
                         + "\n".join(unexpected))

    def test_retired_client_cleanup_inventories_are_exact_and_remain_scoped_to_update(self):
        expected_properties = {name.lower() for name in self.client_retired_gateway_properties}
        expected_environment = {"transport__mode"} | {f"gateway__{name}" for name in expected_properties}

        def set_body(content, marker, closing_pattern, start_at=0):
            start = content.index(marker, start_at) + len(marker)
            close = re.search(closing_pattern, content[start:], re.MULTILINE)
            self.assertIsNotNone(close, f"Could not bound retirement set after {marker}.")
            body = content[start:start + close.start()]
            return {value.lower() for value in re.findall(r"[\"']([^\"']+)[\"']", body)}

        template_path = ROOT / "src/NetRatel/NetRatel.Infrastructure/Artifacts/ScriptTemplateService.cs"
        template = template_path.read_text(encoding="utf-8")
        retired_lists = [match.start() for match in re.finditer(r"(?m)^ {12}retired = \{", template)]
        self.assertEqual(1, len(retired_lists), "Only the preserved macOS template retains environment cleanup.")
        self.assertEqual(expected_environment,
                         set_body(template, "retired = {", r"(?m)^ {12}\}", retired_lists[0]))
        json_lists = [match.start() for match in re.finditer(r"(?m)^ {12}retired_json_gateway = \{", template)]
        self.assertEqual(1, len(json_lists), "Only the preserved macOS template retains exact JSON cleanup.")
        self.assertEqual(expected_properties,
                         set_body(template, "retired_json_gateway = {", r"(?m)^ {12}\}", json_lists[0]))

        resources = {"install.ps1", "install-linux.sh"}
        resource_root = template_path.parent / "Templates"
        self.assertEqual(resources, {path.name for path in resource_root.iterdir() if path.is_file()},
                         "The resource inventory must contain just the two maintained installer entry points.")
        project = (template_path.parents[1] / "NetRatel.Infrastructure.csproj").read_text(encoding="utf-8")
        for name in resources:
            resource = (resource_root / name).read_text(encoding="utf-8")
            self.assertIsNone(self.retired_selector_pattern.search(resource),
                              "Resource installers must not emit retired selectors or hide a migration engine: " + name)
            self.assertIn('Include="Artifacts/Templates/' + name + '"', project)
            self.assertIn('LogicalName="NetRatel.Installers.' + name + '"', project)
        render_start, render_end = self._csharp_member_region(
            template, "private static string Render(DeploymentScriptTemplateRequest request, bool windows)")
        renderer = template[render_start:render_end]
        self.assertIn('windows ? "install.ps1" : "install-linux.sh"', renderer)
        self.assertIn('GetManifestResourceStream("NetRatel.Installers." + name)', renderer)
        self.assertIn("Regex.Replace(reader.ReadToEnd()", renderer)

        api_seed = (ROOT / "src/NetRatel/NetRatel.API/Services/AgentUpdateScriptSeedService.cs").read_text(encoding="utf-8")
        shared_start, shared_end = self._csharp_member_region(
            api_seed, "private static string BuildSeedInstaller(string runtime)")
        shared_renderer = api_seed[shared_start:shared_end]
        self.assertEqual(1, shared_renderer.count("new NetRatel.Infrastructure.Artifacts.ScriptTemplateService().Build("))
        self.assertIn("IsUpdateSeed: true", shared_renderer)
        for platform, runtime in (("Windows", "win-x64"), ("Linux", "linux-x64")):
            start, end = self._csharp_member_region(api_seed, "private static SeedScript Build" + platform + "Script()")
            builder = api_seed[start:end]
            self.assertIn('BuildSeedInstaller("' + runtime + '")', builder)
            self.assertIsNone(self.retired_selector_pattern.search(builder),
                              "Seeds must share rendering without a second retired-selector migration path.")

        updater_path = ROOT / "src/NetRatel/NetRatel.Client/tools/netratel-update.ps1"
        updater = updater_path.read_text(encoding="utf-8")
        updater_function = updater[updater.index("function Remove-NetRatelRetiredClientSettings"):updater.index("function Copy-NetRatelInstalledClientSettings")]
        updater_property_list = updater_function.split("$name -in @(", 1)[1].split(")) {", 1)[0]
        updater_properties = {value.lower() for value in re.findall(r"'([^']+)'", updater_property_list)}
        self.assertEqual(expected_properties, updater_properties)
        self.assertIn("$Settings.Transport.PSObject.Properties.Remove('Mode')", updater_function)

    def test_api_retirement_behavior_test_covers_the_exact_historical_key_inventory(self):
        path = ROOT / "src/NetRatel/NetRatel.Tests/API/NetRatelAkkaRuntimeRegistrationTests.cs"
        source = path.read_text(encoding="utf-8")
        start, end = self._marker_region(source, "RetiredBooleanKeys =\n    [", "    ];")
        actual = set(re.findall(r'"([^"]+)"', source[start:end]))
        expected = {
            f"NetRatelAkkaMigration:{name}"
            for name in self.api_retired_properties
            if name != "AuthorityMode"
        } | {"NetRatelAkkaMigration:Enabled", "LegacyQueueWorker:Enabled"}
        self.assertEqual(expected, actual)
        self.assertIn('enabledConfiguration["NetRatelAkkaMigration:AuthorityMode"] = "Shadow";', source)
        self.assertIn('disabledConfiguration["NetRatelAkkaMigration:AuthorityMode"] = "Authority";', source)

    def test_review_inventory_lists_exact_retired_api_and_client_selector_names(self):
        source = (ROOT / "docs/implementation/RC11_AKKA_RUNTIME_REFACTOR.md").read_text(encoding="utf-8")
        start, end = self._marker_region(
            source,
            "<!-- RETIRED_SELECTOR_INVENTORY_BEGIN -->",
            "<!-- RETIRED_SELECTOR_INVENTORY_END -->",
        )
        inventory = source[start:end]
        api_key_rows = re.findall(r"`(NetRatelAkkaMigration:[^`]+|LegacyQueueWorker:Enabled)`", inventory)
        api_keys = set(api_key_rows)
        expected_api_keys = {
            f"NetRatelAkkaMigration:{name}"
            for name in self.api_retired_properties
        } | {"NetRatelAkkaMigration:Enabled", "LegacyQueueWorker:Enabled"}
        self.assertEqual(expected_api_keys, api_keys)
        self.assertEqual(len(expected_api_keys), len(api_key_rows), "The review inventory must list each API selector exactly once.")

        client_key_rows = re.findall(r"`(Gateway:[^`]+|Transport:Mode)`", inventory)
        client_keys = set(client_key_rows)
        expected_client_keys = {
            f"Gateway:{name}"
            for name in self.client_retired_gateway_properties
        } | {"Transport:Mode"}
        self.assertEqual(expected_client_keys, client_keys)
        self.assertEqual(len(expected_client_keys), len(client_key_rows), "The review inventory must list each client selector exactly once.")

    def test_immutable_database_transport_and_job_identity_contracts_remain_present(self):
        proto = (ROOT / "src/NetRatel/NetRatel.AgentGateway.Contracts/Protos/agent_gateway.proto").read_text()
        package = re.search(r"(?m)^package\s+([A-Za-z_][A-Za-z_0-9.]*)\s*;", proto)
        self.assertIsNotNone(package)
        self.assertEqual(package.group(1), "netratel.gateway.v1")
        self.assertRegex(proto, r"(?m)^\s*string\s+legacy_spacetime_identity\s*=\s*3\s*;")

        ingress = (ROOT / "release/nginx.public-https.conf").read_text()
        self.assertRegex(ingress, r"(?m)^\s*location\s+\^~\s+/netratel\.gateway\.v1\.\s*\{")
        self.assertRegex(ingress, r"(?m)^\s*grpc_pass\s+grpc://api:9223\s*;")

        source_files = tuple(path for path in (ROOT / "src/NetRatel").rglob("*.cs")
                             if not {"bin", "obj"}.intersection(path.parts))
        api_sources = tuple(path for path in (ROOT / "src/NetRatel/NetRatel.API").rglob("*.cs")
                            if not {"bin", "obj"}.intersection(path.parts))
        source_contents = [(path, path.read_text(encoding="utf-8", errors="ignore")) for path in source_files]
        self.assertTrue(any("CurrentSchemaVersion = 1" in source for _path, source in source_contents),
                        "The retained SignalR envelope wire schema version must remain 1.")
        api_text = "\n".join(path.read_text(encoding="utf-8") for path in api_sources)
        self.assertIn('"/hubs/akka-authority"', api_text)
        self.assertIn('"shadowUpdated"', api_text)
        self.assertIn('"akka-shadow:v1:tenant:"', api_text)

        migration_path = ROOT / "src/NetRatel/NetRatel.Infrastructure/Persistence/Migrations/20260807105643_AddJobShadowObservations.cs"
        migration = migration_path.read_text(encoding="utf-8")
        self.assertIn('name: "JobShadowObservations"', migration)
        self.assertIn('SourceEventId = table.Column<long>', migration)
        self.assertIn('migrationBuilder.DropTable(\n                name: "JobShadowObservations")', migration)
        model = (ROOT / "src/NetRatel/NetRatel.Infrastructure/Persistence/OrchestratorDbContext.cs").read_text()
        self.assertIn('entity.ToTable("JobShadowObservations")', model)
        self.assertIn('entity.HasIndex(x => new { x.JobRunId, x.SourceEventId })', model)

        job_contracts = (ROOT / "src/NetRatel/NetRatel.Application/Jobs/JobRuntimeContracts.cs").read_text()
        self.assertEqual(2, job_contracts.count('string SourceSystem = "akka-job-shadow-event"'),
                         "The deployed run/step source identity must remain stable for idempotent replay.")
        inventory = (ROOT / "docs/implementation/RC11_AKKA_RUNTIME_REFACTOR.md").read_text()
        self.assertIn("`akka-job-shadow-event`", inventory)

    def test_web_realtime_consumer_uses_the_normalized_fanout_contract_type(self):
        source = (ROOT / "src/NetRatel/NetRatel.Web/Services/Terminal/AkkaAuthorityFanoutClient.cs").read_text()
        self.assertIn("RealtimeFanoutEnvelope", source)
        self.assertNotIn("ShadowFanoutEnvelope", source)
        self.assertIn('"shadowUpdated"', source)

    def test_deployed_agent_signing_key_filename_remains_a_narrow_file_compatibility(self):
        legacy_key = "/app/storage/keys/spacetime-es256-private.pem"
        signer = (ROOT / "src/NetRatel/NetRatel.Infrastructure/Services/OidcSigningService.cs").read_text()
        api = (ROOT / "src/NetRatel/NetRatel.API/Program.cs").read_text()
        self.assertIn(f'const string legacyContainerPath = "{legacy_key}";', signer)
        self.assertIn("if (File.Exists(legacyContainerPath))", signer)
        self.assertIn(f'candidatePaths.Add("{legacy_key}");', api)

    def test_spacetimedb_runtime_dependencies_and_known_runtime_paths_are_absent(self):
        project_files = [path for path in (ROOT / "src/NetRatel").rglob("*")
                         if path.is_file() and path.suffix in {".csproj", ".props", ".targets"}
                         and not {"bin", "obj"}.intersection(path.parts)]
        dependency_pattern = re.compile(r"(?i)\bSpacetime(?:DB)?(?:\.[A-Za-z0-9_.-]+)?\b")
        dependency_hits = []
        for path in project_files:
            content = path.read_text(encoding="utf-8", errors="ignore")
            if dependency_pattern.search(content):
                dependency_hits.append(path.relative_to(ROOT).as_posix())
        self.assertEqual([], dependency_hits, "No project/package reference may keep the retired SpacetimeDB runtime available.")

        runtime_types = re.compile(
            r"\b(?:SpacetimeDbService|ClientSpacetimeSubscriptions|SpacetimeDB\.Client|Spacetime\.Client|--spacetime-check)\b",
            re.IGNORECASE,
        )
        runtime_hits = []
        for path in (ROOT / "src/NetRatel").rglob("*"):
            if not path.is_file() or path.suffix not in {".cs", ".csproj", ".props", ".targets", ".json"}:
                continue
            if "NetRatel.Tests" in path.parts or {"bin", "obj"}.intersection(path.parts):
                continue
            if runtime_types.search(path.read_text(encoding="utf-8", errors="ignore")):
                runtime_hits.append(path.relative_to(ROOT).as_posix())
        self.assertEqual([], runtime_hits, "Production source must not retain a SpacetimeDB client, service, or probe fallback.")

        retired_paths = (
            "src/NetRatel/NetRatel.Client/Service/Spacetime",
            "src/NetRatel/NetRatel.API/Services/Spacetime",
            "src/NetRatel/NetRatel.Infrastructure/Spacetime",
            "src/NetRatel/NetRatel.Shared/SpacetimeIdentityHelpers.cs",
        )
        existing = [relative for relative in retired_paths if (ROOT / relative).exists()]
        self.assertEqual([], existing, "Known retired runtime implementation paths must remain deleted.")


class OidcUpgradeChannelTests(unittest.TestCase):
    script_path = ROOT / "tools/ci/smoke-postgresql-oidc-upgrade.sh"

    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="netratel-upgrade-channel-")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        (self.root / "tools/ci").mkdir(parents=True)
        shutil.copy2(ROOT / "tools/ci/product-version.py", self.root / "tools/ci/product-version.py")
        shutil.copy2(ROOT / "Directory.Build.props", self.root / "Directory.Build.props")
        self.assertIsNotNone(shutil.which("jq"), "The upgrade fixture requires jq.")

        source = self.script_path.read_text()
        function = source.split("exercise_server_offered_client_update() {\n", 1)[1]
        self.selection = function.split("  access_token=", 1)[0]
        self.catalog_and_tenant = function[function.index('  release_response="$(curl'):]
        self.catalog_and_tenant = self.catalog_and_tenant.split('  attempt_id=""', 1)[0]
        client_channel = re.search(
            r"(?m)^Environment=NetRatelCLIENT__Client__AutoUpdate__Channel=.*$", function)
        self.assertIsNotNone(client_channel, "The native Client unit must declare its update channel.")
        self.client_channel_line = client_channel.group(0)

        self.bin = self.root / "bin"
        self.bin.mkdir()
        curl = self.bin / "curl"
        curl.write_text("""#!/usr/bin/env python3
import json, os, pathlib, sys
args = sys.argv[1:]
url = args[-1]
method = args[args.index('--request') + 1] if '--request' in args else 'GET'
with open(os.environ['REQUEST_LOG'], 'a') as log:
    log.write(json.dumps({'method': method, 'url': url}) + '\\n')
if '/client-updates/releases?' in url and method == 'GET':
    print(pathlib.Path(os.environ['RELEASE_CATALOG']).read_text())
elif url.endswith('/api/v1/tenants/42') and method == 'GET':
    print(json.dumps({'name': 'Synthetic upgrade tenant', 'autoUpdate': False}))
elif url.endswith('/api/v1/tenants/42') and method == 'PUT':
    pathlib.Path(os.environ['TENANT_REQUEST']).write_text(args[args.index('--data') + 1])
else:
    raise SystemExit('Unexpected upgrade probe request: ' + method + ' ' + url)
""")
        curl.chmod(0o755)

    def run_update_probe(self, suffix, releases):
        props_path = self.root / "Directory.Build.props"
        props = ET.parse(props_path)
        props.find(".//VersionPrefix").text = "0.1.0"
        props.find(".//VersionSuffix").text = suffix
        props.write(props_path)
        catalog = self.root / "releases.json"
        catalog.write_text(json.dumps(releases))
        tenant_request = self.root / "tenant-request.json"
        unit = self.root / "client-unit.txt"
        request_log = self.root / "requests.jsonl"
        for output in (tenant_request, unit, request_log):
            output.unlink(missing_ok=True)
        probe = (
            'set -euo pipefail\n'
            'api_url=http://synthetic.invalid\nupgrade_tenant_id=42\n'
            'exercise_update_probe() {\n' + self.selection +
            'access_token=synthetic\n' +
            'cat > "$CLIENT_UNIT" <<UNIT\n' + self.client_channel_line + '\nUNIT\n' +
            self.catalog_and_tenant + '\n}\nexercise_update_probe\n'
        )
        result = subprocess.run(
            ["bash", "-c", probe], cwd=self.root, capture_output=True, text=True,
            env={**os.environ, "PATH": str(self.bin) + os.pathsep + os.environ["PATH"],
                 "NETRATEL_RELEASE_SOURCE_ROOT": str(self.root),
                 "RELEASE_CATALOG": str(catalog), "TENANT_REQUEST": str(tenant_request),
                 "CLIENT_UNIT": str(unit), "REQUEST_LOG": str(request_log)},
            timeout=10)
        self.assertTrue(request_log.exists(), result.stderr)
        requests = [json.loads(line) for line in request_log.read_text().splitlines()]
        return result, unit.read_text().strip().split("=", 2)[2], tenant_request, requests

    def assert_candidate_channel(self, suffix, version, channel):
        result, client_channel, tenant_request, requests = self.run_update_probe(
            suffix, [{"version": version, "channel": channel, "enabled": True}])
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(client_channel.lower(), channel)
        tenant = json.loads(tenant_request.read_text())
        self.assertIs(tenant["autoUpdate"], True)
        self.assertEqual(tenant["autoUpdateChannel"], channel)
        self.assertEqual(tenant["autoUpdateTargetVersion"], version)
        self.assertEqual(tenant["name"], "Synthetic upgrade tenant")
        self.assertEqual([request["method"] for request in requests], ["GET", "GET", "PUT"])

    def test_stable_candidate_uses_stable_catalog_tenant_and_client_channel(self):
        self.assert_candidate_channel("", "0.1.0", "stable")

    def test_prerelease_candidate_uses_prerelease_catalog_tenant_and_client_channel(self):
        self.assert_candidate_channel("rc.20", "0.1.0-rc.20", "prerelease")

    def test_opposite_channel_disabled_or_other_version_cannot_enable_tenant_updates(self):
        for suffix, version, channel in (("", "0.1.0", "stable"),
                                         ("rc.20", "0.1.0-rc.20", "prerelease")):
            for mismatch in ("opposite_channel", "disabled", "other_version"):
                release = {"version": version, "channel": channel, "enabled": True}
                if mismatch == "opposite_channel":
                    release["channel"] = "prerelease" if channel == "stable" else "stable"
                elif mismatch == "disabled":
                    release["enabled"] = False
                else:
                    release["version"] = "9.9.9"
                with self.subTest(version=version, mismatch=mismatch):
                    result, _, tenant_request, requests = self.run_update_probe(suffix, [release])
                    self.assertNotEqual(result.returncode, 0)
                    self.assertFalse(tenant_request.exists(), "Rejected releases must not update tenant policy.")
                    self.assertEqual([request["method"] for request in requests], ["GET"])


class ProductVersionTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="netratel-version-tests-")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        (self.root / "tools/ci").mkdir(parents=True)
        (self.root / "release").mkdir()
        (self.root / "src/NetRatel/Fixture").mkdir(parents=True)
        for name in ("verify-product-version.py", "verify-product-version.sh", "product-version.py"):
            shutil.copy2(ROOT / "tools/ci" / name, self.root / "tools/ci" / name)
        shutil.copy2(ROOT / "Directory.Build.props", self.root / "Directory.Build.props")
        self.project = self.root / "src/NetRatel/Fixture/Fixture.csproj"
        self.project.write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>')
        (self.root / "release/release-manifest.json").write_text(json.dumps({"components": []}))

    def run_gate(self, **environment):
        return subprocess.run(["/bin/bash", str(self.root / "tools/ci/verify-product-version.sh")],
                              env={**os.environ, **environment}, capture_output=True, text=True)

    def test_one_props_edit_updates_version_and_bundle_manifest(self):
        props_path = self.root / "Directory.Build.props"
        props = ET.parse(props_path)
        props.find(".//VersionSuffix").text = "rc.999"
        props.write(props_path)
        helper = self.root / "tools/ci/product-version.py"
        version = subprocess.check_output(["python3", str(helper)], text=True).strip()
        self.assertTrue(version.endswith("-rc.999"))
        output = self.root / "rendered-manifest.json"
        subprocess.run(["python3", str(helper), "--manifest-output", str(output)], check=True)
        manifest = json.loads(output.read_text())
        self.assertEqual(manifest["version"], version)
        self.assertTrue(manifest["prerelease"])

        props.find(".//VersionSuffix").text = None
        props.write(props_path)
        stable = subprocess.check_output(["python3", str(helper)], text=True).strip()
        self.assertEqual(stable, version.split("-", 1)[0])
        subprocess.run(["python3", str(helper), "--manifest-output", str(output)], check=True)
        self.assertFalse(json.loads(output.read_text())["prerelease"])

    def test_release_preflight_uses_tagged_props_and_rejects_a_mismatched_tag(self):
        helper = self.root / "tools/ci/product-version.py"
        version = subprocess.check_output([sys.executable, str(helper)], text=True).strip()
        for tag in ("v9.9.9-rc.999", version):
            with self.subTest(tag=tag):
                result = subprocess.run(
                    [sys.executable, str(helper), "--validate-release-ref"],
                    env={**os.environ, "GITHUB_REF_TYPE": "tag", "GITHUB_REF_NAME": tag},
                    capture_output=True, text=True)
                self.assertNotEqual(result.returncode, 0)
                self.assertEqual(result.stdout, "")
                self.assertIn(f"'v{version}'", result.stderr)
                self.assertIn("Merge the version change first", result.stderr)

        result = subprocess.run(
            [sys.executable, str(helper), "--validate-release-ref"],
            env={**os.environ, "GITHUB_REF_TYPE": "tag", "GITHUB_REF_NAME": f"v{version}"},
            capture_output=True, text=True)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(result.stdout.strip(), version)

    def test_release_rehearsal_requires_the_committed_version(self):
        helper = self.root / "tools/ci/product-version.py"
        version = subprocess.check_output([sys.executable, str(helper)], text=True).strip()
        for expected in ("", "9.9.9", version):
            with self.subTest(expected=expected):
                result = subprocess.run(
                    [sys.executable, str(helper), "--validate-release-ref"],
                    env={**os.environ, "GITHUB_REF_TYPE": "branch", "GITHUB_REF_NAME": "main",
                         "NETRATEL_EXPECTED_VERSION": expected},
                    capture_output=True, text=True)
                self.assertEqual(result.returncode, 0 if expected == version else 1, result.stderr)
                if expected == version:
                    self.assertEqual(result.stdout.strip(), version)
                else:
                    self.assertEqual(result.stdout, "")
                    self.assertIn("Directory.Build.props", result.stderr)

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
        prefix = subprocess.check_output(["python3", str(ROOT / "tools/ci/product-version.py")],
                                         text=True).strip().split("-", 1)[0]
        for suffix in ("rc.1", "rc.3", ""):
            with self.subTest(suffix=suffix):
                result = subprocess.run(["dotnet", "msbuild", str(self.project), "-nologo",
                    f"-p:VersionSuffix={suffix}",
                    "-getProperty:Version,PackageVersion,InformationalVersion,AssemblyVersion,FileVersion"],
                    capture_output=True, text=True)
                self.assertEqual(result.returncode, 0, result.stderr)
                properties = json.loads(result.stdout)["Properties"]
                version = prefix + ("-" + suffix if suffix else "")
                for name in ("Version", "PackageVersion", "InformationalVersion"):
                    self.assertEqual(properties[name], version)
                self.assertEqual(properties["AssemblyVersion"], f"{prefix}.0")
                self.assertEqual(properties["FileVersion"], f"{prefix}.0")


class ReleaseBuildStatusTests(unittest.TestCase):
    def setUp(self):
        self.selector = module("release-build-status")
        self.tag = "v0.1.0-rc.9"
        self.revision = "a" * 40

    def run_status(self, **overrides):
        run = {
            "databaseId": 123,
            "headSha": self.revision,
            "headBranch": self.tag,
            "event": "push",
            "status": "completed",
            "conclusion": "success",
            "createdAt": "2026-09-25T20:00:00Z",
            "url": "https://github.com/example/actions/runs/123",
        }
        run.update(overrides)
        return self.selector.classify_runs([run], self.tag, self.revision)

    def test_successful_matching_run_is_publishable(self):
        self.assertEqual(self.run_status(), {
            "state": "success",
            "run_id": "123",
            "url": "https://github.com/example/actions/runs/123",
        })

    def test_active_matching_run_is_waitable(self):
        self.assertEqual(self.run_status(status="in_progress", conclusion=None), {
            "state": "active",
            "run_id": "123",
            "status": "in_progress",
            "url": "https://github.com/example/actions/runs/123",
        })

    def test_failed_matching_run_is_reported(self):
        self.assertEqual(self.run_status(conclusion="failure"), {
            "state": "failed",
            "run_id": "123",
            "conclusion": "failure",
            "url": "https://github.com/example/actions/runs/123",
        })

    def test_wrong_identity_is_ignored(self):
        self.assertEqual(self.run_status(headSha="b" * 40), {"state": "waiting"})
        self.assertEqual(self.run_status(headBranch="Netratel-" + self.tag), {"state": "waiting"})
        self.assertEqual(self.run_status(event="workflow_dispatch"), {"state": "waiting"})

    def test_existing_success_can_be_used_while_a_newer_rerun_is_active(self):
        runs = [
            {
                "databaseId": 456,
                "headSha": self.revision,
                "headBranch": self.tag,
                "event": "push",
                "status": "in_progress",
                "conclusion": None,
                "createdAt": "2026-09-25T20:05:00Z",
                "url": "https://github.com/example/actions/runs/456",
            },
            {
                "databaseId": 123,
                "headSha": self.revision,
                "headBranch": self.tag,
                "event": "push",
                "status": "completed",
                "conclusion": "success",
                "createdAt": "2026-09-25T20:00:00Z",
                "url": "https://github.com/example/actions/runs/123",
            },
        ]
        self.assertEqual(self.selector.classify_runs(runs, self.tag, self.revision)["run_id"], "123")


class ReleasePublishMetadataTests(unittest.TestCase):
    def test_release_publish_accepts_only_an_explicit_boolean_false_draft(self):
        workflow = (ROOT / ".github/workflows/release-publish.yml").read_text()
        filter_match = re.search(
            r'''(?m)^\s*release_draft="\$\(jq -r '([^']+)' <<< "\$release_json"\)"$''', workflow)
        self.assertIsNotNone(filter_match, "The publication workflow must parse release draft metadata with jq.")
        self.assertIn(
            'if [[ "$release_draft" != false || "$release_tag" != "$tag" ]]; then', workflow,
            "Release metadata must remain fail-closed unless draft is false and the tag matches.")

        jq = shutil.which("jq")
        self.assertIsNotNone(jq, "jq is required by the publication workflow and its metadata check.")
        jq_filter = filter_match.group(1)
        cases = (
            ({"draft": False}, "false"),
            ({"draft": True}, "true"),
            ({}, ""),
            ({"draft": None}, ""),
            ({"draft": "false"}, ""),
        )
        for metadata, expected in cases:
            with self.subTest(metadata=metadata):
                result = subprocess.run(
                    [jq, "-r", jq_filter], input=json.dumps(metadata), capture_output=True, text=True)
                self.assertEqual(result.returncode, 0, result.stderr)
                self.assertEqual(result.stdout.removesuffix("\n"), expected)


class ClientMeasurementHelperTests(unittest.TestCase):
    def setUp(self):
        self.helper = module("measure-client-publish")

    def test_windows_uses_git_installation_bash_and_passes_paths_as_literal_posix_arguments(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            git = root / "Git/cmd/git.exe"
            bash = root / "Git/bin/bash.exe"
            for file in (git, bash):
                file.parent.mkdir(parents=True, exist_ok=True)
                file.touch()
            with patch.object(self.helper.shutil, "which", side_effect=lambda name: str(git) if name == "git" else "C:/Windows/System32/bash.exe") as which:
                self.assertEqual(str(bash), self.helper.resolve_bash(platform="nt"))
                which.assert_called_once_with("git")
            with patch.object(self.helper, "resolve_bash", return_value=str(bash)):
                command = self.helper.artifact_verifier_command(Path("D:/a/source tree"), Path("D:/a/artifacts dir"), "1.0.0", "win-x64")
            self.assertEqual(str(bash), command[0])
            self.assertIn("/d/a/source tree/tools/ci/verify-client-release-artifact.sh", command)
            self.assertIn("/d/a/artifacts dir", command)
            self.assertEqual("/c/Program Files/Git/bin/bash.exe", self.helper.bash_path(r"C:\Program Files\Git\bin\bash.exe"))

    def test_verification_reuses_bound_original_archive_without_changing_measurements_or_building(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            source, original, evidence, state = (root / name for name in ("source", "original", "receipt", "state"))
            for directory in (source, original / "artifacts", evidence, state):
                directory.mkdir(parents=True)
            (source / "global.json").write_text('{"sdk":{"version":"10.0.401"}}')
            sha = "a" * 40
            distribution = root / "fixture/netratel-client-win-x64"
            distribution.mkdir(parents=True)
            (distribution / "NetRatel.Client.exe").write_bytes(b"native-probe-test-fixture")
            (distribution / "netratel-client-manifest.json").write_text(json.dumps({"schema": "netratel.client.manifest.v1",
                "commitSha": sha, "runtimeId": "win-x64", "executable": "NetRatel.Client.exe"}))
            archive = original / "artifacts/client.zip"
            with zipfile.ZipFile(archive, "w") as target:
                for file in distribution.iterdir():
                    target.write(file, "netratel-client-win-x64/" + file.name)
            metrics = {"sourceSha": sha, "runtime": "win-x64", "mode": "untrimmed", "dotnetSdkVersion": "10.0.401",
                       "runner": {"ImageVersion": "original-image"}, "archiveName": archive.name, "archiveBytes": archive.stat().st_size,
                       "archiveSha256": self.helper.digest(archive), "productVersion": "1.0.0",
                       "stages": {"publish": {"exitCode": 0, "seconds": 12.345}}, "exitCode": 1}
            sbom = original / "artifacts/netratel-client-1.0.0-win-x64.spdx.json"
            sbom.write_bytes(b'{"fixture":true}\r\n')
            checksum_index = original / "artifacts/SHA256SUMS"
            checksum_index.write_bytes("".join(f"{self.helper.digest(path)}  {path.name}\r\n" for path in (archive, sbom)).encode())
            preserved_index = checksum_index.read_bytes()
            preserved_sbom = sbom.read_bytes()
            original_path = original / "metrics.json"
            original_path.write_text(json.dumps(metrics))
            preserved = original_path.read_bytes()
            (original / "distribution-inventory.json").write_text(json.dumps(self.helper.inventory(distribution)))
            stages = []
            with patch.object(self.helper.subprocess, "check_output", return_value="10.0.401\n"), \
                 patch.object(self.helper, "artifact_verifier_command", return_value=["git-bash", "verifier.sh"]) as command, \
                 patch.object(self.helper, "run_stage", side_effect=lambda receipt, output, stage, *args: stages.append(stage)), \
                 contextlib.redirect_stdout(io.StringIO()):
                result = self.helper.verify_existing(SimpleNamespace(verify_existing=original, runtime="win-x64", mode="untrimmed", verify_content=True),
                                                     source, sha, state, evidence)
            self.assertEqual(0, result)
            self.assertEqual(["verify", "verify-content", "launch-fresh", "launch-cached"], stages)
            command.assert_called_once_with(source, state / "validation-packages", "1.0.0", "win-x64")
            self.assertEqual(preserved, original_path.read_bytes())
            self.assertEqual(preserved_index, checksum_index.read_bytes())
            self.assertEqual(preserved_sbom, sbom.read_bytes())
            validation = state / "validation-packages"
            self.assertEqual(preserved_index.replace(b"\r\n", b"\n"), (validation / "SHA256SUMS").read_bytes())
            self.assertEqual(archive.read_bytes(), (validation / archive.name).read_bytes())
            self.assertEqual(preserved_sbom, (validation / sbom.name).read_bytes())
            receipt = json.loads((evidence / "verification.json").read_text())
            self.assertEqual(metrics["archiveSha256"], receipt["original"]["archiveSha256"])
            self.assertEqual(metrics["runner"], receipt["original"]["runner"])
            self.assertEqual(hashlib.sha256(preserved_index).hexdigest(), receipt["checksumIndex"]["originalSha256"])
            self.assertEqual(hashlib.sha256(preserved_index.replace(b"\r\n", b"\n")).hexdigest(), receipt["checksumIndex"]["validationSha256"])
            self.assertEqual(metrics["stages"]["publish"]["seconds"], json.loads(original_path.read_text())["stages"]["publish"]["seconds"])
            archive.write_bytes(archive.read_bytes() + b"changed archive bytes")
            with patch.object(self.helper.subprocess, "check_output", return_value="10.0.401\n"), \
                 patch.object(self.helper, "run_stage") as verifier, contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
                result = self.helper.verify_existing(SimpleNamespace(verify_existing=original, runtime="win-x64", mode="untrimmed", verify_content=False),
                                                     source, sha, state, evidence)
            self.assertEqual(1, result)
            verifier.assert_not_called()
            self.assertEqual(preserved, original_path.read_bytes())


class ClientRuntimeContentTests(unittest.TestCase):
    def setUp(self):
        self.content = module("verify-client-runtime-content")
        self.sbom = module("generate-runtime-sbom")

    def test_supported_external_helpers_survive_and_embedded_engine_and_server_assets_fail(self):
        document = {"packages": [{"name": "NetRatel.Client"}, {"name": "SIPSorcery"}],
                    "files": [{"fileName": "netratel-client-win-x64/updater/netratel-update.ps1"}]}
        self.content.verify(document)
        for name in ("Microsoft.PowerShell.SDK", "System.Management.Automation", "NetRatel.Infrastructure",
                     "Microsoft.EntityFrameworkCore.Relational", "Npgsql", "NJsonSchema"):
            with self.subTest(package=name), self.assertRaisesRegex(ValueError, "removed dependency"):
                self.content.verify({**document, "packages": [{"name": name}]})
        for path in ("netratel-client-linux-x64/powershell.config.json", "System.Management.Automation.dll",
                     "runtimes/unix/lib/net9.0/Modules/Microsoft.PowerShell.Management/Microsoft.PowerShell.Management.psd1",
                     "NetRatel.Infrastructure.dll"):
            with self.subTest(path=path), self.assertRaisesRegex(ValueError, "removed engine/server content"):
                self.content.verify(document, inputs=[path])

    def test_explicit_final_graph_excludes_restored_and_stale_untrimmed_dependencies(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            assets = root / "project.assets.json"
            assets.write_text(json.dumps({"project": {"restore": {"projectName": "NetRatel.Client"}},
                                          "packageFolders": {}, "targets": {"net10.0/linux-x64": {
                                              "Microsoft.PowerShell.SDK/7.6.6": {"type": "package"}}}}))
            distribution = root / "distribution"
            distribution.mkdir()
            stale_graph = {"runtimeTarget": {"name": "net10.0/linux-x64"},
                           "targets": {"net10.0/linux-x64": {"Microsoft.PowerShell.SDK/7.6.6": {}}},
                           "libraries": {"Microsoft.PowerShell.SDK/7.6.6": {"type": "package"}}}
            (distribution / "NetRatel.Client.deps.json").write_text(json.dumps(stale_graph))
            final_graph = root / "final.deps.json"
            final_graph.write_text(json.dumps({"runtimeTarget": {"name": "net10.0/linux-x64"},
                                               "targets": {"net10.0/linux-x64": {"NetRatel.Client/1.0.0": {}, "SIPSorcery/10.0.16": {}}},
                                               "libraries": {"NetRatel.Client/1.0.0": {"type": "project"},
                                                             "SIPSorcery/10.0.16": {"type": "package"}}}))
            packages = self.sbom.packages_from_assets(assets, "linux-x64", distribution, final_graph)
            self.assertEqual(["NetRatel.Client", "SIPSorcery"], [package["name"] for package in packages])
            with self.assertRaisesRegex(ValueError, "Explicit publish runtime graph does not exist"):
                self.sbom.packages_from_assets(assets, "linux-x64", distribution, root / "missing.deps.json")


class SavedImageCredentialScanTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="netratel-saved-image-")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)

    def layer(self, layout, content=b"ordinary public content\n", empty=False):
        buffer = io.BytesIO()
        with tarfile.open(fileobj=buffer, mode="w") as archive:
            if not empty:
                member = tarfile.TarInfo("app/example.txt")
                member.size = len(content)
                archive.addfile(member, io.BytesIO(content))
        data = buffer.getvalue()
        name = ("blobs/sha256/" + hashlib.sha256(data).hexdigest()
                if layout == "oci" else f"layer-{hashlib.sha256(data).hexdigest()}/layer.tar")
        path = self.root / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(data)
        return name

    def scan(self, references):
        (self.root / "manifest.json").write_text(json.dumps([{"Layers": references}]))
        return subprocess.run([sys.executable, str(ROOT / "tools/ci/scan-saved-image.py"), str(self.root)],
                              capture_output=True, text=True)

    def test_oci_and_legacy_scan_actual_layers_and_deduplicate_empty_references(self):
        for layout in ("oci", "legacy"):
            with self.subTest(layout=layout):
                layer = self.layer(layout)
                empty = self.layer(layout, empty=True)
                result = self.scan([layer, empty, empty, empty])
                self.assertEqual(0, result.returncode, result.stderr)
                coverage = json.loads(result.stdout)
                self.assertEqual(4, coverage["layerReferences"])
                self.assertEqual(2, coverage["uniqueLayers"])
                self.assertEqual(1, coverage["regularFiles"])
                self.assertGreater(coverage["regularBytes"], 0)
                self.assertEqual(1, coverage["textFiles"])

    def test_oci_marker_is_rejected_by_public_scanner(self):
        # Exercise the actual shell entrypoint with an isolated Docker protocol
        # fixture; no Docker daemon/container or registry writes are involved.
        marker = b"-----BEGIN " + b"PRIVATE KEY-----\nsynthetic\n"
        reference = self.layer("oci", marker)
        (self.root / "manifest.json").write_text(json.dumps([{"Layers": [reference]}]))
        saved = self.root / "save.tar"
        with tarfile.open(saved, "w") as archive:
            archive.add(self.root / "manifest.json", arcname="manifest.json")
            archive.add(self.root / reference, arcname=reference)
        bin_directory = self.root / "bin"
        bin_directory.mkdir()
        docker = bin_directory / "docker"
        docker.write_text("#!/usr/bin/env python3\nimport json,os,sys\n"
            "args=sys.argv[1:]\n"
            "if args[:2]==['image','inspect']:\n"
            " print(json.dumps([{'Config':{'Labels':{'org.opencontainers.image.title':'NetRatel',"
            "'org.opencontainers.image.source':'https://github.com/BostonTechnologies/netratel',"
            "'org.opencontainers.image.version':'1.2.3','org.opencontainers.image.revision':'a'*40}}}]))\n"
            "elif args[:2]==['image','save']:\n"
            " sys.stdout.buffer.write(open(os.environ['TEST_SAVED_IMAGE'],'rb').read())\n"
            "elif args[0]!='run': sys.exit(2)\n")
        docker.chmod(0o755)
        result = subprocess.run(["bash", str(ROOT / "tools/ci/scan-public-image.sh"),
            "--image", "synthetic", "--version", "1.2.3", "--revision", "a" * 40],
            env={**os.environ, "PATH": str(bin_directory) + os.pathsep + os.environ["PATH"],
                 "TEST_SAVED_IMAGE": str(saved)}, capture_output=True, text=True)
        self.assertNotEqual(0, result.returncode, result.stdout)
        self.assertIn("potential credential", result.stderr)

    def test_empty_missing_corrupt_and_unreviewed_matches_fail(self):
        cases = [([], "no layers"),
                 ([self.layer("oci", empty=True)], "no nonempty"),
                 (["missing/layer.tar"], "could not read"),
                 ([self.layer("legacy", b"<" + b"key>synthetic</key>\n")], "potential credential")]
        for references, error in cases:
            with self.subTest(error=error):
                result = self.scan(references)
                self.assertNotEqual(0, result.returncode)
                self.assertIn(error, result.stderr)
        reference = self.layer("oci")
        (self.root / reference).write_bytes(b"corrupt")
        result = self.scan([reference])
        self.assertNotEqual(0, result.returncode)
        self.assertIn("digest", result.stderr)
        # Digest verification follows normalized references; aliases cannot
        # convert content-addressed blobs into unchecked legacy layers.
        for alias in ("./" + reference, "blobs/../" + reference):
            result = self.scan([alias])
            self.assertNotEqual(0, result.returncode)
            self.assertIn("digest", result.stderr)
        linked = self.root / "layer-alias"
        linked.symlink_to(self.root / reference)
        result = self.scan([linked.name])
        self.assertNotEqual(0, result.returncode)
        self.assertIn("symbolic links", result.stderr)
        sensitive_name = "missing/gh" + "p_" + "synthetic" * 5
        result = self.scan([sensitive_name])
        self.assertNotEqual(0, result.returncode)
        self.assertNotIn(sensitive_name, result.stderr)
        self.assertNotIn("synthetic", result.stderr)

    def test_pure_oci_index_resolves_manifest_layers(self):
        reference = self.layer("oci")
        document = json.dumps({"layers": [{"digest": "sha256:" + Path(reference).name}]}).encode()
        digest = hashlib.sha256(document).hexdigest()
        (self.root / "blobs/sha256" / digest).write_bytes(document)
        (self.root / "index.json").write_text(json.dumps({"manifests": [{"digest": "sha256:" + digest}]}))
        result = subprocess.run([sys.executable, str(ROOT / "tools/ci/scan-saved-image.py"), str(self.root)],
                                capture_output=True, text=True)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(1, json.loads(result.stdout)["uniqueLayers"])

    def test_exact_reviewed_benign_bytes_retain_raw_match_and_changed_bytes_fail(self):
        scanner = module("scan-saved-image")
        content = b"public documentation: <" + b"key>example</key>\n"
        reference = self.layer("oci", content)
        (self.root / "manifest.json").write_text(json.dumps([{"Layers": [reference]}]))
        with patch.dict(scanner.REVIEWED, {"app/example.txt": hashlib.sha256(content).hexdigest()}):
            with contextlib.redirect_stdout(io.StringIO()):
                coverage = scanner.scan(self.root)
            self.assertEqual(1, coverage["rawMatchingFiles"])
            self.assertEqual(0, coverage["reviewedBenignMatches"][0]["rawGrepExit"])
            reference = self.layer("oci", content + b"changed upstream bytes\n")
            (self.root / "manifest.json").write_text(json.dumps([{"Layers": [reference]}]))
            with contextlib.redirect_stdout(io.StringIO()), self.assertRaisesRegex(ValueError, "potential credential"):
                scanner.scan(self.root)


class DistributionTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="netratel-distribution-tests-")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.promotion = module("promote-release")
        self.verifier = module("verify-runtime-sbom")

    def receipt(self, revision="b" * 40):
        version = "0.1.0-rc.3"
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

    def populate_flat_output(self, directory, version="0.1.0-rc.3"):
        directory.mkdir(parents=True, exist_ok=True)
        for name in self.promotion.required_artifacts(version):
            (directory / name).write_bytes(name.encode())
        self.promotion.checksums(directory)

    def create_release_bundle(self, version):
        archive = self.root / f"netratel-compose-{version}.tar.gz"
        manifest = json.dumps({"version": version}).encode()
        with tarfile.open(archive, "w:gz") as target:
            for name in ("compose.images.yaml", "compose.external-postgres.yaml", "compose.public-https.yaml", "nginx.public-https.conf", "compose.mcp-http.yaml", "compose.mcp-http.ports.yaml", "compose.mcp-http.config.yaml", "compose.mcp-http.secrets.yaml", "compose.setup.yaml", "setup-http-mcp.sh", ".env.mcp-http.example", ".env.mcp-http.outbound.example", ".env.mcp-http.api.example", ".env.images.example", "INSTALL.md"):
                target.add(ROOT / "release" / name, arcname=name)
            manifest_info = tarfile.TarInfo("release-manifest.json")
            manifest_info.size = len(manifest)
            target.addfile(manifest_info, io.BytesIO(manifest))
            target.add(ROOT / "docs/mcp-http/local-credential-mode.md", arcname="docs/mcp-http/local-credential-mode.md")
            for name in ("LICENSE", "NOTICE"):
                target.add(ROOT / name, arcname=name)
        return archive

    def test_nested_container_tools_have_one_complete_sbom_file_inventory(self):
        distribution = self.root / "out"
        assets = []
        distributions = []
        for name, path, dependency in (("NetRatel.Mcp.Http", distribution, "Example.Http"),
                ("NetRatel.Cli", distribution / "tooling" / "cli", "Example.Terminal"),
                ("NetRatel.Mcp", distribution / "tooling" / "stdio", "Example.Protocol")):
            path.mkdir(parents=True, exist_ok=True)
            asset = self.root / (name + ".assets.json")
            asset.write_text(json.dumps({"project": {"restore": {"projectName": name}}, "packageFolders": {}, "targets": {"net10.0": {}}}))
            (path / (name + ".deps.json")).write_text(json.dumps({"runtimeTarget": {"name": "net10.0"},
                "targets": {"net10.0": {name + "/1.0.0": {}, dependency + "/1.0.0": {}}},
                "libraries": {name + "/1.0.0": {"type": "project"}, dependency + "/1.0.0": {"type": "package"}}}))
            (path / (name + ".dll")).write_text("distribution fixture: " + name)
            assets += ["--project-assets", str(asset)]
            distributions += ["--distribution", str(path)]
        output = self.root / "runtime.spdx.json"
        result = subprocess.run([sys.executable, str(ROOT / "tools/ci/generate-runtime-sbom.py"),
            *assets, *distributions, "--distribution-root", str(distribution), "--name", "NetRatel.Mcp.Http", "--version", "1.0.0", "--output", str(output)],
            capture_output=True, text=True)
        self.assertEqual(0, result.returncode, result.stderr)
        document = json.loads(output.read_text())
        file_names = [item["fileName"] for item in document["files"]]
        self.assertEqual(len(file_names), len(set(file_names)))
        self.assertIn("./out/tooling/cli/NetRatel.Cli.dll", file_names)
        self.assertIn("./out/tooling/stdio/NetRatel.Mcp.dll", file_names)
        self.assertTrue({"Example.Http", "Example.Terminal", "Example.Protocol"}.issubset({item["name"] for item in document["packages"]}))
        self.verifier.verify(document, self.root)

    def test_paginated_gh_json_is_parsed_without_slurp(self):
        self.assertEqual(
            self.promotion.json_pages('[{"name":"first"}]\n[{"name":"second"}]'),
            [[{"name": "first"}], [{"name": "second"}]],
        )
        with self.assertRaisesRegex(ValueError, "JSON arrays"):
            self.promotion.json_pages('{"name":"not-a-page"}')

    def test_flat_staging_rejects_missing_files_wrong_checksums_and_nested_names(self):
        inputs = self.root / "inputs"
        inputs.mkdir()
        sums = inputs / "SHA256SUMS"
        sums.write_text("")
        with self.assertRaisesRegex(ValueError, "Missing required"):
            self.promotion.stage(inputs, self.root / "output", "0.1.0-rc.3")
        sums.write_text("0" * 64 + "  missing.tar.gz\n")
        with self.assertRaisesRegex(ValueError, "Missing/corrupt"):
            self.promotion.stage(inputs, self.root / "output", "0.1.0-rc.3")
        sums.write_text("0" * 64 + "  cli/nested.nupkg\n")
        with self.assertRaisesRegex(ValueError, "flat safe"):
            self.promotion.stage(inputs, self.root / "output", "0.1.0-rc.3")

    def test_flat_staging_verifies_all_required_downloads_without_rearrangement(self):
        inputs = self.root / "inputs"
        inputs.mkdir()
        lines = []
        for name in self.promotion.required_artifacts("0.1.0-rc.3"):
            (inputs / name).write_bytes(name.encode())
            lines.append(f"{hashlib.sha256(name.encode()).hexdigest()}  {name}\n")
        (inputs / "SHA256SUMS").write_text("".join(lines))
        output = self.root / "flat"
        self.promotion.stage(inputs, output, "0.1.0-rc.3")
        self.promotion.verify_staged(output, "0.1.0-rc.3")
        self.assertEqual(subprocess.run(["sha256sum", "-c", "SHA256SUMS"], cwd=output, capture_output=True).returncode, 0)
        with self.assertRaisesRegex(ValueError, "new directory"):
            self.promotion.stage(inputs, output, "0.1.0-rc.3")
        (output / self.promotion.required_artifacts("0.1.0-rc.3")[0]).write_bytes(b"corrupted after staging")
        with self.assertRaisesRegex(ValueError, "changed or missing"):
            self.promotion.verify_staged(output, "0.1.0-rc.3")

    def test_staging_rejects_unexpected_output_files(self):
        output = self.root / "output"
        self.populate_flat_output(output)
        (output / "unreviewed-upload.txt").write_text("must not be published")
        with self.assertRaisesRegex(ValueError, "unexpected files"):
            self.promotion.verify_staged(output, "0.1.0-rc.3")

    def test_partial_resume_preserves_completed_digest_and_rejects_wrong_source_or_package_contract(self):
        path = self.root / "journal.json"
        receipt = self.receipt()
        state = self.promotion.resume_state(path, "0.1.0-rc.3", "b" * 40, receipt)
        reference = self.promotion.IMAGE_REPOSITORIES["api"] + "@sha256:" + "a" * 64
        state["images"]["api"] = reference
        path.write_text(json.dumps(state))
        self.assertEqual(self.promotion.resume_state(path, "0.1.0-rc.3", "b" * 40, receipt)["images"], {"api": reference})
        with self.assertRaisesRegex(ValueError, "different approved"):
            self.promotion.resume_state(path, "0.1.0-rc.3", "c" * 40, self.receipt("c" * 40))
        changed_receipt = self.receipt()
        changed_receipt["files"][next(iter(changed_receipt["files"]))]["sha256"] = "c" * 64
        with self.assertRaisesRegex(ValueError, "different approved"):
            self.promotion.resume_state(path, "0.1.0-rc.3", "b" * 40, changed_receipt)
        state["images"]["api"] = reference[:-64] + "REPLACE_AFTER_APPROVED_PUBLIC_RELEASE"
        path.write_text(json.dumps(state))
        with self.assertRaisesRegex(ValueError, "invalid digest"):
            self.promotion.resume_state(path, "0.1.0-rc.3", "b" * 40, receipt)

        state["images"] = {"api": reference}
        state["packageNames"]["api"] = "netratel-rc4-api"
        path.write_text(json.dumps(state))
        with self.assertRaisesRegex(ValueError, "package contract"):
            self.promotion.resume_state(path, "0.1.0-rc.3", "b" * 40, receipt)

    def test_registry_contract_allows_only_the_five_public_linked_packages(self):
        self.assertEqual(
            self.promotion.PACKAGE_NAMES,
            {
                "api": "netratel-api",
                "web": "netratel-web",
                "migrations": "netratel-migrations",
                "mcp-http": "netratel-mcp-http",
                "client": "netratel-client",
            },
        )
        self.assertEqual(
            self.promotion.release_image_tag("api", "0.1.0-rc.5"),
            "ghcr.io/bostontechnologies/netratel-api:0.1.0-rc.5",
        )
        inventory = {
            package: {"name": package, "visibility": "public",
                      "repository": {"full_name": self.promotion.REPOSITORY}}
            for package in self.promotion.PACKAGE_NAMES.values()
        }
        self.promotion.validate_registry_packages(inventory)
        for package in self.promotion.PACKAGE_NAMES.values():
            with self.subTest(package=package):
                missing = dict(inventory)
                missing.pop(package)
                with self.assertRaisesRegex(ValueError, "missing"):
                    self.promotion.validate_registry_packages(missing)
        non_public = dict(inventory)
        non_public[self.promotion.PACKAGE_NAMES["api"]] = {"visibility": "internal", "repository": {"full_name": self.promotion.REPOSITORY}}
        with self.assertRaisesRegex(ValueError, "not public"):
            self.promotion.validate_registry_packages(non_public)
        unlinked = dict(inventory)
        unlinked[self.promotion.PACKAGE_NAMES["api"]] = {"visibility": "public", "repository": None}
        with self.assertRaisesRegex(ValueError, "not linked"):
            self.promotion.validate_registry_packages(unlinked)

    def test_public_registry_guard_rejects_existing_release_tag(self):
        with patch.object(self.promotion, "public_registry_token", return_value=("bostontechnologies/netratel-api", "token")):
            with patch.object(self.promotion.urllib.request, "urlopen"):
                with self.assertRaisesRegex(ValueError, "already exists"):
                    self.promotion.require_unused_release_tag("api", "0.1.0-rc.7")
            missing = HTTPError("https://ghcr.io", 404, "Not Found", {}, None)
            with patch.object(self.promotion.urllib.request, "urlopen", side_effect=missing):
                self.promotion.require_unused_release_tag("api", "0.1.0-rc.7")
            missing.close()

    def test_partial_or_placeholder_image_sets_cannot_finalize_a_bundle(self):
        with self.assertRaisesRegex(ValueError, "All five"):
            self.promotion.validate_digests({"api": "missing"})
        with self.assertRaisesRegex(ValueError, "Invalid/unresolved"):
            self.promotion.validate_digests({name: "ghcr.io/example/image@sha256:REPLACE_AFTER_APPROVED_PUBLIC_RELEASE"
                                            for name in self.promotion.COMPONENTS})

    def test_promotion_uses_outputs_and_embeds_instructions_without_source_checkout(self):
        version = "0.1.0-rc.3"
        for name in self.promotion.required_artifacts(version):
            (self.root / name).write_bytes(name.encode())
        archive = self.create_release_bundle(version)
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
        candidate = json.loads((self.root / "publication.candidate.json").read_text())
        self.assertEqual(candidate["publicCommit"], "b" * 40)
        expected_verification = {
            "state": "candidate",
            "scope": "build-distribution-integrity",
            "requiredSmokes": [],
            "functionalAcceptance": {"state": "pending-owner-testing", "executedSmokes": []},
        }
        self.assertEqual(expected_verification, candidate["verification"])
        self.promotion.complete_bundle(self.root, version, "b" * 40, images, receipt)
        expected_verification["state"] = "complete"
        self.assertEqual(expected_verification, json.loads((self.root / "publication.json").read_text())["verification"])

    def test_receipt_identity_rejects_wrong_source_missing_files_and_invalid_digest(self):
        receipt = self.receipt()
        self.promotion.validate_input_receipt_identity(receipt, "0.1.0-rc.3", "b" * 40)
        receipt["headSha"] = "c" * 40
        with self.assertRaisesRegex(ValueError, "approved repository"):
            self.promotion.validate_input_receipt_identity(receipt, "0.1.0-rc.3", "b" * 40)
        receipt = self.receipt()
        receipt["files"].pop(next(iter(receipt["files"])))
        with self.assertRaisesRegex(ValueError, "every required"):
            self.promotion.validate_input_receipt_identity(receipt, "0.1.0-rc.3", "b" * 40)
        receipt = self.receipt()
        receipt["files"][next(iter(receipt["files"]))]["sha256"] = "not-a-digest"
        with self.assertRaisesRegex(ValueError, "invalid identity"):
            self.promotion.validate_input_receipt_identity(receipt, "0.1.0-rc.3", "b" * 40)

    def test_authenticated_receipt_accepts_explicit_root_path_with_duplicate_nested_package(self):
        version = "0.1.0-rc.3"
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
        version = "0.1.0-rc.3"
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
        version = "0.1.0-rc.3"
        archive = self.create_release_bundle(version)
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


class WindowsMcpArchiveTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix="netratel-windows-mcp-contract-")
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.verifier = module("verify-windows-mcp-archive")
        self.version = "9.8.7-regression.4"
        self.archive = self.root / f"netratel-mcp-stdio-{self.version}-win-x64.zip"
        self.sbom = self.root / f"netratel-mcp-stdio-{self.version}-win-x64.spdx.json"
        self.files = {name: ("synthetic-" + name).encode() for name in self.verifier.REQUIRED}
        for name, source in {
                "http-mcp-windows.ps1": "tools/client/http-mcp-windows.ps1",
                "HttpMcpProcessRelay.cs": "tools/client/HttpMcpProcessRelay.cs",
                "LICENSE": "LICENSE", "NOTICE": "NOTICE"}.items():
            self.files[name] = (ROOT / source).read_bytes()
        self.files["NetRatel.Mcp.runtimeconfig.json"] = json.dumps({"runtimeOptions": {
            "tfm": "net10.0", "framework": {"name": "Microsoft.NETCore.App", "version": "10.0.0"}}}).encode()
        self.files["NetRatel.Mcp.deps.json"] = json.dumps({"runtimeTarget": {"name": ".NETCoreApp,Version=v10.0/win-x64"}}).encode()

    def write_bundle(self):
        files = {f"{self.verifier.DISTRIBUTION}/{name}": value for name, value in self.files.items()}
        with zipfile.ZipFile(self.archive, "w") as output:
            for name, value in files.items(): output.writestr(name, value)
        document = {
            "name": "NetRatel-MCP-win-x64", "relationships": [],
            "packages": [
                {"SPDXID": "SPDXRef-distribution", "name": "NetRatel-MCP-win-x64", "versionInfo": self.version},
                {"SPDXID": "SPDXRef-mcp", "name": "NetRatel.Mcp"},
                {"SPDXID": "SPDXRef-runtime", "name": "Microsoft.NETCore.App", "externalRefs": [{"referenceType": "purl"}]},
            ],
            "files": [{"SPDXID": f"SPDXRef-file-{index}", "fileName": name,
                       "checksums": [{"algorithm": "SHA256", "checksumValue": hashlib.sha256(value).hexdigest()}]}
                      for index, (name, value) in enumerate(files.items())],
        }
        self.sbom.write_text(json.dumps(document))
        self.checksums()

    def checksums(self):
        (self.root / "SHA256SUMS").write_text("".join(
            f"{hashlib.sha256(path.read_bytes()).hexdigest()}  {path.name}\n" for path in (self.archive, self.sbom)))

    def verify(self):
        with contextlib.redirect_stdout(io.StringIO()):
            return self.verifier.verify(self.root, self.version)

    def test_complete_archive_binds_launcher_runtime_legal_and_all_spdx_file_bytes(self):
        self.write_bundle()
        receipt = self.verify()
        self.assertEqual(receipt["filesVerified"], len(self.files))
        self.assertEqual(receipt["archiveSha256"], hashlib.sha256(self.archive.read_bytes()).hexdigest())
        self.assertEqual(receipt["runtime"], "win-x64")
        self.assertEqual(receipt["version"], self.version)

    def test_changed_outer_bytes_and_missing_launcher_fail(self):
        self.write_bundle()
        self.archive.write_bytes(self.archive.read_bytes() + b"changed")
        with self.assertRaisesRegex(ValueError, "checksum mismatch"): self.verify()
        del self.files["http-mcp-windows.ps1"]
        self.write_bundle()
        with self.assertRaisesRegex(ValueError, "missing runtime, documentation or launcher"): self.verify()

    def test_self_consistent_modified_launcher_is_not_accepted_as_source(self):
        self.files["HttpMcpProcessRelay.cs"] += b"\n// changed artifact\n"
        self.write_bundle()
        with self.assertRaisesRegex(ValueError, "differs from the validated source"): self.verify()

    def test_wrong_runtime_and_self_contained_framework_are_rejected(self):
        for options in (
                {"tfm": "net9.0", "framework": {"name": "Microsoft.NETCore.App", "version": "9.0.0"}},
                {"tfm": "net10.0", "includedFrameworks": [{"name": "Microsoft.NETCore.App", "version": "10.0.0"}]}):
            with self.subTest(runtime=options):
                self.files["NetRatel.Mcp.runtimeconfig.json"] = json.dumps({"runtimeOptions": options}).encode()
                self.write_bundle()
                with self.assertRaisesRegex(ValueError, "framework-dependent"): self.verify()

    def test_inventory_omissions_and_wrong_file_digests_fail_after_outer_reindex(self):
        for change in ("missing", "digest"):
            with self.subTest(change=change):
                self.write_bundle()
                document = json.loads(self.sbom.read_text())
                if change == "missing": document["files"].pop()
                else: document["files"][0]["checksums"][0]["checksumValue"] = "b" * 64
                self.sbom.write_text(json.dumps(document)); self.checksums()
                with self.assertRaisesRegex(ValueError, "inventory every|Incorrect SBOM digest"): self.verify()

    def test_windows_path_aliases_and_credential_exports_are_rejected(self):
        for name in ("../escape", "nested\\escape", "NOTICE:stream", "notice", "credential.clixml"):
            with self.subTest(name=name):
                self.files[name] = b"synthetic-fixture"
                self.write_bundle()
                with self.assertRaisesRegex(ValueError, "unsafe path|duplicate or special|must not be packaged"): self.verify()
                del self.files[name]

    def test_potential_key_material_is_rejected(self):
        self.files["extra.txt"] = b"-----BEGIN " + b"PRIVATE KEY-----\nsynthetic-marker-only\n"
        self.write_bundle()
        with self.assertRaisesRegex(ValueError, "potential key material"): self.verify()


class PublicImageSourceIdentityTests(unittest.TestCase):
    def invoke(self, revision):
        with tempfile.TemporaryDirectory(prefix="netratel-image-identity-") as temporary:
            root = Path(temporary); command_log = root / "docker.jsonl"
            docker = root / "docker"
            docker.write_text("#!/usr/bin/env python3\nimport json, os, sys\nfrom pathlib import Path\n"
                              "with Path(os.environ['NETRATEL_IMAGE_BUILD_CALLS']).open('a') as stream:\n"
                              "    stream.write(json.dumps(sys.argv[1:]) + '\\n')\n")
            docker.chmod(0o755)
            result = subprocess.run([
                "bash", str(ROOT / "tools/ci/build-public-image.sh"), "--dockerfile", str(ROOT / "docker/mcp-http/Dockerfile"),
                "--image", "synthetic.invalid/mcp:regression", "--version", "9.8.7-regression", "--revision", revision],
                cwd=ROOT, env=dict(os.environ, PATH=str(root) + os.pathsep + os.environ.get("PATH", ""),
                                  NETRATEL_IMAGE_BUILD_CALLS=str(command_log)), capture_output=True, text=True, timeout=10)
            calls = [json.loads(line) for line in command_log.read_text().splitlines()] if command_log.exists() else []
            return result, calls

    def test_image_build_and_oci_label_use_the_same_verified_revision(self):
        revision = "ab" * 20
        result, calls = self.invoke(revision)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(len(calls), 1)
        self.assertEqual(calls[0][0], "build")
        self.assertEqual(calls[0][calls[0].index("--build-arg") + 1], f"PublicSourceRevision={revision}")
        self.assertIn(f"org.opencontainers.image.revision={revision}", calls[0])
        for relative in ("api/Dockerfile", "web/Dockerfile", "mcp-http/Dockerfile", "migrations/Dockerfile", "client/Dockerfile.public"):
            with self.subTest(dockerfile=relative):
                self.assertRegex((ROOT / "docker" / relative).read_text(), r"(?m)^ARG PublicSourceRevision$")

    def test_invalid_revision_invokes_no_docker_command(self):
        for revision in ("", "short", "g" * 40, "A" * 40, "a" * 40 + "\n"):
            with self.subTest(revision=revision):
                result, calls = self.invoke(revision)
                self.assertNotEqual(result.returncode, 0)
                self.assertEqual(calls, [])


class WindowsMcpReleaseContractTests(unittest.TestCase):
    def test_release_requires_original_windows_archive_smoke_and_attestation(self):
        workflow = (ROOT / ".github/workflows/release-build.yml").read_text()
        jobs = dict(re.findall(r"(?ms)^  ([a-z-]+):\n(.*?)(?=^  [a-z-]+:|\Z)", workflow.split("\njobs:\n", 1)[1]))
        windows = jobs["mcp-windows"]
        self.assertIn("env:\n  PublicSourceRevision: ${{ github.sha }}", workflow)
        self.assertIn('verify-release-artifacts.sh --artifacts artifacts --version "$VERSION" --source-revision "$GITHUB_SHA"', workflow)
        for required in ("needs: [validate, contracts]", "runs-on: windows-latest", "bash tools/ci/package-windows-mcp.sh",
                         "test-windows-mcp-archive.ps1", "-SourceRevision $env:GITHUB_SHA", "uses: actions/attest@",
                         "artifacts/SHA256SUMS", "-win-x64.zip", "-win-x64.spdx.json"):
            self.assertIn(required, windows)
        self.assertNotIn("continue-on-error", windows)
        self.assertIn("needs: [validate, contracts, package, mcp-windows, client-packages]", jobs["release-rehearsal"])
        self.assertIn('[[ "$MCP_WINDOWS_RESULT" == success ]]', jobs["release-rehearsal"])
        packaging = (ROOT / "tools/ci/package-windows-mcp.sh").read_text()
        for required in ("--runtime win-x64 --self-contained false", "LICENSE NOTICE", "http-mcp-windows.ps1",
                         "HttpMcpProcessRelay.cs", "CLI_AND_MCP.md", "http-mcp-bridge.md", "generate-runtime-sbom.py",
                         "verify-windows-mcp-archive.py", '"$(git rev-parse HEAD)" == "$PublicSourceRevision"'):
            self.assertIn(required, packaging)

    def test_windows_branch_smoke_is_optional_and_accepts_existing_campaign_choice(self):
        optional = (ROOT / ".github/workflows/integration-validation.yml").read_text()
        windows = optional.split("\n  windows-mcp:\n", 1)[1].split("\n  client-measurement:", 1)[0]
        self.assertIn("if: inputs.suite == 'windows-mcp' || inputs.suite == 'mcp-campaign'", windows)
        self.assertIn("ref: ${{ inputs.source_ref }}", windows)
        self.assertIn("runs-on: windows-latest", windows)
        self.assertIn("-SourceRevision $env:PublicSourceRevision", windows)
        self.assertNotIn("continue-on-error", windows)
        self.assertNotIn("test-windows-mcp-archive", (ROOT / ".github/workflows/public-pr-validation.yml").read_text())
        smoke = (ROOT / "tools/ci/test-windows-mcp-archive.ps1").read_text()
        self.assertIn("$data.hostRelease.sourceRevision -ceq $SourceRevision", smoke)
        self.assertIn("$data.nativeLoadedMetadata -eq 'not_observed'", smoke)
        self.assertIn("SetAccessRuleProtection($true, $false)", smoke)
        self.assertIn("Export-Clixml", smoke)
        self.assertIn("HttpMcpWindowsRelaySmoke", smoke)


class PublicationImageJournalTests(unittest.TestCase):
    """Real local validation with synthetic authority and no external transports."""

    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="netratel-publication-journal-tests-")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.promotion = module("promote-release")
        self.version = "9.8.7-regression.4"
        self.revision = "a" * 40
        self.tooling_revision = "b" * 40
        self.workflow_revision = "c" * 40
        self.tag_revision = self.revision
        self.dirty = ""
        self.inputs = self.root / "inputs"
        self.inputs.mkdir()
        self.receipt_path = self.root / "input-receipt.json"
        self.commands = []
        self.downloads = []
        self.archives = {}
        self.metadata = []
        self.receipt = {
            "repository": self.promotion.REPOSITORY, "workflow": ".github/workflows/release-build.yml",
            "runId": 1010, "attempt": 2, "headSha": self.revision,
            "productVersion": self.version, "files": {},
        }
        groups = {101: [], 102: [], 103: [], 104: [], 105: []}
        for name in self.promotion.required_artifacts(self.version):
            artifact_id = 105 if name.startswith(f"netratel-mcp-stdio-{self.version}-win-x64.") else 101
            if name.startswith(f"netratel-client-{self.version}-"):
                artifact_id = next(value for runtime, value in
                                   (("linux-x64", 102), ("win-x64", 103), ("osx-arm64", 104))
                                   if runtime in name)
            content = ("synthetic downloadable artifact: " + name).encode()
            (self.inputs / name).write_bytes(content)
            groups[artifact_id].append((name, content))
        self.promotion.checksums(self.inputs)
        for artifact_id, payloads in groups.items():
            archive = self.root / f"{artifact_id}.zip"
            with zipfile.ZipFile(archive, "w") as output:
                for name, content in payloads:
                    output.writestr(name, content)
            self.archives[artifact_id] = archive
            digest = "sha256:" + hashlib.sha256(archive.read_bytes()).hexdigest()
            artifact_name = f"synthetic-original-{artifact_id}"
            self.metadata.append({"id": artifact_id, "name": artifact_name,
                                  "digest": digest, "expired": False})
            for name, content in payloads:
                self.receipt["files"][name] = {
                    "artifact": artifact_name, "artifactId": artifact_id, "artifactDigest": digest,
                    "path": name, "sha256": hashlib.sha256(content).hexdigest(),
                }
        self.save_receipt()
        self.current_run = {"id": 2020, "run_attempt": 3, "path": ".github/workflows/release-publish.yml",
                            "head_sha": self.workflow_revision, "status": "in_progress", "event": "release"}
        self.original_run = {"conclusion": "success", "head_sha": self.revision,
                             "path": ".github/workflows/release-build.yml"}
        self.environment = {
            "GITHUB_ACTIONS": "true", "GITHUB_REPOSITORY": self.promotion.REPOSITORY,
            "RELEASE_TAG": f"v{self.version}", "GITHUB_RUN_ID": "2020", "GITHUB_RUN_ATTEMPT": "3",
            "GITHUB_SHA": self.workflow_revision,
            "GITHUB_WORKFLOW_REF": self.promotion.REPOSITORY + "/.github/workflows/release-publish.yml@refs/heads/main",
            "GITHUB_EVENT_NAME": "release",
        }
        self.real_run = self.promotion.run
        for guard in (
                patch.dict(os.environ, self.environment, clear=True),
                patch.object(self.promotion, "ROOT", self.root),
                patch.object(self.promotion, "run", side_effect=self.command),
                patch.object(self.promotion, "download_artifact", side_effect=self.download),
                patch.object(self.promotion.subprocess, "check_output", side_effect=self.tooling_command),
                patch.object(self.promotion.subprocess, "run", side_effect=AssertionError("Unexpected external subprocess")),
                patch.object(self.promotion.urllib.request, "urlopen", side_effect=AssertionError("Unexpected network request"))):
            guard.start()
            self.addCleanup(guard.stop)

    def save_receipt(self):
        self.receipt_path.write_text(json.dumps(self.receipt))

    def command(self, *command, env=None):
        self.commands.append(command)
        self.assertIsNone(env, "Unexpected external command environment")
        if command == ("python3", str(Path(self.promotion.__file__).with_name("product-version.py"))):
            return self.version
        if command == ("git", "rev-parse", "HEAD"):
            return self.revision
        if command == ("git", "rev-list", "-n", "1", f"v{self.version}"):
            return self.tag_revision
        if command == ("git", "status", "--porcelain"):
            return self.dirty
        if command[:2] == ("gh", "api"):
            self.assertEqual(len(command), 3, "Unexpected metadata command arguments")
            endpoint = command[2]
            prefix = f"repos/{self.promotion.REPOSITORY}/actions/runs/"
            if endpoint == prefix + "2020/attempts/3":
                return json.dumps(self.current_run)
            if endpoint == prefix + "1010/attempts/2":
                return json.dumps(self.original_run)
            if endpoint == prefix + "1010":
                return json.dumps(self.original_run)
            if endpoint == prefix + "1010/artifacts?per_page=100":
                return json.dumps({"artifacts": self.metadata})
            # The fake authority refuses any unowned original run or attempt.
            if endpoint in (prefix + "1011/attempts/2", prefix + "1010/attempts/1"):
                return json.dumps({"conclusion": "failure"})
        raise AssertionError(f"Unexpected external command: {command[:2]}")

    def tooling_command(self, command, **kwargs):
        self.assertEqual(tuple(command), ("git", "-C", str(Path(self.promotion.__file__).resolve().parents[2]), "rev-parse", "HEAD"))
        self.assertEqual(kwargs, {"text": True})
        return self.tooling_revision

    def download(self, artifact_id, destination):
        self.assertIn(artifact_id, self.archives, "Unexpected unauthenticated artifact download")
        self.downloads.append(artifact_id)
        shutil.copyfile(self.archives[artifact_id], destination)

    def args(self, phase="prepared", component="api"):
        return SimpleNamespace(inputs=self.inputs, receipt=self.receipt_path,
                               output=self.root / "journal" / f"{phase}.json", component=component,
                               phase=phase, local_image_config="sha256:" + "d" * 64,
                               image_digest=None if phase == "prepared" else
                               self.promotion.IMAGE_REPOSITORIES[component] + "@sha256:" + "e" * 64)

    def write(self, args=None):
        args = args or self.args()
        self.promotion.write_publication_image_journal(args)
        return json.loads(args.output.read_text())

    def refuse(self, args=None):
        args = args or self.args()
        with self.assertRaises((ValueError, OSError)):
            self.promotion.write_publication_image_journal(args)
        self.assertFalse(args.output.exists())
        self.assertFalse(any(command[:2] in (("docker", "push"), ("gh", "release"))
                             for command in self.commands))

    def bind_changed_archive(self, artifact_id=101):
        digest = "sha256:" + hashlib.sha256(self.archives[artifact_id].read_bytes()).hexdigest()
        for item in self.receipt["files"].values():
            if item["artifactId"] == artifact_id:
                item["artifactDigest"] = digest
        next(item for item in self.metadata if item["id"] == artifact_id)["digest"] = digest
        self.save_receipt()

    def test_prepared_and_recorded_phases_bind_real_local_payloads_without_resume_authority(self):
        for component in self.promotion.COMPONENTS:
            with self.subTest(component=component):
                args = self.args(component=component)
                args.output = self.root / component / "prepared.json"
                prepared = self.write(args)
                args.phase = "digest-recorded"
                args.output = args.output.with_name("digest-recorded.json")
                args.image_digest = self.promotion.IMAGE_REPOSITORIES[component] + "@sha256:" + "e" * 64
                recorded = self.write(args)
                self.assertEqual((prepared["revision"], prepared["version"], prepared["tag"], prepared["component"]),
                                 (self.revision, self.version, f"v{self.version}", component))
                self.assertEqual(prepared["imageRepository"], self.promotion.IMAGE_REPOSITORIES[component])
                self.assertEqual(prepared["imageVersionTag"], self.promotion.release_image_tag(component, self.version))
                self.assertEqual(prepared["scannedLocalImageConfig"], args.local_image_config)
                self.assertEqual((prepared["publication"]["runId"], prepared["publication"]["attempt"],
                                  prepared["publication"]["workflowSourceSha"], prepared["publication"]["toolingSourceSha"]),
                                 (2020, 3, self.workflow_revision, self.tooling_revision))
                self.assertFalse(prepared["resumeAuthority"])
                self.assertFalse(recorded["resumeAuthority"])
                self.assertIsNone(prepared["imageDigest"])
                self.assertEqual(recorded["imageDigest"], args.image_digest)
                self.assertEqual(prepared["inputReceipt"]["files"], self.receipt["files"])
                for name, item in recorded["inputReceipt"]["files"].items():
                    self.assertEqual(item["sha256"], hashlib.sha256((self.inputs / name).read_bytes()).hexdigest())
                stable = lambda document: {key: value for key, value in document.items()
                                           if key not in {"phase", "recordedAtUtc", "imageDigest"}}
                self.assertEqual(stable(prepared), stable(recorded))
        self.assertEqual(set(self.downloads), {101, 102, 103, 104, 105})

    def test_source_tag_dirty_checkout_and_workflow_environment_fail_before_evidence_creation(self):
        for key, value in (("GITHUB_ACTIONS", "false"), ("GITHUB_REPOSITORY", "example/other"),
                           ("RELEASE_TAG", "v0.0.0-wrong"), ("GITHUB_RUN_ID", "0"),
                           ("GITHUB_RUN_ATTEMPT", "-1"), ("GITHUB_SHA", "not-a-source"),
                           ("GITHUB_WORKFLOW_REF", "example/other/.github/workflows/release-publish.yml@refs/heads/main")):
            with self.subTest(field=key), patch.dict(os.environ, {key: value}):
                self.refuse()
        for field, value in (("tag_revision", "f" * 40), ("dirty", " M unreviewed.py")):
            with self.subTest(field=field), patch.object(self, field, value):
                self.refuse()
        with patch.object(self.promotion.subprocess, "check_output", return_value="malformed-tooling-source"):
            self.refuse()

    def test_current_publication_authority_requires_the_exact_running_attempt(self):
        for key, value in (("id", 2021), ("run_attempt", 2), ("path", ".github/workflows/other.yml"),
                           ("head_sha", "f" * 40), ("status", "completed"), ("event", "push")):
            with self.subTest(field=key), patch.dict(self.current_run, {key: value}):
                self.refuse()
        with patch.dict(os.environ, {"GITHUB_EVENT_NAME": "workflow_dispatch"}):
            self.refuse()

    def test_component_config_and_published_reference_cannot_claim_another_image(self):
        for config in ("sha512:" + "d" * 64, "sha256:short", "sha256:" + "D" * 64):
            with self.subTest(config=config):
                args = self.args(); args.local_image_config = config; self.refuse(args)
        args = self.args(); args.image_digest = "sha256:" + "e" * 64; self.refuse(args)
        self.write()
        for reference in (None, "sha256:" + "e" * 64,
                          self.promotion.IMAGE_REPOSITORIES["web"] + "@sha256:" + "e" * 64,
                          self.promotion.IMAGE_REPOSITORIES["api"] + "@sha256:malformed"):
            with self.subTest(reference=reference):
                args = self.args("digest-recorded"); args.image_digest = reference; self.refuse(args)

    def test_cli_rejects_unknown_phase_and_component_before_running_any_transport(self):
        for option, value in (("--phase", "restore"), ("--component", "unknown")):
            argv = ["promote-release.py", "image-journal", "--inputs", str(self.inputs),
                    "--receipt", str(self.receipt_path), "--output", str(self.root / "never.json"),
                    "--component", "api", "--phase", "prepared", "--local-image-config", "sha256:" + "d" * 64]
            argv[argv.index(option) + 1] = value
            with self.subTest(option=option), patch.object(sys, "argv", argv), contextlib.redirect_stderr(io.StringIO()):
                with self.assertRaises(SystemExit) as rejected:
                    runpy.run_path(str(ROOT / "tools/ci/promote-release.py"), run_name="__main__")
                self.assertEqual(rejected.exception.code, 2)
        self.assertEqual(self.commands, [])
        self.assertFalse((self.root / "never.json").exists())

    def test_original_receipt_and_workflow_authority_are_revalidated_before_each_phase(self):
        for key, value in (("repository", "example/other"), ("workflow", ".github/workflows/other.yml"),
                           ("headSha", "f" * 40), ("productVersion", "0.0.0-wrong"), ("runId", 1011), ("attempt", 1)):
            with self.subTest(field=key), patch.dict(self.receipt, {key: value}):
                self.save_receipt(); self.refuse()
        self.save_receipt()
        for key, value in (("conclusion", "failure"), ("head_sha", "f" * 40), ("path", ".github/workflows/other.yml")):
            with self.subTest(field=key), patch.dict(self.original_run, {key: value}):
                self.refuse()
        self.write()
        prepared = self.args().output.read_bytes()
        raw_digest = self.root / "raw-published-digest.txt"
        raw_digest.write_text(self.args("digest-recorded").image_digest)
        with patch.dict(self.original_run, {"conclusion": "failure"}):
            self.refuse(self.args("digest-recorded"))
        self.assertEqual(self.args().output.read_bytes(), prepared)
        self.assertEqual(raw_digest.read_text(), self.args("digest-recorded").image_digest)

    def test_missing_expired_unowned_or_changed_artifact_metadata_is_rejected(self):
        before = list(self.downloads)
        for key, value in (("id", 999), ("expired", True), ("name", "unowned-artifact"), ("digest", "sha256:" + "f" * 64)):
            with self.subTest(field=key), patch.dict(self.metadata[0], {key: value}):
                self.refuse()
                self.assertEqual(self.downloads, before)
        with patch.object(self, "metadata", self.metadata[1:]):
            self.refuse()
            self.assertEqual(self.downloads, before)
        name = next(iter(self.receipt["files"]))
        with patch.dict(self.receipt["files"][name], {"artifactId": 999}):
            self.save_receipt(); self.refuse()
            self.assertEqual(self.downloads, before)

    def test_original_receipt_creation_requires_the_successful_push_and_exact_source_tag(self):
        grouped = self.root / "original-downloads"; grouped.mkdir()
        for metadata in self.metadata:
            members = [name for name, item in self.receipt["files"].items() if item["artifactId"] == metadata["id"]]
            runtime = {101: None, 102: "linux-x64", 103: "win-x64", 104: "osx-arm64", 105: None}[metadata["id"]]
            metadata["name"] = (f"netratel-mcp-stdio-{self.version}-win-x64" if metadata["id"] == 105 else
                                f"netratel-client-{self.version}-{runtime}" if runtime else
                                f"netratel-{self.version}-linux-x64")
            directory = grouped / metadata["name"]; directory.mkdir()
            for name in members: shutil.copyfile(self.inputs / name, directory / name)
            self.promotion.checksums(directory)
        self.original_run.update(event="push", head_branch=f"v{self.version}", run_attempt=2)
        output = self.root / "created-original-receipt.json"
        for key, value in (("event", "workflow_dispatch"), ("head_branch", "v0.0.0-wrong"), ("run_attempt", 0)):
            with self.subTest(field=key), patch.dict(self.original_run, {key: value}), self.assertRaises(ValueError):
                self.promotion.create_input_receipt(grouped, output, 1010, self.version, self.revision)
            self.assertFalse(output.exists())
        self.promotion.create_input_receipt(grouped, output, 1010, self.version, self.revision)
        receipt = json.loads(output.read_text())
        self.assertEqual(receipt["attempt"], 2)
        self.assertEqual(set(receipt["files"]), set(self.promotion.required_artifacts(self.version)))
        args = self.args(); args.receipt = output
        journal = self.write(args)
        self.assertEqual(journal["inputReceipt"], receipt)

    def test_receipt_download_selects_latest_upload_by_time_across_partial_reruns(self):
        self.original_run.update(event="push", head_branch=f"v{self.version}", run_attempt=2)
        for metadata in self.metadata:
            runtime = {101: None, 102: "linux-x64", 103: "win-x64", 104: "osx-arm64", 105: None}[metadata["id"]]
            metadata["name"] = (f"netratel-mcp-stdio-{self.version}-win-x64" if metadata["id"] == 105 else
                                f"netratel-client-{self.version}-{runtime}" if runtime else
                                f"netratel-{self.version}-linux-x64")
            metadata["created_at"] = "2026-01-01T00:00:00Z"
            archive = self.archives[metadata["id"]]
            with zipfile.ZipFile(archive, "a") as output:
                members = [(name, item) for name, item in self.receipt["files"].items()
                           if item["artifactId"] == metadata["id"]]
                output.writestr("SHA256SUMS", "".join(f"{item['sha256']}  {name}\n" for name, item in members))
            metadata["digest"] = "sha256:" + self.promotion.sha256(archive)
        # The earlier upload has a larger ID and occurs first in the API result.
        older = dict(self.metadata[0], id=999, created_at="2025-12-31T23:59:00Z")
        self.metadata.insert(0, older)
        inputs = self.root / "downloaded"
        output = self.root / "downloaded-receipt.json"
        self.promotion.create_input_receipt(inputs, output, 1010, self.version, self.revision, download=True)
        receipt = json.loads(output.read_text())
        self.assertEqual({item["artifactId"] for item in receipt["files"].values()}, {101, 102, 103, 104, 105})
        self.promotion.verified_input_receipt(inputs, output, self.version, self.revision)
        self.assertNotIn(999, self.downloads)
        older["created_at"] = self.metadata[1]["created_at"]
        with self.assertRaisesRegex(ValueError, "Ambiguous"):
            self.promotion.create_input_receipt(self.root / "ambiguous", output, 1010, self.version, self.revision, download=True)

    def test_authentication_checks_outer_zip_digest_and_selected_inner_payload_bytes(self):
        self.archives[101].write_bytes(self.archives[101].read_bytes() + b"unapproved outer bytes")
        self.refuse()
        # The synthetic authority now authenticates the altered ZIP; its selected inner bytes still must match.
        name = next(name for name, item in self.receipt["files"].items() if item["artifactId"] == 101)
        with zipfile.ZipFile(self.archives[101]) as archive:
            payloads = {member: archive.read(member) for member in archive.namelist()}
        payloads[name] = b"different authenticated inner payload"
        with zipfile.ZipFile(self.archives[101], "w") as archive:
            for member, content in payloads.items(): archive.writestr(member, content)
        self.bind_changed_archive()
        self.refuse()

    def test_authenticated_zip_rejects_traversal_absolute_symlink_and_duplicate_members(self):
        original = self.archives[101].read_bytes()
        for kind in ("traversal", "absolute", "symlink", "duplicate"):
            with self.subTest(kind=kind):
                self.archives[101].write_bytes(original)
                with zipfile.ZipFile(self.archives[101], "a") as archive:
                    if kind == "traversal": archive.writestr("../outside.txt", b"unsafe")
                    elif kind == "absolute": archive.writestr("/outside.txt", b"unsafe")
                    elif kind == "symlink":
                        member = zipfile.ZipInfo("link"); member.create_system = 3
                        member.external_attr = (stat.S_IFLNK | 0o777) << 16
                        archive.writestr(member, "../outside.txt")
                    else:
                        member = archive.namelist()[0]
                        with self.assertWarns(UserWarning): archive.writestr(member, b"duplicate")
                self.bind_changed_archive()
                self.refuse()
                self.assertFalse((self.root / "outside.txt").exists())

    def test_prepared_identity_changes_preserve_prior_evidence_and_refuse_new_result(self):
        prepared = self.write()
        mutations = [
            (key, value) for key, value in (("revision", "f" * 40), ("version", "0.0.0-wrong"),
                                           ("component", "web"), ("scannedLocalImageConfig", "sha256:" + "f" * 64),
                                           ("resumeAuthority", True), ("schema", "untrusted-schema"))
        ]
        for key, value in mutations:
            with self.subTest(field=key):
                changed = copy.deepcopy(prepared); changed[key] = value
                self.args().output.write_text(json.dumps(changed)); before = self.args().output.read_bytes()
                self.refuse(self.args("digest-recorded")); self.assertEqual(self.args().output.read_bytes(), before)
        for field in ("publication-attempt", "publication-tooling", "publication-source", "inputReceipt"):
            with self.subTest(field=field):
                changed = copy.deepcopy(prepared)
                if field == "publication-attempt": changed["publication"]["attempt"] = 4
                elif field == "publication-tooling": changed["publication"]["toolingSourceSha"] = "f" * 40
                elif field == "publication-source": changed["publication"]["workflowSourceSha"] = "f" * 40
                else: changed[field]["files"][next(iter(changed[field]["files"]))]["sha256"] = "f" * 64
                self.args().output.write_text(json.dumps(changed)); self.refuse(self.args("digest-recorded"))
        changed = copy.deepcopy(prepared); changed["unapprovedKey"] = "untrusted"
        self.args().output.write_text(json.dumps(changed)); self.refuse(self.args("digest-recorded"))
        del changed["unapprovedKey"]; del changed["tag"]
        self.args().output.write_text(json.dumps(changed)); self.refuse(self.args("digest-recorded"))

    def test_missing_malformed_nonobject_and_symlink_prepared_documents_are_refused(self):
        self.refuse(self.args("digest-recorded"))
        self.args().output.parent.mkdir()
        for contents in ("{bad-json", "[]", "null", '"not-an-object"'):
            with self.subTest(contents=contents):
                self.args().output.write_text(contents); self.refuse(self.args("digest-recorded"))
        target = self.root / "retained-prepared.json"; target.write_text("retained sentinel")
        self.args().output.unlink(); self.args().output.symlink_to(target)
        self.refuse(self.args("digest-recorded")); self.assertEqual(target.read_text(), "retained sentinel")

    def test_phase_files_are_exclusive_and_never_replace_an_existing_document(self):
        for phase in ("prepared", "digest-recorded"):
            with self.subTest(phase=phase):
                if phase == "digest-recorded": self.write()
                args = self.args(phase); args.output.parent.mkdir(exist_ok=True)
                args.output.write_bytes(b"existing immutable phase sentinel")
                with self.assertRaises(FileExistsError): self.promotion.write_publication_image_journal(args)
                self.assertEqual(args.output.read_bytes(), b"existing immutable phase sentinel")
                args.output.unlink()

    def test_allowlisted_journals_do_not_serialize_environment_or_receipt_secret_canaries(self):
        canary = "unit-secret-canary-do-not-serialize"
        self.receipt["token"] = canary
        for item in self.receipt["files"].values(): item["password"] = canary
        self.save_receipt()
        with patch.dict(os.environ, {"GH_TOKEN": canary, "DOCKER_PASSWORD": canary, "UNRELATED_PRIVATE_VALUE": canary}):
            self.write(); self.write(self.args("digest-recorded"))
        for path in self.args().output.parent.iterdir():
            self.assertNotIn(canary, path.read_text())

    def test_command_error_redacts_secret_canaries_and_creates_no_journal(self):
        canary = "unit-command-secret-canary"
        error = subprocess.CalledProcessError(1, ("python3", "product-version.py"),
                                              stderr=f"token={canary} password={canary} authorization={canary}")
        with patch.object(self.promotion, "run", self.real_run), \
                patch.object(self.promotion.subprocess, "run", side_effect=error):
            with self.assertRaises(ValueError) as rejected:
                self.promotion.write_publication_image_journal(self.args())
        self.assertNotIn(canary, str(rejected.exception))
        self.assertFalse(self.args().output.exists())

    def test_registry_absence_is_only_typed_404_and_never_auth_timeout_or_existing_tag(self):
        from urllib.error import URLError
        with patch.object(self.promotion, "public_registry_token", return_value=("bostontechnologies/netratel-api", "synthetic-token")):
            for status in (401, 403, 429, 500):
                with self.subTest(status=status):
                    error = HTTPError("https://registry.example.test", status, "synthetic failure", {}, None)
                    with patch.object(self.promotion.urllib.request, "urlopen", side_effect=error), self.assertRaises(ValueError):
                        self.promotion.require_unused_release_tag("api", self.version)
                    error.close()
            for error in (URLError("synthetic timeout"), TimeoutError("synthetic timeout")):
                with patch.object(self.promotion.urllib.request, "urlopen", side_effect=error), self.assertRaises((ValueError, OSError)):
                    self.promotion.require_unused_release_tag("api", self.version)
            absent = HTTPError("https://registry.example.test", 404, "Not Found", {}, None)
            with patch.object(self.promotion.urllib.request, "urlopen", side_effect=absent):
                self.promotion.require_unused_release_tag("api", self.version)
            absent.close()
            with patch.object(self.promotion.urllib.request, "urlopen", return_value=io.BytesIO(b"malformed manifest")), self.assertRaisesRegex(ValueError, "already exists"):
                self.promotion.require_unused_release_tag("api", self.version)

    def test_existing_public_assets_are_reused_and_only_missing_exact_bytes_are_uploaded(self):
        directory = self.root / "complete"; directory.mkdir()
        for name in self.promotion.required_artifacts(self.version): (directory / name).write_bytes(name.encode())
        record = {"productVersion": self.version, "verification": {"state": "complete"},
                  "artifacts": {name: hashlib.sha256((directory / name).read_bytes()).hexdigest()
                                for name in self.promotion.required_artifacts(self.version)}}
        (directory / "publication.json").write_text(json.dumps(record)); self.promotion.checksums(directory)
        files = {path.name: "sha256:" + hashlib.sha256(path.read_bytes()).hexdigest() for path in directory.iterdir()}
        first = min(files)
        existing = {first: files[first]}
        uploads = []
        def transport(*command, env=None):
            if command[:2] == ("gh", "api"):
                self.assertEqual(command[2], f"repos/{self.promotion.REPOSITORY}/releases/tags/v{self.version}")
                return json.dumps({"draft": False, "tag_name": f"v{self.version}",
                                   "assets": [{"name": name, "digest": digest} for name, digest in existing.items()]})
            self.assertEqual(command[:3], ("gh", "release", "upload"))
            self.assertNotIn("--clobber", command)
            path = Path(command[4]); self.assertEqual(path.parent, directory)
            self.assertNotIn(path.name, existing)
            uploads.append(path.name); existing[path.name] = "sha256:" + hashlib.sha256(path.read_bytes()).hexdigest()
            return ""
        with patch.object(self.promotion, "run", side_effect=transport), contextlib.redirect_stdout(io.StringIO()):
            self.promotion.publish_assets(directory, f"v{self.version}")
        self.assertEqual(set(uploads), set(files) - {first})
        self.assertEqual(existing, files)
        uploads.clear(); existing[first] = "sha256:" + "f" * 64
        with patch.object(self.promotion, "run", side_effect=transport), self.assertRaisesRegex(ValueError, "differs"):
            self.promotion.publish_assets(directory, f"v{self.version}")
        self.assertEqual(uploads, [])

class HttpHostLauncherTests(unittest.TestCase):
    """Execute the real host launcher against a metadata-only Docker fixture."""

    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.root = Path(self.directory.name)
        self.stack = self.root / "stack with spaces"
        self.stack.mkdir()
        self.compose_file = self.stack / "compose.yaml"
        self.compose_file.write_text("services:\n  mcp-http:\n    image: fixture\n")
        self.compose_file.chmod(0o600)
        self.env_file = self.stack / ".env"
        self.env_file.write_text("UNCHANGED=fixture\n")
        self.log = self.root / "docker.jsonl"
        self.log.write_text("")
        self.bin = self.root / "bin"
        self.bin.mkdir()
        docker = self.bin / "docker"
        docker.write_text("""#!/usr/bin/env python3
import hashlib, json, os, pathlib, sys
args = sys.argv[1:]
with open(os.environ["LAUNCHER_DOCKER_LOG"], "a") as log:
    log.write(json.dumps(args) + "\\n")
if args[0] == "compose":
    capability = os.environ.get("LAUNCHER_COMPOSE_CAPABILITY", "supported")
    if capability in ("missing", "broken"):
        print("private plugin error sentinel", file=sys.stderr)
        sys.exit(1)
    if "version" in args:
        print(os.environ.get("LAUNCHER_COMPOSE_VERSION", "2.35.0"))
        sys.exit(0)
    if "--help" in args:
        flags = "--format --no-interpolate --no-path-resolution --quiet --services"
        if capability != "unsupported": flags += " --no-env-resolution"
        print(flags)
        sys.exit(0)
    if "config" in args:
        selected_file = pathlib.Path(args[args.index("-f") + 1]) if "-f" in args else None
        if selected_file and selected_file.name == ".compose-provider-probe.json":
            print(selected_file.read_text())
            sys.exit(0)
        if "--services" in args:
            print("api\\nweb\\nmcp-http")
        elif "--format" in args:
            if "--no-env-resolution" in args:
                print(os.environ.get("LAUNCHER_RESOLVED_JSON", json.dumps({"services": {"mcp-http": {"image": "fixture"}}})))
            else:
                print(os.environ.get("LAUNCHER_RESOLVED_JSON", json.dumps({"services": {"mcp-http": {"image": "fixture"}}})))
        elif "--quiet" not in args:
            print(pathlib.Path(os.environ["LAUNCHER_COMPOSE_FILE"]).read_text(), end="")
        if os.environ.get("LAUNCHER_PREFLIGHT_FAIL") == "1":
            print("private diagnostic sentinel", file=sys.stderr)
            sys.exit(1)
    elif "ps" in args:
        if args[-1] == "web":
            for index in range(int(os.environ.get("LAUNCHER_WEB_COUNT", "0"))):
                print("web-container-" + str(index))
        elif os.environ.get("LAUNCHER_EXISTING_VOLUMES") == "1":
            print("serving-container")
    elif "run" in args:
        if "--entrypoint" in args:
            if os.environ.get("LAUNCHER_STORAGE_FAIL") == "1": sys.exit(1)
            source = sys.stdin.buffer.read()
            pathlib.Path(os.environ["LAUNCHER_DOCKER_LOG"] + ".source").write_bytes(source)
            if os.environ.get("LAUNCHER_MUTATE_SOURCE") == "stage":
                pathlib.Path(args[args.index("-f") + 1]).write_text("changed during staging")
            print(hashlib.sha256(source).hexdigest() + "  -")
            sys.exit(0)
        if os.environ.get("LAUNCHER_MUTATE_SOURCE") == "wizard":
            pathlib.Path(args[args.index("-f") + 1]).write_text("changed during review")
        sys.exit(int(os.environ.get("LAUNCHER_SETUP_STATUS", "2")))
elif args[0] == "image":
    if args[args.index("--format") + 1] == "{{.Id}}": print("sha256:" + "a" * 64)
    else:
        print(os.environ.get("LAUNCHER_IMAGE_ENV_RECORDS", json.dumps([
            "DOTNET_RUNNING_IN_CONTAINER=true", "ASPNETCORE_HTTP_PORTS=8080"])))
        print()  # Docker appends its own newline after the formatted JSON.
elif args[0] == "create":
    print("image-inspection-container")
elif args[0] == "info":
    print(os.environ.get("LAUNCHER_DAEMON_ID", "fixture-daemon"))
elif args[0] == "ps":
    count = int(os.environ.get("LAUNCHER_CONTAINER_COUNT", "1"))
    for index in range(count): print("serving-container-" + str(index))
elif args[0] == "inspect":
    template = args[2]
    if template == "{{.Image}}":
        print("sha256:" + "a" * 64)
        sys.exit(0)
    if "com.docker.compose.project.config_files" in template:
        files = os.environ.get("LAUNCHER_SAVED_FILES", os.environ["LAUNCHER_COMPOSE_FILE"])
        print(os.environ.get("LAUNCHER_PROJECT", "customer-stack") + "|" + files + "|" + os.environ["LAUNCHER_COMPOSE_DIRECTORY"] + "|" + os.environ["LAUNCHER_ENV_FILE"] + "|" + os.environ.get("LAUNCHER_MANAGER_METADATA", "external-compose") + "|fixture|mcp-http")
        sys.exit(0)
    if "oneoff" in template:
        print("False")
    elif "NETRATEL_SETUP_DEPLOYMENT_MANAGER" in template:
        owner = os.environ.get("LAUNCHER_RUNTIME_MANAGER", "")
        print("NETRATEL_SETUP_DEPLOYMENT_MANAGER=" + owner if owner else "")
    elif ".Config.Env" in template:
        if 'eq $key "apibaseurl"' not in template or 'eq $key "webgateway__mcp__backendurl"' not in template:
            print(os.environ.get("LAUNCHER_RUNTIME_ENV_RECORDS", json.dumps(["NetRatel__Mcp__Http__Instance=prod"])))
            print()  # Docker inspect emits an outer newline for runtime Env too.
            sys.exit(0)
        records = ["ApiBaseUrl=http://api-private:9440/custom-api/"]
        if os.environ.get("LAUNCHER_WEB_MCP_ABSENT") != "1":
            records.append("WebGateway__Mcp__BackendUrl=http://relay-private:9550/custom-mcp/")
        print(os.environ.get("LAUNCHER_WEB_BACKEND_RECORDS", json.dumps(records)))
    elif "/setup-output" in template:
        print("volume|" + ("existing-server-generation" if os.environ.get("LAUNCHER_EXISTING_VOLUMES") == "1" else os.environ.get("LAUNCHER_PROJECT", "customer-stack") + "-http-host-setup"))
    elif "/var/lib/netratel/clients" in template:
        print("volume|existing-client-profiles")
    elif ".State.Running" in template:
        print("false")
elif args[0] == "cp":
    if args[1].endswith(":/setup-output/.http-host.setup.json"):
        if os.environ.get("LAUNCHER_GENERATION_MISSING") == "1": sys.exit(1)
        manager = "release-compose" if os.environ.get("LAUNCHER_MANAGER_METADATA") == "release-compose" else "komodo"
        pathlib.Path(args[2]).write_text(os.environ.get("LAUNCHER_GENERATION", json.dumps({"DeploymentManager": manager, "PublicOrigin": "https://web.example.invalid"})))
        sys.exit(0)
    if args[1].startswith("image-inspection-container:") or ":/app/appsettings" in args[1]:
        if args[1].endswith("/app/appsettings.Production.json") and "LAUNCHER_IMAGE_ENV_APPSETTINGS" in os.environ:
            pathlib.Path(args[2]).write_text(os.environ["LAUNCHER_IMAGE_ENV_APPSETTINGS"])
            sys.exit(0)
        if args[1].endswith("/app/appsettings.json") and "LAUNCHER_IMAGE_APPSETTINGS" in os.environ:
            pathlib.Path(args[2]).write_text(os.environ["LAUNCHER_IMAGE_APPSETTINGS"])
            sys.exit(0)
        sys.exit(1)  # appsettings are optional image providers in this fixture.
    if os.environ.get("LAUNCHER_EXPORT_FAIL") == "1":
        print("fixture copy failure", file=sys.stderr)
        sys.exit(1)
    content = b"services:\\n  mcp-http:\\n    image: fixture\\n"
    if "/.export-" in args[1]:
        if os.environ.get("LAUNCHER_RECEIPT_MISSING") == "1":
            sys.exit(1)
        digest = hashlib.sha256(content).hexdigest()
        if os.environ.get("LAUNCHER_RECEIPT_MISMATCH") == "1":
            digest = "0" * 64
        content = (digest + "\\n").encode()
    pathlib.Path(args[2]).write_bytes(content)
""")
        docker.chmod(0o755)
        self.environment = dict(os.environ, PATH=str(self.bin) + os.pathsep + os.environ["PATH"],
                                LAUNCHER_DOCKER_LOG=str(self.log), LAUNCHER_COMPOSE_FILE=str(self.compose_file),
                                LAUNCHER_ENV_FILE=str(self.env_file), LAUNCHER_COMPOSE_DIRECTORY=str(self.stack))
        self.launcher = ROOT / "release/setup-http-mcp.sh"

    def tearDown(self):
        self.directory.cleanup()

    def run_launcher(self, *extra, **environment):
        env = dict(self.environment, **environment)
        return subprocess.run(
            ["bash", str(self.launcher), "--compose-file", str(self.compose_file),
             "--project", "customer-stack", "--manager", "komodo", *extra],
            cwd=self.root, env=env, text=True, capture_output=True, timeout=15)

    def commands(self):
        return [json.loads(line) for line in self.log.read_text().splitlines()]

    def run_command(self):
        return next(command for command in self.commands()
                    if command[0] == "compose" and "run" in command and "--name" in command)

    def test_compose_unsupported_capability_stops_before_source_render_or_container_creation(self):
        result = self.run_launcher(LAUNCHER_COMPOSE_CAPABILITY="unsupported",
                                   LAUNCHER_COMPOSE_VERSION="2.26.1-4")
        self.assertNotEqual(result.returncode, 2, result.stderr)
        self.assertIn("compose_capability_unsupported", result.stderr)
        self.assertIn("2.26.1-4", result.stderr)
        self.assertIn("--no-env-resolution", result.stderr)
        self.assertIn("2.35.0", result.stderr)
        self.assertIn("--isolated-compose", result.stderr)
        self.assertFalse(any("config" in call and "--help" not in call for call in self.commands()))
        self.assertFalse(any("run" in call or "create" in call for call in self.commands()))
        self.assertNotIn("private plugin error sentinel", result.stdout + result.stderr)

    def test_compose_supported_distribution_suffix_uses_capabilities_not_version_ordering(self):
        result = self.run_launcher(LAUNCHER_COMPOSE_VERSION="2.35.0-1ubuntu1~distribution")
        self.assertEqual(result.returncode, 2, result.stderr)
        calls = self.commands()
        capability = next(i for i, call in enumerate(calls) if "config" in call and "--help" in call)
        rendering = next(i for i, call in enumerate(calls) if "config" in call and "--help" not in call)
        self.assertLess(capability, rendering)

    def test_compose_missing_or_broken_plugin_has_one_redacted_prerequisite_diagnostic(self):
        for capability in ("missing", "broken"):
            with self.subTest(capability=capability):
                self.log.write_text("")
                result = self.run_launcher(LAUNCHER_COMPOSE_CAPABILITY=capability)
                self.assertNotEqual(result.returncode, 2)
                self.assertIn("compose_capability_unsupported", result.stderr)
                self.assertIn("unavailable", result.stderr)
                self.assertEqual(result.stderr.count("Setup not started:"), 1)
                self.assertNotIn("private plugin error sentinel", result.stdout + result.stderr)
                self.assertFalse(any("run" in call or "create" in call for call in self.commands()))

    def test_selected_compose_plugin_and_docker_configuration_are_retained_in_resume(self):
        plugin = self.root / "selected compose plugin"
        plugin.write_text("#!/usr/bin/env bash\nexec docker compose \"$@\"\n")
        plugin.chmod(0o700)
        config = self.root / "docker config with spaces"
        config.mkdir()
        (config / "config.json").write_text('{"auths":{"registry.example.invalid":{}},"currentContext":"selected-context"}')
        original = (config / "config.json").read_bytes()
        result = self.run_launcher("--compose-plugin", str(plugin), DOCKER_CONFIG=str(config),
                                   DOCKER_CONTEXT="selected-context", DOCKER_HOST="tcp://docker.example.invalid:2376")
        self.assertEqual(result.returncode, 2, result.stderr)
        self.assertEqual((config / "config.json").read_bytes(), original)
        resume = next(line.strip() for line in result.stdout.splitlines() if "--verify" in line)
        arguments = shlex.split(resume)
        self.assertEqual(arguments[0], "env")
        self.assertIn("DOCKER_CONFIG=" + str(config), arguments)
        self.assertIn("DOCKER_CONTEXT=selected-context", arguments)
        self.assertIn("DOCKER_HOST=tcp://docker.example.invalid:2376", arguments)
        self.assertEqual(arguments[arguments.index("--compose-plugin") + 1], str(plugin))
        # Execute the exact printed Bash command with the fixture's environment.
        resumed = subprocess.run(arguments, cwd=self.root, env=dict(self.environment, LAUNCHER_SETUP_STATUS="0"),
                                 text=True, capture_output=True, timeout=15)
        self.assertEqual(resumed.returncode, 0, resumed.stderr)

    def test_missing_explicit_compose_plugin_uses_the_same_prerequisite_remediation(self):
        result = self.run_launcher("--compose-plugin", str(self.root / "missing compose plugin"))
        self.assertNotEqual(result.returncode, 2)
        self.assertIn("compose_capability_unsupported", result.stderr)
        self.assertIn("unavailable", result.stderr)
        command = shlex.split(result.stderr.split("privately if needed): ", 1)[1])
        self.assertIn("--isolated-compose", command)
        self.assertNotIn("--compose-plugin", command)
        self.assertEqual(self.commands(), [])

    def test_isolated_compose_rejects_unverified_download_without_executing_or_installing_it(self):
        curl = self.bin / "curl"
        curl.write_text("#!/usr/bin/env python3\nimport pathlib, sys\npathlib.Path(sys.argv[sys.argv.index('--output') + 1]).write_bytes(b'untrusted synthetic bytes')\n")
        curl.chmod(0o700)
        tooling = self.root / "isolated tools"
        result = self.run_launcher("--isolated-compose", str(tooling))
        self.assertNotEqual(result.returncode, 2)
        self.assertIn("integrity verification failed", result.stderr)
        self.assertFalse((tooling / "docker-compose").exists())
        self.assertEqual(list(tooling.iterdir()), [])
        self.assertEqual(self.commands(), [])

    def test_isolated_compose_rechecks_existing_bytes_before_execution(self):
        tooling = self.root / "isolated tools"
        tooling.mkdir(mode=0o700)
        plugin = tooling / "docker-compose"
        plugin.write_text("#!/bin/sh\nexit 0\n")
        plugin.chmod(0o700)
        result = self.run_launcher("--isolated-compose", str(tooling))
        self.assertNotEqual(result.returncode, 2)
        self.assertIn("integrity verification failed", result.stderr)
        self.assertEqual(self.commands(), [])

    def test_compose_remediation_replaces_an_unsupported_explicit_selection(self):
        plugin = self.root / "old compose plugin"
        plugin.write_text("#!/usr/bin/env bash\nexec docker compose \"$@\"\n")
        plugin.chmod(0o700)
        result = self.run_launcher("--compose-plugin", str(plugin), LAUNCHER_COMPOSE_CAPABILITY="unsupported",
                                   LAUNCHER_COMPOSE_VERSION="2.26.1-4")
        command = shlex.split(result.stderr.split("privately if needed): ", 1)[1])
        self.assertEqual(command.count("--isolated-compose"), 1)
        self.assertNotIn("--compose-plugin", command)
        self.assertIn(str(self.compose_file), command)

    def test_compose_provider_discovery_uses_native_interpolation_without_flattening_env_files(self):
        result = self.run_launcher()
        self.assertEqual(result.returncode, 2, result.stderr)
        calls = self.commands()
        raw = next(call for call in calls if "config" in call and "--no-env-resolution" in call
                   and "--no-interpolate" in call and "--no-path-resolution" in call and "--format" in call)
        interpolated = next(call for call in calls if "config" in call and "-f" in call
                            and call[call.index("-f") + 1].endswith("/.compose-provider-probe.json"))
        self.assertEqual([raw[i + 1] for i, value in enumerate(raw) if value == "-f"], [str(self.compose_file)])
        self.assertNotIn("--no-interpolate", interpolated)
        self.assertEqual(interpolated[interpolated.index("--env-file") + 1], str(self.env_file))
        self.assertEqual(interpolated[interpolated.index("--project-directory") + 1], str(self.stack))
        probe = json.loads((self.stack / ".netratel-http-mcp-customer-stack/.compose-provider-probe.json").read_text())
        self.assertNotIn("env_file", probe["services"]["netratel-source-providers"])
        export = next(call for call in calls if "config" in call and "--no-env-resolution" in call
                      and "--no-interpolate" in call and "--format" not in call and "--help" not in call)
        self.assertIn("--no-path-resolution", export)
        self.assertFalse(any("run" in call for call in calls[:calls.index(interpolated)]))

    def test_isolated_compose_cannot_modify_an_active_docker_plugin_directory(self):
        config = self.root / "selected docker config"
        config.mkdir()
        extra = self.root / "custom plugin directory"
        (config / "config.json").write_text(json.dumps({"cliPluginsExtraDirs": [str(extra)]}))
        original = (config / "config.json").read_bytes()
        for directory in (config / "cli-plugins", extra):
            with self.subTest(directory=directory.name):
                result = self.run_launcher("--isolated-compose", str(directory), DOCKER_CONFIG=str(config))
                self.assertNotEqual(result.returncode, 2)
                self.assertIn("outside Docker plugin search directories", result.stderr)
                self.assertFalse(directory.exists())
                self.assertEqual((config / "config.json").read_bytes(), original)
        self.assertEqual(self.commands(), [])

    def test_compose_provider_discovery_decodes_native_dollar_escaping_once(self):
        provider = self.stack / "dollar$directory.env"
        provider.write_text("SYNTHETIC=provider\n")
        raw = json.dumps({"services": {"mcp-http": {"image": "fixture", "env_file": [
            {"path": "./dollar$$directory.env", "required": True}]}}})
        result = self.run_launcher(LAUNCHER_RESOLVED_JSON=raw)
        self.assertEqual(result.returncode, 2, result.stderr)
        output = self.stack / ".netratel-http-mcp-customer-stack"
        inputs = json.loads((output / ".compose-inputs.json").read_text())
        self.assertEqual(inputs["services"]["mcp-http"]["env_file"][0]["path"], str(provider))
        initial = json.loads((output / ".initial-source-state.json").read_text())
        self.assertIn(str(provider), initial)
        self.assertNotIn("SYNTHETIC", result.stdout + result.stderr)

    def test_compose_remediation_never_echoes_private_wizard_arguments(self):
        secret = "synthetic-wizard-secret-sentinel"
        result = self.run_launcher("--", "--client-secret", secret,
                                   LAUNCHER_COMPOSE_CAPABILITY="unsupported", LAUNCHER_COMPOSE_VERSION="2.26.1-4")
        self.assertIn("compose_capability_unsupported", result.stderr)
        self.assertNotIn(secret, result.stdout + result.stderr)
        self.assertNotIn("--client-secret", result.stdout + result.stderr)
        command = shlex.split(result.stderr.split("privately if needed): ", 1)[1])
        self.assertIn(str(self.compose_file), command)
        self.assertNotIn("--", command)

    def test_isolated_compose_preserves_default_plugin_with_an_alternate_docker_config(self):
        synthetic_home = self.root / "synthetic home"
        synthetic_home.mkdir()
        config = self.root / "alternate docker config"
        config.mkdir()
        default_plugin_directory = synthetic_home / ".docker/cli-plugins"
        result = self.run_launcher("--isolated-compose", str(default_plugin_directory),
                                   HOME=str(synthetic_home), DOCKER_CONFIG=str(config))
        self.assertNotEqual(result.returncode, 2)
        self.assertIn("outside Docker plugin search directories", result.stderr)
        self.assertFalse(default_plugin_directory.exists())
        self.assertEqual(self.commands(), [])

    def test_actual_file_quoted_arguments_and_private_export_with_exact_resume_command(self):
        self.launcher = self.root / "copied host setup.sh"
        self.launcher.write_bytes((ROOT / "release/setup-http-mcp.sh").read_bytes())
        self.launcher.chmod(0o644)  # docker cp retains the image's readable file mode.
        result = self.run_launcher("--", "--instance", "https://web.example.invalid",
                                   "--api-base-url", "https://api.example.invalid")
        self.assertEqual(result.returncode, 2, result.stderr)
        command = self.run_command()
        self.assertEqual(command[command.index("-f") + 1], str(self.compose_file))
        self.assertEqual(command[command.index("--project-name") + 1], "customer-stack")
        self.assertEqual(command[command.index("--env-file") + 1], str(self.env_file))
        self.assertFalse(any("/setup-input" in argument for argument in command))
        self.assertIn("customer-stack-http-host-setup:/setup-output", command)
        self.assertIn("customer-stack-http-client-data:/var/lib/netratel/clients", command)
        self.assertEqual(command[-10:], [
            "--target", "http-host", "--deployment-manager", "komodo",
            "--compose-file", "/setup-output/.compose-source.yaml", "--instance",
            "https://web.example.invalid", "--api-base-url", "https://api.example.invalid"])
        self.assertNotIn("--rm", command)
        self.assertNotIn("compose.setup.yaml", "\n".join(command))
        self.assertFalse(any("docker.sock" in argument for argument in command))
        output = self.stack / ".netratel-http-mcp-customer-stack/compose.komodo.yaml"
        self.assertTrue(output.is_file())
        self.assertEqual(stat.S_IMODE(output.stat().st_mode), 0o600)
        self.assertEqual(stat.S_IMODE(output.parent.stat().st_mode), 0o700)
        self.assertIn("--verify", result.stdout)
        self.assertIn("Save and Deploy", result.stdout)
        self.assertNotIn("services:", result.stdout)
        self.assertTrue(any(command[0] == "rm" for command in self.commands()))
        self.assertEqual(self.compose_file.read_text(), "services:\n  mcp-http:\n    image: fixture\n")
        self.assertEqual(stat.S_IMODE(self.compose_file.stat().st_mode), 0o600)
        snapshot = Path(str(self.log) + ".source")
        self.assertIn(b"services", snapshot.read_bytes())
        stage = next(command for command in self.commands() if command[0] == "compose" and "--entrypoint" in command)
        self.assertEqual(stage[stage.index("--user") + 1], "10001:10001")
        self.assertTrue(any("head -c 262145" in argument for argument in stage))
        resume = next(line.strip() for line in result.stdout.splitlines()
                      if line.startswith("  ") and "--verify" in line)
        verified = subprocess.run(shlex.split(resume), cwd=self.root,
                                  env=dict(self.environment, LAUNCHER_SETUP_STATUS="0"),
                                  text=True, capture_output=True, timeout=15)
        self.assertEqual(verified.returncode, 0, verified.stderr)
        self.assertIn("Server verification completed", verified.stdout)

    def test_reuses_existing_named_volumes_and_inspects_only_mounts_and_labels(self):
        result = self.run_launcher(LAUNCHER_EXISTING_VOLUMES="1")
        self.assertEqual(result.returncode, 2, result.stderr)
        command = self.run_command()
        self.assertIn("existing-server-generation:/setup-output", command)
        self.assertIn("existing-client-profiles:/var/lib/netratel/clients", command)
        self.assertIn("NETRATEL_SETUP_VOLUME=existing-server-generation", command)
        self.assertIn("NETRATEL_CLIENT_VOLUME=existing-client-profiles", command)
        for call in self.commands():
            if call[0] == "inspect":
                self.assertNotIn(".Config.Env", call[2])
                self.assertTrue(any(value in call[2] for value in (".Mounts", ".Config.Labels", ".State.Running")))

    def test_missing_compose_file_stops_before_any_docker_call(self):
        self.compose_file.unlink()
        result = self.run_launcher()
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("existing absolute regular file", result.stderr)
        self.assertEqual(self.commands(), [])

    def test_failed_compose_preflight_does_not_start_setup_or_print_private_diagnostics(self):
        result = self.run_launcher(LAUNCHER_PREFLIGHT_FAIL="1")
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("failed validation", result.stderr)
        self.assertNotIn("private diagnostic sentinel", result.stdout + result.stderr)
        self.assertFalse(any("run" in command for command in self.commands()))
        diagnostics = self.stack / ".netratel-http-mcp-customer-stack/preflight.log"
        self.assertIn("private diagnostic sentinel", diagnostics.read_text())

    def test_client_target_uses_client_flow_without_host_flags_or_host_export(self):
        result = self.run_launcher("--target", "http-client", LAUNCHER_SETUP_STATUS="0")
        self.assertEqual(result.returncode, 0, result.stderr)
        command = self.run_command()
        self.assertEqual(command[-3:], ["setup", "--target", "http-client"])
        self.assertNotIn("--deployment-manager", command)
        self.assertFalse(any(command[0] == "cp" and ":/setup-output/" in command[1] for command in self.commands()))
        self.assertNotIn("Compose editor", result.stdout)

    def test_export_failure_keeps_container_and_setup_generation_for_recovery(self):
        result = self.run_launcher(LAUNCHER_EXPORT_FAIL="1")
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("retained", result.stderr)
        self.assertFalse(any(command[0] == "rm" and command[1] != "image-inspection-container" for command in self.commands()))
        self.assertFalse((self.stack / ".netratel-http-mcp-customer-stack/compose.komodo.yaml").exists())

    def test_verify_resumes_without_exporting_or_reimporting_saved_compose(self):
        result = self.run_launcher("--verify", LAUNCHER_SETUP_STATUS="0")
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("--verify", self.run_command())
        self.assertFalse(any(command[0] == "cp" and ":/setup-output/" in command[1] for command in self.commands()))
        self.assertIn("Server verification completed", result.stdout)
        self.assertNotIn("Save and Deploy", result.stdout)

    def test_missing_or_mismatched_current_receipt_cannot_export_stale_compose(self):
        output = self.stack / ".netratel-http-mcp-customer-stack/compose.komodo.yaml"
        output.parent.mkdir(mode=0o700)
        output.write_text("prior private export")
        output.chmod(0o600)
        for option in ("LAUNCHER_RECEIPT_MISSING", "LAUNCHER_RECEIPT_MISMATCH"):
            with self.subTest(option=option):
                self.log.write_text("")
                result = self.run_launcher(**{option: "1"})
                self.assertNotEqual(result.returncode, 0)
                self.assertIn("retained", result.stderr)
                self.assertEqual(output.read_text(), "prior private export")
                self.assertNotIn("Save and Deploy", result.stdout)
                self.assertFalse(any(command[0] == "rm" and command[1] != "image-inspection-container" for command in self.commands()))

    def test_changed_source_stops_before_wizard_or_export_as_applicable(self):
        for stage in ("stage", "wizard"):
            with self.subTest(stage=stage):
                self.compose_file.write_text("services:\n  mcp-http:\n    image: fixture\n")
                self.log.write_text("")
                result = self.run_launcher(LAUNCHER_MUTATE_SOURCE=stage)
                self.assertNotEqual(result.returncode, 0)
                self.assertIn("changed", result.stderr)
                self.assertFalse(any(command[0] == "cp" and ":/setup-output/" in command[1] for command in self.commands()))
                if stage == "stage":
                    self.assertFalse(any("--name" in command for command in self.commands()))

    def test_preserves_only_two_effective_web_destinations_and_rejects_multiple_servers(self):
        result = self.run_launcher(LAUNCHER_WEB_COUNT="1")
        self.assertEqual(result.returncode, 2, result.stderr)
        command = self.run_command()
        self.assertIn("NETRATEL_SETUP_EXISTING_WEB_API_BACKEND_URL=http://api-private:9440/custom-api/", command)
        self.assertIn("NETRATEL_SETUP_EXISTING_WEB_MCP_BACKEND_URL=http://relay-private:9550/custom-mcp/", command)
        environment_probes = [call for call in self.commands() if call[0] == "inspect" and ".Config.Env" in call[2]]
        self.assertEqual(len(environment_probes), 1)
        self.assertIn('eq $key "apibaseurl"', environment_probes[0][2])
        self.assertIn('eq $key "webgateway__mcp__backendurl"', environment_probes[0][2])
        self.assertIn('printf "%.4097s"', environment_probes[0][2])
        self.assertNotIn("api-private", result.stdout + result.stderr)
        self.assertNotIn("relay-private", result.stdout + result.stderr)
        self.log.write_text("")
        result = self.run_launcher(LAUNCHER_WEB_COUNT="2")
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("More than one serving Web container", result.stderr)
        self.assertFalse(any("--name" in call for call in self.commands()))

    def test_observed_web_environment_distinguishes_absent_mcp_backend_from_unavailable_metadata(self):
        result = self.run_launcher(LAUNCHER_WEB_COUNT="1", LAUNCHER_WEB_MCP_ABSENT="1")
        self.assertEqual(result.returncode, 2, result.stderr)
        command = self.run_command()
        self.assertIn("NETRATEL_SETUP_WEB_ENVIRONMENT_OBSERVED=true", command)
        self.assertIn("NETRATEL_SETUP_EXISTING_WEB_API_BACKEND_URL=http://api-private:9440/custom-api/", command)
        self.assertFalse(any(value.startswith("NETRATEL_SETUP_EXISTING_WEB_MCP_BACKEND_URL=") for value in command))


    def discovered_launcher(self, *arguments, **environment):
        return subprocess.run(["bash", str(self.launcher), *arguments], cwd=self.root,
                              env=dict(self.environment, **environment), text=True, capture_output=True, timeout=15)

    def test_discovery_selects_one_instance_and_explicit_external_ownership_exports_only(self):
        result = self.discovered_launcher()
        self.assertEqual(result.returncode, 2, result.stderr)
        command = self.run_command()
        self.assertEqual(command[command.index("--project-name") + 1], "customer-stack")
        self.assertIn("NETRATEL_SETUP_DEPLOYMENT_MANAGER=komodo", command)
        self.assertTrue(any(call[0] == "image" for call in self.commands()))
        self.assertFalse(any("up" in call for call in self.commands()))

    def test_multiple_instances_require_interactive_selection_or_exact_container(self):
        result = self.discovered_launcher(LAUNCHER_CONTAINER_COUNT="2")
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("Multiple MCP containers", result.stderr)
        self.assertFalse(any("run" in call for call in self.commands()))
        self.log.write_text("")
        result = self.discovered_launcher("--container", "serving-container-1", LAUNCHER_CONTAINER_COUNT="2")
        self.assertEqual(result.returncode, 2, result.stderr)

    def test_missing_saved_source_and_storage_fail_before_wizard(self):
        result = self.discovered_launcher(LAUNCHER_SAVED_FILES=str(self.stack / "missing.yaml"))
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("missing or unreadable", result.stderr)
        self.assertFalse(any("--name" in call for call in self.commands()))
        self.log.write_text("")
        result = self.run_launcher(LAUNCHER_STORAGE_FAIL="1")
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("not writable by UID 10001", result.stderr)
        self.assertFalse(any("--name" in call for call in self.commands()))

    def test_ordered_multifile_and_interpolation_inputs_are_preserved_in_resume_command(self):
        override = self.stack / "override.yaml"; override.write_text("services: {}\n")
        env = self.stack / "second.env"; env.write_text("OTHER=unchanged\n")
        result = self.run_launcher("--compose-file", str(override), "--env-file", str(self.env_file), "--env-file", str(env))
        self.assertEqual(result.returncode, 2, result.stderr)
        command = self.run_command()
        self.assertEqual([command[i + 1] for i, value in enumerate(command) if value == "-f"],
                         [str(self.compose_file), str(override)])
        self.assertEqual([command[i + 1] for i, value in enumerate(command) if value == "--env-file"],
                         [str(self.env_file), str(env)])
        self.assertIn("override.yaml", result.stdout)
        self.assertIn("second.env", result.stdout)

    def test_fresh_unchanged_source_verification_succeeds_and_changed_source_stops_before_tooling(self):
        self.assertEqual(self.run_launcher().returncode, 2)
        self.log.write_text("")
        verified = self.run_launcher("--verify", LAUNCHER_SETUP_STATUS="0")
        self.assertEqual(verified.returncode, 0, verified.stderr)
        self.assertIn("NETRATEL_SETUP_RUNTIME_ENVIRONMENT_SOURCE=/setup-output/.runtime-environment.json", self.run_command())
        self.assertFalse(any(call[0] == "cp" and any(call[1].endswith(":/setup-output/" + name)
                             for name in ("compose.komodo.yaml", "compose.http-host.yaml")) for call in self.commands()))
        self.compose_file.write_text(self.compose_file.read_text() + "# concurrent owner edit\n")
        self.log.write_text("")
        changed = self.run_launcher("--verify", LAUNCHER_SETUP_STATUS="0")
        self.assertNotEqual(changed.returncode, 0)
        self.assertIn("approved sources changed", changed.stderr)
        self.assertFalse(any("run" in call for call in self.commands()))

    def test_interpolation_edit_and_missing_runtime_stop_verification_before_consent(self):
        self.assertEqual(self.run_launcher().returncode, 2)
        self.env_file.write_text("UNCHANGED=changed\n")
        self.log.write_text("")
        changed = self.run_launcher("--verify", LAUNCHER_SETUP_STATUS="0")
        self.assertNotEqual(changed.returncode, 0)
        self.assertFalse(any("--name" in call for call in self.commands()))
        self.env_file.write_text("UNCHANGED=fixture\n")
        self.log.write_text("")
        absent = self.run_launcher("--verify", LAUNCHER_CONTAINER_COUNT="0")
        self.assertNotEqual(absent.returncode, 0)
        self.assertFalse(any("--name" in call for call in self.commands()))

    def test_release_ownership_is_explicit_and_exports_reviewed_overlay_without_applying(self):
        result = self.discovered_launcher(LAUNCHER_MANAGER_METADATA="release-compose")
        self.assertEqual(result.returncode, 2, result.stderr)
        self.assertIn("NETRATEL_SETUP_DEPLOYMENT_MANAGER=release-compose", self.run_command())
        self.assertTrue((self.stack / ".netratel-http-mcp-customer-stack/compose.http-host.yaml").is_file())
        self.assertFalse(any("up" in call for call in self.commands()))
        self.assertNotIn("compose.setup.yaml", result.stdout)

    def test_public_external_manager_alias_preserves_compatibility(self):
        result = self.run_launcher("--manager", "external-compose")
        self.assertEqual(result.returncode, 2, result.stderr)
        self.assertIn("NETRATEL_SETUP_DEPLOYMENT_MANAGER=komodo", self.run_command())

    def test_image_configuration_is_private_and_bind_replacement_removes_hidden_defaults(self):
        protected = self.stack / "appsettings.json"
        protected.write_text(json.dumps({"NetRatel": {"Mcp": {"Http": {"DevApiBaseUrl": ""}}}}))
        rendered = json.dumps({"services": {"mcp-http": {"image": "fixture", "volumes": [
            {"type": "bind", "source": str(protected), "target": "/app/appsettings.json"}]}}})
        image = json.dumps({"NetRatel": {"Mcp": {"Http": {"DevApiBaseUrl": "https://private-image-default.invalid"}}}})
        result = self.run_launcher(LAUNCHER_RESOLVED_JSON=rendered, LAUNCHER_IMAGE_APPSETTINGS=image)
        self.assertEqual(result.returncode, 2, result.stderr)
        snapshot = json.loads((self.stack / ".netratel-http-mcp-customer-stack/.effective-deployment.json").read_text())
        self.assertEqual(snapshot["x-netratel-protected-configuration"]["mcp-http"]["NetRatel:Mcp:Http:DevApiBaseUrl"], "")
        self.assertNotIn("private-image-default", result.stdout + result.stderr)
        protected.write_text(json.dumps({"NetRatel": {"Mcp": {"Http": {"DevApiBaseUrl": "https://changed.invalid"}}}}))
        self.log.write_text("")
        resumed = self.run_launcher("--verify", LAUNCHER_RESOLVED_JSON=rendered, LAUNCHER_IMAGE_APPSETTINGS=image)
        self.assertNotEqual(resumed.returncode, 0)
        self.assertIn("approved sources changed", resumed.stderr)
        self.assertFalse(any("--name" in call for call in self.commands()))

    def test_saved_service_env_file_is_hashed_separately_from_interpolation_inputs(self):
        service_env = self.stack / "mcp.env"; service_env.write_text("OPTION=original\n")
        rendered = json.dumps({"services": {"mcp-http": {"image": "fixture", "env_file": [
            {"path": str(service_env), "required": True}]}}})
        result = self.run_launcher(LAUNCHER_RESOLVED_JSON=rendered)
        self.assertEqual(result.returncode, 2, result.stderr)
        state = json.loads((self.stack / ".netratel-http-mcp-customer-stack/.approved-source-state.json").read_text())
        self.assertIn(str(service_env), [source["path"] for source in state["sources"]])
        export_source = next(call for call in self.commands() if call[0] == "compose" and "config" in call
                             and "--no-interpolate" in call)
        self.assertIn("--no-env-resolution", export_source)
        service_env.write_text("OPTION=changed\n")
        self.log.write_text("")
        resumed = self.run_launcher("--verify", LAUNCHER_RESOLVED_JSON=rendered)
        self.assertNotEqual(resumed.returncode, 0)
        self.assertFalse(any("--name" in call for call in self.commands()))

    def test_release_export_failure_cannot_republish_a_prior_overlay_or_approve_current_sources(self):
        output = self.stack / ".netratel-http-mcp-customer-stack"
        output.mkdir(mode=0o700)
        overlay = output / "compose.http-host.yaml"; overlay.write_text("prior reviewed overlay")
        state = output / ".approved-source-state.json"; state.write_text('{"sources": []}')
        state.chmod(0o600)
        for option in ("LAUNCHER_RECEIPT_MISSING", "LAUNCHER_RECEIPT_MISMATCH"):
            with self.subTest(option=option):
                self.log.write_text("")
                result = self.discovered_launcher(LAUNCHER_MANAGER_METADATA="release-compose", **{option: "1"})
                self.assertNotEqual(result.returncode, 0)
                self.assertIn("retained", result.stderr)
                self.assertEqual(overlay.read_text(), "prior reviewed overlay")
                self.assertEqual(state.read_text(), '{"sources": []}')
                self.assertNotIn("up -d", result.stdout)

    def test_printed_release_apply_checks_both_approved_sources_and_exported_overlay(self):
        result = self.discovered_launcher(LAUNCHER_MANAGER_METADATA="release-compose")
        self.assertEqual(result.returncode, 2, result.stderr)
        command = next(value.split("=", 1)[1] for value in self.run_command()
                       if value.startswith("NETRATEL_SETUP_HOST_APPLY_COMMAND="))
        for mutation in ("source", "overlay"):
            with self.subTest(mutation=mutation):
                path = self.compose_file if mutation == "source" else self.stack / ".netratel-http-mcp-customer-stack/compose.http-host.yaml"
                original = path.read_bytes()
                path.write_bytes(original + b"# concurrent edit\n")
                self.log.write_text("")
                guarded = subprocess.run(["bash", "-c", command], cwd=self.root, env=self.environment,
                                         text=True, capture_output=True, timeout=5)
                self.assertNotEqual(guarded.returncode, 0)
                self.assertIn("approved sources changed", guarded.stderr)
                self.assertFalse(any("up" in call for call in self.commands()))
                path.write_bytes(original)

    def test_printed_release_apply_retains_selected_plugin_and_docker_connection_in_a_fresh_shell(self):
        plugin = self.root / "reviewed compose plugin"
        observed = self.root / "apply docker environment.json"
        variables = ("DOCKER_CONFIG", "DOCKER_CONTEXT", "DOCKER_HOST", "DOCKER_TLS", "DOCKER_TLS_VERIFY",
                     "DOCKER_CERT_PATH", "DOCKER_API_VERSION")
        plugin.write_text("#!/usr/bin/env python3\nimport json, os, pathlib, sys\n"
                          "if 'up' in sys.argv:\n"
                          f"    pathlib.Path({str(observed)!r}).write_text(json.dumps({{key: os.environ.get(key) for key in {variables!r}}}))\n"
                          "os.execvp('docker', ['docker', 'compose', *sys.argv[1:]])\n")
        plugin.chmod(0o700)
        config = self.root / "reviewed docker config"
        config.mkdir()
        (config / "config.json").write_text('{"auths":{"registry.example.invalid":{}},"currentContext":"reviewed-context"}')
        original = (config / "config.json").read_bytes()
        selection = dict(DOCKER_CONFIG=str(config), DOCKER_CONTEXT="reviewed-context",
                         DOCKER_HOST="tcp://docker.example.invalid:2376", DOCKER_TLS="1", DOCKER_TLS_VERIFY="1",
                         DOCKER_CERT_PATH=str(self.root / "reviewed cert directory"), DOCKER_API_VERSION="1.48")
        result = self.discovered_launcher("--compose-plugin", str(plugin),
                                           LAUNCHER_MANAGER_METADATA="release-compose", **selection)
        self.assertEqual(result.returncode, 2, result.stderr)
        command = next(value.split("=", 1)[1] for value in self.run_command()
                       if value.startswith("NETRATEL_SETUP_HOST_APPLY_COMMAND="))
        apply = shlex.split(command.rsplit(" && ", 1)[1])
        self.assertEqual(apply[0], "env")
        for key, value in selection.items(): self.assertIn(key + "=" + value, apply)
        self.assertIn(str(plugin), apply)
        self.log.write_text("")
        fresh_environment = {key: value for key, value in self.environment.items() if key not in selection}
        applied = subprocess.run(["bash", "-c", command], cwd=self.root, env=fresh_environment,
                                 text=True, capture_output=True, timeout=5)
        self.assertEqual(applied.returncode, 0, applied.stderr)
        self.assertEqual(json.loads(observed.read_text()), selection)
        self.assertTrue(any("up" in call for call in self.commands()))
        self.assertEqual((config / "config.json").read_bytes(), original)

    def test_missing_ownership_requires_one_explicit_manager_choice_before_preflight_or_setup(self):
        result = self.discovered_launcher(LAUNCHER_MANAGER_METADATA="")
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("deployment_manager_unknown", result.stderr)
        self.assertIn("--manager external-compose", result.stderr)
        self.assertFalse(any(call[0] == "compose" and "run" in call for call in self.commands()))
        self.log.write_text("")
        explicit = self.discovered_launcher("--manager", "external-compose", LAUNCHER_MANAGER_METADATA="")
        self.assertEqual(explicit.returncode, 2, explicit.stderr)
        self.assertIn("NETRATEL_SETUP_DEPLOYMENT_MANAGER=komodo", self.run_command())

    def test_reviewed_owner_is_reused_by_a_fresh_plain_verify_without_a_label(self):
        initial = self.discovered_launcher("--manager", "external-compose", LAUNCHER_MANAGER_METADATA="")
        self.assertEqual(initial.returncode, 2, initial.stderr)
        self.log.write_text("")
        verified = self.discovered_launcher("--verify", LAUNCHER_MANAGER_METADATA="", LAUNCHER_SETUP_STATUS="0")
        self.assertEqual(verified.returncode, 0, verified.stderr)
        self.assertNotIn("Deployment ownership (1 or 2)", verified.stdout + verified.stderr)
        self.assertIn("--verify", self.run_command())
        self.assertIn("NETRATEL_SETUP_DEPLOYMENT_MANAGER=komodo", self.run_command())
        state = self.stack / ".netratel-http-mcp-customer-stack/.approved-source-state.json"
        self.assertEqual(stat.S_IMODE(state.stat().st_mode), 0o600)
        saved = json.loads(state.read_text())
        self.assertEqual(saved["manager"], "komodo")
        self.assertEqual(saved["binding"]["project"], "customer-stack")
        self.assertEqual(saved["binding"]["composeFiles"], [str(self.compose_file)])
        self.assertEqual(len(saved["generationHash"]), 64)

    def test_remembered_owner_survives_recreation_and_exact_reviewed_import(self):
        initial = self.discovered_launcher("--manager", "external-compose", LAUNCHER_MANAGER_METADATA="")
        self.assertEqual(initial.returncode, 2, initial.stderr)
        output = self.stack / ".netratel-http-mcp-customer-stack"
        self.compose_file.write_bytes((output / "compose.komodo.yaml").read_bytes())
        self.log.write_text("")
        verified = self.discovered_launcher("--container", "recreated-container", "--verify",
                                            LAUNCHER_MANAGER_METADATA="", LAUNCHER_SETUP_STATUS="0")
        self.assertEqual(verified.returncode, 0, verified.stderr)
        self.assertIn("--verify", self.run_command())

    def test_interactive_first_selection_prompts_once_and_plain_verify_prompts_zero_times(self):
        import pty
        def terminal(*arguments, answer=b"", **environment):
            master, slave = pty.openpty()
            try:
                process = subprocess.Popen(["bash", str(self.launcher), *arguments], cwd=self.root,
                                           env=dict(self.environment, **environment), stdin=slave,
                                           stdout=slave, stderr=slave)
                os.close(slave); slave = None
                if answer: os.write(master, answer)
                try:
                    status = process.wait(timeout=15)
                except subprocess.TimeoutExpired:
                    process.kill(); process.wait()
                    raise
                chunks = []
                while True:
                    try: chunk = os.read(master, 65536)
                    except OSError: break
                    if not chunk: break
                    chunks.append(chunk)
                return status, b"".join(chunks).decode()
            finally:
                if slave is not None: os.close(slave)
                os.close(master)
        status, output = terminal(answer=b"1\n", LAUNCHER_MANAGER_METADATA="")
        self.assertEqual(status, 2, output)
        self.assertEqual(output.count("Deployment ownership (1 or 2):"), 1)
        status, output = terminal("--verify", LAUNCHER_MANAGER_METADATA="", LAUNCHER_SETUP_STATUS="0")
        self.assertEqual(status, 0, output)
        self.assertEqual(output.count("Deployment ownership (1 or 2):"), 0)

    def test_configured_and_invalid_runtime_ownership_are_actionable_before_setup(self):
        proposal = json.dumps({"services": {"mcp-http": {"image": "fixture", "environment": {
            "NETRATEL_SETUP_DEPLOYMENT_MANAGER": "release-compose"}}}})
        result = self.discovered_launcher("--manager", "external-compose", LAUNCHER_RESOLVED_JSON=proposal)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("deployment_manager_conflict", result.stderr)
        self.assertFalse(any("run" in call for call in self.commands()))
        self.log.write_text("")
        result = self.discovered_launcher(LAUNCHER_MANAGER_METADATA="", LAUNCHER_RUNTIME_MANAGER="invalid-owner")
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("deployment_manager_conflict", result.stderr)
        self.assertFalse(any("run" in call for call in self.commands()))

    def test_explicit_sources_can_resolve_the_configured_owner_without_a_container_label(self):
        proposal = json.dumps({"services": {"mcp-http": {"image": "fixture", "environment": {
            "NETRATEL_SETUP_DEPLOYMENT_MANAGER": "external-compose"}}}})
        result = self.discovered_launcher("--compose-file", str(self.compose_file), "--project", "customer-stack",
                                          LAUNCHER_RESOLVED_JSON=proposal, LAUNCHER_MANAGER_METADATA="")
        self.assertEqual(result.returncode, 2, result.stderr)
        self.assertIn("NETRATEL_SETUP_DEPLOYMENT_MANAGER=komodo", self.run_command())
        self.assertNotIn("Deployment ownership (1 or 2)", result.stdout + result.stderr)

    def test_remembered_owner_rejects_changed_deployment_generation_and_sources(self):
        self.assertEqual(self.discovered_launcher("--manager", "external-compose", LAUNCHER_MANAGER_METADATA="").returncode, 2)
        for change in ({"LAUNCHER_DAEMON_ID": "another-daemon"},
                       {"LAUNCHER_GENERATION": '{"DeploymentManager":"komodo","PublicOrigin":"https://other.invalid"}'},
                       {"LAUNCHER_GENERATION_MISSING": "1"},
                       {"LAUNCHER_PROJECT": "another-project"}):
            with self.subTest(change=next(iter(change))):
                self.log.write_text("")
                result = self.discovered_launcher("--verify", LAUNCHER_MANAGER_METADATA="", **change)
                self.assertNotEqual(result.returncode, 0)
                self.assertIn("deployment_manager_", result.stderr)
                self.assertFalse(any("run" in call for call in self.commands()))
        self.compose_file.write_text(self.compose_file.read_text() + "# changed\n")
        changed = self.discovered_launcher("--verify", LAUNCHER_MANAGER_METADATA="")
        self.assertNotEqual(changed.returncode, 0)
        self.assertIn("approved sources changed", changed.stderr)

    def test_owner_metadata_conflict_and_corrupt_state_stop_without_setup(self):
        self.assertEqual(self.discovered_launcher("--manager", "external-compose", LAUNCHER_MANAGER_METADATA="").returncode, 2)
        for metadata in ({"LAUNCHER_MANAGER_METADATA": "release-compose"},
                         {"LAUNCHER_MANAGER_METADATA": "external-compose", "LAUNCHER_RUNTIME_MANAGER": "release-compose"}):
            with self.subTest(metadata=metadata):
                self.log.write_text("")
                result = self.discovered_launcher("--verify", **metadata)
                self.assertNotEqual(result.returncode, 0)
                self.assertIn("deployment_manager_conflict", result.stderr)
                self.assertFalse(any("run" in call for call in self.commands()))
        state = self.stack / ".netratel-http-mcp-customer-stack/.approved-source-state.json"
        state.write_text("{broken")
        self.log.write_text("")
        corrupt = self.discovered_launcher("--verify", LAUNCHER_MANAGER_METADATA="")
        self.assertNotEqual(corrupt.returncode, 0)
        self.assertIn("deployment_manager_state_invalid", corrupt.stderr)
        self.assertFalse(any("run" in call for call in self.commands()))
        state.unlink()
        absent = self.discovered_launcher("--verify", LAUNCHER_MANAGER_METADATA="")
        self.assertNotEqual(absent.returncode, 0)
        self.assertIn("deployment_manager_unknown", absent.stderr)

    def test_reviewed_release_apply_overlay_is_the_only_allowed_source_addition(self):
        generation = json.dumps({"DeploymentManager": "release-compose", "PublicOrigin": "https://web.example.invalid"})
        initial = self.discovered_launcher(LAUNCHER_MANAGER_METADATA="release-compose", LAUNCHER_GENERATION=generation)
        self.assertEqual(initial.returncode, 2, initial.stderr)
        overlay = self.stack / ".netratel-http-mcp-customer-stack/compose.http-host.yaml"
        verified = self.discovered_launcher("--verify", LAUNCHER_MANAGER_METADATA="", LAUNCHER_GENERATION=generation,
                                            LAUNCHER_SAVED_FILES=str(self.compose_file) + "," + str(overlay),
                                            LAUNCHER_SETUP_STATUS="0")
        self.assertEqual(verified.returncode, 0, verified.stderr)
        overlay.write_text(overlay.read_text() + "# unreviewed\n")
        self.log.write_text("")
        changed = self.discovered_launcher("--verify", LAUNCHER_MANAGER_METADATA="", LAUNCHER_GENERATION=generation,
                                           LAUNCHER_SAVED_FILES=str(self.compose_file) + "," + str(overlay))
        self.assertNotEqual(changed.returncode, 0)
        self.assertIn("deployment_manager_state_stale", changed.stderr)
        self.assertFalse(any("run" in call for call in self.commands()))

    def test_multiple_projects_keep_independent_reviewed_ownership_records(self):
        for project in ("customer-stack", "another-project"):
            with self.subTest(project=project):
                initial = self.discovered_launcher("--manager", "external-compose", LAUNCHER_MANAGER_METADATA="", LAUNCHER_PROJECT=project)
                self.assertEqual(initial.returncode, 2, initial.stderr)
                saved = json.loads((self.stack / (".netratel-http-mcp-" + project) / ".approved-source-state.json").read_text())
                self.assertEqual(saved["binding"]["project"], project)
        for project in ("another-project", "customer-stack"):
            with self.subTest(verify=project):
                verified = self.discovered_launcher("--verify", LAUNCHER_MANAGER_METADATA="", LAUNCHER_PROJECT=project, LAUNCHER_SETUP_STATUS="0")
                self.assertEqual(verified.returncode, 0, verified.stderr)

    def test_highest_protected_http_array_provider_replaces_lower_entries_including_empty_arrays(self):
        original = {"NetRatel": {"Mcp": {"Http": {"RequiredGroups": ["lower-one", "lower-two"],
                    "RequiredScopes": ["lower-scope"], "AllowedOrigins": ["https://lower.invalid"]}}}}
        for replacement in ([], ["reviewed"]):
            with self.subTest(replacement=replacement):
                higher = {"NetRatel": {"Mcp": {"Http": {"RequiredGroups": replacement, "RequiredScopes": [], "AllowedOrigins": []}}}}
                result = self.run_launcher(LAUNCHER_IMAGE_APPSETTINGS=json.dumps(original),
                                           LAUNCHER_IMAGE_ENV_APPSETTINGS=json.dumps(higher))
                self.assertEqual(result.returncode, 2, result.stderr)
                snapshot = json.loads((self.stack / ".netratel-http-mcp-customer-stack/.effective-deployment.json").read_text())
                protected = snapshot["x-netratel-protected-configuration"]["mcp-http"]
                selected = {key: value for key, value in protected.items() if key.startswith("NetRatel:Mcp:Http:RequiredGroups")}
                expected = {"NetRatel:Mcp:Http:RequiredGroups:0": "reviewed"} if replacement else {"NetRatel:Mcp:Http:RequiredGroups": ""}
                self.assertEqual(selected, expected)
                self.assertEqual(protected["NetRatel:Mcp:Http:RequiredScopes"], "")
                self.assertEqual(protected["NetRatel:Mcp:Http:AllowedOrigins"], "")

    def test_docker_inspect_outer_newline_preserves_image_and_runtime_records(self):
        result = self.run_launcher()
        self.assertEqual(result.returncode, 2, result.stderr)
        output = self.stack / ".netratel-http-mcp-customer-stack"
        source = json.loads((output / ".effective-deployment.json").read_text())
        self.assertEqual(source["x-netratel-image-environment"]["mcp-http"]["DOTNET_RUNNING_IN_CONTAINER"], "true")
        self.assertEqual(source["x-netratel-image-environment"]["mcp-http"]["ASPNETCORE_HTTP_PORTS"], "8080")
        resumed = self.run_launcher("--verify", LAUNCHER_SETUP_STATUS="0")
        self.assertEqual(resumed.returncode, 0, resumed.stderr)
        runtime = json.loads((output / ".runtime-environment.json").read_text())
        self.assertEqual(runtime["mcp-http"]["NetRatel__Mcp__Http__Instance"], "prod")

    def test_image_environment_json_preserves_multiline_values_without_injecting_settings(self):
        value = "Operators\nNetRatel__Mcp__Http__LocalCredentialMode=false"
        records = json.dumps(["NetRatel__Mcp__Http__RequiredGroups__0=" + value])
        result = self.run_launcher(LAUNCHER_IMAGE_ENV_RECORDS=records)
        self.assertEqual(result.returncode, 2, result.stderr)
        source = json.loads((self.stack / ".netratel-http-mcp-customer-stack/.effective-deployment.json").read_text())
        image = source["x-netratel-image-environment"]["mcp-http"]
        self.assertEqual(image["NetRatel__Mcp__Http__RequiredGroups__0"], value)
        self.assertNotIn("NetRatel__Mcp__Http__LocalCredentialMode", image)
        self.assertNotIn("Operators", result.stdout + result.stderr)

    def test_equal_recognized_aliases_coalesce_in_image_runtime_and_compose_layers(self):
        records = json.dumps(["NETRATEL_KEYS_DIR=/synthetic/keys", "NetRatel_KEYS_DIR=/synthetic/keys",
                              "NETRATEL_HTTP_PORT=9111", "NetRatel_HTTP_PORT=9111"])
        rendered = json.dumps({"services": {"web": {"image": "fixture", "environment": {
            "NETRATEL_KEYS_DIR": "/higher/keys", "NetRatel_KEYS_DIR": "/higher/keys"}},
            "mcp-http": {"image": "fixture"}}})
        result = self.run_launcher(LAUNCHER_IMAGE_ENV_RECORDS=records, LAUNCHER_RESOLVED_JSON=rendered)
        self.assertEqual(result.returncode, 2, result.stderr)
        output = self.stack / ".netratel-http-mcp-customer-stack"
        source = json.loads((output / ".effective-deployment.json").read_text())
        self.assertEqual(source["x-netratel-image-environment"]["web"]["NetRatel_KEYS_DIR"], "/synthetic/keys")
        self.assertEqual(source["services"]["web"]["environment"], {"NETRATEL_KEYS_DIR": "/higher/keys", "NetRatel_KEYS_DIR": "/higher/keys"})
        self.assertIn("NetRatel_HTTP_PORT", source["x-netratel-image-environment"]["web"])
        self.assertNotIn("NETRATEL_HTTP_PORT", source["x-netratel-image-environment"]["web"])
        verified = self.run_launcher("--verify", LAUNCHER_SETUP_STATUS="0", LAUNCHER_IMAGE_ENV_RECORDS=records,
                                     LAUNCHER_RUNTIME_ENV_RECORDS=records, LAUNCHER_RESOLVED_JSON=rendered)
        self.assertEqual(verified.returncode, 0, verified.stderr)
        runtime = json.loads((output / ".runtime-environment.json").read_text())
        self.assertEqual(runtime["web"]["NetRatel_KEYS_DIR"], "/synthetic/keys")

    def test_conflicting_aliases_have_redacted_service_entry_provenance_before_approval(self):
        result = self.run_launcher(LAUNCHER_IMAGE_ENV_RECORDS=json.dumps([
            "NETRATEL_KEYS_DIR=/private-first", "NetRatel_KEYS_DIR=/private-second"]))
        self.assertNotEqual(result.returncode, 0)
        for expected in ("environment_alias_conflict:", "mcp-http", "netratel_keys_dir", "NETRATEL_KEYS_DIR",
                         "NetRatel_KEYS_DIR", "image environment entry 1", "image environment entry 2", "remove the unintended alias"):
            self.assertIn(expected, result.stderr)
        for private in ("/private-first", "/private-second"):
            self.assertNotIn(private, result.stdout + result.stderr)
        self.assertFalse(any("--name" in call for call in self.commands()))

    def test_compose_alias_null_and_empty_are_distinct(self):
        rendered = json.dumps({"services": {"mcp-http": {"image": "fixture", "environment": {
            "NETRATEL_KEYS_DIR": None, "NetRatel_KEYS_DIR": ""}}}})
        result = self.run_launcher(LAUNCHER_RESOLVED_JSON=rendered)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("environment_alias_conflict:", result.stderr)
        self.assertIn("Compose environment entry", result.stderr)
        self.assertFalse(any("--name" in call for call in self.commands()))

    def test_serving_web_backend_aliases_coalesce_and_conflicts_have_safe_entry_locations(self):
        equal = ["ApiBaseUrl=http://backend.invalid/api/", "apibaseurl=http://backend.invalid/api/",
                 "WebGateway__Mcp__BackendUrl=http://backend.invalid/mcp/", "webgateway:mcp:backendurl=http://backend.invalid/mcp/"]
        result = self.run_launcher(LAUNCHER_WEB_COUNT="1", LAUNCHER_WEB_BACKEND_RECORDS=json.dumps(equal))
        self.assertEqual(result.returncode, 2, result.stderr)
        self.assertIn("NETRATEL_SETUP_EXISTING_WEB_API_BACKEND_URL=http://backend.invalid/api/", self.run_command())
        self.log.write_text("")
        conflicting = equal[:2] + ["APIBASEURL=http://different.invalid/"]
        result = self.run_launcher(LAUNCHER_WEB_COUNT="1", LAUNCHER_WEB_BACKEND_RECORDS=json.dumps(conflicting))
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(result.stderr.count("environment_alias_conflict:"), 1)
        self.assertIn("service web", result.stderr)
        self.assertIn("serving runtime environment entry 1", result.stderr)
        self.assertIn("serving runtime environment entry 3", result.stderr)
        self.assertNotIn("backend.invalid", result.stderr)
        self.assertNotIn("different.invalid", result.stderr)
        self.assertFalse(any("--name" in call for call in self.commands()))

    def test_protected_aliases_keep_provider_precedence_and_raw_json_requires_unique_keys(self):
        lower = json.dumps({"NetRatel_KEYS_DIR": "/lower/keys"})
        higher = json.dumps({"NETRATEL_KEYS_DIR": "/higher/keys"})
        result = self.run_launcher(LAUNCHER_IMAGE_APPSETTINGS=lower, LAUNCHER_IMAGE_ENV_APPSETTINGS=higher)
        self.assertEqual(result.returncode, 2, result.stderr)
        source = json.loads((self.stack / ".netratel-http-mcp-customer-stack/.effective-deployment.json").read_text())
        self.assertEqual(source["x-netratel-protected-configuration"]["mcp-http"]["NetRatel_KEYS_DIR"], "/higher/keys")
        for second in ("/first", "/second"):
            with self.subTest(second=second):
                conflict = self.run_launcher(LAUNCHER_IMAGE_APPSETTINGS=json.dumps({"NetRatel_KEYS_DIR": "/first", "NETRATEL_KEYS_DIR": second}))
                self.assertNotEqual(conflict.returncode, 0)
                self.assertIn("protected_configuration_duplicate:", conflict.stderr)
                self.assertIn("protected provider 1 entry", conflict.stderr)
                self.assertIn("Remove the repeated property", conflict.stderr)
                self.assertNotIn("/first", conflict.stdout + conflict.stderr)

    def test_arbitrary_inspected_linux_names_are_case_sensitive(self):
        result = self.run_launcher(LAUNCHER_IMAGE_ENV_RECORDS=json.dumps([
            "DOTNET_CUSTOM=upper", "dotnet_custom=lower", "NETRATEL_CUSTOM=first", "NetRatel_CUSTOM=second"]))
        self.assertEqual(result.returncode, 2, result.stderr)
        source = json.loads((self.stack / ".netratel-http-mcp-customer-stack/.effective-deployment.json").read_text())
        self.assertEqual(len(source["x-netratel-image-environment"]["mcp-http"]), 4)

    def test_recognized_m2m_client_credential_aliases_coalesce_and_conflicts_are_redacted(self):
        secret = "synthetic-m2m-credential-do-not-print"
        records = ["M2MClients__worker__Secret=" + secret, "m2mclients__worker__Secret=" + secret]
        equal = self.run_launcher(LAUNCHER_IMAGE_ENV_RECORDS=json.dumps(records))
        self.assertEqual(equal.returncode, 2, equal.stderr)
        self.assertNotIn(secret, equal.stdout + equal.stderr)
        records[1] += "-different"
        self.log.write_text("")
        conflicting = self.run_launcher(LAUNCHER_IMAGE_ENV_RECORDS=json.dumps(records))
        self.assertNotEqual(conflicting.returncode, 2)
        self.assertIn("environment_alias_conflict:", conflicting.stderr)
        self.assertIn("m2mclients__worker__secret", conflicting.stderr)
        self.assertNotIn(secret, conflicting.stdout + conflicting.stderr)
        self.assertFalse(any("--name" in call for call in self.commands()))

    def test_protected_m2m_client_provider_is_retained_and_later_provider_wins(self):
        lower = json.dumps({"M2MClients": {"worker": {"Secret": "synthetic-lower-secret", "Audiences": ["retained-audience"]}}})
        higher = json.dumps({"m2mclients": {"worker": {"Secret": "synthetic-higher-secret"}}})
        result = self.run_launcher(LAUNCHER_IMAGE_APPSETTINGS=lower, LAUNCHER_IMAGE_ENV_APPSETTINGS=higher)
        self.assertEqual(result.returncode, 2, result.stderr)
        source = json.loads((self.stack / ".netratel-http-mcp-customer-stack/.effective-deployment.json").read_text())
        protected = {key.casefold(): value for key, value in source["x-netratel-protected-configuration"]["mcp-http"].items()}
        self.assertEqual(protected["m2mclients:worker:secret"], "synthetic-higher-secret")
        self.assertEqual(protected["m2mclients:worker:audiences:0"], "retained-audience")
        self.assertNotIn("synthetic-", result.stdout + result.stderr)

    def test_image_environment_json_rejects_canonical_conflicts_and_oversized_records(self):
        cases = (["NetRatel__Mcp__Http__Enabled=true", "netratel:mcp:http:enabled=false"],
                 ["DOTNET_CUSTOM=" + "x" * 4097])
        for records in cases:
            with self.subTest(records=len(records)):
                self.log.write_text("")
                result = self.run_launcher(LAUNCHER_IMAGE_ENV_RECORDS=json.dumps(records))
                self.assertNotEqual(result.returncode, 0)
                self.assertIn("environment_alias_conflict:" if len(records) == 2 else "ambiguous image settings", result.stderr)
                self.assertFalse(any("--name" in call for call in self.commands()))


if __name__ == "__main__":
    unittest.main()
