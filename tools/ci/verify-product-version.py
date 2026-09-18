#!/usr/bin/env python3
"""Fail-closed XML scanner for NetRatel's central product version policy."""

from __future__ import annotations

import argparse
import sys
import xml.etree.ElementTree as element_tree
from pathlib import Path

VERSION_PROPERTIES = {
    "Version",
    "VersionPrefix",
    "VersionSuffix",
    "PackageVersion",
    "AssemblyVersion",
    "FileVersion",
    "InformationalVersion",
}
EXCLUDED_PARTS = {".git", "bin", "obj", "artifacts"}


def local_name(name: str) -> str:
    return name.rsplit("}", 1)[-1]


def files_to_scan(root: Path) -> list[Path]:
    candidates: list[Path] = []
    for path in root.rglob("*"):
        if any(part in EXCLUDED_PARTS for part in path.relative_to(root).parts):
            continue
        if path.is_file() and path.suffix in {".csproj", ".props", ".targets"}:
            candidates.append(path)
    return sorted(candidates)


def find_version_metadata(path: Path) -> list[str]:
    try:
        root = element_tree.parse(path).getroot()
    except (OSError, element_tree.ParseError) as error:
        raise RuntimeError(f"Cannot parse {path}: {error}") from error

    matches: list[str] = []
    for node in root.iter():
        element = local_name(node.tag)
        if element in VERSION_PROPERTIES:
            matches.append(element)
    return matches


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", required=True, type=Path)
    args = parser.parse_args()
    root = args.root.resolve()
    canonical = root / "Directory.Build.props"

    if not canonical.is_file():
        print("Directory.Build.props is missing.", file=sys.stderr)
        return 1

    try:
        for path in files_to_scan(root):
            matches = find_version_metadata(path)
            if not matches or path.resolve() == canonical:
                continue
            relative = path.relative_to(root)
            print(
                f"{relative} declares local product version metadata ({', '.join(matches)}); "
                "use Directory.Build.props instead.",
                file=sys.stderr,
            )
            return 1
    except RuntimeError as error:
        print(error, file=sys.stderr)
        return 1

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
