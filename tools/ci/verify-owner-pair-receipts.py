#!/usr/bin/env python3
"""Bind six owner rows/24 visuals to the prepared image handoff and checkout."""
import argparse
import hashlib
import importlib.util
import json
import os
import pathlib
import re
import subprocess
import sys

SHA = re.compile(r"[0-9a-f]{40}")
IMAGE_ID = re.compile(r"sha256:[0-9a-f]{64}")
DIGEST_REF = re.compile(r"[a-z0-9./:_-]+@sha256:[0-9a-f]{64}")
SEMVER = re.compile(r"(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?")
SERVICES = {"nr-api", "nr-web", "nr-migrations", "rd-api", "rd-web", "nr-postgres", "rd-postgres", "nr-volume-init"}
IMAGE_FIELDS = {"service", "reference", "id", "source", "version"}


def require(condition, message):
    if not condition:
        raise SystemExit(message)


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        require(key not in result, "Duplicate JSON keys are rejected in owner handoffs/receipts.")
        result[key] = value
    return result


def load_json(path):
    require(path.is_file() and not path.is_symlink(), "The actual handoff/receipt must be a regular file.")
    return json.loads(path.read_text(encoding="utf-8"), object_pairs_hook=unique_object)


def version(value):
    if not isinstance(value, str):
        return False
    match = SEMVER.fullmatch(value)
    return match is not None and all(not part.isdigit() or len(part) == 1 or not part.startswith("0")
                                    for part in (match.group(4) or "").split("."))


def current_checkout():
    # No caller-supplied root or ambient release-source override can select an old checkout.
    root = pathlib.Path(__file__).resolve().parents[2]
    def git(*arguments):
        result = subprocess.run(["git", "-C", str(root), *arguments], capture_output=True, text=True, timeout=30)
        require(result.returncode == 0, "The actual CI repository Git identity is unavailable.")
        return result.stdout.strip()
    require(pathlib.Path(git("rev-parse", "--show-toplevel")).resolve() == root,
            "The owner verifier must execute from this repository's tools/ci directory.")
    source = git("rev-parse", "HEAD")
    require(SHA.fullmatch(source), "The actual checkout requires its complete Git source SHA.")
    hosted_source = os.environ.get("GITHUB_SHA")
    require(hosted_source is None or hosted_source == source, "The actual checkout differs from its hosted CI source SHA.")
    spec = importlib.util.spec_from_file_location("owner_current_product_version", root / "tools/ci/product-version.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    current_version, _ = module.product_version(root)
    require(version(current_version), "The actual repository product version is invalid.")
    return source, current_version


def image_table(rows):
    require(isinstance(rows, list) and len(rows) == 8 and all(isinstance(row, dict) and set(row) == IMAGE_FIELDS for row in rows),
            "Require exactly eight explicit immutable image identities.")
    names = [row["service"] for row in rows]
    require(len(set(names)) == 8 and set(names) == SERVICES, "Image services must be exact and nonduplicate.")
    require(all(isinstance(row["id"], str) and IMAGE_ID.fullmatch(row["id"]) for row in rows), "Each image requires its actual immutable local SHA-256 ID.")
    return {row["service"]: row for row in rows}


def validate_pins(pins, source, current_version):
    require(isinstance(pins, dict) and set(pins) == {"netRatel", "ratelDesk", "fixture", "images"}, "The complete prepared owner handoff is required.")
    nr, rd, fixture = pins["netRatel"], pins["ratelDesk"], pins["fixture"]
    require(isinstance(nr, dict) and set(nr) == {"version", "source", "api", "web", "migrations", "apiId", "webId", "migrationsId"} and
            isinstance(rd, dict) and set(rd) == {"version", "source", "api", "web", "postgres"} and
            isinstance(fixture, dict) and set(fixture) == {"netRatelPostgres", "volumeInit"}, "Owner image sections contain missing or unsupported fields.")
    require(nr["source"] == source and nr["version"] == current_version and SHA.fullmatch(source) and version(current_version),
            "Prepared candidate images must match the actual current checkout and evaluated version.")
    require(isinstance(rd["source"], str) and SHA.fullmatch(rd["source"]) and version(rd["version"]), "Explicit published companion source/version pins are required.")
    expected = {}
    for role in ("api", "web", "migrations"):
        reference, identity = nr[role], nr[role + "Id"]
        require(isinstance(reference, str) and reference and re.fullmatch(r"[a-z0-9./:_-]+", reference) and
                isinstance(identity, str) and IMAGE_ID.fullmatch(identity), "Candidate image references/IDs must be explicit and canonical.")
        expected["nr-" + role] = {"reference": reference, "id": identity, "source": source, "version": current_version}
    for role in ("api", "web"):
        reference = rd[role]
        require(isinstance(reference, str) and reference.startswith("ghcr.io/bostontechnologies/rateldesk-" + role + "@sha256:") and DIGEST_REF.fullmatch(reference),
                "Companion product references must use their exact public repository digest.")
        expected["rd-" + role] = {"reference": reference, "source": rd["source"], "version": rd["version"]}
    for service, reference in (("nr-postgres", fixture["netRatelPostgres"]), ("rd-postgres", rd["postgres"]), ("nr-volume-init", fixture["volumeInit"])):
        require(isinstance(reference, str) and DIGEST_REF.fullmatch(reference), "Every auxiliary image requires its exact repository digest.")
        expected[service] = {"reference": reference, "source": "", "version": ""}
    table = image_table(pins["images"])
    for service, values in expected.items():
        require(all(table[service][field] == value for field, value in values.items()), "The prepared image table differs from its explicit source/published/fixture sections.")
    return table


def verify_receipts(root, expected_images):
    root = root.resolve()
    require(root.is_dir(), "The actual owner receipt directory is absent.")
    expected_files = {f"{scenario}-{role}.json" for scenario in ("connected", "signed-out", "peer-loss")
                      for role in ("netratel-initiates", "rateldesk-initiates")}
    files = {path.name for path in root.glob("*.json")}
    require(files == expected_files, "All six actual owner receipts must be present; extra/stale receipts are rejected.")
    visual_files = set()
    for file in sorted(expected_files):
        receipt = load_json(root / file)
        expected_scenario, expected_role, _ = file.removesuffix(".json").rsplit("-", 2)
        require(receipt["scenario"] == expected_scenario and receipt["netRatelInitiates"] is (expected_role == "netratel"),
                "An actual receipt is mislabeled for its scenario/initiator role.")
        require(receipt["outcome"] == "passed" and receipt["phase"] == "safe-success-receipt", "An actual owner row failed or did not reach its final evidence boundary.")
        require(receipt["contract"] == "bostec.service-link.v1" and receipt["readOnlyProbePassed"] is True and receipt["incidentDeliveryReady"] is False,
                "A receipt lacks the actual read-only foundation result or claims unsupported incident delivery.")
        require(receipt["browserPageErrorCount"] == 0, "An actual owner row emitted a browser error.")
        require(image_table(receipt["images"]) == expected_images, "An actual row differs from the prepared current image handoff.")
        require(receipt["databaseVersions"]["nr-postgres"] // 10000 == 17 and receipt["databaseVersions"]["rd-postgres"] // 10000 == 16,
                "The two actual databases do not match the required source/published fixture profiles.")
        for product in ("netRatel", "ratelDesk"):
            state = receipt[product]
            require(state["lifecycleState"] == "active" and state["decision"] == "commit" and state["localInboundActive"] is True
                    and state["localBusinessSenderEnabled"] is True and state["peerActiveAcknowledged"] is True,
                    "An actual product lacks complete current directional authority.")
        nr, rd = receipt["netRatel"], receipt["ratelDesk"]
        require(nr["attemptId"] == rd["attemptId"] == receipt["original"]["attemptId"] and nr["linkId"] == rd["linkId"]
                and nr["grantHash"] == rd["grantHash"] and nr["commitId"] == rd["commitId"], "The two actual products disagree on their original link/decision.")
        commands = receipt["ownerCommandCounts"]
        continued = receipt["scenario"] == "signed-out"
        require(commands == {"start": 1, "responderConsent": 1, "finalConsent": 1, "continuation": int(continued)}, "Owner command/consent counts are not exact.")
        require(receipt["protectedBrowserHops"] == {"approvalHopCount": 2 if continued else 1, "callbackHopCount": 1, "protectedHopFailures": 0},
                "The real proof-bearing browser hops lack required secrecy headers/counts.")
        visuals = receipt["visuals"]
        require(len(visuals) == (12 if receipt["scenario"] == "connected" else 0), "The actual consent visual case count is incomplete.")
        if visuals:
            require({(v["phase"], v["theme"], v["width"], v["height"], v["zoomPercent"]) for v in visuals} ==
                    {(phase, theme, width, height, zoom) for phase in ("responder-final", "initiator-final") for theme in ("light", "dark")
                     for width, height, zoom in ((1440, 900, 100), (360, 800, 100), (720, 900, 200))}, "A required light/dark/viewport/zoom consent case is absent.")
        for visual in visuals:
            path = (root / visual["file"]).resolve()
            require(root in path.parents and path.suffix == ".png" and path.is_file() and not path.is_symlink(), "A visual artifact path escapes the actual receipt directory.")
            require(hashlib.sha256(path.read_bytes()).hexdigest() == visual["sha256"], "An actual screenshot differs from its receipt hash.")
            visual_files.add(path)
    require(len(visual_files) == 24, "Require exactly 24 actual consent screenshots across both initiating roles.")
    require({path.resolve() for path in (root / "screenshots").glob("*.png")} == visual_files, "Extra/stale screenshot artifacts are rejected.")
    print("Verified six actual owner receipts and 24 screenshot hashes. Visual inspection remains an explicit separate gate.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("receipts_directory", type=pathlib.Path, nargs="?")
    parser.add_argument("--pins-file", type=pathlib.Path, required=True)
    parser.add_argument("--check-pins-only", action="store_true")
    args = parser.parse_args()
    require(args.check_pins_only != (args.receipts_directory is not None), "Choose either a pins-only preflight or the actual receipt directory.")
    source, current_version = current_checkout()
    table = validate_pins(load_json(args.pins_file), source, current_version)
    if args.check_pins_only:
        print("Verified prepared owner image handoff against current checkout/source/version.")
    else:
        verify_receipts(args.receipts_directory, table)


if __name__ == "__main__":
    try:
        main()
    except (OSError, ValueError, KeyError, TypeError, IndexError, AttributeError, subprocess.TimeoutExpired):
        raise SystemExit("The actual owner receipts could not be validated; raw artifacts are excluded from failure output.") from None
