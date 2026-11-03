#!/usr/bin/env python3
"""Inspect actual HTTP MCP image files and a same-source RID-neutral control."""
import argparse
import hashlib
import json
from pathlib import Path
import re


DISTRIBUTIONS = {"http": ("", "NetRatel.Mcp.Http"),
                 "cli": ("tooling/cli", "netratel"),
                 "stdio": ("tooling/stdio", "NetRatel.Mcp")}


def inventory(root):
    files = {path.relative_to(root).as_posix(): {"bytes": path.stat().st_size,
             "sha256": hashlib.sha256(path.read_bytes()).hexdigest()}
             for path in sorted(root.rglob("*")) if path.is_file() and not path.is_symlink()}
    distributions = {}
    for name, (relative, assembly) in DISTRIBUTIONS.items():
        directory = root / relative
        deps = json.loads((directory / (assembly + ".deps.json")).read_text())
        runtime = json.loads((directory / (assembly + ".runtimeconfig.json")).read_text())["runtimeOptions"]
        frameworks = runtime.get("frameworks", [runtime["framework"]] if "framework" in runtime else [])
        expected = {"Microsoft.NETCore.App", "Microsoft.AspNetCore.App"} if name == "http" else {"Microsoft.NETCore.App"}
        if {item["name"] for item in frameworks} != expected or "includedFrameworks" in runtime:
            raise ValueError("Distribution is no longer framework-dependent: " + name)
        libraries = {identity.split("/", 1)[0] for identity in deps["libraries"]}
        if name in {"http", "stdio"} and not {
                "NetRatel.Mcp.Core", "ModelContextProtocol", "ModelContextProtocol.Core", "NJsonSchema", "Newtonsoft.Json"} <= libraries:
            raise ValueError("MCP hosting/schema dependencies are missing: " + name)
        if name == "cli" and "NetRatel.Mcp.Core" in libraries:
            raise ValueError("CLI regained an MCP hosting dependency")
        notice = directory / "THIRD-PARTY-NOTICES.txt"
        if not notice.is_file() or not notice.stat().st_size:
            raise ValueError("Distribution legal notices are missing: " + name)
        distributions[name] = {"runtimeTarget": deps["runtimeTarget"]["name"], "frameworks": frameworks,
                               "libraries": sorted(deps["libraries"]),
                               "packages": sorted(identity for identity, value in deps["libraries"].items()
                                                  if value["type"] == "package")}
    grouped = {}
    for name, item in files.items():
        grouped.setdefault(item["sha256"], []).append(name)
    duplicates = [{"sha256": digest, "bytesPerCopy": files[names[0]]["bytes"], "paths": names,
                   "bytesBeyondFirstCopy": files[names[0]]["bytes"] * (len(names) - 1)}
                  for digest, names in grouped.items() if len(names) > 1]
    wrong = {name: value for name, value in files.items()
             if re.search(r"(?:^|/)runtimes/(?:win|osx|android|ios|browser|maccatalyst|tvos)(?:-|/)", name)}
    windows = {name: value for name, value in wrong.items() if re.search(r"(?:^|/)runtimes/win(?:-|/)", name)}
    return {"files": files, "regularFileBytes": sum(item["bytes"] for item in files.values()),
            "fileCount": len(files), "nonLinuxRuntimeAssets": wrong,
            "nonLinuxRuntimeAssetBytes": sum(item["bytes"] for item in wrong.values()),
            "windowsRuntimeAssetBytes": sum(item["bytes"] for item in windows.values()),
            "symbolBytes": sum(item["bytes"] for name, item in files.items() if name.endswith(".pdb")),
            "duplicateBytesBeyondFirstCopy": sum(item["bytesBeyondFirstCopy"] for item in duplicates),
            "duplicates": duplicates, "distributions": distributions,
            "sharedAssemblies": {name: value for name, value in files.items() if name.endswith("/NetRatel.Shared.dll") or name == "NetRatel.Shared.dll"}}


def compare(candidate, control):
    if candidate["nonLinuxRuntimeAssets"]:
        raise ValueError("Linux image still contains wrong-platform runtime assets")
    if not control["nonLinuxRuntimeAssetBytes"]:
        raise ValueError("Control contains no wrong-platform runtime bytes; RID savings are not established")
    for name in DISTRIBUTIONS:
        if not candidate["distributions"][name]["runtimeTarget"].endswith("/linux-x64"):
            raise ValueError("Candidate distribution lacks the selected Linux RID: " + name)
        if "/" in control["distributions"][name]["runtimeTarget"]:
            raise ValueError("Control distribution is not RID-neutral: " + name)
    for name, value in control["files"].items():
        if name.endswith((".sh", ".ps1")) and candidate["files"].get(name) != value:
            raise ValueError("Supported launcher content changed or disappeared: " + name)
    symbols = [name for name in control["files"] if name.endswith(".pdb") and "/runtimes/" not in "/" + name]
    if any(not candidate["files"].get(name, {}).get("bytes") for name in symbols):
        raise ValueError("A retained application symbol file disappeared")
    return {"expandedBytesSaved": control["regularFileBytes"] - candidate["regularFileBytes"],
            "removedFiles": sorted(set(control["files"]) - set(candidate["files"])),
            "addedFiles": sorted(set(candidate["files"]) - set(control["files"])),
            "retainedSymbolPaths": symbols,
            "duplicateContentDisposition": "measured only; no shared files were removed or relinked"}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--candidate", type=Path, required=True)
    parser.add_argument("--control", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--source-sha", required=True)
    args = parser.parse_args()
    candidate, control = inventory(args.candidate), inventory(args.control)
    record = {"sourceSha": args.source_sha, "candidate": candidate, "control": control}
    # Preserve actual inventories even when a comparison assertion fails.
    args.output.write_text(json.dumps(record, indent=2) + "\n")
    record["comparison"] = compare(candidate, control)
    args.output.write_text(json.dumps(record, indent=2) + "\n")
    print(json.dumps({"sourceSha": args.source_sha, "comparison": record["comparison"],
                      **{arm: {key: record[arm][key] for key in ("regularFileBytes", "fileCount", "nonLinuxRuntimeAssetBytes",
                          "windowsRuntimeAssetBytes", "symbolBytes", "duplicateBytesBeyondFirstCopy", "sharedAssemblies")}
                         for arm in ("candidate", "control")}}, indent=2))
