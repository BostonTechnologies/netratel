#!/usr/bin/env python3
"""Package the canonical installer as an offline, identity-preserving repair entry point."""
from __future__ import annotations

import argparse
import json
from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[2]
TEMPLATE = ROOT / "src/NetRatel/NetRatel.Infrastructure/Artifacts/Templates/install.ps1"


def render(manifest: dict) -> str:
    runtime = manifest.get("runtimeId", "")
    version = manifest.get("version", "")
    source = manifest.get("commitSha", "")
    if (manifest.get("schema") != "netratel.client.manifest.v1"
            or manifest.get("product") != "NetRatel.Client"
            or runtime not in ("win-x64", "win-arm64")
            or manifest.get("executable") != "NetRatel.Client.exe"
            or not re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?", version)
            or not re.fullmatch(r"[0-9a-fA-F]{40}", source)):
        raise ValueError("A valid published Windows client manifest is required.")
    values = {
        "API_BASE_URL": "''", "GATEWAY_ENDPOINT": "''", "TENANT_ID": "0",
        "ENROLLMENT_CODE": "''", "RUNTIME_ID": f"'{runtime}'",
        "ARTIFACT_VERSION": f"'{version}'", "ARTIFACT_SHA256": "''",
        "VALID_TO_UTC": "''", "INSTALL_AS_SERVICE": "$true", "SILENT_INSTALL": "$true",
        "SEED_INITIALIZE": "", "SEED_BEFORE_MUTATION": "", "SEED_SUCCESS": "",
        "SEED_FAILURE": "", "SEED_CLEANUP": "",
        # Offline recovery rejects missing services; this normal-install branch is unreachable.
        "WINDOWS_RECOVERY_RESET": "86400",
        "WINDOWS_RECOVERY_ACTIONS": "'restart/30000/restart/60000/restart/300000'",
    }
    content = TEMPLATE.read_text(encoding="utf-8")
    names = set(re.findall(r"@@([A-Z0-9_]+)@@", content))
    if names != set(values):
        raise ValueError(f"Installer template parameters changed: {sorted(names ^ set(values))}")
    for name, value in values.items():
        content = content.replace(f"@@{name}@@", value)
    marker = "$offlineRepair = $false"
    if content.count(marker) != 1:
        raise ValueError("The installer offline-repair boundary is missing or ambiguous.")
    content = content.replace(marker, """$offlineRepair = $true
if ([bool]$PackageVersion -ne [bool]$PackageSourceRevision) { throw 'Specify both the approved package version and source, or neither.' }
if ($PackageVersion) { $Version = $PackageVersion; $repairSourceRevision = $PackageSourceRevision }""")
    header = """# Generated from the canonical Windows installer; verify the containing release archive first.
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$ArchivePath,
    [Parameter(Mandatory=$true)][ValidatePattern('^[a-fA-F0-9]{64}$')][string]$Sha256,
    [ValidateSet('Preview','Apply')][string]$Mode = 'Preview',
    [ValidatePattern('^[0-9]+\\.[0-9]+\\.[0-9]+(?:-[0-9A-Za-z.-]+)?(?:\\+[0-9A-Za-z.-]+)?$')][string]$PackageVersion,
    [ValidatePattern('^[a-fA-F0-9]{40}$')][string]$PackageSourceRevision
)
"""
    return header + f"$repairSourceRevision = '{source}'\n" + content


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--distribution", required=True, type=Path)
    parser.add_argument("--check", action="store_true")
    args = parser.parse_args()
    manifest = json.loads((args.distribution / "netratel-client-manifest.json").read_text(encoding="utf-8-sig"))
    if not manifest.get("runtimeId", "").startswith("win-"):
        return
    expected = render(manifest).encode("utf-8")
    destination = args.distribution / "updater/repair-windows-client.ps1"
    if args.check:
        if not destination.is_file() or destination.read_bytes() != expected:
            raise SystemExit("Packaged Windows recovery differs from the validated installer and manifest.")
    else:
        destination.parent.mkdir(parents=True, exist_ok=True)
        destination.write_bytes(expected)


if __name__ == "__main__":
    main()
