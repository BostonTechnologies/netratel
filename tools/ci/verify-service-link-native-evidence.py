#!/usr/bin/env python3
"""Require both actual-native cases and bounded same-checkout physical receipts. Draft; unexecuted."""
import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import uuid
import xml.etree.ElementTree as ET


METHOD = "Real_recorded_form_task_executes_on_authenticated_native_agent_and_receives_provider_callback"
SHA = re.compile(r"[0-9a-f]{40}\Z")
DIGEST = re.compile(r"[0-9a-f]{64}\Z")
FIELDS = {
    "phase", "candidateSource", "reviewedSource", "clientAssemblySha256", "clientProductVersion",
    "trustVerified", "untrustedCaRejected", "caSha256", "netRatelInitiates", "provider", "nativeExecution",
    "ratelDeskVersion", "ratelDeskSource", "ratelDeskApiImage", "ratelDeskWebImage", "linkId",
    "semanticRevision", "grantHash", "parentRequestId", "requestTaskId", "automationBindingId",
    "netRatelRequestId", "runId", "agentId", "jobId", "stepId", "activityId", "proofMarker",
    "callbackStatus", "finalTaskStatus", "successWorklogId", "replay", "boundary",
}


def require(condition, message):
    if not condition:
        raise ValueError(message)


def read_bounded(path, maximum):
    require(path.is_file() and path.stat().st_size <= maximum, f"Missing or oversized required evidence: {path.name}")
    with path.open("rb") as stream:
        content = stream.read(maximum + 1)
    require(len(content) <= maximum, f"Oversized required evidence: {path.name}")
    return content


def strict_object(pairs):
    result = {}
    for name, value in pairs:
        require(name not in result, "Receipt contains duplicate JSON members")
        result[name] = value
    return result


def git(repository, *arguments):
    result = subprocess.run(["git", "-C", str(repository), *arguments], check=True,
                            capture_output=True, text=True, timeout=5)
    require(len(result.stdout) <= 65536, "Git source evidence exceeds the allowed bound")
    return result.stdout.strip()


def peer_pins(repository):
    source = read_bounded(repository / "src/NetRatel/NetRatel.API.IntegrationTests/ServiceLinkPublishedRatelDeskPeer.cs", 131072).decode("utf-8")
    values = {}
    for name in ("PublishedSource", "PublishedVersion", "ApiImage", "WebImage"):
        matches = re.findall(r"\bconst\s+string\s+" + name + r'\s*=\s*"([^"\r\n]+)"\s*;', source)
        require(len(matches) == 1, f"Pinned companion {name} is missing or ambiguous")
        values[name] = matches[0]
    require(re.fullmatch(r"(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?", values["PublishedVersion"]) is not None,
            "Companion version must be a canonical product version")
    require(SHA.fullmatch(values["PublishedSource"]) is not None, "Companion source must be an immutable commit")
    for name in ("ApiImage", "WebImage"):
        require(re.fullmatch(r"ghcr\.io/[a-z0-9./_-]+@sha256:[0-9a-f]{64}", values[name]) is not None,
                f"Companion {name} must be an immutable image digest")
    publication = json.loads(read_bounded(repository / "tests/compose/owner-pair-publication.json", 16384),
                             object_pairs_hook=strict_object,
                             parse_constant=lambda value: (_ for _ in ()).throw(ValueError("Non-finite publication number")))
    require(type(publication) is dict and type(publication.get("ratelDesk")) is dict,
            "Accepted companion publication identity is missing")
    peer = publication["ratelDesk"]
    for constant, field in (("PublishedVersion", "version"), ("PublishedSource", "source"), ("ApiImage", "api"), ("WebImage", "web")):
        require(peer.get(field) == values[constant], f"Companion {constant} differs from the accepted publication")
    return values


def native_version(repository):
    props = ET.fromstring(read_bounded(repository / "Directory.Build.props", 65536))
    def one(name):
        values = [element.text or "" for element in props.iter(name)]
        require(len(values) == 1, f"Product {name} authority is missing or ambiguous")
        return values[0]
    prefix, suffix = one("VersionPrefix"), one("VersionSuffix")
    return prefix + ("-" + suffix if suffix else "")


def require_native_trx(repository, reports):
    verifier_path = repository / "tools/ci/verify-mtp-trx.py"
    spec = importlib.util.spec_from_file_location("verify_mtp_trx", verifier_path)
    require(spec is not None and spec.loader is not None, "Existing generic TRX verifier is unavailable")
    verifier = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(verifier)
    paths = sorted(reports.glob("netratel-tests-NetRatel.API.IntegrationTests_*.trx"))
    require(len(paths) == 1, "Require exactly one current generic API integration TRX receipt")
    # Keep the whole generic project's mandatory success/zero-skip validation, without freezing its total.
    read_bounded(paths[0], 32 * 1024 * 1024)
    verifier.validate_success_report(paths[0])
    root = ET.parse(paths[0]).getroot()
    results = [element for element in root.iter() if element.tag.rsplit("}", 1)[-1] == "UnitTestResult"
               and METHOD in element.attrib.get("testName", "")]
    require(len(results) == 2, "Both initiating-role physical cases must execute in ordinary generic CI")
    require(all(element.attrib.get("outcome") == "Passed" for element in results), "A required physical case did not pass")
    identities = [element.attrib.get("executionId", "") for element in results]
    require(all(identities) and len(set(identities)) == 2, "Physical TRX cases must have distinct execution identities")


def verify_receipt(receipt, candidate, reviewed, version, client_digest, pins):
    require(type(receipt) is dict and set(receipt) == FIELDS, "Receipt schema differs or contains unapproved fields")
    expected = {
        "phase": "actual-native-orchestration-completed", "candidateSource": candidate, "reviewedSource": reviewed,
        "clientAssemblySha256": client_digest, "clientProductVersion": version,
        "trustVerified": True, "untrustedCaRejected": True, "provider": "PostgreSQL",
        "nativeExecution": "production enrollment/token/presence/job clients and real bounded bash executor",
        "ratelDeskVersion": pins["PublishedVersion"], "ratelDeskSource": pins["PublishedSource"],
        "ratelDeskApiImage": pins["ApiImage"], "ratelDeskWebImage": pins["WebImage"],
        "callbackStatus": 204, "finalTaskStatus": "Completed", "replay": "204, same worklog IDs and single run/binding",
        "boundary": "isolated actual candidate and immutable published companion; no production deployment interoperability claim",
    }
    for name, value in expected.items():
        require(type(receipt[name]) is type(value) and receipt[name] == value, f"Receipt {name} differs from required actual evidence")
    require(type(receipt["netRatelInitiates"]) is bool, "Receipt initiating role must be explicit")
    require(type(receipt["semanticRevision"]) is int and receipt["semanticRevision"] > 0, "Receipt semantic revision is invalid")
    require(type(receipt["netRatelRequestId"]) is int and receipt["netRatelRequestId"] > 0, "Receipt native request ID is invalid")
    for name in ("caSha256", "grantHash"):
        require(type(receipt[name]) is str and DIGEST.fullmatch(receipt[name]) is not None, f"Receipt {name} is invalid")
    for name in ("runId", "jobId", "stepId", "activityId"):
        require(type(receipt[name]) is str and re.fullmatch(r"[1-9][0-9]{0,19}", receipt[name]) is not None
                and int(receipt[name]) <= (1 << 64) - 1, f"Receipt {name} is not a native positive ulong ID")
    # RatelDesk IDs and link IDs are opaque wire identifiers; do not impose an invented UUID schema.
    for name in ("linkId", "parentRequestId", "requestTaskId", "automationBindingId", "successWorklogId"):
        require(type(receipt[name]) is str and 1 <= len(receipt[name]) <= 256
                and all(ord(character) >= 32 for character in receipt[name]), f"Receipt {name} is invalid")
    require(type(receipt["agentId"]) is str and str(uuid.UUID(receipt["agentId"])) == receipt["agentId"]
            and uuid.UUID(receipt["agentId"]).int != 0, "Receipt enrolled Agent-ID is invalid")
    require(type(receipt["proofMarker"]) is str and re.fullmatch(r"native-proof-[0-9a-f]{32}", receipt["proofMarker"]) is not None,
            "Receipt native command marker is invalid")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repository", type=Path, default=Path("."))
    parser.add_argument("--reports", type=Path, default=Path("TestResults"))
    parser.add_argument("--evidence", type=Path, default=Path("TestResults/service-link-proof"))
    parser.add_argument("--configuration", choices=("Debug", "Release"), default="Release")
    parser.add_argument("--reviewed-source", default=os.environ.get("NETRATEL_REVIEW_SOURCE_SHA"))
    args = parser.parse_args()
    try:
        repository = args.repository.resolve(strict=True)
        candidate = git(repository, "rev-parse", "HEAD")
        require(SHA.fullmatch(candidate) is not None, "Candidate has no exact current source commit")
        require(not git(repository, "status", "--porcelain", "--untracked-files=normal"), "Physical proof source is not clean and committed")
        tested_source = os.environ.get("NETRATEL_REVIEW_TEST_MERGE_SHA")
        require(tested_source is None or tested_source == candidate, "Declared CI test checkout differs from actual candidate source")
        reviewed = args.reviewed_source or candidate
        require(SHA.fullmatch(reviewed) is not None, "Reviewed source must identify an exact commit")
        require_native_trx(repository, args.reports)
        pins = peer_pins(repository)
        client = repository / f"src/NetRatel/NetRatel.Client/bin/{args.configuration}/net10.0/NetRatel.Client.dll"
        client_digest = hashlib.sha256(read_bounded(client, 32 * 1024 * 1024)).hexdigest()
        paths = sorted(args.evidence.glob("physical-*.json"))
        require(len(paths) == 2, "Require exactly two final physical receipts; clear stale evidence before running")
        receipts = [json.loads(read_bounded(path, 16384), object_pairs_hook=strict_object,
                               parse_constant=lambda value: (_ for _ in ()).throw(ValueError("Non-finite JSON number"))) for path in paths]
        for receipt in receipts:
            verify_receipt(receipt, candidate, reviewed, native_version(repository), client_digest, pins)
        require({receipt["netRatelInitiates"] for receipt in receipts} == {True, False}, "Both initiating roles need actual receipts")
        for name in ("caSha256", "linkId", "parentRequestId", "requestTaskId", "automationBindingId", "agentId", "proofMarker", "successWorklogId"):
            require(len({receipt[name] for receipt in receipts}) == 2, f"Independent roles reused receipt {name}")
    except (OSError, ValueError, ET.ParseError, subprocess.SubprocessError) as error:
        print(f"Mandatory native orchestration evidence rejected: {error}", file=sys.stderr)
        return 1
    print(f"Mandatory native orchestration evidence accepted: 2 passed, 0 skipped; tested source={candidate}; reviewed source={reviewed}; companion={pins['PublishedVersion']}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
