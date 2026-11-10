#!/usr/bin/env python3
"""Require disjoint successful Core receipts covering prebuilt xUnit discovery."""
import argparse
from collections import Counter, defaultdict
import importlib.util
import json
from pathlib import Path
import xml.etree.ElementTree as ET

spec = importlib.util.spec_from_file_location("mtp_receipt", Path(__file__).with_name("verify-mtp-trx.py"))
mtp = importlib.util.module_from_spec(spec)
spec.loader.exec_module(mtp)
INFRASTRUCTURE = "NetRatel.Tests.Infrastructure"
API = "NetRatel.Tests.API"
EXCLUDED = {"compose", "hosted", "manual-integration"}


def in_namespace(class_name, prefix):
    namespace = class_name.rpartition(".")[0]
    return namespace == prefix or namespace.startswith(prefix + ".")


def validate(discovered, reports, api_discovered=None):
    if not discovered or len({case["ID"] for case in discovered}) != len(discovered):
        raise ValueError("Discovery must contain nonzero unique logical cases")
    expected = Counter()
    for case in discovered:
        if case.get("Explicit") or any(name.casefold() == "category" and
                any(value.casefold() in EXCLUDED for value in values)
                for name, values in case.get("Traits", {}).items()):
            raise ValueError("Discovery selected an excluded opt-in case")
        expected[(case["Class"], case["Method"], case["DisplayName"])] += 1
    if api_discovered is not None:
        api_expected = Counter((case["Class"], case["Method"], case["DisplayName"])
                               for case in api_discovered)
        if api_expected != expected:
            raise ValueError("Core discovery differs between the solution and API runner builds")
    actual = Counter()
    method_groups = defaultdict(set)
    execution_ids = set()
    counts = {}
    for group, path in reports.items():
        receipt = mtp.validate_success_report(path)
        root = ET.parse(path).getroot()
        definitions = {}
        for unit in root.iter():
            if unit.tag.endswith("UnitTest"):
                method = next(item for item in unit if item.tag.endswith("TestMethod"))
                definitions[unit.attrib["id"]] = (method.attrib["className"], method.attrib["name"])
        results = [item for item in root.iter() if item.tag.endswith("UnitTestResult")]
        if len(results) != receipt["executed"]:
            raise ValueError("Executed counters differ from actual result rows")
        counts[group] = len(results)
        for result in results:
            if result.attrib.get("outcome") != "Passed":
                raise ValueError("Core receipt contains a non-passing result row")
            identifier = result.attrib["executionId"]
            if identifier in execution_ids:
                raise ValueError("Duplicate execution record across Core receipts")
            execution_ids.add(identifier)
            class_name, method_name = definitions[result.attrib["testId"]]
            partition = "infrastructure" if in_namespace(class_name, INFRASTRUCTURE) else (
                "api" if "api" in reports and in_namespace(class_name, API) else "remaining")
            if group != "all" and group != partition:
                raise ValueError("Core result is in the wrong namespace partition")
            method_groups[(class_name, method_name)].add(group)
            actual[(class_name, method_name, result.attrib["testName"])] += 1
    if any(len(groups) != 1 for groups in method_groups.values()):
        raise ValueError("A Core method executed in overlapping partitions")

    deferred = []
    for key, count in expected.items():
        if actual[key] == count:
            del actual[key]
            continue
        # xUnit defers theories containing non-serializable MemberData. Their
        # discovery case has no arguments; runtime rows can include timestamps.
        # Namespace filters select the whole owning method before enumeration.
        class_name, method_name, display = key
        rows = {other: value for other, value in actual.items()
                if other[:2] == key[:2] and other[2].startswith(display + "(") and other[2].endswith(")")}
        siblings = [other for other in expected if other[:2] == key[:2]]
        if count != 1 or len(siblings) != 1 or not rows or actual[key]:
            raise ValueError("Core discovery case is missing or duplicated: " + display)
        deferred.append({"class": class_name, "method": method_name, "runtimeRows": sum(rows.values())})
        for other in rows:
            del actual[other]
    if actual:
        raise ValueError("Core receipts contain undiscovered or duplicate cases")
    return {"discoveredLogicalCases": sum(expected.values()), "executedRows": sum(counts.values()),
            "partitionExecutedRows": counts, "deferredTheoryExpansions": deferred,
            "coverage": "prebuilt discovery equals the disjoint completed Core receipt union"}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--discovery", type=Path, required=True)
    parser.add_argument("--infrastructure", type=Path, required=True)
    parser.add_argument("--remaining", type=Path, required=True)
    parser.add_argument("--api", type=Path)
    parser.add_argument("--api-discovery", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    if bool(args.api) != bool(args.api_discovery):
        parser.error("--api and --api-discovery must be supplied together")
    reports = {"infrastructure": args.infrastructure, "remaining": args.remaining}
    if args.api:
        reports["api"] = args.api
    record = validate(json.loads(args.discovery.read_text()), reports,
                      json.loads(args.api_discovery.read_text()) if args.api_discovery else None)
    args.output.write_text(json.dumps(record, indent=2) + "\n")
    print(json.dumps(record, indent=2))
