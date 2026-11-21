#!/usr/bin/env python3
"""Verify SPDX dependency coverage and every file digest against extracted bytes."""
import argparse
import json
from pathlib import Path
import re

from viewer_runtime import METADATA, sha256, viewer_notices, viewer_packages


def verify_browser_inventory(document, root, inventoried):
    distributions = [path.parent for path in root.rglob("NetRatel.Web.deps.json")]
    if document.get("name") == "NetRatel.Web" and not distributions:
        raise ValueError("Missing Web runtime distribution")
    for distribution in distributions:
        viewers = viewer_packages(distribution)
        npm_ids = {}
        for directory, metadata in viewers:
            for package in [metadata, *metadata["embeddedDependencies"]]:
                name, version = package["name"], package["version"]
                purl = f"pkg:npm/{name}@{version}"
                matches = [item for item in document["packages"] if any(
                    ref.get("referenceType") == "purl" and ref.get("referenceLocator") == purl
                    for ref in item.get("externalRefs", []))]
                if len(matches) != 1 or (
                        matches[0]["name"], matches[0]["versionInfo"], matches[0]["licenseDeclared"],
                        matches[0]["downloadLocation"]) != (name, version, package["license"], package["source"]):
                    raise ValueError(f"Missing or inconsistent browser SBOM package {purl}")
                npm_ids[name] = matches[0]["SPDXID"]
            for relative in [METADATA, *metadata["files"]]:
                if (directory / relative).resolve() not in inventoried:
                    raise ValueError(f"Missing browser SBOM file checksum: {directory / relative}")

        relationships = {(link["spdxElementId"], link["relationshipType"], link["relatedSpdxElement"])
                         for link in document["relationships"]}
        for _, metadata in viewers:
            source = npm_ids[metadata["name"]]
            required = {(source, "DEPENDS_ON", npm_ids[name]) for name in metadata["externalDependencies"]}
            required.update((source, "CONTAINS", npm_ids[item["name"]]) for item in metadata["embeddedDependencies"])
            if not required <= relationships:
                raise ValueError(f"Missing browser SBOM dependency relationships for {metadata['name']}")

        notices = (distribution / "THIRD-PARTY-NOTICES.txt").read_text(encoding="utf-8")
        if not notices.endswith(viewer_notices(viewers)):
            raise ValueError("Missing or inconsistent viewer notices")


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
    inventoried = set()
    for item in document["files"]:
        path = (root / item["fileName"]).resolve()
        if not path.is_relative_to(root.resolve()) or not path.is_file():
            raise ValueError(f"Missing or unsafe SBOM file: {item['fileName']}")
        checksums = [checksum["checksumValue"] for checksum in item["checksums"] if checksum["algorithm"] == "SHA256"]
        if len(checksums) != 1 or not re.fullmatch(r"[a-f0-9]{64}", checksums[0]) or checksums[0] == "0" * 64:
            raise ValueError("Missing, malformed or placeholder SHA256")
        if checksums[0] != sha256(path):
            raise ValueError(f"Incorrect SBOM digest: {item['fileName']}")
        inventoried.add(path)
    verify_browser_inventory(document, root, inventoried)
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
