#!/usr/bin/env python3
"""Require a completed MTP TRX receipt with the exact expected test count."""
import argparse
from pathlib import Path
import sys
import xml.etree.ElementTree as ET


COUNTER_NAMES = (
    "total",
    "executed",
    "passed",
    "failed",
    "error",
    "timeout",
    "aborted",
    "inconclusive",
    "passedButRunAborted",
    "notRunnable",
    "notExecuted",
    "disconnected",
    "warning",
    "completed",
    "inProgress",
    "pending",
)
NON_SUCCESS_COUNTERS = (
    "failed",
    "error",
    "timeout",
    "aborted",
    "inconclusive",
    "passedButRunAborted",
    "notRunnable",
    "notExecuted",
    "disconnected",
    "warning",
    "inProgress",
    "pending",
)


def validate_success_report(path: Path, expected_executed: int | None = None) -> dict[str, int | str]:
    run = ET.parse(path).getroot()
    summary = next((element for element in run.iter() if element.tag.endswith("ResultSummary")), None)
    if summary is None:
        raise ValueError(f"{path}: missing ResultSummary")
    counters = next((element for element in summary if element.tag.endswith("Counters")), None)
    if counters is None:
        raise ValueError(f"{path}: missing test counters")

    try:
        values = {name: int(counters.attrib[name]) for name in COUNTER_NAMES}
    except (KeyError, ValueError) as exception:
        raise ValueError(f"{path}: invalid or missing test counters") from exception

    failures = []
    if any(value < 0 for value in values.values()):
        failures.append("the report contains a negative test counter")
    if summary.attrib.get("outcome") != "Completed":
        failures.append(f"outcome={summary.attrib.get('outcome')!r}")
    non_success = {name: values[name] for name in NON_SUCCESS_COUNTERS if values[name] != 0}
    if non_success:
        failures.append(f"the report includes non-success test counters {non_success}")
    if values["total"] != values["executed"] or values["passed"] != values["executed"]:
        failures.append(
            f"total/executed/passed={values['total']}/{values['executed']}/{values['passed']} "
            "must contain only passed executed cases"
        )
    if values["executed"] < 1:
        failures.append("the report selected zero test cases")
    if expected_executed is not None and values["executed"] != expected_executed:
        failures.append(f"executed={values['executed']} expected={expected_executed}")
    if expected_executed is not None and (
        values["passed"] != expected_executed or values["total"] != expected_executed
    ):
        failures.append(
            f"total/executed/passed={values['total']}/{values['executed']}/{values['passed']} "
            f"expected={expected_executed}"
        )
    if any(element.tag.endswith("RunInfo") for element in run.iter()):
        failures.append("the report contains an aborted or incomplete run marker")
    if failures:
        raise ValueError(f"{path}: " + "; ".join(failures))

    return {"outcome": summary.attrib["outcome"], **values}


def validate_report(path: Path, expected_executed: int) -> dict[str, int | str]:
    return validate_success_report(path, expected_executed)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("report", type=Path)
    parser.add_argument("--expected-executed", type=int)
    args = parser.parse_args()
    if args.expected_executed is not None and args.expected_executed < 1:
        parser.error("--expected-executed must be positive")
    try:
        receipt = validate_success_report(args.report, args.expected_executed)
    except (OSError, ET.ParseError, ValueError) as exception:
        print(f"MTP test receipt rejected: {exception}", file=sys.stderr)
        return 1

    print(f"MTP test receipt accepted: {args.report.name} {receipt}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
