#!/usr/bin/env python3
"""Check private campaign preparation records offline; never establish live acceptance."""

import argparse
from datetime import datetime, timedelta, timezone
import hashlib
import json
from pathlib import Path
import re
import sys
from uuid import UUID


# Receipt fields refer to existing local evidence files, relative to the register.
# Protected authentication references are labels only and are never opened.
RECEIPTS = {
    "DEPLOYMENT": ["release.publicationReceipt", "release.actualDeploymentReceipt"],
    "CONSUMER": ["consumer.selectedConnectionProof", "consumer.wireToolsListReceipt"],
    "REVIEW": ["review.actualDecisionReceipt", "review.appliedReceipt", "actors.primary.freshAuthorityReceipt"],
    "POLICY-FIXTURE": ["operational.policyFixturePlan"],
    "OWNED-ID": ["operational.predecessorPlan"],
    "SINK": ["receiver.verifiedEndToEndReceipt"],
    "MONITORING": ["operational.monitoringFixturePlan"],
    "CONNECTIVITY": ["actors.additionalPermissionTestCredential.scopeProposalReference", "actors.additionalPermissionTestCredential.reviewReceipt"],
    "SECRET-READERS": ["actors.noReveal.admissionReceipt", "actors.ordinaryApiUi.restoredBaselineReceipt", "operational.readerMatrixPlan"],
    "REVIEWER-READBACK": ["actors.ordinaryApiUi.authorizedAssignmentReadback"],
    "QA": ["operational.originalQaDisposition"],
    "TIME": ["operational.isolatedTimingMechanism"],
    "LOG-FIXTURE": ["operational.ownedLogGapMechanism"],
    "KEYRING": ["release.dataProtectionContinuityReceipt"],
    "REFRESH-COMPLETION": ["operational.serviceRefreshCompletionRelationship"],
    "FINAL-CLEANUP": ["operational.cleanupDependencyRegister"],
}
MODES = {
    "promptMessageExecutionMode": ("NATIVE-PROMPT", {"native-menu", "server-messages-in-ai-context"}),
    "protocolControlMode": ("NATIVE-PROTOCOL", {"ai-host", "live-bridge-consumer"}),
    "enduranceMode": ("ENDURANCE", {"ai-host-native", "live-bridge-consumer"}),
}
QA_CONTEXT_GATES = {
    "beta27/qa-durable-client": {"QA-CLIENT"},
    "beta27/qa-durable-job": {"QA-JOB"},
    "beta27/qa-start-preview": {"QA-CLIENT", "QA-JOB", "QA-RUNTIME"},
    "beta27/wsl-recovery": {"QA-CLIENT", "QA-RUNTIME"},
}
OS_CONTEXT_GATES = {
    "beta26-continuation/B26-linux-paging-continuation": "EXECUTORS-LINUX",
    "beta26-continuation/B26-windows-paging-continuation": "EXECUTORS-WINDOWS",
    "beta26-continuation/B26-windows-inert-stop-fallback": "EXECUTORS-WINDOWS",
}


def read_object(path):
    value = json.loads(path.read_text(encoding="utf-8-sig"))
    if not isinstance(value, dict):
        raise ValueError("Each input must be a JSON object.")
    return value


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def exact_expiry(value):
    match = re.fullmatch(r"([0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2})(?:\.([0-9]{1,7}))?(Z|[+-][0-9]{2}:[0-9]{2})", value)
    if not match:
        raise ValueError("Invalid expiry timestamp.")
    second = datetime.fromisoformat(match[1] + match[3].replace("Z", "+00:00")).astimezone(timezone.utc)
    return second, (match[2] or "").ljust(7, "0")


def inspect(register, baseline, history, mapping, paths, expected_tag, expected_source, now, validity, remaining=None,
            expected_remaining_sha256=None):
    findings, differences = [], []

    def fail(gate, field, reason):
        findings.append({"gate": gate, "field": field, "reason": reason})

    def value(field):
        current = register
        for part in field.split("."):
            if not isinstance(current, dict):
                return None
            current = current.get(part)
        return current

    def require(gate, field, receipt=False):
        found = value(field)
        if not isinstance(found, str) or not found.strip():
            fail(gate, field, "Missing nonempty preparation record.")
            return None
        if receipt:
            try:
                local = Path(found)
                if not local.is_absolute():
                    local = paths["register"].parent / local
                if not local.is_file() or local.stat().st_size == 0:
                    fail(gate, field, "Referenced local evidence file is absent or empty.")
            except (OSError, ValueError):
                fail(gate, field, "Invalid local evidence reference.")
        return found

    def expiry(gate, field):
        raw = require(gate, field)
        if raw is None:
            return
        try:
            instant = datetime.fromisoformat(raw.replace("Z", "+00:00"))
            if instant.tzinfo is None or instant <= now + timedelta(minutes=validity):
                fail(gate, field, "Expiry is absent, ambiguous, expired or too early for the declared session window.")
        except (ValueError, OverflowError):
            fail(gate, field, "Expiry must be an ISO-8601 timestamp with timezone.")

    rows = mapping.get("contexts", [])
    fields = mapping.get("baselineFieldNames", {})
    original = baseline.get("contexts", [])
    historical = history.get("contexts", [])
    ids = [row.get("contextId") for row in rows]
    if (not ids or len(ids) != mapping.get("retainedContextCount") or len(ids) != len(set(ids))
            or ids != [row.get("contextId") for row in original]
            or ids != [row.get("contextId") for row in historical]):
        fail("HISTORY", "contexts", "Retained context count, unique IDs or exact order differs from the baseline/history.")
    if digest(paths["baseline"]) != mapping.get("sourceEvidenceSha256"):
        fail("HISTORY", "sourceEvidenceSha256", "Immutable baseline hash does not match the planning map.")
    if digest(paths["history"]) != mapping.get("historyEvidenceSha256"):
        fail("HISTORY", "historyEvidenceSha256", "Immutable history hash does not match the planning map.")
    for row, old in zip(rows, original):
        planned = row.get("prospectiveRequiredGateIds")
        if not isinstance(planned, list) or any(not isinstance(gate, str) for gate in planned):
            fail("HISTORY", "prospectiveRequiredGateIds", "Every context must retain its explicit prospective gate list.")
            break
        if (row.get("origin") != old.get("origin")
                or row.get(fields.get("disposition")) != old.get("currentDisposition")
                or row.get(fields.get("invoked")) != old.get("invoked")
                or row.get(fields.get("requiredGateIds")) != old.get("requiredGateIds")
                or row.get(fields.get("receiptIds")) != old.get("actualReceiptIds")):
            fail("HISTORY", "contexts", "A retained baseline disposition, invocation, gate or receipt mapping changed.")
            break

    active_ids = {row.get("contextId") for row in rows if row.get("planningPopulation") == "outstanding"}
    if remaining is not None:
        if (not isinstance(expected_remaining_sha256, str) or not re.fullmatch(r"[0-9a-f]{64}", expected_remaining_sha256)
                or digest(paths["remaining_ledger"]) != expected_remaining_sha256):
            fail("HISTORY", "remaining-ledger-sha256", "The current owner-supplied residual ledger hash does not match the expected checkpoint.")
        residual_rows = remaining.get("remainingOriginalContexts")
        excluded_rows = remaining.get("plannedExclusions")
        if not isinstance(residual_rows, list) or not isinstance(excluded_rows, list):
            fail("HISTORY", "remaining-ledger", "The residual ledger must retain original context and planned exclusion lists.")
        else:
            residual_ids = [row.get("contextId") if isinstance(row, dict) else None for row in residual_rows]
            excluded_ids = [row.get("contextId") if isinstance(row, dict) else None for row in excluded_rows]
            valid_ids = all(isinstance(item, str) and item for item in residual_ids + excluded_ids)
            if (not valid_ids
                    or type(remaining.get("requiredContexts")) is not int or remaining["requiredContexts"] != len(ids)
                    or type(remaining.get("unresolvedOriginalContexts")) is not int
                    or remaining["unresolvedOriginalContexts"] != len(residual_ids)
                    or len(set(residual_ids)) != len(residual_ids) or len(set(excluded_ids)) != len(excluded_ids)
                    or not set(residual_ids + excluded_ids) <= set(ids)
                    or set(residual_ids) & set(excluded_ids)):
                fail("HISTORY", "remaining-ledger", "Residual count, unique original IDs, exclusions or retained universe differs from the immutable map.")
            else:
                active_ids = set(residual_ids)
        if any(row.get("completionBatch") not in mapping.get("batches", {})
               for row in rows if row.get("contextId") in active_ids):
            fail("HISTORY", "remaining-ledger", "Every remaining original must retain a known completion batch.")

    def context_gates(row):
        gates = set(row.get("prospectiveRequiredGateIds", []))
        context_id = row.get("contextId")
        if remaining is not None and context_id in QA_CONTEXT_GATES:
            # Durable registration/definition reads do not require an online runtime.
            gates.discard("QA")
            gates |= QA_CONTEXT_GATES[context_id]
            # The original QA identity is separate from either disposable executor.
            gates -= {"EXECUTORS", "DISPOSABLE-CLIENT"}
        selected_os = OS_CONTEXT_GATES.get(context_id)
        if remaining is not None and selected_os:
            # Newly supported paging retains an empty historical gate list.
            gates -= {"EXECUTORS", "DISPOSABLE-CLIENT"}
            gates.add(selected_os)
        elif gates & {"EXECUTORS", "DISPOSABLE-CLIENT"}:
            gates -= {"EXECUTORS", "DISPOSABLE-CLIENT"}
            gates |= {selected_os} if selected_os else {"EXECUTORS-LINUX", "EXECUTORS-WINDOWS"}
        return gates | {"HISTORY", "DEPLOYMENT", "CONSUMER"}

    if value("release.tag") != expected_tag or value("release.sourceSha") != expected_source:
        fail("DEPLOYMENT", "release", "Declared release tag/source does not match the expected published release.")
    for gate, fields in RECEIPTS.items():
        for field in fields:
            if remaining is not None and gate == "QA":
                continue
            require(gate, field, receipt=True)
    if remaining is not None:
        for gate, field in (("QA-CLIENT", "originalQaClientReceipt"), ("QA-JOB", "originalQaJobReceipt"),
                            ("QA-RUNTIME", "originalQaRuntimeReceipt")):
            if any(gate in context_gates(row) for row in rows if row.get("contextId") in active_ids):
                require(gate, "operational." + field, receipt=True)
    for field in ("consumer.aiConsumerNameAndVersion", "consumer.connectionReference", "consumer.bridgeArchiveSha256",
                  "actors.primary.protectedReference", "actors.noReveal.protectedReference",
                  "receiver.connectorReference", "receiver.syntheticDestinationReference", "receiver.allowedDeliveryAndCleanup"):
        gate = "CONSUMER" if field.startswith("consumer") else "SINK" if field.startswith("receiver") else "REVIEW"
        require(gate, field)
    approval_mode = value("review.approvalMode") or "independent_review"
    if approval_mode == "independent_review":
        require("REVIEW", "review.eligibleDistinctReviewerReference")
    elif approval_mode not in {"owner_direct", "owner_self_approval"}:
        fail("REVIEW", "review.approvalMode", "Use owner_direct, owner_self_approval or independent_review from the actual applied receipt.")
    expiry("REVIEW", "actors.primary.expiry")
    expiry("SECRET-READERS", "actors.noReveal.expiry")
    expiry("CONNECTIVITY", "actors.additionalPermissionTestCredential.expiry")
    try:
        original_expiry_preserved = exact_expiry(value("actors.primary.expiry")) == exact_expiry(mapping.get("protectedPrimaryExpiresAtUtc"))
    except (TypeError, ValueError, OverflowError):
        original_expiry_preserved = False
    if not original_expiry_preserved:
        fail("REVIEW", "actors.primary.expiry", "Original primary expiry must remain exactly as recorded in the protected baseline.")

    protected = {str(UUID(item)) for item in mapping.get("protectedOriginalAgentIds", [])}
    if not protected:
        fail("HISTORY", "protectedOriginalAgentIds", "Protected original identities are missing from the baseline map.")
    fixture_ids = []
    for os_name in ("windows", "linux"):
        prefix = "fixtures." + os_name + "."
        executor_gate = "EXECUTORS-" + os_name.upper()
        for field in ("independentDeviceIdentityReceipt", "runningVersionSourceAndExecutableHashReceipt"):
            require(executor_gate, prefix + field, receipt=True)
        for field in ("protectedConnectionProfileReference", "inertServiceName", "inertServiceInitialState", "cleanupOwnerAndScope"):
            require(executor_gate, prefix + field)
        for field in ("writableRoots", "allowedOperations"):
            entries = value(prefix + field)
            if not isinstance(entries, list) or not entries or any(not isinstance(item, str) or not item.strip() for item in entries):
                fail(executor_gate, prefix + field, "An explicit nonempty fixture list is required.")
        tenant = value(prefix + "tenantId")
        if type(tenant) is not int or tenant <= 0:
            fail(executor_gate, prefix + "tenantId", "A positive exact tenant ID is required.")
        try:
            agent = str(UUID(value(prefix + "agentId")))
            if UUID(agent).int == 0 or agent in protected or agent in fixture_ids:
                fail(executor_gate, prefix + "agentId", "Fixture must have its own nonzero identity, distinct from protected originals and the other fixture.")
            fixture_ids.append(agent)
        except (ValueError, TypeError, AttributeError):
            fail(executor_gate, prefix + "agentId", "A registered fixture UUID is required.")

    observed = value("consumer.consumerLoadedSchemaObserved")
    if type(observed) is not bool:
        fail("CONSUMER", "consumer.consumerLoadedSchemaObserved", "Declare whether the consumer's loaded schema is observable.")
    elif not observed:
        differences.append("Native loaded-schema cache unobserved; recorded wire schema is a different evidence boundary.")
    for field, (gate, allowed) in MODES.items():
        selected = value("consumer." + field)
        if not isinstance(selected, str) or selected not in allowed:
            fail(gate, "consumer." + field, "Choose one documented evidence mode: " + ", ".join(sorted(allowed)))
        elif selected in {"server-messages-in-ai-context", "live-bridge-consumer"}:
            differences.append(field + ": " + selected + "; not a native-menu/cache or AI-host-native claim.")
    if differences:
        require("CONSUMER", "consumer.explicitScopeAmendmentDecisionReference", receipt=True)

    failed_gates = {item["gate"] for item in findings}
    if "REVIEW" in failed_gates:
        failed_gates.add("PING-PREVIEW")
    batches = []
    for key, batch in mapping.get("batches", {}).items():
        assigned = [row for row in rows if row.get("completionBatch") == key and row.get("contextId") in active_ids]
        gates = {gate for row in assigned for gate in context_gates(row)}
        missing = sorted(gates & failed_gates)
        batches.append({"batch": key, "assignedOutstandingContexts": len(assigned), "missingGateIds": missing,
                        "contexts": [{"contextId": row["contextId"], "requiredGateIds": sorted(context_gates(row)),
                                      "missingGateIds": sorted(context_gates(row) & failed_gates),
                                      "preparationRecords": "missing" if context_gates(row) & failed_gates else "present-for-declared-plan"}
                                     for row in assigned],
                        "preparationRecords": "missing" if missing else "present-for-declared-plan" if assigned else "historical-or-excluded"})
    consolidated = {}
    for finding in findings:
        gate = finding["gate"]
        if gate not in consolidated:
            affected = [row["contextId"] for row in rows if row.get("contextId") in active_ids
                        and (gate in context_gates(row) or gate == "REVIEW" and "PING-PREVIEW" in context_gates(row))]
            consolidated[gate] = {"gate": gate, "missingRecords": [], "affectedContextCount": len(affected), "affectedContextIds": affected}
        consolidated[gate]["missingRecords"].append({key: finding[key] for key in ("field", "reason")})
    return {"kind": "Offline campaign preparation record check", "checkedAtUtc": now.isoformat(),
            "recordCheck": "missing-or-inconsistent" if findings else "complete-for-declared-plan",
            "liveAcceptanceEstablished": False, "retainedContextCount": len(ids), "sessionWindowMinutes": validity,
            "selectedOutstandingContexts": len(active_ids), "approvalMode": approval_mode,
            "residualLedgerSha256": digest(paths["remaining_ledger"]) if remaining is not None else None,
            "scopeDifferences": differences, "findings": list(consolidated.values()), "batches": batches,
            "limits": "Local reference existence and declared metadata only. No network, credential-store reads, permission decisions or live tests. Complete records do not establish product admission, fixture health, independent review validity or acceptance."}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("register", "baseline", "history", "context-map"):
        parser.add_argument("--" + name, type=Path, required=True)
    parser.add_argument("--expected-tag", required=True)
    parser.add_argument("--expected-source", required=True)
    parser.add_argument("--remaining-ledger", type=Path,
                        help="Optional latest private residual ledger; preserves the immutable baseline and selects exact remaining originals.")
    parser.add_argument("--remaining-ledger-sha256",
                        help="Expected SHA-256 from the owner-supplied checkpoint; required with --remaining-ledger.")
    parser.add_argument("--session-minutes", type=int, default=360)
    parser.add_argument("--output", type=Path, help="Optional new report file; existing files are never overwritten.")
    args = parser.parse_args()
    if args.session_minutes <= 0 or not re.fullmatch(r"[0-9a-f]{40}", args.expected_source):
        parser.error("A positive session window and a full lowercase source SHA are required.")
    if bool(args.remaining_ledger) != bool(args.remaining_ledger_sha256) or (
            args.remaining_ledger_sha256 and not re.fullmatch(r"[0-9a-f]{64}", args.remaining_ledger_sha256)):
        parser.error("--remaining-ledger and its full lowercase --remaining-ledger-sha256 must be supplied together.")
    paths = {name: getattr(args, name).resolve() for name in ("register", "baseline", "history", "context_map")}
    if args.remaining_ledger:
        paths["remaining_ledger"] = args.remaining_ledger.resolve()
    try:
        if args.output and (args.output.exists() or args.output.resolve() in paths.values()):
            raise ValueError("Choose a new report path; input and previous evidence files must be preserved.")
        report = inspect(*(read_object(paths[name]) for name in ("register", "baseline", "history", "context_map")),
                         paths, args.expected_tag, args.expected_source, datetime.now(timezone.utc), args.session_minutes,
                         read_object(paths["remaining_ledger"]) if args.remaining_ledger else None, args.remaining_ledger_sha256)
        rendered = json.dumps(report, indent=2) + "\n"
        if args.output:
            args.output.write_text(rendered, encoding="utf-8")
            print(json.dumps({key: report[key] for key in ("recordCheck", "liveAcceptanceEstablished", "retainedContextCount")}))
            print("Missing or inconsistent prerequisite groups:", len(report["findings"]))
        else:
            print(rendered, end="")
        return 2 if report["findings"] else 0
    except (OSError, ValueError, TypeError, KeyError, AttributeError, OverflowError):
        print("Preflight input could not be validated. Check JSON structure, required paths and immutable references; no acceptance result was produced.", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
