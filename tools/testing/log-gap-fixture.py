#!/usr/bin/env python3
"""Create a bounded real file gap on an isolated Linux acceptance executor."""

import argparse
import ctypes
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import select
import stat
import struct
import sys
import tempfile
import time
import uuid


SOURCES = {"linux-file-messages": "/var/log/messages", "linux-file-syslog": "/var/log/syslog"}
EVENT = struct.Struct("iIII")
IN_CLOSE_NOWRITE = 0x10
IN_DELETE_SELF = 0x400
IN_MOVE_SELF = 0x800
IN_IGNORED = 0x8000


def utc():
    return datetime.now(timezone.utc).isoformat()


def write_receipt(path, value):
    with path.open("x", encoding="utf-8") as stream:
        json.dump(value, stream, indent=2)
        stream.write("\n")


def prepare(source):
    path = SOURCES[source]
    fixture_id = str(uuid.uuid4())
    seed = f"[NetRatelGapFixture] {fixture_id} seed ".encode() + b"x" * 2048 + b"\n"
    fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
    with os.fdopen(fd, "wb") as stream:
        stream.write(seed)
        stream.flush()
        os.fsync(stream.fileno())
        identity = os.fstat(stream.fileno())
    directory = Path(tempfile.mkdtemp(prefix="netratel-log-gap-"))
    value = {"kind": "netratel-owned-log-gap-v1", "fixtureId": fixture_id, "sourceId": source,
             "path": path, "device": identity.st_dev, "inode": identity.st_ino,
             "createdAtUtc": utc(), "seedBytes": len(seed), "seedSha256": hashlib.sha256(seed).hexdigest()}
    write_receipt(directory / "fixture.json", value)
    return {"stateDirectory": str(directory), **value}


def load(directory):
    value = json.loads((directory / "fixture.json").read_text(encoding="utf-8"))
    if value.get("kind") != "netratel-owned-log-gap-v1" or SOURCES.get(value.get("sourceId")) != value.get("path"):
        raise ValueError("The state does not identify a supported owned fixture.")
    uuid.UUID(value["fixtureId"])
    identity = os.stat(value["path"], follow_symlinks=False)
    if not stat.S_ISREG(identity.st_mode) or (identity.st_dev, identity.st_ino) != (value["device"], value["inode"]):
        raise ValueError("The source was replaced; no mutation was made.")
    return value


def append(value, text):
    fd = os.open(value["path"], os.O_WRONLY | os.O_APPEND | os.O_NOFOLLOW)
    with os.fdopen(fd, "wb") as stream:
        identity = os.fstat(stream.fileno())
        if (identity.st_dev, identity.st_ino) != (value["device"], value["inode"]):
            raise ValueError("The source was replaced; no mutation was made.")
        stream.write(text.encode("utf-8"))
        stream.flush()
        os.fsync(stream.fileno())


def produce(directory):
    value = load(directory)
    if (directory / "gap.json").exists():
        raise ValueError("This fixture already has a gap receipt; preserve it and prepare a new fixture.")
    libc = ctypes.CDLL(None, use_errno=True)
    libc.inotify_init1.argtypes = [ctypes.c_int]
    libc.inotify_init1.restype = ctypes.c_int
    libc.inotify_add_watch.argtypes = [ctypes.c_int, ctypes.c_char_p, ctypes.c_uint32]
    libc.inotify_add_watch.restype = ctypes.c_int
    fd = libc.inotify_init1(os.O_NONBLOCK | os.O_CLOEXEC)
    if fd < 0:
        raise OSError(ctypes.get_errno(), "Cannot watch the owned log source.")
    try:
        if libc.inotify_add_watch(fd, os.fsencode(value["path"]), IN_CLOSE_NOWRITE | IN_DELETE_SELF | IN_MOVE_SELF) < 0:
            raise OSError(ctypes.get_errno(), "Cannot watch the owned log source.")
        print("Watching the owned source; start the native MCP tail now.", file=sys.stderr, flush=True)
        # The first reader is bounded history; a later reader after an append
        # establishes the real polling follower. No API state is manufactured.
        deadline = time.monotonic() + 12
        next_marker = time.monotonic()
        closes = 0
        markers = 0
        while closes < 2:
            now = time.monotonic()
            if now >= deadline:
                raise TimeoutError("No history + live reader observed within 12 seconds; no truncation performed.")
            if now >= next_marker:
                markers += 1
                append(value, f"[NetRatelGapFixture] {value['fixtureId']} live-{markers}\n")
                next_marker = now + 0.5
            if not select.select([fd], [], [], min(0.1, deadline - now))[0]:
                continue
            data = os.read(fd, 4096)
            offset = 0
            while offset + EVENT.size <= len(data):
                _, mask, _, name_length = EVENT.unpack_from(data, offset)
                offset += EVENT.size + name_length
                if mask & (IN_DELETE_SELF | IN_MOVE_SELF | IN_IGNORED):
                    raise ValueError("The source was moved or removed; no truncation performed.")
                if mask & IN_CLOSE_NOWRITE:
                    closes += 1
        load(directory)
        before = os.stat(value["path"]).st_size
        discarded = f"[NetRatelGapFixture] {value['fixtureId']} unread-before-truncate\n"
        append(value, discarded)
        target = os.open(value["path"], os.O_WRONLY | os.O_NOFOLLOW)
        try:
            identity = os.fstat(target)
            if (identity.st_dev, identity.st_ino) != (value["device"], value["inode"]):
                raise ValueError("The source was replaced; no truncation performed.")
            os.ftruncate(target, 0)
            os.fsync(target)
        finally:
            os.close(target)
        truncated_at = utc()
        # The production provider polls each second. Keep the real smaller file
        # visible across two polls, then produce a recoverable current record.
        time.sleep(2.25)
        marker = f"{value['fixtureId']} post-gap"
        append(value, f"[NetRatelGapFixture] {marker}\n")
        receipt = {"kind": "netratel-file-gap-v1", "fixtureId": value["fixtureId"],
                   "sourceId": value["sourceId"], "observedReaderCloses": closes,
                   "bytesBeforeAppendAndTruncate": before, "discardedMarkerBytes": len(discarded.encode()),
                   "bytesImmediatelyAfterTruncate": 0, "truncatedAtUtc": truncated_at,
                   "postGapMarker": marker, "postGapWrittenAtUtc": utc(), "heartbeatMarkers": markers,
                   "liveMcpAcceptance": "pending-native-receipts"}
        write_receipt(directory / "gap.json", receipt)
        return receipt
    finally:
        os.close(fd)


def marker(directory):
    value = load(directory)
    text = f"{value['fixtureId']} post-resync {uuid.uuid4()}"
    append(value, f"[NetRatelGapFixture] {text}\n")
    return {"fixtureId": value["fixtureId"], "marker": text, "writtenAtUtc": utc()}


def cleanup(directory):
    value = load(directory)
    os.unlink(value["path"])
    return {"removed": value["path"], "fixtureId": value["fixtureId"], "receiptsRetainedAt": str(directory)}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    commands.add_parser("prepare").add_argument("--source", choices=SOURCES, default="linux-file-messages")
    commands.add_parser("produce").add_argument("state_directory", type=Path)
    commands.add_parser("marker").add_argument("state_directory", type=Path)
    commands.add_parser("cleanup").add_argument("state_directory", type=Path)
    args = parser.parse_args()
    if not sys.platform.startswith("linux") or os.geteuid() != 0:
        parser.error("Run inside the selected disposable Linux executor as root.")
    try:
        if args.command == "prepare":
            result = prepare(args.source)
        else:
            result = {"produce": produce, "marker": marker, "cleanup": cleanup}[args.command](args.state_directory)
        print(json.dumps(result, indent=2))
    except (OSError, ValueError, TimeoutError) as error:
        print(f"Fixture stopped: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
