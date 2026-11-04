#!/usr/bin/env python3
"""Run the shared protocol probes on an authenticated Windows FDD archive."""
import argparse
from contextlib import contextmanager
import ctypes
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import re
import shutil
import ssl
import subprocess
import sys
import zipfile


TOOLS = Path(__file__).resolve().parent


def module(name):
    spec = importlib.util.spec_from_file_location(name, TOOLS / (name + ".py"))
    loaded = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(loaded)
    return loaded


def crypt32():
    # Resolve the system DLL, never a DLL beside the candidate archive.
    api = ctypes.WinDLL("crypt32.dll", use_last_error=True, winmode=0x800)
    pointer, word = ctypes.c_void_p, ctypes.c_uint32
    api.CertOpenStore.argtypes = [pointer, word, pointer, word, pointer]
    api.CertOpenStore.restype = pointer
    api.CertAddEncodedCertificateToStore.argtypes = [pointer, word, pointer, word, word, ctypes.POINTER(pointer)]
    api.CertAddEncodedCertificateToStore.restype = ctypes.c_int
    api.CertDeleteCertificateFromStore.argtypes = [pointer]
    api.CertDeleteCertificateFromStore.restype = ctypes.c_int
    api.CertCloseStore.argtypes = [pointer, word]
    api.CertCloseStore.restype = ctypes.c_int
    return api


def windows_error(operation):
    return OSError(f"Synthetic Windows certificate {operation} failed: Win32 {ctypes.get_last_error()}")


@contextmanager
def trusted_certificate(certificate):
    """Trust only this fresh fixture CA in CurrentUser/Root, then remove it."""
    encoded = ssl.PEM_cert_to_DER_cert(certificate.read_text(encoding="ascii"))
    record = {"store": "CurrentUser/Root", "sha256": hashlib.sha256(encoded).hexdigest(),
              "added": False, "removed": False}
    api = crypt32()
    name = ctypes.create_unicode_buffer("ROOT")
    # CERT_STORE_PROV_SYSTEM_W; CURRENT_USER | OPEN_EXISTING. No machine store.
    store = api.CertOpenStore(10, 0, None, 0x10000 | 0x4000, ctypes.cast(name, ctypes.c_void_p))
    if not store:
        raise windows_error("store open")
    context = ctypes.c_void_p()
    try:
        data = ctypes.create_string_buffer(encoded)
        # CERT_STORE_ADD_NEW refuses an existing certificate: never own/delete it.
        if not api.CertAddEncodedCertificateToStore(store, 0x10001, data, len(encoded), 1, ctypes.byref(context)):
            raise windows_error("add")
        record["added"] = True
        yield record
    finally:
        errors = []
        if record["added"]:
            # This API also frees the returned context, including on failure.
            if api.CertDeleteCertificateFromStore(context):
                record["removed"] = True
            else:
                errors.append(str(windows_error("remove")))
        if not api.CertCloseStore(store, 0):
            errors.append(str(windows_error("store close")))
        if errors:
            raise OSError("; ".join(errors))


def run(artifacts, source, work, evidence, version, revision):
    if os.name != "nt":
        raise ValueError("Windows archive execution requires a matching Windows host")
    if not re.fullmatch(r"[a-f0-9]{40}", revision):
        raise ValueError("A full selected source revision is required")
    actual_revision = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=source, text=True).strip()
    actual_version = subprocess.check_output([sys.executable, source / "tools/ci/product-version.py"],
                                             cwd=source, text=True).strip()
    if (revision, version) != (actual_revision, actual_version):
        raise ValueError("Selected archive source/version differs from the checked-out source")
    work.mkdir(parents=True)
    evidence.mkdir(parents=True)
    record = {"runtime": "win-x64", "sourceSha": revision, "productVersion": version, "passed": False,
              "scope": "actual Windows FDD archive and shared synthetic HTTPS protocol probes; no live policy claim"}
    fixture = None
    trust = None
    try:
        verifier = module("verify-windows-mcp-archive")
        record["integrity"] = verifier.verify(artifacts, version, source)
        archive = work / "original.zip"
        shutil.copyfile(artifacts / f"netratel-mcp-stdio-{version}-win-x64.zip", archive)
        if verifier.sha256(archive) != record["integrity"]["archiveSha256"]:
            raise ValueError("Original Windows archive changed after integrity verification")
        extracted = work / "archive"
        with zipfile.ZipFile(archive) as bundle:
            # The copied ZIP has the exact verified digest and safe inventory.
            bundle.extractall(extracted)
        probes = module("probe-stdio-assessment")
        fixture = probes.Fixture(work / "https-fixture")
        with trusted_certificate(fixture.certificate) as trust:
            record["probes"] = probes.probe(extracted / verifier.DISTRIBUTION / "NetRatel.Mcp.exe",
                evidence / "probes", work / "profile", version, revision, fixture)
        record["passed"] = record["probes"]["passed"] and trust["added"] and trust["removed"]
    except (ValueError, OSError, KeyError, subprocess.CalledProcessError, zipfile.BadZipFile) as error:
        record["error"] = type(error).__name__ + ": " + str(error)
    finally:
        if fixture is not None:
            fixture.close()
        record["certificateTrust"] = trust
        (evidence / "result.json").write_text(json.dumps(record, indent=2) + "\n", encoding="utf-8")
    return record


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--artifacts", required=True, type=Path)
    parser.add_argument("--source", required=True, type=Path)
    parser.add_argument("--work-root", required=True, type=Path)
    parser.add_argument("--evidence", required=True, type=Path)
    parser.add_argument("--version", required=True)
    parser.add_argument("--source-revision", required=True)
    args = parser.parse_args()
    try:
        record = run(args.artifacts.resolve(), args.source.resolve(), args.work_root.resolve(),
                     args.evidence.resolve(), args.version, args.source_revision)
    except (ValueError, OSError, subprocess.CalledProcessError) as error:
        parser.exit(1, str(error) + "\n")
    print(json.dumps(record, sort_keys=True))
    return 0 if record["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
