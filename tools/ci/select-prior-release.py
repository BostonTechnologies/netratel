#!/usr/bin/env python3
"""Select the latest completed public release for the upgrade smoke."""

import hashlib
import json
from pathlib import Path
import re
import subprocess
import tempfile

REPOSITORY = "BostonTechnologies/netratel"
COMPONENTS = ("api", "web", "migrations", "mcp-http", "client")


def gh(*args):
    return subprocess.run(("gh", *args), check=True, text=True, capture_output=True).stdout


def pages(value):
    decoder = json.JSONDecoder()
    remaining = value.lstrip()
    while remaining:
        page, end = decoder.raw_decode(remaining)
        if not isinstance(page, list):
            raise ValueError("Expected paginated GitHub release arrays")
        yield from page
        remaining = remaining[end:].lstrip()


def select(current_version):
    releases = sorted(
        pages(gh("api", f"repos/{REPOSITORY}/releases?per_page=100", "--paginate")),
        key=lambda release: release.get("published_at") or "", reverse=True)
    for release in releases:
        tag = release.get("tag_name", "")
        if release.get("draft") or tag == f"v{current_version}" or not tag.startswith("v"):
            continue
        asset = next((item for item in release.get("assets", [])
                      if item.get("name") == "publication.json"), None)
        if not asset:
            continue
        with tempfile.TemporaryDirectory(prefix="netratel-prior-release-") as temporary:
            gh("release", "download", tag, "--repo", REPOSITORY,
               "--pattern", "publication.json", "--dir", temporary)
            data = (Path(temporary) / "publication.json").read_bytes()
        if asset.get("digest") != "sha256:" + hashlib.sha256(data).hexdigest():
            raise ValueError(f"Published publication record digest differs for {tag}")
        record = json.loads(data)
        images = record.get("images", {})
        if (record.get("productVersion") != tag[1:] or
                record.get("verification", {}).get("state") != "complete" or
                set(images) != set(COMPONENTS)):
            continue
        for component in COMPONENTS:
            expected = f"ghcr.io/bostontechnologies/netratel-{component}@sha256:"
            if not images[component].startswith(expected) or not re.fullmatch(
                    r"[a-f0-9]{64}", images[component][len(expected):]):
                raise ValueError(f"Invalid published image digest for {component} in {tag}")
        return {"tag": tag, "images": images}
    raise ValueError("No completed public release is available for the upgrade smoke")


if __name__ == "__main__":
    version = subprocess.check_output(
        ("python3", str(Path(__file__).with_name("product-version.py"))), text=True).strip()
    print(json.dumps(select(version)))
