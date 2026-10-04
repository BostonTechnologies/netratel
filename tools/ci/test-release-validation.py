#!/usr/bin/env python3
"""Negative release-gate tests using isolated source fixtures."""
import base64
import importlib.util
import io
import json
import os
import re
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


class MtpCiRunnerSelectionTests(unittest.TestCase):
    def test_ci_uses_sdk10_mtp_selectors_and_requires_each_generic_test_assembly(self):
        paths = (
            ROOT / ".github/workflows/public-pr-validation.yml",
            ROOT / ".github/workflows/release-build.yml",
            ROOT / "tools/ci/smoke-oidc-compose.sh",
            ROOT / "tools/ci/smoke-postgresql-oidc-upgrade.sh",
            ROOT / "tools/ci/smoke-local-first-compose.sh",
        )
        for path in paths:
            source = path.read_text(encoding="utf-8")
            with self.subTest(path=path.name):
                self.assertNotRegex(source, r"--filter(?:\s|=)", "VSTest filter syntax is invalid under the selected MTP runner.")
                self.assertNotIn("-- --report-trx", source, "SDK 10 MTP options are passed directly without a legacy separator.")
                self.assertIn("--report-trx-filename", source)

        required_assemblies = (
            "NetRatel.API.IntegrationTests",
            "NetRatel.Tests",
            "NetRatel.Web.ComponentTests",
            "NetRatel.Web.PlaywrightTests",
        )
        for path in paths[:2]:
            source = path.read_text(encoding="utf-8")
            self.assertIn("validate_success_report", source)
            for assembly in required_assemblies:
                with self.subTest(path=path.name, assembly=assembly):
                    self.assertIn(f'"{assembly}"', source)
            self.assertIn("totals[\"executed\"] == 0", source)

        self.assertIn("selected zero test cases", (ROOT / "tools/ci/verify-mtp-trx.py").read_text())

        for path in paths[:2]:
            source = path.read_text(encoding="utf-8")
            with self.subTest(path=path.name, group="native macOS"):
                self.assertIn("--expected-executed 3", source)

    def test_generic_test_modules_are_serialized_and_browser_failure_evidence_is_collected(self):
        workflow_paths = (
            ROOT / ".github/workflows/public-pr-validation.yml",
            ROOT / ".github/workflows/release-build.yml",
        )
        for path in workflow_paths:
            source = path.read_text(encoding="utf-8")
            with self.subTest(workflow=path.name):
                run_start = source.index("dotnet test --solution NetRatel.sln --configuration Release --no-build")
                run_command = source[run_start:source.index("\n", run_start)]
                self.assertIn("--max-parallel-test-modules 1", run_command)
                self.assertIn("--filter-not-trait category=compose category=hosted", run_command)
                self.assertIn(
                    "NETRATEL_PLAYWRIGHT_ARTIFACT_ROOT: ${{ github.workspace }}/src/NetRatel/NetRatel.Web.PlaywrightTests/bin/Release/net10.0/TestResults/playwright",
                    source,
                )

        pr_workflow = workflow_paths[0].read_text(encoding="utf-8")
        browser_output_path = "src/NetRatel/NetRatel.Web.PlaywrightTests/bin/Release/net10.0/TestResults/playwright"
        self.assertIn(f"path: {browser_output_path}", pr_workflow)
        self.assertIn("NETRATEL_REVIEW_SOURCE_SHA:", pr_workflow)
        self.assertIn("NETRATEL_REVIEW_TEST_MERGE_SHA:", pr_workflow)

        release_workflow = workflow_paths[1].read_text(encoding="utf-8")
        self.assertIn("name: Run generic tests", release_workflow)
        self.assertRegex(release_workflow, r"(?s)if: always\(\).*?name: dotnet-test-results\s+path: TestResults")
        self.assertRegex(
            release_workflow,
            rf"(?s)if: always\(\).*?name: playwright-browser-evidence\s+path: {re.escape(browser_output_path)}\s+if-no-files-found: error",
        )
        self.assertIn("NETRATEL_REVIEW_SOURCE_SHA: ${{ github.sha }}", release_workflow)
        self.assertIn("NETRATEL_REVIEW_TEST_MERGE_SHA: ${{ github.sha }}", release_workflow)

        browser_test = (ROOT / "src/NetRatel/NetRatel.Web.PlaywrightTests/ClientsManagementResponsiveTests.cs").read_text(encoding="utf-8")
        self.assertIn("AbortedCriticalStartupScript_FailsAndWritesDiagnosticsBeforeContextDisposal", browser_test)
        self.assertIn('"startup-diagnostics.json"', browser_test)
        self.assertIn('"startup-failure.png"', browser_test)

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

    def run_fetch(self, *, selected_runtime=None, missing_assets=()):
        self.fetch_count += 1
        output = Path(self.directory.name) / f"published-pack-{self.fetch_count}"
        output.mkdir()
        files = {name: {"sha256": hashlib.sha256(data).hexdigest()}
                 for name, data in self.archive_bytes.items()}
        publication = {
            "productVersion": self.source_version,
            "verification": {"state": "complete"},
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
        output = self.run_fetch(selected_runtime="win-x64")
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

        for workflow in (
            ROOT / ".github/workflows/public-pr-validation.yml",
            ROOT / ".github/workflows/release-build.yml",
        ):
            with self.subTest(workflow=workflow.name):
                self.assertIn("libnss3-tools", workflow.read_text())

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

        relative = "docs/RC11_AKKA_RUNTIME_REFACTOR.md"
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
        source = (ROOT / "docs/RC11_AKKA_RUNTIME_REFACTOR.md").read_text(encoding="utf-8")
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
        inventory = (ROOT / "docs/RC11_AKKA_RUNTIME_REFACTOR.md").read_text()
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
            for name in ("compose.images.yaml", "compose.external-postgres.yaml", "compose.public-https.yaml", "nginx.public-https.conf", "compose.mcp-http.yaml", ".env.images.example", "INSTALL.md"):
                target.add(ROOT / "release" / name, arcname=name)
            manifest_info = tarfile.TarInfo("release-manifest.json")
            manifest_info.size = len(manifest)
            target.addfile(manifest_info, io.BytesIO(manifest))
            target.add(ROOT / "docs/mcp-http/local-credential-mode.md", arcname="docs/mcp-http/local-credential-mode.md")
            for name in ("LICENSE", "NOTICE"):
                target.add(ROOT / name, arcname=name)
        return archive

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
        self.assertEqual(json.loads((self.root / "publication.candidate.json").read_text())["publicCommit"], "b" * 40)
        self.promotion.complete_bundle(self.root, version, "b" * 40, images, receipt)
        self.assertEqual(json.loads((self.root / "publication.json").read_text())["verification"]["state"], "complete")

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


if __name__ == "__main__":
    unittest.main()
