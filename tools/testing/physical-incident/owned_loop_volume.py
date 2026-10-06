"""UNAPPLIED fixture helper. Import only from the future root-capable CI driver.

This provisions a real, fixed ext4 filesystem. It never fills the host drive to
cause a breach. Three GiB of non-sparse backing storage is a separate, explicit
provisioning requirement; the breach stimulus is bounded to 256 MiB. No helper
from this preparation has been executed.
"""
from __future__ import annotations

import json
import os
import pathlib
import re
import shutil
import stat
import subprocess
import time
import uuid

MIB = 1024 * 1024
GIB = 1024 * MIB
HOST_FLOOR = 2 * GIB
SELECTED_FLOOR = 2 * GIB
BACKING_BYTES = 3 * GIB
STIMULUS_CAP = 256 * MIB
BLOCK = bytes([0x6D]) * MIB


class FixturePreconditionError(RuntimeError):
    pass


def command(arguments: list[str], timeout: int = 60) -> str:
    # Commands are arrays, never shell text. Keep arbitrary diagnostics private.
    result = subprocess.run(arguments, capture_output=True, timeout=timeout, check=False)
    if result.returncode or len(result.stdout) > 65536:
        raise FixturePreconditionError("owned-volume-command-failed:" + pathlib.Path(arguments[0]).name)
    return result.stdout.decode("utf-8", "strict").strip()


def available(path: pathlib.Path) -> int:
    value = os.statvfs(path)
    return value.f_bavail * value.f_frsize


def private_json(path: pathlib.Path, value: dict) -> None:
    temporary = path.with_name(path.name + "." + uuid.uuid4().hex)
    fd = os.open(temporary, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
    try:
        with os.fdopen(fd, "w", encoding="utf-8") as handle:
            json.dump(value, handle, sort_keys=True, indent=2)
            handle.write("\n")
            handle.flush()
            os.fsync(handle.fileno())
        os.replace(temporary, path)
    finally:
        temporary.unlink(missing_ok=True)


class OwnedLoopVolume:
    """Owns only a new UUID directory, its exact loop device and one mount.

    The caller must own this object until all Client containers have stopped.
    Cleanup refuses a busy mount, a changed backing/device identity, or a leaked
    container rather than deleting files beneath another live filesystem.
    """

    def __init__(self, authorized_work_root: pathlib.Path):
        self.parent = authorized_work_root.absolute()
        self.root = self.parent / ("netratel-owned-disk-" + uuid.uuid4().hex)
        self.image = self.root / "filesystem.ext4"
        self.mount = self.root / "mounted"
        self.stimulus = self.mount / "physical-allocation.bin"
        self.marker = self.mount / ".fixture-owner"
        self.owner = uuid.uuid4().hex
        self.loop: str | None = None
        self.mounted = False
        self.closed = False
        self.metadata_acknowledged = False
        self.metadata_validated = False
        self.container_users: set[str] = set()
        self.proof: list[dict] = []

    def __enter__(self):
        try:
            self.acquire()
            return self
        except BaseException:
            self.close()
            raise

    def __exit__(self, *unused):
        self.close()

    def preflight(self) -> dict:
        if os.geteuid() != 0:
            raise FixturePreconditionError("owned-ext4-volume-requires-root-capable-fixture")
        for name in ("losetup", "mke2fs", "mount", "umount", "findmnt"):
            if not shutil.which(name):
                raise FixturePreconditionError("missing-owned-volume-tool:" + name)
        if not self.parent.is_dir() or self.parent.resolve() != self.parent or str(self.parent) in ("/", "/home", "/tmp"):
            raise FixturePreconditionError("explicit-private-work-root-required")
        # A backing file on memory/network storage would change the physical claim.
        filesystem = command(["findmnt", "--noheadings", "--output", "FSTYPE", "--target", str(self.parent)])
        if filesystem not in ("ext4", "xfs", "btrfs"):
            raise FixturePreconditionError("physical-disk-backed-work-root-required")
        baseline = available(self.parent)
        if baseline - BACKING_BYTES < HOST_FLOOR:
            raise FixturePreconditionError("insufficient-host-space-for-explicit-three-gib-reservation")
        caps = pathlib.Path("/proc/self/status").read_text().splitlines()
        effective = next(int(line.split()[1], 16) for line in caps if line.startswith("CapEff:"))
        if not effective & (1 << 21):
            raise FixturePreconditionError("owned-ext4-volume-requires-CAP_SYS_ADMIN")
        return {"backingBytes": BACKING_BYTES, "hostAvailableBefore": baseline,
                "hostFloorBytes": HOST_FLOOR, "backingFilesystem": filesystem,
                "stimulusCapBytes": STIMULUS_CAP, "selectedFloorBytes": SELECTED_FLOOR}

    def acquire(self) -> None:
        capacity = self.preflight()
        self.root.mkdir(mode=0o700)
        self.mount.mkdir(mode=0o700)
        private_json(self.root / "ownership.json", {"owner": self.owner, "phase": "allocating", **capacity})
        fd = os.open(self.image, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
        try:
            for _ in range(BACKING_BYTES // len(BLOCK)):
                if available(self.parent) < HOST_FLOOR + len(BLOCK):
                    raise FixturePreconditionError("host-capacity-changed-during-bounded-provisioning")
                view = memoryview(BLOCK)
                while view:
                    written = os.write(fd, view)
                    if written <= 0:
                        raise FixturePreconditionError("physical-backing-write-did-not-progress")
                    view = view[written:]
            os.fsync(fd)
        finally:
            os.close(fd)
        observed = self.image.stat()
        if observed.st_size != BACKING_BYTES or observed.st_blocks * 512 < BACKING_BYTES:
            raise FixturePreconditionError("sparse-or-incomplete-backing-is-not-physical-proof")
        command(["mke2fs", "-q", "-t", "ext4", "-F", "-m", "0", "-E",
                 "nodiscard,lazy_itable_init=0,lazy_journal_init=0", str(self.image)])
        formatted = self.image.stat()
        if formatted.st_size != BACKING_BYTES or formatted.st_blocks * 512 < BACKING_BYTES:
            raise FixturePreconditionError("formatting-changed-non-sparse-backing-allocation")
        self.loop = command(["losetup", "--find", "--show", "--nooverlap", str(self.image)])
        if not self.loop.startswith("/dev/loop") or not self.loop[9:].isdigit():
            raise FixturePreconditionError("unexpected-owned-loop-device")
        private_json(self.root / "ownership.json", {"owner": self.owner, "phase": "loop-attached", "loop": self.loop, **capacity})
        command(["mount", "-t", "ext4", "-o", "nodev,nosuid,noexec", self.loop, str(self.mount)])
        self.mounted = True
        self.assert_owned()
        # The unprivileged, real production Client can write only inside its fixture volumes.
        os.chown(self.mount, 10001, 10001)
        os.chmod(self.mount, 0o700)
        self.marker.write_text(self.owner, encoding="ascii")
        os.chown(self.marker, 10001, 10001)
        os.chmod(self.marker, 0o400)
        self.proof.append({"phase": "provisioned", "backingBytes": BACKING_BYTES,
                           "selectedAvailable": available(self.mount), "hostAvailable": available(self.parent),
                           "realFilesystem": "ext4", "sparseBacking": False, "physicalStimulusBytes": 0})
        private_json(self.root / "ownership.json", {"owner": self.owner, "phase": "mounted", "loop": self.loop, **capacity})

    def assert_owned(self) -> None:
        if not self.loop:
            raise FixturePreconditionError("no-owned-loop-device")
        identity = json.loads(command(["losetup", "--json", "--output", "NAME,BACK-FILE", self.loop]))["loopdevices"]
        if len(identity) != 1 or identity[0]["name"] != self.loop or pathlib.Path(identity[0]["back-file"]).resolve() != self.image:
            raise FixturePreconditionError("loop-backing-identity-changed")
        if self.mounted:
            source = command(["findmnt", "--noheadings", "--output", "SOURCE", "--mountpoint", str(self.mount)])
            if source != self.loop:
                raise FixturePreconditionError("owned-mount-identity-changed")
            if self.marker.exists() and self.marker.read_text(encoding="ascii") != self.owner:
                raise FixturePreconditionError("owned-volume-marker-changed")

    def allocate(self, count: int = 256 * MIB) -> dict:
        self.assert_owned()
        baseline = available(self.mount)
        if self.stimulus.exists() or count <= 0 or count > STIMULUS_CAP or count > baseline // 10 or baseline - count < SELECTED_FLOOR:
            raise FixturePreconditionError("physical-stimulus-bounds-not-satisfied")
        fd = os.open(self.stimulus, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
        try:
            remaining = count
            while remaining:
                chunk = BLOCK[:min(remaining, len(BLOCK))]
                if available(self.mount) - len(chunk) < SELECTED_FLOOR or available(self.parent) < HOST_FLOOR:
                    raise FixturePreconditionError("space-floor-changed-during-owned-stimulus")
                view = memoryview(chunk)
                while view:
                    written = os.write(fd, view)
                    if written <= 0:
                        raise FixturePreconditionError("physical-stimulus-write-did-not-progress")
                    view = view[written:]
                    remaining -= written
            os.fsync(fd)
        finally:
            os.close(fd)
        after = available(self.mount)
        value = self.stimulus.stat()
        if value.st_size != count or value.st_blocks * 512 < count or baseline - after < count:
            raise FixturePreconditionError("actual-block-allocation-not-proven")
        receipt = {"phase": "allocated", "stimulusBytes": count, "actualAllocatedBytes": value.st_blocks * 512,
                   "selectedAvailableBefore": baseline, "selectedAvailableAfter": after,
                   "hostAvailableAfter": available(self.parent), "observedAtUtcUnixNanoseconds": time.time_ns()}
        self.proof.append(receipt)
        return receipt

    def recover(self) -> dict:
        self.assert_owned()
        if self.stimulus.is_symlink() or not self.stimulus.is_file():
            raise FixturePreconditionError("owned-stimulus-identity-missing")
        before = available(self.mount)
        self.stimulus.unlink()
        directory = os.open(self.mount, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
        try:
            os.fsync(directory)
        finally:
            os.close(directory)
        value = {"phase": "recovered", "selectedAvailableBefore": before, "selectedAvailableAfter": available(self.mount),
                 "hostAvailableAfter": available(self.parent), "observedAtUtcUnixNanoseconds": time.time_ns()}
        self.proof.append(value)
        return value

    def close(self) -> None:
        if self.closed:
            return
        if self.container_users:
            raise FixturePreconditionError("stop-all-owned-client-containers-before-volume-cleanup")
        # A command timeout may follow an actual kernel mutation. Reconcile
        # only this exact freshly created image/mount before deleting anything.
        if self.image.exists() and not self.loop and shutil.which("losetup"):
            rows = json.loads(command(["losetup", "--json", "--associated", str(self.image),
                                       "--output", "NAME,BACK-FILE"]))["loopdevices"]
            if len(rows) > 1:
                raise FixturePreconditionError("ambiguous-owned-loop-reconciliation")
            if rows:
                self.loop = rows[0]["name"]
        if self.mount.exists() and shutil.which("findmnt"):
            check = subprocess.run(["findmnt", "--noheadings", "--output", "SOURCE", "--mountpoint", str(self.mount)],
                                   capture_output=True, timeout=10, check=False)
            if check.returncode == 0:
                if not self.loop or check.stdout.decode("utf-8", "strict").strip() != self.loop:
                    raise FixturePreconditionError("unexpected-mount-during-owned-cleanup")
                self.mounted = True
            elif check.returncode != 1:
                raise FixturePreconditionError("owned-cleanup-mount-state-could-not-be-read")
        if self.loop:
            self.assert_owned()
        if self.mounted:
            command(["umount", str(self.mount)], timeout=15)  # No force/lazy unmount.
            self.mounted = False
        if self.loop:
            command(["losetup", "--detach", self.loop], timeout=15)
            self.loop = None
        if self.image.exists():
            if self.image.is_symlink() or not stat.S_ISREG(self.image.lstat().st_mode):
                raise FixturePreconditionError("owned-backing-file-type-changed")
            self.image.unlink()
        # Keep allowlisted identity/capacity/cleanup evidence; remove no external path.
        if self.mount.exists():
            self.mount.rmdir()
        if self.root.exists():
            private_json(self.root / "cleanup.json", {"owner": self.owner, "phase": "cleaned", "mountRemoved": True,
                                                       "loopDetached": True, "backingRemoved": True, "proof": self.proof})
        self.closed = True

    def acknowledge_cleanup(self, ownership_id: str) -> None:
        """Remove only exact safe metadata after the caller consumed close facts."""
        if self.metadata_acknowledged:
            return
        if ownership_id != self.owner or not self.closed or self.container_users or self.mounted or self.loop:
            raise FixturePreconditionError("owned-cleanup-acknowledgement-not-authorized")
        if self.image.exists() or self.mount.exists() or self.root.parent != self.parent or \
                not re.fullmatch(r"netratel-owned-disk-[0-9a-f]{32}", self.root.name) or \
                self.root.is_symlink() or self.root.resolve() != self.root or not self.root.is_dir():
            raise FixturePreconditionError("owned-cleanup-metadata-root-identity-changed")
        allowed = {"ownership.json", "cleanup.json"}
        children = list(self.root.iterdir())
        names = {entry.name for entry in children}
        if names != allowed and not (self.metadata_validated and names <= allowed):
            raise FixturePreconditionError("unexpected-owned-cleanup-metadata-entry")
        for entry in children:
            if entry.is_symlink() or not stat.S_ISREG(entry.lstat().st_mode):
                raise FixturePreconditionError("owned-cleanup-metadata-file-type-changed")
            value = json.loads(entry.read_text(encoding="utf-8"))
            if value.get("owner") != self.owner:
                raise FixturePreconditionError("owned-cleanup-metadata-owner-changed")
            if entry.name == "cleanup.json" and not (value.get("phase") == "cleaned" and
                    value.get("mountRemoved") is True and value.get("loopDetached") is True and value.get("backingRemoved") is True):
                raise FixturePreconditionError("owned-cleanup-metadata-not-complete")
        self.metadata_validated = True
        # No recursive deletion and no external/parent path is removed.
        for name in sorted(allowed):
            (self.root / name).unlink(missing_ok=True)
        self.root.rmdir()
        self.metadata_acknowledged = True
