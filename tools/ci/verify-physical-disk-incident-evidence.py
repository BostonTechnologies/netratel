#!/usr/bin/env python3
"""Verify separate cleanup-first actual physical proof and its actual MTP TRX.

This gate does not build, run, grant, seed, fill, dispatch or repair either product.
It leaves the existing exactly-two-native-receipts verifier unchanged.
"""
from __future__ import annotations
import argparse
import datetime as dt
import json
import pathlib
import re
import sys
import xml.etree.ElementTree as ET

CASE = "Real_full_Client_disk_breach_survives_actual_worker_process_crashes_rotation_and_unlink_without_duplicates"

def require(value: bool, code: str) -> None:
    if not value:
        raise ValueError(code)

def no_duplicates(pairs):
    value = {}
    for key, item in pairs:
        require(key not in value, "duplicate-json-property")
        value[key] = item
    return value

def timestamp(value: str) -> dt.datetime:
    result = dt.datetime.fromisoformat(value.replace("Z", "+00:00"))
    require(result.utcoffset() == dt.timedelta(0), "non-utc-proof-time")
    return result

def shape(value, contract, definitions):
    if "$ref" in contract:
        return shape(value, definitions[contract["$ref"].rsplit("/", 1)[1]], definitions)
    if "const" in contract:
        require(type(value) is type(contract["const"]) and value == contract["const"], "invalid-proof-constant"); return
    if "anyOf" in contract:
        if value is None: return
        return shape(value, contract["anyOf"][0], definitions)
    kind = contract["type"]
    if kind == "object":
        require(type(value) is dict and set(value) == set(contract["properties"]), "unexpected-proof-object-shape")
        for key, sub in contract["properties"].items(): shape(value[key], sub, definitions)
    elif kind == "array":
        require(type(value) is list and len(value) <= contract["maxItems"], "invalid-proof-array")
        for item in value: shape(item, contract["items"], definitions)
    elif kind == "boolean": require(type(value) is bool, "invalid-proof-boolean")
    elif kind == "integer": require(type(value) is int and value >= contract.get("minimum", -(2**63)), "invalid-proof-integer")
    elif kind == "null": require(value is None, "invalid-proof-null")
    else:
        require(type(value) is str and len(value) <= contract.get("maxLength", 1024), "invalid-proof-string")
        if "pattern" in contract: require(re.fullmatch(contract["pattern"], value) is not None, "invalid-canonical-proof-identity")
        if contract.get("format") == "date-time": timestamp(value)

def collection_window(samples, after=None):
    require(3 <= len(samples) <= 128, "physical-window-needs-three-unique-collections")
    require(len({x["collectionId"] for x in samples}) == len(samples), "cached-copy-counted-as-fresh")
    times = [timestamp(x["collectedAtUtc"]) for x in samples]
    require(all(b > a for a, b in zip(times, times[1:])) and times[-1]-times[0] >= dt.timedelta(seconds=10), "physical-hold-not-proven")
    for row, collected in zip(samples, times):
        frame, received = timestamp(row["frameObservedAtUtc"]), timestamp(row["receivedAtUtc"])
        require(row["scope"] == "/netratel-physical-disk" and collected <= frame <= received and
                received-collected <= dt.timedelta(seconds=15) and 0 <= row["freeBytes"] <= row["totalBytes"] and
                row["collectionComplete"] and row["transportAccepted"] and row["durableCommittedOwner"], "invalid-real-accepted-collection")
        if after: require(collected > after, "pre-mutation-collection-used-as-physical-evidence")

def main() -> None:
    parser = argparse.ArgumentParser()
    for argument in ("directory", "trx", "schema", "source-sha", "reviewed-source-sha", "tree-sha", "client-image-id",
                     "client-executable-sha256", "candidate-version", "companion-source", "companion-version", "companion-api-image", "companion-web-image"):
        parser.add_argument("--"+argument, required=True)
    args = parser.parse_args()
    folder = pathlib.Path(args.directory)
    files = sorted(folder.glob("disk-incident-*.json"))
    require(len(files) == 1 and files[0].stat().st_size <= 262144, "exactly-one-bounded-actual-disk-proof-required")
    proof = json.loads(files[0].read_text(), object_pairs_hook=no_duplicates)
    contract = json.loads(pathlib.Path(args.schema).read_text(), object_pairs_hook=no_duplicates)
    shape(proof, contract, contract["$defs"])
    require(proof["candidateSource"] == proof["actualTestCheckout"] == args.source_sha and proof["reviewedSource"] == args.reviewed_source_sha and
            proof["candidateTree"] == args.tree_sha, "physical-source-checkout-mismatch")
    producer = proof["producer"]
    require(producer["source"] == args.source_sha and producer["imageId"] == args.client_image_id and
            producer["executableSha256"] == args.client_executable_sha256 and producer["version"] == args.candidate_version and
            producer["producer"] == "full-production-NetRatel.Client" and producer["slowCollectionSeconds"] == 5 and
            producer["maximumLifetimeSeconds"] == 300 and producer["trustedTlsAlpnVerified"] and producer["unrelatedCaRejected"] and producer["ownedExt4Visible"],
            "actual-full-client-provenance-trust-cadence-not-proven")
    companion = proof["companion"]
    require(companion == {"version":args.companion_version,"source":args.companion_source,"apiImage":args.companion_api_image,"webImage":args.companion_web_image},
            "actual-published-compatible-companion-mismatch")
    admission = proof["admission"]
    require(admission["committedOwner"] and admission["productionSystemClock"] and admission["connectionEpoch"] > 0 and
            admission["producer"] == "full-production-NetRatel.Client", "actual-committed-owner-admission-not-proven")
    volume = proof["volume"]; allocation = volume["allocation"]; recovery = volume["recovery"]; second_allocation = volume["secondAllocation"]
    require(volume["backingBytes"] == 3*1024**3 and volume["filesystem"] == "ext4" and not volume["sparseBacking"] and
            allocation["bytes"] == 256*1024**2 and allocation["actualBlocksBytes"] >= allocation["bytes"] and
            allocation["bytes"] <= allocation["selectedFreeBefore"]//10 and allocation["selectedFreeAfter"] >= 2*1024**3 and
            allocation["hostFreeAfter"] >= 2*1024**3 and recovery["hostFreeAfter"] >= 2*1024**3 and recovery["selectedFreeAfter"] > recovery["selectedFreeBefore"],
            "bounded-owned-real-block-allocation-recovery-not-proven")
    collection_window(proof["baselineCollections"])
    collection_window(proof["breachCollections"], timestamp(allocation["completedAtUtc"]))
    collection_window(proof["continuedBreachCollections"], timestamp(allocation["completedAtUtc"]))
    collection_window(proof["recoveryCollections"], timestamp(recovery["completedAtUtc"]))
    require(second_allocation["bytes"] == allocation["bytes"] and second_allocation["actualBlocksBytes"] >= second_allocation["bytes"] and
            second_allocation["bytes"] <= second_allocation["selectedFreeBefore"]//10 and second_allocation["selectedFreeAfter"] >= 2*1024**3 and
            second_allocation["hostFreeAfter"] >= 2*1024**3 and timestamp(second_allocation["completedAtUtc"]) > timestamp(recovery["completedAtUtc"]),
            "second-bounded-real-block-allocation-not-proven")
    collection_window(proof["secondBreachCollections"], timestamp(second_allocation["completedAtUtc"]))
    baseline_min = min(x["freeBytes"] for x in proof["baselineCollections"])
    breach_threshold, recovery_threshold = baseline_min-allocation["bytes"]//2, baseline_min-allocation["bytes"]//4
    require(all(x["freeBytes"] < breach_threshold for name in ("breachCollections","continuedBreachCollections","secondBreachCollections") for x in proof[name]) and
            all(x["freeBytes"] > recovery_threshold for x in proof["recoveryCollections"]), "physical-breach-and-recovery-values-not-proven")
    ids = proof["durableIdentities"]
    for name in ("occurrences", "raisedEvents", "dispatchIntentKeys", "flowRunIds", "actions", "receipts", "incidents", "confirmationEffects"):
        require(len(ids[name]) == 2, "expected-two-distinct-physical-effects")
    require(not ids["mayHaveCommitted"] and ids["actionSucceeded"] and len({x["receiverKey"] for x in ids["actions"]}) == 2 and
            len({x["occurrenceId"] for x in ids["actions"]}) == 2 and len(set(ids["receipts"])) == 2 and len(set(ids["incidents"])) == 2 and
            len(set(ids["confirmationEffects"])) == 2, "durable-rearm-or-same-key-reconciliation-not-proven")
    loss = proof["committedLoss"]; first = ids["actions"][0]
    require(loss["actualUpstreamStatus"] == 201 and loss["transactionCommitted"] and loss["responseAbortedAfterIndependentRead"] and
            (loss["incidentRows"], loss["receiptRows"], loss["confirmationEnqueues"]) == (1,1,1) and
            loss["key"] == first["receiverKey"] and loss["fingerprint"] == first["receiverFingerprint"] and loss["namespaceId"] == first["sourceNamespaceId"],
            "independent-actual-committed-response-loss-not-proven")
    rotation = proof["rotation"]
    require(rotation["afterCredentialRevision"] > rotation["beforeCredentialRevision"] and rotation["beforeSemanticRevision"] == rotation["afterSemanticRevision"] and
            rotation["beforeSourceNamespaceId"] == rotation["afterSourceNamespaceId"] == first["sourceNamespaceId"], "rotation-changed-semantic-action-identity")
    processes = proof["apiProcesses"]
    require(len(processes) == 4 and len({x["processId"] for x in processes}) == 4 and all(x["exitedAtUtc"] and x["exitCode"] is not None for x in processes) and
            sorted(x["role"] for x in processes) == ["primary","primary","replica","replica"], "actual-independent-api-worker-process-lifecycle-not-proven")
    unlink = proof["unlink"]
    require(unlink["positiveControlStatusBeforeUnlink"] == 200 and unlink["protectedDenialStatus"] in (401,403) and unlink["samePrivateAuthorizationHandleUsed"] and
            unlink["positivelyControlledInboundReplicas"] == unlink["immediateDeniedInboundReplicas"] == unlink["settledDeniedInboundReplicas"] == 2 and
            timestamp(unlink["cachedAuthorizationCommonExpiresAtUtc"]) > timestamp(unlink["latestDenialObservedAtUtc"]), "unexpired-current-cached-token-both-channel-both-replica-unlink-denial-not-proven")
    cleanup = proof["cleanup"]
    require(all(cleanup[x] for x in ("clientStoppedAndRemoved","mountRemoved","loopDetached","backingRemoved","privateCredentialsRemoved","allApiProcessesExited",
                                    "bothProductFixturesDisposed","apiPrivateStateRemoved","ownedMetadataRemoved")) and
            cleanup["imagesRemoved"] == cleanup["fleetChanges"] == 0, "actual-owned-cleanup-not-proven")
    task = proof["recordedTask"]
    require(task["callbackStatus"] == 204 and all(task[x] for x in ("parentRequestId","taskId","automationBindingId","jobRunId","stepId","activityId","successWorklogId")),
            "actual-recorded-task-native-result-callback-not-proven")
    tree = ET.parse(args.trx).getroot()
    results = [x for x in tree.iter() if x.tag.rsplit("}",1)[-1] == "UnitTestResult"]
    require(results and all(x.attrib.get("outcome") == "Passed" for x in results), "physical-trx-contains-failure-or-skipped-case")
    selected = [x for x in results if CASE in x.attrib.get("testName", "")]
    require(len(selected) == 1 and selected[0].attrib.get("outcome") == "Passed", "registered-actual-physical-case-not-passed")
    print("PASS_ACTUAL_PHYSICAL_INCIDENT_EVIDENCE")

if __name__ == "__main__":
    try: main()
    except Exception as error:
        print("Physical disk proof rejected: " + type(error).__name__, file=sys.stderr)
        raise SystemExit(1)
