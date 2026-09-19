#!/usr/bin/env python3
"""Verify SPDX dependency coverage and every file digest against extracted bytes."""
import argparse
import hashlib
import json
from pathlib import Path
import re


def verify(document, root):
    packages = document["packages"]
    names = {package["name"] for package in packages}
    if not any(name.startswith("NetRatel.") for name in names) or len(packages) < 3:
        raise ValueError("SBOM must include first-party components and runtime dependencies")
    if not any(package.get("externalRefs") for package in packages):
        raise ValueError("SBOM has no dependency provenance")
    ids = [item["SPDXID"] for item in packages + document["files"]]
    if len(ids) != len(set(ids)):
        raise ValueError("Duplicate SPDX identifiers")
    known = set(ids) | {"SPDXRef-DOCUMENT"}
    for relation in document["relationships"]:
        if relation["spdxElementId"] not in known or relation["relatedSpdxElement"] not in known:
            raise ValueError("Dangling SPDX relationship")
    if not document["files"]:
        raise ValueError("No distribution files in SBOM")
    for item in document["files"]:
        path = (root / item["fileName"]).resolve()
        if not path.is_relative_to(root.resolve()) or not path.is_file():
            raise ValueError(f"Missing or unsafe SBOM file: {item['fileName']}")
        checksums = [checksum["checksumValue"] for checksum in item["checksums"] if checksum["algorithm"] == "SHA256"]
        if len(checksums) != 1 or not re.fullmatch(r"[a-f0-9]{64}", checksums[0]) or checksums[0] == "0" * 64:
            raise ValueError("Missing, malformed or placeholder SHA256")
        with path.open("rb") as stream:
            actual = hashlib.file_digest(stream, "sha256").hexdigest()
        if checksums[0] != actual:
            raise ValueError(f"Incorrect SBOM digest: {item['fileName']}")
    print(f"Verified {len(packages)} packages and {len(document['files'])} distribution file digests.")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--sbom", required=True, type=Path)
    parser.add_argument("--root", required=True, type=Path)
    args = parser.parse_args()
    try:
        verify(json.loads(args.sbom.read_text()), args.root)
    except (ValueError, OSError, KeyError) as error:
        parser.exit(1, f"Invalid runtime SBOM: {error}\n")
