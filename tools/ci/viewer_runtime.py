"""Validate the first-party provenance shipped with the Web viewer files."""

import base64
import hashlib
import json
import re
from pathlib import Path

VIEWER_ROOT = Path("wwwroot/js/viewers")
METADATA = "runtime-package.json"
SCHEMA = "netratel.viewer-runtime.v1"
REQUIRED_FILES = {
    "docx-preview": {"LICENSE", "docx-preview.min.js"},
    "jszip": {"LICENSE.markdown", "jszip.min.js"},
    "pdfjs-dist": {
        "LICENSE", "build/pdf.min.mjs", "build/pdf.worker.min.mjs",
        "cmaps/LICENSE", "standard_fonts/LICENSE_FOXIT", "wasm/LICENSE_PDFJS_OPENJPEG",
    },
}
JSZIP_EMBEDDED = {"immediate", "lie", "pako", "setimmediate"}


def sha256(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def npm_source(name, version):
    return f"https://registry.npmjs.org/{name}/-/{name}-{version}.tgz"


def validate_package(package):
    name, version = package["name"], package["version"]
    if (not re.fullmatch(r"[a-z0-9][a-z0-9-]*", name)
            or not re.fullmatch(r"\d+\.\d+\.\d+(?:-[a-zA-Z0-9.-]+)?", version)
            or package["source"] != npm_source(name, version)
            or not package["license"].strip()):
        raise ValueError(f"Invalid npm runtime provenance for {name}@{version}")
    integrity = package["integrity"]
    if not re.fullmatch(r"sha512-[A-Za-z0-9+/]{86}==", integrity) or len(base64.b64decode(integrity[7:])) != 64:
        raise ValueError(f"Invalid npm integrity for {name}@{version}")


def viewer_packages(distribution):
    """Return package metadata and verify complete coverage of the published viewer directory."""
    root = distribution / VIEWER_ROOT
    if not root.is_dir() or root.is_symlink() or {path.name for path in root.iterdir()} != REQUIRED_FILES.keys():
        raise ValueError(f"Missing or unexpected shipped browser viewer packages in {root}")

    result = []
    for name, required in sorted(REQUIRED_FILES.items()):
        versions = list((root / name).iterdir())
        if len(versions) != 1 or not versions[0].is_dir() or (root / name).is_symlink():
            raise ValueError(f"Expected one shipped version of {name}")
        directory = versions[0]
        provenance = directory / METADATA
        if directory.is_symlink() or not provenance.is_file() or provenance.is_symlink():
            raise ValueError(f"Missing runtime provenance for {name}")
        package = json.loads(provenance.read_text(encoding="utf-8"))
        validate_package(package)
        if (package["schema"], package["name"], package["version"]) != (SCHEMA, name, directory.name):
            raise ValueError(f"Runtime provenance identifies the wrong viewer: {directory}")

        files = package["files"]
        licenses = package["licenseFiles"]
        paths = list(directory.rglob("*"))
        actual = {path.relative_to(directory).as_posix() for path in paths if path.is_file() and path != provenance}
        if (any(path.is_symlink() or path.name in {"package.json", "package-lock.json"} for path in paths)
                or set(files) != actual or not required <= actual or not licenses or set(licenses) - actual):
            raise ValueError(f"Incomplete or unexpected runtime file inventory for {name}")
        if not {path for path in actual if Path(path).name.upper().startswith(("LICENSE", "LICENCE", "NOTICE"))} <= set(licenses):
            raise ValueError(f"Incomplete runtime license inventory for {name}")
        for path, digest in files.items():
            if sha256(directory / path) != digest:
                raise ValueError(f"Viewer asset checksum mismatch for {name}/{path}")

        embedded = package["embeddedDependencies"]
        if {item["name"] for item in embedded} != (JSZIP_EMBEDDED if name == "jszip" else set()):
            raise ValueError(f"Incomplete embedded npm inventory for {name}")
        seen = set()
        for item in embedded:
            validate_package(item)
            if item["name"] in seen or item["licenseFiles"] != [
                    path for path in licenses if path.startswith(f"licenses/{item['name']}/")]:
                raise ValueError(f"Inconsistent embedded npm provenance for {item['name']}")
            if not item["licenseFiles"]:
                raise ValueError(f"Missing embedded npm license for {item['name']}")
            seen.add(item["name"])
        result.append((directory, package))

    versions = {package["name"]: package["version"] for _, package in result}
    for _, package in result:
        expected = {"jszip": versions["jszip"]} if package["name"] == "docx-preview" else {}
        if package["externalDependencies"] != expected:
            raise ValueError(f"Incorrect external runtime graph for {package['name']}")
    return result


def viewer_notices(viewers):
    paragraphs = []
    for directory, package in viewers:
        identity = f"{package['name']} {package['version']}"
        lines = [identity, f"Source: {package['source']}", f"License: {package['license']}"]
        for dependency in package["embeddedDependencies"]:
            lines.append(f"Embedded: {dependency['name']} {dependency['version']} ({dependency['license']})")
        for relative in package["licenseFiles"]:
            lines.extend([f"--- {relative} ---", (directory / relative).read_text(encoding="utf-8-sig")])
        paragraphs.append("\n".join(lines))
    return "\nSelf-hosted browser runtime packages\n\n" + "\n\n".join(paragraphs) + "\n"
