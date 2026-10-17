#!/usr/bin/env python3
"""Verify the Windows MCP ZIP, complete SPDX inventory and shipped launcher bytes."""
import argparse
import hashlib
import importlib.util
import json
from pathlib import Path, PurePosixPath
import re
import shutil
import stat
import tempfile
import zipfile


ROOT = Path(__file__).resolve().parents[2]
DISTRIBUTION = "netratel-mcp-win-x64"
REQUIRED = {
    "NetRatel.Mcp.exe", "NetRatel.Mcp.dll", "NetRatel.Mcp.deps.json",
    "NetRatel.Mcp.runtimeconfig.json", "LICENSE", "NOTICE", "THIRD-PARTY-NOTICES.txt",
    "CLI_AND_MCP.md", "http-mcp-bridge.md", "http-mcp-windows.ps1", "HttpMcpProcessRelay.cs",
}


def sha256(path):
    with path.open("rb") as source:
        return hashlib.file_digest(source, "sha256").hexdigest()


def safe_entry(name):
    path = PurePosixPath(name.rstrip("/"))
    if not name or "\\" in name or ":" in name or path.is_absolute() or \
            any(part in {"", ".", ".."} for part in name.rstrip("/").split("/")) or \
            not path.parts or path.parts[0] != DISTRIBUTION:
        raise ValueError("Windows MCP archive contains an unsafe path")
    return path


def verify(artifacts, version, source_root=ROOT):
    if not re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?", version):
        raise ValueError("Invalid release version")
    archive_name = f"netratel-mcp-stdio-{version}-win-x64.zip"
    sbom_name = f"netratel-mcp-stdio-{version}-win-x64.spdx.json"
    indexed = {}
    for line in (artifacts / "SHA256SUMS").read_text(encoding="utf-8").splitlines():
        parts = line.split(maxsplit=1)
        if len(parts) != 2:
            raise ValueError("Malformed Windows MCP checksum index")
        digest, name = parts[0], parts[1].lstrip("*")
        if name in indexed or Path(name).name != name or not re.fullmatch(r"[a-f0-9]{64}", digest):
            raise ValueError("Unsafe or duplicate Windows MCP checksum entry")
        indexed[name] = digest
    if set(indexed) != {archive_name, sbom_name}:
        raise ValueError("Windows MCP checksum index must cover exactly the ZIP and SPDX")
    for name, digest in indexed.items():
        path = artifacts / name
        if path.is_symlink() or not path.is_file() or sha256(path) != digest:
            raise ValueError("Windows MCP artifact checksum mismatch")

    document = json.loads((artifacts / sbom_name).read_text(encoding="utf-8"))
    if document.get("name") != "NetRatel-MCP-win-x64" or \
            not any(item.get("name") == document["name"] and item.get("versionInfo") == version
                    for item in document.get("packages", [])):
        raise ValueError("Windows MCP SPDX has the wrong distribution or version")
    with zipfile.ZipFile(artifacts / archive_name) as archive, tempfile.TemporaryDirectory(prefix="netratel-windows-mcp-") as temporary:
        root = Path(temporary)
        members = {}
        seen = set()
        for entry in archive.infolist():
            path = safe_entry(entry.filename)
            folded = path.as_posix().casefold()
            mode = entry.external_attr >> 16
            if folded in seen or stat.S_ISLNK(mode) or stat.S_IFMT(mode) not in {0, stat.S_IFREG, stat.S_IFDIR}:
                raise ValueError("Windows MCP archive contains duplicate or special entries")
            seen.add(folded)
            if entry.is_dir():
                continue
            if path.suffix.casefold() == ".clixml":
                raise ValueError("Protected runtime credentials must not be packaged")
            members[path.as_posix()] = entry
        required = {f"{DISTRIBUTION}/{name}" for name in REQUIRED}
        if not required.issubset(members) or any(members[name].file_size == 0 for name in required):
            raise ValueError("Windows MCP archive is missing runtime, documentation or launcher content")
        inventory = [str(PurePosixPath(item["fileName"])) for item in document.get("files", [])]
        if len(inventory) != len(set(inventory)) or set(inventory) != set(members):
            raise ValueError("Windows MCP SPDX must inventory every archive file exactly once")
        for name, entry in members.items():
            target = root / name
            target.parent.mkdir(parents=True, exist_ok=True)
            with archive.open(entry) as source, target.open("wb") as output:
                shutil.copyfileobj(source, output)
        distribution = root / DISTRIBUTION
        for name, source in {
            "http-mcp-windows.ps1": source_root / "tools/client/http-mcp-windows.ps1",
            "HttpMcpProcessRelay.cs": source_root / "tools/client/HttpMcpProcessRelay.cs",
            "LICENSE": source_root / "LICENSE", "NOTICE": source_root / "NOTICE",
        }.items():
            if (distribution / name).read_bytes() != source.read_bytes():
                raise ValueError("Windows MCP launcher or legal content differs from the validated source")
        runtime = json.loads((distribution / "NetRatel.Mcp.runtimeconfig.json").read_text(encoding="utf-8"))["runtimeOptions"]
        frameworks = runtime.get("frameworks", []) + ([runtime["framework"]] if "framework" in runtime else [])
        if runtime.get("tfm") != "net10.0" or runtime.get("includedFrameworks") or \
                not any(item.get("name") == "Microsoft.NETCore.App" and str(item.get("version", "")).startswith("10.")
                        for item in frameworks):
            raise ValueError("Windows MCP must be framework-dependent on .NET 10")
        deps = json.loads((distribution / "NetRatel.Mcp.deps.json").read_text(encoding="utf-8"))
        if not deps.get("runtimeTarget", {}).get("name", "").endswith("/win-x64"):
            raise ValueError("Windows MCP runtime graph must target win-x64")
        marker = re.compile(rb"-----BEGIN ([A-Z ]*PRIVATE KEY|CERTIFICATE)|AKIA[0-9A-Z]{16}|gh[pousr]_[A-Za-z0-9_]{20,}")
        for name in members:
            content = (root / name).read_bytes()
            if b"\0" not in content[:4096] and marker.search(content):
                raise ValueError("Windows MCP archive contains potential key material")
        spec = importlib.util.spec_from_file_location("runtime_sbom_verifier", ROOT / "tools/ci/verify-runtime-sbom.py")
        verifier = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(verifier)
        verifier.verify(document, root)
    return {"runtime": "win-x64", "version": version, "archiveSha256": indexed[archive_name],
            "sbomSha256": indexed[sbom_name], "filesVerified": len(members)}


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--artifacts", required=True, type=Path)
    parser.add_argument("--version", required=True)
    args = parser.parse_args()
    try:
        print(json.dumps(verify(args.artifacts, args.version), sort_keys=True))
    except (ValueError, OSError, KeyError, TypeError, zipfile.BadZipFile) as error:
        parser.exit(1, f"Invalid Windows MCP archive: {error}\n")
