#!/usr/bin/env python3
"""Generate an SPDX document from resolved .NET dependencies and publish inputs."""

from __future__ import annotations

import argparse
import hashlib
import json
import re
from pathlib import Path


def spdx_id(value: str, prefix: str) -> str:
    normalized = re.sub(r"[^A-Za-z0-9.-]", "-", value)
    return f"SPDXRef-{prefix}-{normalized}".rstrip("-")


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as source:
        for chunk in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def runtime_target(assets: dict, runtime: str | None) -> dict:
    targets = assets.get("targets", {})
    if runtime:
        matches = [value for key, value in targets.items() if key.endswith(f"/{runtime}")]
        if matches:
            return matches[0]
    if targets:
        return next(iter(targets.values()))
    raise ValueError("project assets file has no resolved targets")


def packages_from_assets(assets_path: Path, runtime: str | None) -> list[dict]:
    assets = json.loads(assets_path.read_text(encoding="utf-8"))
    target = runtime_target(assets, runtime)
    libraries = assets.get("libraries", {})
    packages: list[dict] = []
    for identity, target_entry in sorted(target.items()):
        library = libraries.get(identity, {})
        if library.get("type") != "package" or target_entry.get("type") != "package":
            continue
        name, version = identity.rsplit("/", 1)
        packages.append(
            {
                "name": name,
                "SPDXID": spdx_id(identity, "Package"),
                "versionInfo": version,
                "downloadLocation": "NOASSERTION",
                "filesAnalyzed": False,
                "licenseConcluded": "NOASSERTION",
                "licenseDeclared": "NOASSERTION",
                "copyrightText": "NOASSERTION",
                "externalRefs": [
                    {
                        "referenceCategory": "PACKAGE-MANAGER",
                        "referenceType": "purl",
                        "referenceLocator": f"pkg:nuget/{name}@{version}",
                    }
                ],
            }
        )
    return packages


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--project-assets", action="append", required=True, type=Path)
    parser.add_argument("--distribution", action="append", required=True, type=Path)
    parser.add_argument("--runtime")
    parser.add_argument("--name", required=True)
    parser.add_argument("--version", required=True)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()

    if not all(path.is_file() for path in args.project_assets):
        parser.error("every --project-assets input must exist")
    if not all(path.is_dir() for path in args.distribution):
        parser.error("every --distribution input must be a directory")

    root_id = spdx_id(args.name, "Package")
    dependency_packages = {
        package["SPDXID"]: package
        for assets_path in args.project_assets
        for package in packages_from_assets(assets_path, args.runtime)
    }
    files = []
    relationships = []
    for directory in args.distribution:
        directory_key = hashlib.sha256(str(directory.resolve()).encode("utf-8")).hexdigest()[:12]
        for path in sorted(candidate for candidate in directory.rglob("*") if candidate.is_file()):
            relative = path.relative_to(directory).as_posix()
            file_name = f"./{directory.name}-{directory_key}/{relative}"
            file_id = spdx_id(file_name, "File")
            files.append(
                {
                    "fileName": file_name,
                    "SPDXID": file_id,
                    "checksums": [{"algorithm": "SHA256", "checksumValue": sha256(path)}],
                    "licenseConcluded": "NOASSERTION",
                    "copyrightText": "NOASSERTION",
                }
            )
            relationships.append({"spdxElementId": root_id, "relationshipType": "CONTAINS", "relatedSpdxElement": file_id})

    for package_id in sorted(dependency_packages):
        relationships.append({"spdxElementId": root_id, "relationshipType": "DEPENDS_ON", "relatedSpdxElement": package_id})

    if not dependency_packages:
        parser.error("no resolved NuGet packages were found in the supplied project assets files")
    if not files:
        parser.error("no files were found in the supplied distribution directories")

    document = {
        "spdxVersion": "SPDX-2.3",
        "dataLicense": "CC0-1.0",
        "SPDXID": "SPDXRef-DOCUMENT",
        "name": args.name,
        "documentNamespace": f"https://github.com/BostonTechnologies/netratel/sbom/{args.version}/{spdx_id(args.name, 'Document')}",
        "creationInfo": {"creators": ["Tool: NetRatel runtime SBOM generator"], "created": "2026-01-01T00:00:00Z"},
        "packages": [
            {
                "name": args.name,
                "SPDXID": root_id,
                "versionInfo": args.version,
                "downloadLocation": "NOASSERTION",
                "filesAnalyzed": True,
                "licenseConcluded": "Apache-2.0",
                "licenseDeclared": "Apache-2.0",
                "copyrightText": "NOASSERTION",
            },
            *[dependency_packages[key] for key in sorted(dependency_packages)],
        ],
        "files": files,
        "relationships": relationships,
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(document, indent=2) + "\n", encoding="utf-8")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
