#!/usr/bin/env python3
"""Inspect every referenced physical layer in an extracted Docker/OCI save."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import tarfile
import tempfile


PATTERN = (r"-----BEGIN [A-Z ]*PRIVATE KEY|<key[[:space:]>]|"
           r"<encryptedKey[[:space:]>]|AKIA[0-9A-Z]{16}|gh[pousr]_[A-Za-z0-9_]{20,}")
# Exact public upstream bytes reviewed in #281. A changed file or any other
# matching file fails; retain and report the raw match rather than suppress it.
REVIEWED = {
    "usr/share/zsh/vendor-completions/_curl": "b47429e50b5a814d18190a7d374367c12762ac0ab04fc1140a6a2cb15a9743fc",
    "usr/lib/python3.12/_osx_support.py": "363d3240acbba18a270bd3161f1ddb478f8492dc14fc451b2dc314db5c5ee09c",
    "usr/lib/python3.12/http/cookies.py": "c5e25f6245cc6578e8c208b3a1f5b13bb6b1f40a37f8d2760d4f02be741ff4a7",
    "usr/lib/python3.12/locale.py": "d1134b7212bad37b7b81339061dc37a19a194aa183ec2cb60412cf365993b151",
    "usr/lib/python3.12/plistlib.py": "7f427a3b717507598c73bae26ff09d3fb7664540d9b7209ca97be80c76648069",
}


def contained(root, reference):
    if not isinstance(reference, str) or not reference or Path(reference).is_absolute():
        raise ValueError("Invalid saved-image layer reference")
    candidate = root / reference
    for component in (candidate, *candidate.parents):
        if component == root:
            break
        if component.is_symlink():
            raise ValueError("Saved-image references cannot use symbolic links")
    candidate = candidate.resolve(strict=True)
    if not candidate.is_relative_to(root) or not candidate.is_file():
        raise ValueError("Saved-image reference must resolve to a contained regular file")
    return candidate


def layers(root):
    manifest = root / "manifest.json"
    if manifest.is_file():
        images = json.loads(manifest.read_text())
        if not isinstance(images, list) or not images:
            raise ValueError("Saved image has no manifests")
        for image in images:
            references = image.get("Layers")
            if not isinstance(references, list) or not references:
                raise ValueError("Saved image manifest has no layers")
            for reference in references:
                path = contained(root, reference)
                # OCI content-addressed references must identify the actual bytes.
                canonical = path.relative_to(root).as_posix()
                if canonical.startswith("blobs/sha256/"):
                    verify_digest(path, "sha256:" + canonical.removeprefix("blobs/sha256/"))
                yield path
        return
    visited = set()

    def descriptors(document):
        for descriptor in document.get("manifests", []):
            digest = descriptor["digest"]
            path = blob(digest)
            if digest not in visited:
                visited.add(digest)
                yield from descriptors(json.loads(path.read_text()))
        for descriptor in document.get("layers", []):
            yield blob(descriptor["digest"])

    def blob(digest):
        if not isinstance(digest, str) or not digest.startswith("sha256:"):
            raise ValueError("Unsupported OCI digest")
        path = contained(root, "blobs/sha256/" + digest.removeprefix("sha256:"))
        verify_digest(path, digest)
        return path

    yield from descriptors(json.loads((root / "index.json").read_text()))


def verify_digest(path, digest):
    with path.open("rb") as source:
        actual = "sha256:" + hashlib.file_digest(source, "sha256").hexdigest()
    if actual != digest:
        raise ValueError("OCI blob digest does not match its manifest reference")


def grep_files(directory, pattern):
    result = subprocess.run(["grep", "-rI", "-lZ", "-E", "--", pattern, str(directory)],
                            env={**os.environ, "LC_ALL": "C"}, stdout=subprocess.PIPE, check=False)
    if result.returncode not in (0, 1):
        raise ValueError("Public image credential scanner failed")
    return [os.fsdecode(path) for path in result.stdout.split(b"\0") if path]


def scan(root):
    root = Path(root).resolve(strict=True)
    references = list(layers(root))
    unique = list(dict.fromkeys(references))
    coverage = {"layerReferences": len(references), "uniqueLayers": len(unique),
                "regularFiles": 0, "regularBytes": 0, "textFiles": 0,
                "rawMatchingFiles": 0, "reviewedBenignMatches": []}
    failures = []
    with tempfile.TemporaryDirectory(prefix="netratel-layer-scan-") as directory:
        destination = Path(directory) / "layer"
        for layer in unique:
            destination.mkdir()
            paths = {}
            with tarfile.open(layer, "r:*") as archive:
                for member in archive:
                    # Links/whiteouts need no extraction; inspect every regular
                    # entry in every layer, including overwritten/deleted files.
                    if not member.isfile() or member.size == 0:
                        continue
                    coverage["regularFiles"] += 1
                    coverage["regularBytes"] += member.size
                    target_path = destination / str(len(paths))
                    paths[str(target_path)] = member.name.removeprefix("./")
                    with archive.extractfile(member) as source, target_path.open("wb") as target:
                        shutil.copyfileobj(source, target)
            # Two grep invocations per layer rather than one process per file;
            # scan time scales with content, including large runtime images.
            matches = grep_files(destination, PATTERN)
            coverage["textFiles"] += len(grep_files(destination, "."))
            for match in matches:
                coverage["rawMatchingFiles"] += 1
                with Path(match).open("rb") as content:
                    digest = hashlib.file_digest(content, "sha256").hexdigest()
                name = paths[match]
                if REVIEWED.get(name) == digest:
                    coverage["reviewedBenignMatches"].append({"path": name, "sha256": digest,
                                                             "rawGrepExit": 0, "review": "#281"})
                else:
                    # Do not echo possible secret material or attacker-
                    # supplied filenames in publication logs.
                    failures.append(digest)
            shutil.rmtree(destination)
    print(json.dumps(coverage, sort_keys=True), flush=True)
    if not unique or not coverage["regularBytes"] or not coverage["textFiles"]:
        raise ValueError("Public image scan inspected no nonempty regular text content")
    if failures:
        raise ValueError(f"Public image contains potential credential material in {len(failures)} file(s)")
    return coverage


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directory", type=Path)
    try:
        scan(parser.parse_args().directory)
    except (OSError, KeyError, TypeError, tarfile.TarError, json.JSONDecodeError):
        raise SystemExit("Public image scan could not read valid contained image content")
    except ValueError as error:
        raise SystemExit(str(error))
