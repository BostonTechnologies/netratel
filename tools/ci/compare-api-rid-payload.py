#!/usr/bin/env python3
"""Constrain the actual API payload delta to NuGet's Linux x64 asset selection."""
from collections import Counter
from datetime import datetime
import hashlib
import json
import re


MOVES = {
    "liblmdb.so": "runtimes/linux-x64/native/liblmdb.so",
    "System.Drawing.Common.dll": "runtimes/unix/lib/net6.0/System.Drawing.Common.dll",
}
METADATA = {"NetRatel.API.deps.json", "runtime.spdx.json"}
LINUX_OMISSIONS = {"web.config"}


def compare(control, candidate, control_root, candidate_root):
    for destination, source in MOVES.items():
        if destination not in candidate or source not in control or candidate[destination] != control[source]:
            raise ValueError("Linux runtime asset is missing or changed: " + destination)
    if any(path.startswith("runtimes/") for path in candidate):
        raise ValueError("RID-specific publish retained a portable runtime asset tree")
    for path, value in candidate.items():
        if path in METADATA or value == control.get(path):
            continue
        if path not in MOVES or value != control.get(MOVES[path]):
            raise ValueError("Unexpected API payload change: " + path)
    removed = sorted(control.keys() - candidate.keys())
    if any(not path.startswith("runtimes/") and path not in LINUX_OMISSIONS for path in removed):
        raise ValueError("RID selection removed a non-runtime file or symbol")
    for path in MOVES.values():
        if path not in control:
            raise ValueError("Missing known portable runtime input: " + path)
    old_deps = json.loads((control_root / "NetRatel.API.deps.json").read_text())
    new_deps = json.loads((candidate_root / "NetRatel.API.deps.json").read_text())
    expected_target = dict(old_deps["runtimeTarget"])
    expected_target["name"] += "/linux-x64"
    if ("/" in old_deps["runtimeTarget"]["name"] or new_deps["runtimeTarget"] != expected_target or
            old_deps["libraries"] != new_deps["libraries"]):
        raise ValueError("RID selection changed the runtime dependency/package identities")
    old = json.loads((control_root / "runtime.spdx.json").read_text())
    new = json.loads((candidate_root / "runtime.spdx.json").read_text())
    varying = {"files", "relationships", "creationInfo", "documentNamespace"}
    if ({key: value for key, value in old.items() if key not in varying} !=
            {key: value for key, value in new.items() if key not in varying}):
        raise ValueError("RID selection changed SPDX document or package metadata")
    old_creation, new_creation = dict(old["creationInfo"]), dict(new["creationInfo"])
    for creation in (old_creation, new_creation):
        datetime.strptime(creation.pop("created"), "%Y-%m-%dT%H:%M:%SZ")
    if old_creation != new_creation:
        raise ValueError("RID selection changed SPDX creators")
    prefix = old["documentNamespace"].rsplit("/", 1)[0] + "/"
    for document, inventory in ((old, control), (new, candidate)):
        suffix = hashlib.sha256(json.dumps(document["files"], sort_keys=True).encode()).hexdigest()
        if document["documentNamespace"] != prefix + suffix:
            raise ValueError("SPDX namespace does not bind its actual file inventory")
        for item in document["files"]:
            path = item["fileName"].removeprefix("./publish/")
            if item["fileName"] != "./publish/" + path or path not in inventory:
                raise ValueError("Unexpected SPDX distribution path")
            expected_id = "SPDXRef-File-" + re.sub(r"[^A-Za-z0-9.-]", "-", item["fileName"]).rstrip("-")
            if (item["SPDXID"] != expected_id or item["checksums"] != [
                    {"algorithm": "SHA256", "checksumValue": inventory[path]["sha256"]}]):
                raise ValueError("SPDX file identity differs from actual payload")
        contained = Counter(json.dumps(item, sort_keys=True) for item in document["relationships"]
                            if item["relationshipType"] == "CONTAINS")
        expected = Counter(json.dumps({"spdxElementId": document["packages"][0]["SPDXID"],
                                     "relationshipType": "CONTAINS", "relatedSpdxElement": item["SPDXID"]},
                                    sort_keys=True) for item in document["files"])
        if contained != expected:
            raise ValueError("SPDX file relationships differ")
    old_files = {item["fileName"].removeprefix("./publish/"): item for item in old["files"]}
    new_files = {item["fileName"].removeprefix("./publish/"): item for item in new["files"]}
    expected_files = (old_files.keys() - set(removed)) | (candidate.keys() - control.keys() - METADATA)
    if new_files.keys() != expected_files:
        raise ValueError("SPDX file coverage differs from the allowed payload delta")
    for path, item in new_files.items():
        source = MOVES.get(path, path) if candidate.get(path) != control.get(path) and path in MOVES else path
        ignored = {"fileName", "SPDXID", "checksums"}
        if ({key: value for key, value in item.items() if key not in ignored} !=
                {key: value for key, value in old_files[source].items() if key not in ignored}):
            raise ValueError("RID selection changed retained SPDX file metadata")
    def dependency_relations(document):
        return Counter(json.dumps(item, sort_keys=True) for item in document["relationships"]
                       if item["relationshipType"] != "CONTAINS")
    if dependency_relations(old) != dependency_relations(new):
        raise ValueError("RID selection changed dependency relationships")
    return {
        "format": "Actual uncompressed app files; controlled source/metadata/base comparison, not registry bytes",
        "controlAppBytes": sum(item["bytes"] for item in control.values()),
        "candidateAppBytes": sum(item["bytes"] for item in candidate.values()),
        "removedPaths": removed,
        "removedRuntimePaths": [path for path in removed if path.startswith("runtimes/")],
        "runtimeAssetMoves": MOVES,
        "omittedWindowsHostingFiles": sorted(set(removed) & LINUX_OMISSIONS),
        "retainedPdbPaths": sorted(path for path in control if path.endswith(".pdb")),
        "packages": len(new["packages"]),
        "changedPaths": sorted(path for path in candidate if candidate[path] != control.get(path)),
    }
