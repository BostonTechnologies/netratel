#!/usr/bin/env python3
"""Read the sole product version from Directory.Build.props."""

import argparse
import json
import os
from pathlib import Path
import re
import xml.etree.ElementTree as ET

ROOT = Path(os.environ.get("NETRATEL_RELEASE_SOURCE_ROOT", Path(__file__).resolve().parents[2])).resolve()


def product_version(root=ROOT):
    props = ET.parse(root / "Directory.Build.props").getroot()
    values = {}
    for name in ("VersionPrefix", "VersionSuffix"):
        nodes = props.findall(f".//{name}")
        if len(nodes) != 1 or (name == "VersionPrefix" and nodes[0].text is None):
            raise ValueError(f"Directory.Build.props must declare one {name}")
        values[name] = (nodes[0].text or "").strip()
    prefix, suffix = values["VersionPrefix"], values["VersionSuffix"]
    if not re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+", prefix):
        raise ValueError("VersionPrefix must be a numeric three-part version")
    if suffix and not re.fullmatch(r"[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*", suffix):
        raise ValueError("VersionSuffix is not a valid prerelease suffix")
    return prefix + (f"-{suffix}" if suffix else ""), bool(suffix)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest-output", type=Path)
    args = parser.parse_args()
    version, prerelease = product_version()
    if args.manifest_output:
        manifest = json.loads((ROOT / "release/release-manifest.json").read_text())
        if "version" in manifest or "prerelease" in manifest:
            raise SystemExit("Release manifest must not pin product version fields")
        manifest.update(version=version, prerelease=prerelease)
        args.manifest_output.write_text(json.dumps(manifest, indent=2) + "\n")
    else:
        print(version)
