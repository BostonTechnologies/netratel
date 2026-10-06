"""Own the real fixture volume/Client for one bounded integration-test process.

JSON lines travel over redirected private stdin/stdout. Enrollment and CA input
are never arguments, environment values or exported proof. This entry point does
not build/pull/publish images, configure product authority or manufacture samples.
"""
from __future__ import annotations

import argparse
import json
import pathlib
import re
import signal
import sys

from owned_loop_volume import FixturePreconditionError, OwnedLoopVolume
from production_client_container import ProductionClientContainer


def emit(request_id: str, facts: dict) -> None:
    raw = json.dumps({"id": request_id, "facts": facts}, separators=(",", ":"))
    if len(raw.encode("utf-8")) > 65536:
        raise FixturePreconditionError("typed-fixture-response-limit-exceeded")
    sys.stdout.write(raw + "\n")
    sys.stdout.flush()


def interrupted(signum, frame) -> None:
    raise FixturePreconditionError("owned-fixture-process-interrupted")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--work-root", required=True, type=pathlib.Path)
    args = parser.parse_args()
    volume = OwnedLoopVolume(args.work_root)
    client: ProductionClientContainer | None = None
    provisioned = False
    closed = False
    for sig in (signal.SIGINT, signal.SIGTERM):
        signal.signal(sig, interrupted)

    def close() -> dict:
        nonlocal closed
        if not closed:
            if client is not None:
                client.close()  # Stop only exact owned container IDs before unmount.
            volume.close()
            closed = True
        if volume.container_users or volume.mounted or volume.loop or volume.image.exists():
            raise FixturePreconditionError("owned-fixture-cleanup-not-proven")
        return {"ownedClientStoppedAndRemoved": client is None or client.closed,
                "ownedMountRemoved": not volume.mount.exists(),
                "ownedLoopDetached": volume.loop is None,
                "ownedBackingRemoved": not volume.image.exists(),
                "privateCredentialsRemoved": client is None or not client.root.exists(),
                "ownershipId": volume.owner,
                "imagesRemoved": 0, "fleetChanges": 0}

    try:
        while True:
            line = sys.stdin.buffer.readline(65537)
            if not line:
                # A lost parent closes its private pipe; cleanup still owns the disk.
                close()
                return 1
            if len(line) > 65536 or not line.endswith(b"\n"):
                raise FixturePreconditionError("private-fixture-request-limit-exceeded")
            request = json.loads(line)
            if not isinstance(request, dict) or set(request) != {"id", "operation", "input"}:
                raise FixturePreconditionError("invalid-private-fixture-request-shape")
            request_id, operation, data = request["id"], request["operation"], request["input"]
            if not isinstance(request_id, str) or not re.fullmatch(r"[A-Za-z0-9_-]{1,64}", request_id):
                raise FixturePreconditionError("invalid-private-fixture-request-id")
            if not isinstance(data, dict):
                raise FixturePreconditionError("invalid-private-fixture-input-shape")
            if operation == "provision" and not provisioned and not closed and data == {}:
                volume.acquire()
                provisioned = True
                emit(request_id, {"provisioned": True, "volume": volume.proof[-1]})
            elif operation == "start" and provisioned and client is None and not closed:
                expected = {"imageId", "sourceSha", "version", "executableSha256", "trustedCaPem",
                            "apiBaseUrl", "gatewayEndpoint", "enrollmentCode"}
                if set(data) != expected or any(not isinstance(value, str) for value in data.values()):
                    raise FixturePreconditionError("invalid-private-client-start-shape")
                client = ProductionClientContainer(volume, data["imageId"], data["sourceSha"],
                    data["version"], data["executableSha256"], data["trustedCaPem"], data["apiBaseUrl"],
                    data["gatewayEndpoint"], data["enrollmentCode"])
                client.start()
                data.clear()
                request.clear()
                emit(request_id, client.proof.copy())
            elif operation == "allocate" and client is not None and not closed and data == {}:
                client.assert_alive()
                emit(request_id, volume.allocate(256 * 1024 * 1024))
            elif operation == "recover" and client is not None and not closed and data == {}:
                client.assert_alive()
                emit(request_id, volume.recover())
            elif operation == "release-command" and client is not None and not closed and data == {}:
                emit(request_id, client.release_command())
            elif operation == "alive" and client is not None and not closed and data == {}:
                client.assert_alive()
                emit(request_id, {"alive": True, "maximumClientLifetimeSeconds": 300})
            elif operation == "close" and data == {}:
                emit(request_id, close())
                # Stay alive until C# has consumed and validated the real close
                # receipt. A failed/lost caller leaves exact safe ownership facts.
            elif operation == "cleanup-ack" and closed and set(data) == {"ownershipId"}:
                volume.acknowledge_cleanup(data["ownershipId"])
                emit(request_id, {"ownedMetadataRemoved": volume.metadata_acknowledged})
                return 0
            else:
                raise FixturePreconditionError("invalid-private-fixture-operation-or-phase")
    except BaseException as error:
        # No exception message/raw body/secret enters CI output. Failure returns
        # nonzero even if bounded owned cleanup succeeds.
        sys.stderr.write("Physical fixture process failed: " + type(error).__name__ + "\n")
        return 1
    finally:
        try:
            close()
        except BaseException as error:
            sys.stderr.write("Physical fixture cleanup failed: " + type(error).__name__ + "\n")
            # Teardown failure must override an earlier return of zero.
            raise SystemExit(1)


if __name__ == "__main__":
    raise SystemExit(main())
