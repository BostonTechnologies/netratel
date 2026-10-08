#!/usr/bin/env python3
"""Reject obsolete embedded engine/server assets in the actual client distribution."""
import argparse
import json
from pathlib import Path
import re


FORBIDDEN_PACKAGES = re.compile(
    r"^(?:System\.Management\.Automation|Microsoft\.PowerShell(?:\.|$)|Microsoft\.WSMan\.Management|"
    r"NetRatel\.(?:Infrastructure|Akka)(?:\.|$)|Akka(?:\.|$)|"
    r"Microsoft\.EntityFrameworkCore(?:\.|$)|Npgsql(?:\.|$)|NJsonSchema(?:\.|$))", re.I)
FORBIDDEN_FILES = re.compile(
    r"(?:^|/)(?:_psprofile|Modules/Microsoft\.PowerShell[^/]*)(?:/|$)|"
    r"(?:^|/)(?:powershell\.config\.json|"
    r"(?:System\.Management\.Automation|Microsoft\.PowerShell[^/]*|Microsoft\.WSMan\.Management|"
    r"NetRatel\.Infrastructure|Microsoft\.EntityFrameworkCore[^/]*|Npgsql[^/]*|NJsonSchema[^/]*)\.dll)$", re.I)


def verify(document, graph=None, inputs=()):
    for package in document["packages"]:
        if FORBIDDEN_PACKAGES.match(package["name"]):
            raise ValueError("Client runtime graph includes removed dependency: " + package["name"])
    paths = [entry["fileName"] for entry in document["files"]]
    if graph:
        target = graph["targets"][graph["runtimeTarget"]["name"]]
        for identity, entry in target.items():
            name = identity.rsplit("/", 1)[0]
            if FORBIDDEN_PACKAGES.match(name):
                raise ValueError("Client final publish graph includes removed dependency: " + identity)
            for kind in ("runtime", "native", "resources", "runtimeTargets"):
                paths.extend(entry.get(kind, {}))
    paths.extend(inputs)
    for path in paths:
        if FORBIDDEN_FILES.search(path.replace("\\", "/")):
            raise ValueError("Client distribution includes removed engine/server content: " + path)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--sbom", type=Path, required=True)
    parser.add_argument("--runtime-graph", type=Path)
    parser.add_argument("--publish-inputs", type=Path)
    parser.add_argument("--distribution", type=Path, help="Also scan extracted files, including files omitted from the SBOM")
    args = parser.parse_args()
    try:
        graph = json.loads(args.runtime_graph.read_text(encoding="utf-8-sig")) if args.runtime_graph else None
        inputs = []
        if args.publish_inputs:
            for line in args.publish_inputs.read_text(encoding="utf-8-sig").splitlines():
                source, relative, _ = line.split("|")
                inputs.extend((source, relative))
        if args.distribution:
            inputs.extend(path.relative_to(args.distribution).as_posix() for path in args.distribution.rglob("*") if path.is_file())
        verify(json.loads(args.sbom.read_text(encoding="utf-8-sig")), graph, inputs)
    except (ValueError, OSError, KeyError, IndexError) as error:
        parser.exit(1, f"Invalid client runtime content: {error}\n")
    print("Verified absence of embedded PowerShell and server-only client runtime content.")
