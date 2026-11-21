#!/usr/bin/env python3
"""Copy or verify the browser files selected from integrity-checked npm packages."""

import argparse
import hashlib
import json
import shutil
from pathlib import Path

from viewer_runtime import METADATA, SCHEMA, VIEWER_ROOT, npm_source, viewer_packages

ROOT = Path(__file__).resolve().parents[2]
TOOL = ROOT / "tools/viewer-assets"
WEB = ROOT / "src/NetRatel/NetRatel.Web"
ASSET_ROOT = WEB / VIEWER_ROOT

# The official JSZip release pairs these minified and readable upstream files.
# A JSZip bump requires reviewing that pair and the embedded modules again.
JSZIP_RELEASE_FILES = {
    "dist/jszip.js": "e2e1be5c22d3f15b0b89e676d3eede94644529996d9898a6b1587c9908a78506",
    "dist/jszip.min.js": "acc7e41455a80765b5fd9c7ee1b8078a6d160bbbca455aeae854de65c947d59e",
}
EMBEDDED_FILES = {
    "immediate": ("lib/browser.js", "LICENSE.txt"),
    "lie": ("lib/browser.js", "license.md"),
    "pako": ("index.js", "LICENSE"),
    "setimmediate": ("setImmediate.js", "LICENSE.txt"),
}
# JSZip substitutes its own Node stream stub in this Browserify build.
# readable-stream remains in the audited npm graph and has no browser package entry.


def package_identity(lock, directory):
    metadata = json.loads((directory / "package.json").read_text(encoding="utf-8"))
    name, version = metadata["name"], metadata["version"]
    entry = lock["packages"][f"node_modules/{name}"]
    if (entry["version"], entry["resolved"], entry["license"]) != (version, npm_source(name, version), metadata["license"]):
        raise ValueError(f"npm lock record does not match installed {name}@{version}")
    return {key: metadata[key] for key in ("name", "version", "license")} | {
        "source": entry["resolved"], "integrity": entry["integrity"],
    }


def embed_jszip_dependencies(lock, modules, jszip, assets):
    for relative, checksum in JSZIP_RELEASE_FILES.items():
        if hashlib.sha256((jszip / relative).read_bytes()).hexdigest() != checksum:
            raise ValueError("The JSZip bundle pair changed. Review the upstream release and embedded modules.")
    bundle = (jszip / "dist/jszip.js").read_text(encoding="utf-8")
    embedded = []
    for name, (entry, license_file) in EMBEDDED_FILES.items():
        directory = modules / name
        sources = [directory / entry]
        if name == "pako":
            sources.extend(sorted((directory / "lib").rglob("*.js")))
        if any(source.read_text(encoding="utf-8").strip() not in bundle for source in sources):
            raise ValueError(f"Shipped JSZip does not contain the locked {name} browser sources")
        identity = package_identity(lock, directory)
        relative = f"licenses/{name}/{license_file}"
        assets[relative] = directory / license_file
        embedded.append(identity | {"licenseFiles": [relative]})
    return embedded


def prepare(write):
    manifest = json.loads((TOOL / "package.json").read_text(encoding="utf-8"))
    lock = json.loads((TOOL / "package-lock.json").read_text(encoding="utf-8"))
    modules = TOOL / "node_modules"
    # DOCX's UMD browser branch uses the external JSZip global. The npm
    # dependency must likewise resolve to the common exact JSZip install.
    docx = modules / "docx-preview"
    if (docx / "node_modules/jszip").exists() or "node_modules/docx-preview/node_modules/jszip" in lock["packages"]:
        raise ValueError("docx-preview must resolve the same JSZip version as the browser")
    if ".docx={},e.JSZip)" not in (docx / "dist/docx-preview.min.js").read_text(encoding="utf-8"):
        raise ValueError("Review the changed docx-preview browser wrapper and its JSZip dependency")

    explorer = (WEB / "wwwroot/js/full-explorer.js").read_text(encoding="utf-8")
    for name, version in sorted(manifest["dependencies"].items()):
        package = modules / name
        identity = package_identity(lock, package)
        if identity["version"] != version or f"/js/viewers/{name}/{version}/" not in explorer:
            raise ValueError(f"Expected the exact locked {name}@{version} in full-explorer.js")
        embedded = []
        external = {}
        if name == "docx-preview":
            assets = {"docx-preview.min.js": package / "dist/docx-preview.min.js", "LICENSE": package / "LICENSE"}
            external = {"jszip": manifest["dependencies"]["jszip"]}
        elif name == "jszip":
            assets = {"jszip.min.js": package / "dist/jszip.min.js", "LICENSE.markdown": package / "LICENSE.markdown"}
            embedded = embed_jszip_dependencies(lock, modules, package, assets)
        elif name == "pdfjs-dist":
            assets = {file: package / file for file in ("LICENSE", "build/pdf.min.mjs", "build/pdf.worker.min.mjs")}
            for subtree in ("cmaps", "standard_fonts", "wasm"):
                assets.update({path.relative_to(package).as_posix(): path
                               for path in (package / subtree).rglob("*") if path.is_file()})
        else:
            raise ValueError(f"No browser asset selection for {name}")

        directory = ASSET_ROOT / name / version
        content = {relative: path.read_bytes() for relative, path in sorted(assets.items())}
        provenance = identity | {
            "schema": SCHEMA,
            "generatedBy": "tools/ci/prepare-viewer-assets.py",
            "externalDependencies": external,
            "embeddedDependencies": embedded,
            "licenseFiles": [relative for relative in content
                             if Path(relative).name.upper().startswith(("LICENSE", "LICENCE", "NOTICE"))],
            "files": {relative: hashlib.sha256(data).hexdigest() for relative, data in content.items()},
        }
        content[METADATA] = (json.dumps(provenance, indent=2) + "\n").encode()
        if write:
            for stale in (ASSET_ROOT / name).glob("*"):
                if stale != directory and stale.is_dir() and (stale / METADATA).is_file():
                    shutil.rmtree(stale)
            (directory / "package.json").unlink(missing_ok=True)
            for relative, data in content.items():
                path = directory / relative
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes(data)
        else:
            for relative, data in content.items():
                path = directory / relative
                if not path.is_file() or path.read_bytes() != data:
                    raise ValueError(f"Committed viewer asset differs from npm inputs: {path.relative_to(ROOT)}")
    viewer_packages(WEB)
    action = "Regenerated and validated" if write else "Verified"
    print(f"{action} browser viewer assets, embedded JSZip modules and runtime provenance.")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--write", action="store_true", help="regenerate committed runtime files")
    args = parser.parse_args()
    try:
        prepare(args.write)
    except (KeyError, OSError, ValueError) as error:
        parser.exit(1, f"Invalid viewer vendoring input: {error}\n")
