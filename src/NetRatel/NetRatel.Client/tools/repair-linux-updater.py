#!/usr/bin/env python3
"""Replace an installed Linux updater from a verified Client release archive.

This repairs an updater that cannot accept a newer release itself. It does not
activate a Client package or change enrollment state; the normal update flow
does that after the repair.
"""

import argparse
import fcntl
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import stat
import tarfile
import tempfile


def archive_member(archive, name, maximum_size):
    matches = [member for member in archive if member.name == name]
    if len(matches) != 1 or not matches[0].isfile() or not 0 < matches[0].size <= maximum_size:
        raise ValueError(f"Client archive must contain one regular {name} within its size limit")
    return archive.extractfile(matches[0])


def repair(archive_path, expected_sha256, updater_path, lock_path):
    if not re.fullmatch(r"[0-9a-fA-F]{64}", expected_sha256):
        raise ValueError("Expected archive SHA-256 must be 64 hexadecimal characters")
    if archive_path.stat().st_size > 2 * 1024 * 1024 * 1024:
        raise ValueError("Client archive exceeds the 2 GiB repair limit")

    digest = hashlib.sha256()
    with archive_path.open("rb") as source:
        for block in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(block)
    if digest.hexdigest() != expected_sha256.lower():
        raise ValueError("Client archive SHA-256 does not match the approved checksum")

    prefix = "netratel-client-linux-x64/"
    with tarfile.open(archive_path, "r:gz") as archive:
        with archive_member(archive, prefix + "netratel-client-manifest.json", 1024 * 1024) as source:
            manifest = json.load(source)
        if not isinstance(manifest, dict):
            raise ValueError("Client archive manifest must be an object")
        version = manifest.get("version")
        commit = manifest.get("commitSha")
        if (manifest.get("schema") != "netratel.client.manifest.v1" or
                manifest.get("product") != "NetRatel.Client" or
                manifest.get("runtimeId") != "linux-x64" or
                manifest.get("executable") != "NetRatel.Client" or
                not isinstance(commit, str) or not re.fullmatch(r"[0-9a-fA-F]{40}", commit) or
                not isinstance(version, str) or
                not re.fullmatch(r"(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)"
                                 r"(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?", version)):
            raise ValueError("Client archive manifest is not a Linux x64 release manifest")
        with archive_member(archive, prefix + "updater/netratel-update.sh", 256 * 1024) as source:
            replacement = source.read()
    if not replacement.startswith(b"#!/usr/bin/env bash\n"):
        raise ValueError("Client archive updater is not the expected shell script")

    if updater_path.is_symlink() or not updater_path.is_file():
        raise ValueError("Installed updater must be a regular file, not a symlink")
    installed = updater_path.stat()
    if not stat.S_ISREG(installed.st_mode):
        raise ValueError("Installed updater must be a regular file")

    # The installed updater holds this lock for its entire activation attempt.
    lock_descriptor = os.open(lock_path, os.O_RDWR | os.O_CREAT | os.O_NOFOLLOW | os.O_CLOEXEC, 0o600)
    with os.fdopen(lock_descriptor, "r+b") as lock:
        if not stat.S_ISREG(os.fstat(lock.fileno()).st_mode):
            raise ValueError("Updater lock must be a regular file")
        try:
            fcntl.flock(lock.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError as error:
            raise ValueError("An updater attempt is running; retry after it completes") from error

        if updater_path.read_bytes() == replacement:
            print(f"Updater already matches the verified {manifest['version']} archive")
            return

        backup = updater_path.with_name(updater_path.name + ".before-repair." + digest.hexdigest()[:12])
        if backup.exists() or backup.is_symlink():
            raise ValueError(f"Backup already exists; inspect it before retrying: {backup}")
        with updater_path.open("rb") as original, backup.open("xb") as saved:
            shutil.copyfileobj(original, saved)
            saved.flush()
            os.fsync(saved.fileno())
        os.chown(backup, installed.st_uid, installed.st_gid)
        os.chmod(backup, stat.S_IMODE(installed.st_mode))

        staged_path = None
        try:
            with tempfile.NamedTemporaryFile(prefix=".netratel-update.", suffix=".tmp",
                                             dir=updater_path.parent, delete=False) as staged:
                staged_path = Path(staged.name)
                staged.write(replacement)
                staged.flush()
                os.fsync(staged.fileno())
            os.chown(staged_path, installed.st_uid, installed.st_gid)
            os.chmod(staged_path, stat.S_IMODE(installed.st_mode))
            os.replace(staged_path, updater_path)
            directory = os.open(updater_path.parent, os.O_RDONLY | os.O_DIRECTORY)
            try:
                os.fsync(directory)
            finally:
                os.close(directory)
        finally:
            if staged_path is not None:
                staged_path.unlink(missing_ok=True)
    print(f"Installed updater repaired from verified {manifest['version']} archive; backup: {backup}")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--archive", type=Path, required=True, help="approved Linux x64 client TAR.GZ")
    parser.add_argument("--sha256", required=True, help="archive SHA-256 from the release checksum record")
    parser.add_argument("--updater", type=Path,
                        default=Path("/opt/netratel/client/updater/netratel-update.sh"))
    parser.add_argument("--lock", type=Path, default=Path("/var/lib/netratel/update/update.lock"))
    args = parser.parse_args()
    try:
        repair(args.archive, args.sha256, args.updater, args.lock)
    except (OSError, ValueError, tarfile.TarError, json.JSONDecodeError) as error:
        parser.exit(1, f"Updater repair failed: {error}\n")


if __name__ == "__main__":
    main()
