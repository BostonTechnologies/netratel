#!/usr/bin/env python3
"""Fetch the latest completed public client pack for read-only hosted acceptance."""

import argparse
import hashlib
import json
from pathlib import Path
import re
import subprocess
import time

REPOSITORY = "BostonTechnologies/netratel"


def command(*args):
    return subprocess.run(args, check=True, capture_output=True, text=True).stdout.strip()


def gh(*args):
    return command("gh", *args)


def checked_download(tag, name, asset, directory):
    data = directory / name
    for attempt in range(4):
        download = subprocess.run(
            ("gh", "release", "download", tag, "--repo", REPOSITORY,
             "--pattern", name, "--dir", str(directory)),
            capture_output=True, text=True, check=False)
        if download.returncode == 0:
            break
        data.unlink(missing_ok=True)
        if attempt == 3:
            raise RuntimeError(f"Published asset download failed after four attempts: {name}")
        time.sleep(2 ** attempt)
    if not data.is_file() or data.stat().st_size != asset["size"]:
        raise ValueError(f"Published asset size differs: {name}")
    digest = asset.get("digest")
    if digest:
        with data.open("rb") as source:
            checksum = hashlib.file_digest(source, "sha256").hexdigest()
        if digest != "sha256:" + checksum:
            raise ValueError(f"Published asset digest differs: {name}")
    return data


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    directory = args.output.resolve()
    directory.mkdir(parents=True, exist_ok=True)
    if any(directory.iterdir()):
        raise ValueError("The fixture output directory must be empty")

    current_version = command("python3", "tools/ci/product-version.py")
    selection = json.loads(command("python3", "tools/ci/select-prior-release.py"))
    tag = selection["tag"]
    release = json.loads(gh("api", f"repos/{REPOSITORY}/releases/tags/{tag}"))
    if release.get("draft") or release.get("tag_name") != tag:
        raise ValueError("Selected release is a draft or tag identity changed")
    assets = {asset["name"]: asset for asset in release["assets"] if asset.get("state") == "uploaded"}
    for name in ("publication.json", "SHA256SUMS"):
        if name not in assets:
            raise ValueError(f"Completed release is missing {name}")
        checked_download(tag, name, assets[name], directory)

    publication = json.loads((directory / "publication.json").read_text())
    if publication.get("productVersion") != tag.removeprefix("v") or \
            publication.get("verification", {}).get("state") != "complete" or \
            publication.get("inputReceipt", {}).get("repository") != REPOSITORY:
        raise ValueError("Publication record is incomplete or identifies a different release")
    tag_commit = json.loads(gh("api", f"repos/{REPOSITORY}/commits/{tag}"))["sha"]
    if tag_commit != publication.get("publicCommit") or \
            tag_commit != publication.get("inputReceipt", {}).get("headSha"):
        raise ValueError("Release tag does not resolve to its recorded immutable build commit")

    pattern = re.compile(rf"^netratel-client-{re.escape(tag.removeprefix('v'))}-"
                         r"(?P<runtime>[a-z0-9-]+)\.(?:zip|tar\.gz)$")
    inventory = publication["inputReceipt"]["files"]
    names = sorted(name for name in inventory if pattern.fullmatch(name))
    if not names:
        raise ValueError("Completed release declares no client runtime archives")
    runtime_ids = set()
    for name in names:
        runtime = pattern.fullmatch(name).group("runtime")
        if runtime in runtime_ids:
            raise ValueError(f"Duplicate published client runtime: {runtime}")
        runtime_ids.add(runtime)
        if name not in assets:
            raise ValueError(f"Publication inventory asset is absent: {name}")
        checked_download(tag, name, assets[name], directory)

    print(json.dumps({"sourceTag": tag, "sourceCommit": tag_commit,
                      "candidateVersion": current_version, "runtimes": sorted(runtime_ids)}))


if __name__ == "__main__":
    main()
