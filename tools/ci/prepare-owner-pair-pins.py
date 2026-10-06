#!/usr/bin/env python3
"""Bind already-built candidate images to independently supplied publication/digest pins.

Proposed hosted-only helper. It neither builds nor publishes images. Failures never
echo Docker bodies, environment, credentials, or arbitrary publication content.
"""
import argparse
import importlib.util
import json
import os
import pathlib
import re
import subprocess

SPEC = importlib.util.spec_from_file_location("owner_receipt_verifier", pathlib.Path(__file__).with_name("verify-owner-pair-receipts.py"))
VERIFIER = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(VERIFIER)


def require(condition, message):
    if not condition:
        raise SystemExit(message)


def docker(*arguments):
    result = subprocess.run(["docker", *arguments], capture_output=True, text=True, timeout=180, check=False)
    require(result.returncode == 0, "The selected owner-pair image operation failed; raw Docker output is excluded.")
    return result.stdout


def digest(reference):
    return isinstance(reference, str) and re.fullmatch(r"[a-z0-9./:_-]+@sha256:[0-9a-f]{64}", reference) is not None


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--version", required=True)
    parser.add_argument("--source", required=True)
    parser.add_argument("--api", required=True)
    parser.add_argument("--web", required=True)
    parser.add_argument("--migrations", required=True)
    parser.add_argument("--publication-pins", type=pathlib.Path, required=True)
    parser.add_argument("--output", type=pathlib.Path, required=True)
    args = parser.parse_args()
    current_source, current_version = VERIFIER.current_checkout()
    require(args.source == current_source and args.version == current_version,
            "Candidate source/version arguments must match the actual current repository checkout.")
    require(re.fullmatch(r"[0-9a-f]{40}", args.source) is not None, "The current candidate source SHA is required.")
    require(args.version and "__" not in args.version, "The evaluated current candidate version is required.")
    supplied = VERIFIER.load_json(args.publication_pins)
    require(isinstance(supplied, dict) and set(supplied) == {"ratelDesk", "fixture"}, "The explicit independently verified publication/fixture pin sections are required.")
    companion, fixture = supplied["ratelDesk"], supplied["fixture"]
    require(re.fullmatch(r"[0-9a-f]{40}", companion["source"]) is not None, "The published companion source pin is required.")
    require(companion["version"] and "__" not in companion["version"], "The independently verified published companion version pin is required.")
    require(companion["api"].startswith("ghcr.io/bostontechnologies/rateldesk-api@sha256:") and digest(companion["api"]), "The companion API requires its exact public repository digest.")
    require(companion["web"].startswith("ghcr.io/bostontechnologies/rateldesk-web@sha256:") and digest(companion["web"]), "The companion Web requires its exact public repository digest.")
    for reference in (companion["api"], companion["web"], companion["postgres"], fixture["netRatelPostgres"], fixture["volumeInit"]):
        require(digest(reference), "All companion/database/volume-initializer fixture images require exact digest pins.")
        docker("pull", reference)
    candidate = {"version": args.version, "source": args.source, "api": args.api, "web": args.web, "migrations": args.migrations}
    images = []
    for role in ("api", "web", "migrations"):
        image = json.loads(docker("image", "inspect", candidate[role]))[0]
        labels = image["Config"]["Labels"]
        require(labels.get("org.opencontainers.image.revision") == args.source and labels.get("org.opencontainers.image.version") == args.version,
                "A candidate source image has mismatched version/source labels.")
        require(re.fullmatch(r"sha256:[0-9a-f]{64}", image["Id"]) is not None, "A candidate image has no immutable local ID.")
        candidate[role + "Id"] = image["Id"]
        images.append({"service": "nr-" + role, "reference": candidate[role], "id": image["Id"], "source": args.source, "version": args.version})
    for role in ("api", "web"):
        image = json.loads(docker("image", "inspect", companion[role]))[0]
        labels = image["Config"]["Labels"]
        require(labels.get("org.opencontainers.image.revision") == companion["source"] and labels.get("org.opencontainers.image.version") == companion["version"],
                "A companion image has mismatched published version/source labels.")
        require(companion[role] in image["RepoDigests"], "A selected companion image differs from its published digest pin.")
        require(isinstance(image.get("Id"), str) and re.fullmatch(r"sha256:[0-9a-f]{64}", image["Id"]) is not None,
                "A selected companion image has no immutable local ID.")
        images.append({"service": "rd-" + role, "reference": companion[role], "id": image["Id"], "source": companion["source"], "version": companion["version"]})
    for service, reference in (("nr-postgres", fixture["netRatelPostgres"]), ("rd-postgres", companion["postgres"]), ("nr-volume-init", fixture["volumeInit"])):
        image = json.loads(docker("image", "inspect", reference))[0]
        require(isinstance(image.get("Id"), str) and re.fullmatch(r"sha256:[0-9a-f]{64}", image["Id"]) is not None and reference in image["RepoDigests"],
                "An auxiliary image differs from its actual immutable ID/repository digest pin.")
        images.append({"service": service, "reference": reference, "id": image["Id"], "source": "", "version": ""})
    handoff = {"netRatel": candidate, "ratelDesk": companion, "fixture": fixture, "images": images}
    VERIFIER.validate_pins(handoff, current_source, current_version)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(handoff, indent=2) + "\n")
    os.chmod(args.output, 0o600)
    print("Prepared exact candidate image IDs and independently supplied companion/fixture digest pins.")


if __name__ == "__main__":
    try:
        main()
    except (OSError, ValueError, KeyError, TypeError, subprocess.TimeoutExpired):
        raise SystemExit("The owner-pair image handoff could not be validated; raw input/Docker diagnostics are excluded.") from None
