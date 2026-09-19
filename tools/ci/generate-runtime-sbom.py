#!/usr/bin/env python3
"""Generate an SPDX document from resolved .NET dependencies and publish inputs."""

from __future__ import annotations

import argparse
import hashlib
import json
import re
from datetime import datetime, timezone
import xml.etree.ElementTree as ET
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
        raise ValueError(f"No resolved target for runtime {runtime}")
    if targets:
        return next(iter(targets.values()))
    raise ValueError("project assets file has no resolved targets")


def packages_from_assets(assets_path: Path, runtime: str | None, distribution: Path) -> list[dict]:
    assets = json.loads(assets_path.read_text(encoding="utf-8"))
    name = assets["project"]["restore"]["projectName"]
    deps_path = distribution / f"{name}.deps.json"
    distributed_graphs = list(distribution.glob("*.deps.json"))
    if not deps_path.is_file() and len(distributed_graphs) == 1:
        deps_path = distributed_graphs[0]
    if not deps_path.is_file():
        candidates = list((assets_path.parent.parent / "bin/Release").glob(
            f"*/{runtime}/{name}.deps.json" if runtime else f"*/{name}.deps.json"))
        if len(candidates) != 1:
            raise ValueError(f"Expected exactly one published runtime graph for {name}")
        deps_path = candidates[0]
    deps = json.loads(deps_path.read_text(encoding="utf-8"))
    target_name = deps["runtimeTarget"]["name"]
    if runtime and not target_name.endswith("/" + runtime):
        raise ValueError(f"Publish graph for {name} does not target {runtime}")
    target = deps["targets"][target_name]
    libraries = deps["libraries"]
    notices = ["Third-party runtime dependency inventory",
               "License metadata below is declared by upstream packages, not independently certified.",
               "The inventory is derived from the publish runtime graph, including single-file inputs."]
    packages: list[dict] = []
    for identity, target_entry in sorted(target.items()):
        library = libraries.get(identity, {})
        name, version = identity.rsplit("/", 1)
        first_party = library.get("type") == "project" and name.startswith("NetRatel.")
        license_declared = "Apache-2.0" if first_party else "NOASSERTION"
        if not first_party:
            notice = [f"{name} {version}"]
            package_path = library.get("path", f"{name.removeprefix('runtimepack.').lower()}/{version.lower()}")
            package_dir = next((Path(folder) / package_path for folder in assets["packageFolders"]
                                if (Path(folder) / package_path).is_dir()), None)
            if package_dir:
                nuspec = list(package_dir.glob("*.nuspec"))
                if len(nuspec) != 1:
                    raise ValueError(f"Expected one upstream nuspec for {identity}")
                additional_license = None
                for node in ET.parse(nuspec[0]).getroot().iter():
                    field = node.tag.rsplit("}", 1)[-1]
                    if field in {"authors", "copyright", "license", "licenseUrl", "projectUrl"} and node.text:
                        notice.append(f"{field}: {node.text}")
                    if field == "license" and node.get("type") == "expression":
                        license_declared = node.text or "NOASSERTION"
                    if field == "license" and node.get("type") == "file":
                        additional_license = (package_dir / (node.text or "")).resolve()
                        if not additional_license.is_relative_to(package_dir.resolve()) or not additional_license.is_file():
                            raise ValueError(f"Invalid license file for {identity}")
                texts = {path for path in package_dir.rglob("*") if path.is_file()
                         and re.match(r"^(license|licence|notice|copying|third-party-notices)([._-]|$)", path.name, re.I)}
                if additional_license:
                    texts.add(additional_license)
                for path in sorted(texts):
                    notice.extend([f"--- {path.relative_to(package_dir)} ---", path.read_text(encoding="utf-8-sig")])
                if not texts:
                    notice.append("No license/notice text is bundled in this upstream package; see its declared license metadata.")
            else:
                notice.append("Runtime/framework component; consult the runtime distribution's license and third-party notices.")
            notices.append("\n".join(notice))
        packages.append(
            {
                "name": name,
                "SPDXID": spdx_id(identity, "Package"),
                "versionInfo": version,
                "downloadLocation": "NOASSERTION",
                "filesAnalyzed": False,
                "licenseConcluded": "NOASSERTION",
                "licenseDeclared": license_declared,
                "copyrightText": "NOASSERTION",
                "_dependencies": [spdx_id(f"{dep}/{version}", "Package")
                                  for dep, version in target_entry.get("dependencies", {}).items()
                                  if f"{dep}/{version}" in target],
                "externalRefs": [
                    {
                        "referenceCategory": "PACKAGE-MANAGER",
                        "referenceType": "purl",
                        "referenceLocator": f"pkg:nuget/{name}@{version}",
                    }
                ],
            }
        )
    (distribution / "THIRD-PARTY-NOTICES.txt").write_text("\n\n".join(notices) + "\n", encoding="utf-8")
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
    if len(args.project_assets) != len(args.distribution):
        parser.error("each distribution must have one matching project-assets input")
    if len({path.name for path in args.distribution}) != len(args.distribution):
        parser.error("distribution basenames must be unique")

    root_id = spdx_id(args.name, "Package")
    dependency_packages = {
        package["SPDXID"]: package
        for assets_path, distribution in zip(args.project_assets, args.distribution)
        for package in packages_from_assets(assets_path, args.runtime, distribution)
    }
    files = []
    relationships = []
    for directory in args.distribution:
        for path in sorted(candidate for candidate in directory.rglob("*") if candidate.is_file()):
            relative = path.relative_to(directory).as_posix()
            file_name = f"./{directory.name}/{relative}"
            file_id = spdx_id(file_name, "File")
            files.append(
                {
                    "fileName": file_name,
                    "SPDXID": file_id,
                    "checksums": [{"algorithm": "SHA256", "checksumValue": sha256(path)}],
                    "licenseConcluded": "NOASSERTION",
                    "licenseInfoInFiles": ["NOASSERTION"],
                    "copyrightText": "NOASSERTION",
                }
            )
            relationships.append({"spdxElementId": root_id, "relationshipType": "CONTAINS", "relatedSpdxElement": file_id})

    for package_id in sorted(dependency_packages):
        relationships.append({"spdxElementId": root_id, "relationshipType": "DEPENDS_ON", "relatedSpdxElement": package_id})
        for dependency in dependency_packages[package_id].pop("_dependencies"):
            if dependency in dependency_packages:
                relationships.append({"spdxElementId": package_id, "relationshipType": "DEPENDS_ON", "relatedSpdxElement": dependency})
    relationships.append({"spdxElementId": "SPDXRef-DOCUMENT", "relationshipType": "DESCRIBES", "relatedSpdxElement": root_id})

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
        "creationInfo": {"creators": ["Tool: NetRatel runtime SBOM generator"], "created": datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")},
        "packages": [
            {
                "name": args.name,
                "SPDXID": root_id,
                "versionInfo": args.version,
                "downloadLocation": "NOASSERTION",
                "filesAnalyzed": False,
                "licenseConcluded": "NOASSERTION",
                "licenseDeclared": "NOASSERTION",
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
