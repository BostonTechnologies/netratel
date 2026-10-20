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


def inspect(register, baseline, history, mapping, paths, expected_tag, expected_source, now, validity):
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
        except ValueError:
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

    if value("release.tag") != expected_tag or value("release.sourceSha") != expected_source:
        fail("DEPLOYMENT", "release", "Declared release tag/source does not match the expected published release.")
    for gate, fields in RECEIPTS.items():
        for field in fields:
            require(gate, field, receipt=True)
    for field in ("consumer.aiConsumerNameAndVersion", "consumer.connectionReference", "consumer.bridgeArchiveSha256",
                  "review.eligibleDistinctReviewerReference", "actors.primary.protectedReference", "actors.noReveal.protectedReference",
                  "receiver.connectorReference", "receiver.syntheticDestinationReference", "receiver.allowedDeliveryAndCleanup"):
        gate = "CONSUMER" if field.startswith("consumer") else "SINK" if field.startswith("receiver") else "REVIEW"
        require(gate, field)
    expiry("REVIEW", "actors.primary.expiry")
    expiry("SECRET-READERS", "actors.noReveal.expiry")
    expiry("CONNECTIVITY", "actors.additionalPermissionTestCredential.expiry")
    try:
        original_expiry_preserved = exact_expiry(value("actors.primary.expiry")) == exact_expiry(mapping.get("protectedPrimaryExpiresAtUtc"))
    except (TypeError, ValueError):
        original_expiry_preserved = False
    if not original_expiry_preserved:
        fail("REVIEW", "actors.primary.expiry", "Original primary expiry must remain exactly as recorded in the protected baseline.")

    protected = {str(UUID(item)) for item in mapping.get("protectedOriginalAgentIds", [])}
    if not protected:
        fail("HISTORY", "protectedOriginalAgentIds", "Protected original identities are missing from the baseline map.")
    fixture_ids = []
    for os_name in ("windows", "linux"):
        prefix = "fixtures." + os_name + "."
        for field in ("independentDeviceIdentityReceipt", "runningVersionSourceAndExecutableHashReceipt"):
            require("EXECUTORS", prefix + field, receipt=True)
        for field in ("protectedConnectionProfileReference", "inertServiceName", "inertServiceInitialState", "cleanupOwnerAndScope"):
            require("EXECUTORS", prefix + field)
        for field in ("writableRoots", "allowedOperations"):
            entries = value(prefix + field)
            if not isinstance(entries, list) or not entries or any(not isinstance(item, str) or not item.strip() for item in entries):
                fail("EXECUTORS", prefix + field, "An explicit nonempty fixture list is required.")
        tenant = value(prefix + "tenantId")
        if type(tenant) is not int or tenant <= 0:
            fail("EXECUTORS", prefix + "tenantId", "A positive exact tenant ID is required.")
        try:
            agent = str(UUID(value(prefix + "agentId")))
            if UUID(agent).int == 0 or agent in protected or agent in fixture_ids:
                fail("EXECUTORS", prefix + "agentId", "Fixture must have its own nonzero identity, distinct from protected originals and the other fixture.")
            fixture_ids.append(agent)
        except (ValueError, TypeError, AttributeError):
            fail("EXECUTORS", prefix + "agentId", "A registered fixture UUID is required.")

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
    if "EXECUTORS" in failed_gates:
        failed_gates.add("DISPOSABLE-CLIENT")
    batches = []
    for key, batch in mapping.get("batches", {}).items():
        assigned = [row for row in rows if row.get("completionBatch") == key and row.get("planningPopulation") == "outstanding"]
        gates = {gate for row in assigned for gate in row.get("prospectiveRequiredGateIds", [])}
        if assigned:
            gates |= {"HISTORY", "DEPLOYMENT", "CONSUMER"}
        missing = sorted(gates & failed_gates)
        batches.append({"batch": key, "assignedOutstandingContexts": len(assigned), "missingGateIds": missing,
                        "preparationRecords": "missing" if missing else "present-for-declared-plan" if assigned else "historical-or-excluded"})
    consolidated = {}
    for finding in findings:
        gate = finding["gate"]
        if gate not in consolidated:
            affected = [row["contextId"] for row in rows if row.get("planningPopulation") == "outstanding"
                and (gate in {"HISTORY", "DEPLOYMENT", "CONSUMER"} or gate in row.get("prospectiveRequiredGateIds", [])
                     or gate == "EXECUTORS" and "DISPOSABLE-CLIENT" in row.get("prospectiveRequiredGateIds", []))]
            consolidated[gate] = {"gate": gate, "missingRecords": [], "affectedContextCount": len(affected), "affectedContextIds": affected}
        consolidated[gate]["missingRecords"].append({key: finding[key] for key in ("field", "reason")})
    return {"kind": "Offline campaign preparation record check", "checkedAtUtc": now.isoformat(),
            "recordCheck": "missing-or-inconsistent" if findings else "complete-for-declared-plan",
            "liveAcceptanceEstablished": False, "retainedContextCount": len(ids), "sessionWindowMinutes": validity,
            "scopeDifferences": differences, "findings": list(consolidated.values()), "batches": batches,
            "limits": "Local reference existence and declared metadata only. No network, credential-store reads, permission decisions or live tests. Complete records do not establish product admission, fixture health, independent review validity or acceptance."}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("register", "baseline", "history", "context-map"):
        parser.add_argument("--" + name, type=Path, required=True)
    parser.add_argument("--expected-tag", required=True)
    parser.add_argument("--expected-source", required=True)
    parser.add_argument("--session-minutes", type=int, default=360)
    parser.add_argument("--output", type=Path, help="Optional new report file; existing files are never overwritten.")
    args = parser.parse_args()
    if args.session_minutes <= 0 or not re.fullmatch(r"[0-9a-f]{40}", args.expected_source):
        parser.error("A positive session window and a full lowercase source SHA are required.")
    paths = {name: getattr(args, name).resolve() for name in ("register", "baseline", "history", "context_map")}
    try:
        if args.output and (args.output.exists() or args.output.resolve() in paths.values()):
            raise ValueError("Choose a new report path; input and previous evidence files must be preserved.")
        report = inspect(*(read_object(paths[name]) for name in ("register", "baseline", "history", "context_map")),
                         paths, args.expected_tag, args.expected_source, datetime.now(timezone.utc), args.session_minutes)
        rendered = json.dumps(report, indent=2) + "\n"
        if args.output:
            args.output.write_text(rendered, encoding="utf-8")
            print(json.dumps({key: report[key] for key in ("recordCheck", "liveAcceptanceEstablished", "retainedContextCount")}))
            print("Missing or inconsistent prerequisite groups:", len(report["findings"]))
        else:
            print(rendered, end="")
        return 2 if report["findings"] else 0
    except (OSError, ValueError, TypeError, KeyError, AttributeError):
        print("Preflight input could not be validated. Check JSON structure, required paths and immutable references; no acceptance result was produced.", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
