#!/usr/bin/env python3
"""Negative release-gate tests using isolated source fixtures."""
import importlib.util
import io
import json
import os
import re
from pathlib import Path
import shutil
import subprocess
import tempfile
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

        native_windows_methods = (
            "GeneratedPowerShellInstallerDownloadsVerifiesAndEnrollsNativePackage",
            "GeneratedServiceInstallerRejectsAnUnownedServiceImageBeforeStoppingOrReplacingFiles",
            "GeneratedServiceInstallerRetainsIdentityWhenAgentIsDisabledAndNeverReportsReady",
            "GeneratedServiceInstallerRequiresLocalSystemGatewayAdmissionAndAcknowledgedHeartbeats",
            "GeneratedServiceInstallerPreservesAnExistingAdminDpapiCredentialWhenLocalSystemCannotReadIt",
        )
        for path in paths[:2]:
            source = path.read_text(encoding="utf-8")
            windows_methods = re.findall(
                r"-method NetRatel\.Tests\.Infrastructure\.WindowsInstallerNativeTests\.([A-Za-z0-9_]+)",
                source,
            )
            with self.subTest(path=path.name, group="native Windows"):
                self.assertEqual(native_windows_methods, tuple(windows_methods))
                self.assertIn("--expected-executed 7", source)
            with self.subTest(path=path.name, group="native macOS"):
                self.assertIn("--expected-executed 3", source)

    def test_windows_hosted_prerequisites_are_a_required_sanitized_qualification(self):
        script_path = ROOT / "tools/ci/smoke-windows-hosted-prerequisites.ps1"
        script = script_path.read_text(encoding="utf-8")
        workflow_paths = (
            ROOT / ".github/workflows/public-pr-validation.yml",
            ROOT / ".github/workflows/release-build.yml",
        )
        for workflow_path in workflow_paths:
            source = workflow_path.read_text(encoding="utf-8")
            with self.subTest(workflow=workflow_path.name):
                self.assertIn("windows-hosted-prerequisites:", source)
                match = re.search(
                    r"(?ms)^  windows-hosted-prerequisites:\n(?P<body>.*?)(?=^  [A-Za-z0-9_-]+:\n|\Z)",
                    source,
                )
                self.assertIsNotNone(match, "The prerequisite job must be a complete top-level workflow job.")
                job = match.group("body")
                self.assertIn("runs-on: windows-11-arm", job)
                self.assertIn("tools/ci/smoke-windows-hosted-prerequisites.ps1", job)
                self.assertIn("name: windows-hosted-prerequisites", job)
                self.assertIn("NETRATEL_REVIEW_SOURCE_SHA:", job)
                self.assertIn("NETRATEL_REVIEW_TEST_MERGE_SHA: ${{ github.sha }}", job)
                if workflow_path.name == "public-pr-validation.yml":
                    self.assertIn("NETRATEL_REVIEW_SOURCE_SHA: ${{ github.event.pull_request.head.sha || github.sha }}", job)
                else:
                    self.assertIn("NETRATEL_REVIEW_SOURCE_SHA: ${{ github.sha }}", job)
                self.assertIn("integrated acceptance was not run", script)
                self.assertRegex(source, r"(?m)^    needs: .*windows-hosted-prerequisites")
                self.assertIn("WINDOWS_HOSTED_PREREQUISITES_RESULT: ${{ needs.windows-hosted-prerequisites.result }}", source)
                self.assertIn('[[ "$WINDOWS_HOSTED_PREREQUISITES_RESULT" == success ]]', source)

        pinned_assets = (
            (
                "https://get.enterprisedb.com/postgresql/postgresql-17.11-4-windows-x64-binaries.zip",
                "b9424ee7bc60b52450ff910a3630225df32e633f3cb29c1d126d9299d59aea28",
                "$postgresArchive",
            ),
            (
                "https://github.com/traefik/traefik/releases/download/v3.7.13/traefik_v3.7.13_windows_arm64.zip",
                "b742323dc327e4a6eed0659b52109fb180ab48bc228075790401a669a11aa8c3",
                "$traefikArchive",
            ),
        )
        for url, digest, variable in pinned_assets:
            with self.subTest(asset=url):
                self.assertIn(url, script)
                self.assertIn(digest, script)
                self.assertLess(
                    script.index(f"Assert-ArchiveSha256 -Path {variable}"),
                    script.index(f"Expand-Archive -LiteralPath {variable}"),
                )

        postgres_tools = script[
            script.index("$script:pgBin = Join-Path $script:postgresExtract") : script.index("$script:postgresVersion =")
        ]
        self.assertIn("'pgsql\\bin'", postgres_tools)
        for executable in ("initdb.exe", "postgres.exe", "pg_ctl.exe", "createdb.exe", "psql.exe"):
            with self.subTest(postgresql_executable=executable):
                self.assertIn(f"Join-Path $script:pgBin '{executable}'", postgres_tools)
        self.assertIn("Get-ChildItem -LiteralPath $traefikExtract -Filter 'traefik.exe'", postgres_tools)
        self.assertNotIn("Get-ChildItem -LiteralPath $script:postgresExtract", postgres_tools)
        self.assertIn("pgAdmin's private psql.exe", script)

        self.assertIn("NetRatel.Migrations/NetRatel.Migrations.csproj", script)
        self.assertIn("NetRatel.API.dll", script)
        self.assertIn("Cert:\\CurrentUser\\Root", script)
        self.assertIn("AllowAutoRedirect = $false", script)
        self.assertIn("UseProxy = $false", script)
        self.assertNotIn("ServerCertificateCustomValidationCallback", script)
        self.assertIn("http://127.0.0.1:$($script:apiPort)", script)
        self.assertIn("RUNNER_ENVIRONMENT -cne 'github-hosted'", script)
        self.assertIn("NETRATEL_REVIEW_SOURCE_SHA", script)
        self.assertIn("NETRATEL_REVIEW_TEST_MERGE_SHA", script)
        self.assertIn("provenance = [ordered]@", script)
        self.assertIn("failureDetails = @($script:failureDetails)", script)
        self.assertIn("$checkoutSha -cne $script:testMergeSha", script)
        self.assertIn("DataProtection__KeysDirectory", script)
        self.assertIn("Remove-Item -LiteralPath $certificate.PSPath -Force -DeleteKey", script)
        self.assertIn("$descriptor.state -ne 0", script)
        self.assertIn("postgres_stopped_status_conflicts_with_tracked_process", script)
        self.assertIn("captured_processes_stopped", script)
        self.assertIn("SetAccessRuleProtection($true, $false)", script)
        self.assertLess(script.index("Invoke-QualificationCheck 'windows_runner'"), script.index("Invoke-WebRequest -Uri"))
        self.assertIn("integratedAcceptance = 'not_run'", script)

    def test_windows_hosted_prerequisite_failure_codes_are_an_explicit_allowlist(self):
        script = (ROOT / "tools/ci/smoke-windows-hosted-prerequisites.ps1").read_text(encoding="utf-8")
        allowlist = re.search(
            r"(?ms)^(?:\[void\])?\$script:safeFailureCodes\.UnionWith\(\[string\[\]\]@\((?P<codes>.*?)^\)\)",
            script,
        )
        self.assertIsNotNone(allowlist, "Sanitized failure codes must come from a bounded explicit list.")
        codes = set(re.findall(r"'([a-z][a-z0-9_]{0,79})'", allowlist.group("codes")))
        self.assertIn("process_timeout", codes)
        self.assertNotIn("arbitrary_lowercase_message", codes)
        self.assertIn("$script:safeFailureCodes.Contains($SafeCode)", script)
        self.assertNotIn("$SafeCode -match", script)


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
        self.assertIn('claims=${claims_json}', smoke)
        self.assertIn('claims=${operator_claims_json}', smoke)
        request_helper = re.search(
            r"request_oidc_access_token\(\) \{([\s\S]*?)\n\}",
            smoke,
        )
        self.assertIsNotNone(request_helper)
        for token_variable in (
            "$access_token", "$id_token", "$refreshed_access_token", "$refreshed_id_token",
        ):
            self.assertIn(f'verify_oidc_smoke_token_claims "{token_variable}"', smoke)
            self.assertRegex(
                request_helper.group(1),
                re.escape(f'verify_oidc_smoke_token_claims "{token_variable}"')
                + r'\s+"\$claims_json".*\|\|\s*return 1',
            )
        self.assertIn("grant_type=refresh_token", smoke)


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
        powershell_cleanup = content.index("function Remove-RetiredClientSettings")
        powershell_property_start = content.index("$name -in @(", powershell_cleanup)
        powershell_property_end = content.index(")) {", powershell_property_start) + 4
        add(relative, content, "PowerShell JSON property cleanup inventory",
            powershell_property_start, powershell_property_end)
        marker(relative, content, "PowerShell service-environment cleanup inventory",
               "$retiredClientSettings = @(", "\n            foreach ($entry in $existingServiceEnvironment) {")
        first_retired = content.index("            retired = {")
        marker(relative, content, "Linux service-environment and JSON cleanup inventories",
               "            retired = {", "            retired_json_gateway = {", first_retired)
        linux_json = content.index("            retired_json_gateway = {", first_retired)
        marker(relative, content, "Linux nested JSON cleanup inventory",
               "            retired_json_gateway = {", "            def words(value):", linux_json)
        final_retired = content.rindex("            retired = {")
        marker(relative, content, "macOS service-environment and JSON cleanup inventories",
               "            retired = {", "            retired_json_gateway = {", final_retired)
        macos_json = content.index("            retired_json_gateway = {", final_retired)
        marker(relative, content, "macOS nested JSON cleanup inventory",
               "            retired_json_gateway = {", "            environment = {", macos_json)
        json_inventory_starts = [
            match.start()
            for match in re.finditer(r"(?m)^ {12}retired_json_gateway = \{", content)
        ]
        if len(json_inventory_starts) != 3:
            raise AssertionError("The generated Linux, Windows, and macOS installers must each have one JSON cleanup inventory.")
        windows_json_start = json_inventory_starts[1]
        windows_json_end = content.index("\n            }", windows_json_start) + len("\n            }")
        add(relative, content, "Windows installer nested JSON cleanup inventory",
            windows_json_start, windows_json_end)
        inline_json_retired = content.index("                retired = {\"requiredpresenceauthority\"}")
        inline_end = content.index("\n", inline_json_retired)
        add(relative, content, "Linux nested JSON cleanup value", inline_json_retired, inline_end)

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

        relative = "src/NetRatel/NetRatel.Tests/API/AgentUpdateScriptSeedServiceTests.cs"
        content = (ROOT / relative).read_text(encoding="utf-8")
        member(relative, content, "seeded Linux updater uses shared verified installer and protected handoff",
               "public async Task Seeded_Linux_Update_UsesSharedVerifiedInstallerAndProtectedDetachedHandoff(")
        member(relative, content, "seeded Linux worker rejects a changed service process identity",
               "public async Task Seeded_Linux_WorkerRejectsChangedServiceProcessIdentity(")
        member(relative, content, "seeded Linux updater asserts retired selectors are absent",
               "private static void AssertNoRetiredLinuxSelectors(string script)")
        fixture_start = content.index("private static async Task<LinuxSeedFixture> CreateLinuxSeedFixtureAsync(")
        fixture_settings_start = content.index(
            'await File.WriteAllTextAsync(Path.Combine(installedVersion, "clientsettings.json"), """',
            fixture_start,
        )
        fixture_settings_end = content.index('\n            """);', fixture_settings_start)
        add(relative, content, "seeded Linux fixture legacy client-settings input",
            fixture_settings_start, fixture_settings_end)

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
        member(relative, content, "Linux installer retires stale values while preserving ordinary settings",
               "public async Task Build_Bash_InstallsAnExactArtifactAtomically_AndStartsTheNewUnit(")
        member(relative, content, "macOS installer retires stale values while preserving ordinary settings",
               "public async Task Build_MacOS_Service_Preserves_Only_ExplicitSplitGateway(")
        member(relative, content, "generated installer has no retired client defaults",
               "private static void AssertNoRetiredClientDefaults(")

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
        ps_function = template[template.index("function Remove-RetiredClientSettings"):template.index("$stageClientSettingsPath =")]
        property_list = ps_function.split("$name -in @(", 1)[1].split(")) {", 1)[0]
        property_names = {value.lower() for value in re.findall(r"'([^']+)'", property_list)}
        self.assertEqual(expected_properties, property_names)
        self.assertIn("$settings.Transport.PSObject.Properties.Remove('Mode')", ps_function)

        ps_environment = set_body(template, "$retiredClientSettings = @(", r"\)")
        self.assertEqual(expected_environment, ps_environment)

        retired_lists = [match.start() for match in re.finditer(r"(?m)^ {12}retired = \{", template)]
        self.assertEqual(2, len(retired_lists), "Linux and macOS must each have one explicit environment retirement inventory.")
        for start in retired_lists:
            values = set_body(template, "retired = {", r"(?m)^ {12}\}", start)
            self.assertEqual(expected_environment, values)
        json_lists = [match.start() for match in re.finditer(r"(?m)^ {12}retired_json_gateway = \{", template)]
        self.assertEqual(3, len(json_lists), "Windows, Linux, and macOS each have one exact JSON-property retirement inventory.")
        for start in json_lists:
            values = set_body(template, "retired_json_gateway = {", r"(?m)^ {12}\}", start)
            self.assertEqual(expected_properties, values)

        api_seed_path = ROOT / "src/NetRatel/NetRatel.API/Services/AgentUpdateScriptSeedService.cs"
        api_seed = api_seed_path.read_text(encoding="utf-8")
        seed_start, seed_end = self._csharp_member_region(
            api_seed, "private static SeedScript BuildLinuxScript()")
        linux_seed_builder = api_seed[seed_start:seed_end]
        self.assertEqual(1, linux_seed_builder.count(
            "new NetRatel.Infrastructure.Artifacts.ScriptTemplateService().Build("))
        self.assertIn("new NetRatel.Application.Artifacts.DeploymentScriptTemplateRequest(", linux_seed_builder)
        self.assertIn('"linux-x64"', linux_seed_builder)
        self.assertIn("InstallAsService: true", linux_seed_builder)
        self.assertIn("LinuxScriptManifest + Environment.NewLine + content", linux_seed_builder)
        self.assertNotIn("retired = {", linux_seed_builder)
        self.assertNotIn("retired_json_gateway", linux_seed_builder)
        self.assertNotIn("Remove-RetiredClientSettings", linux_seed_builder)

        seed_test_path = ROOT / "src/NetRatel/NetRatel.Tests/API/AgentUpdateScriptSeedServiceTests.cs"
        seed_tests = seed_test_path.read_text(encoding="utf-8")
        update_test_start, update_test_end = self._csharp_member_region(
            seed_tests, "public async Task Seeded_Linux_Update_UsesSharedVerifiedInstallerAndProtectedDetachedHandoff(")
        update_test = seed_tests[update_test_start:update_test_end]
        self.assertIn("AssertNoRetiredLinuxSelectors(script);", update_test)
        self.assertIn("CreateLinuxSeedFixtureAsync(script)", update_test)
        worker_test_start, worker_test_end = self._csharp_member_region(
            seed_tests, "public async Task Seeded_Linux_WorkerRejectsChangedServiceProcessIdentity(")
        worker_test = seed_tests[worker_test_start:worker_test_end]
        self.assertIn("CreateLinuxSeedFixtureAsync(GetSeedScript(\"LinuxScript\"))", worker_test)
        fixture_wrapper_start, fixture_wrapper_end = self._csharp_member_region(
            seed_tests, "private static async Task<LinuxSeedFixture> CreateLinuxSeedFixtureAsync(")
        fixture_wrapper = seed_tests[fixture_wrapper_start:fixture_wrapper_end]
        self.assertIn("CreateLinuxSeedFixtureCoreAsync(root, script)", fixture_wrapper)
        fixture_method_start, fixture_method_end = self._csharp_member_region(
            seed_tests, "private static async Task<LinuxSeedFixture> CreateLinuxSeedFixtureCoreAsync(")
        fixture_method = seed_tests[fixture_method_start:fixture_method_end]
        fixture_settings_start = fixture_method.index(
            'await File.WriteAllTextAsync(Path.Combine(installedVersion, "clientsettings.json"), """')
        fixture_settings_end = fixture_method.index('\n            """);', fixture_settings_start)
        fixture_settings = fixture_method[fixture_settings_start:fixture_settings_end]
        self.assertEqual(1, fixture_settings.count('"Transport": { "Mode": "AkkaPresence" }'))

        template_build_start, template_build_end = self._csharp_member_region(
            template, "public string Build(DeploymentScriptTemplateRequest request)")
        template_build = template[template_build_start:template_build_end]
        self.assertIn('request.RuntimeId.StartsWith("linux-", StringComparison.OrdinalIgnoreCase)', template_build)
        self.assertIn("return BuildBash(request);", template_build)
        bash_start = template.index("private static string BuildBash(")
        bash_end = template.index("private static string BuildMacBash(", bash_start)
        linux_bash_template = template[bash_start:bash_end]
        preparation_start = linux_bash_template.index("var preparationBlock = request.InstallAsService")
        service_block_start = linux_bash_template.index("var serviceBlock = request.InstallAsService", preparation_start)
        preparation_source = linux_bash_template[preparation_start:service_block_start]
        self.assertEqual(1, linux_bash_template.count("var preparationBlock = request.InstallAsService"))
        self.assertEqual(1, linux_bash_template.count("{{preparationBlock}}"))
        self.assertEqual(1, preparation_source.count('preserved_client_environment="$(python3'))
        self.assertEqual(1, preparation_source.count('settings_migration="$(python3'))
        self.assertEqual(1, preparation_source.count("setting in retired"))
        self.assertEqual(1, preparation_source.count("if lowered in retired_json_gateway:"))

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
