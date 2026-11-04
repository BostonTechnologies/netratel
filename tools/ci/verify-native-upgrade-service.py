#!/usr/bin/env python3
"""Retain nonsecret ownership and durable-state proof from the existing native upgrade fixture."""
import argparse
from datetime import datetime
import hashlib
import hmac
import json
import os
from pathlib import Path
import pwd
import re
import stat
import subprocess
import uuid


def read_json(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def utc(value):
    result = datetime.fromisoformat(value.replace("Z", "+00:00"))
    if result.tzinfo is None:
        raise ValueError("Native proof timestamp must include its UTC offset")
    return result


def digest(path):
    with path.open("rb") as source:
        return hashlib.file_digest(source, "sha256").hexdigest()


def protected_file(path, service_uid):
    if not path.is_absolute() or not path.is_file() or path.stat().st_size > 64 * 1024:
        raise ValueError("Native protected state is missing or oversized: " + path.name)
    for entry in (path, *path.parents):
        metadata = entry.lstat()
        if stat.S_ISLNK(metadata.st_mode) or metadata.st_uid not in (0, service_uid):
            raise ValueError("Native protected state has an untrusted owner or symlink")
        sticky_directory = stat.S_ISDIR(metadata.st_mode) and metadata.st_mode & stat.S_ISVTX
        if not sticky_directory and metadata.st_mode & 0o022:
            raise ValueError("Native protected state has a writable untrusted ancestor")
    metadata = path.stat()
    if stat.S_IMODE(metadata.st_mode) != 0o600:
        raise ValueError("Native durable writer did not retain mode0600: " + path.name)
    return {"sha256": digest(path), "bytes": metadata.st_size, "uid": metadata.st_uid, "mode": "0600", "protectedAncestors": True}


def validate_service(properties, process, expected_executable, expected_uid):
    if (properties.get("ActiveState"), properties.get("SubState"), properties.get("FragmentPath"),
            properties.get("Restart"), properties.get("RestartUSec"), properties.get("RestartPreventExitStatus"),
            properties.get("StartLimitIntervalUSec")) != (
            "active", "running", "/etc/systemd/system/netratel-client.service", "always", "30s", "78", "0"):
        raise ValueError("Native service ownership or canonical recovery policy differs")
    if int(properties.get("MainPID", "0")) <= 1 or int(properties["MainPID"]) != process["pid"] or process["uid"] != expected_uid:
        raise ValueError("Native service MainPID or real unprivileged process owner differs")
    invocation = properties.get("InvocationID", "")
    if not re.fullmatch(r"[a-f0-9]{32}", invocation) or process["invocationId"] != invocation:
        raise ValueError("Native process is not in the real systemd invocation")
    if process["executable"] != expected_executable or process["arguments"] != ["--service"]:
        raise ValueError("Native service executable or ordinary service arguments differ")
    if process["container"] or process["mode"].lower() != "service":
        raise ValueError("Native updater must use its real non-container service enablement")


def validate_activation(request, ready, activation, server, version, pid):
    attempt, release, confirmation = (str(uuid.UUID(server[name])) for name in ("attemptId", "releasePublicId", "confirmationId"))
    if any(value == str(uuid.UUID(int=0)) for value in (attempt, release, confirmation)):
        raise ValueError("Server update identities must be nonempty")
    if (uuid.UUID(server["gatewayConnectionId"]).int == 0 or uuid.UUID(server["agentId"]).int == 0 or
            type(server.get("gatewayConnectionEpoch")) is not int or server["gatewayConnectionEpoch"] <= 0 or
            type(server.get("tenantId")) is not int or server["tenantId"] <= 0):
        raise ValueError("Server activation must bind a real tenant/agent/connection/epoch")
    if (server.get("state"), server.get("targetVersion"), server.get("runtimeId")) != ("Accepted", version, "linux-x64") or not server.get("readmittedAtUtc") or not server.get("confirmedAtUtc"):
        raise ValueError("Native candidate lacks an actual server activation and confirmation")
    if (request.get("schema"), request.get("attemptId"), request.get("releaseId"), request.get("toVersion"), request.get("runtimeId")) != (
            "netratel.update.request.v2", attempt, release, version, "linux-x64"):
        raise ValueError("Protected request differs from the server-approved attempt/release")
    nonce = request.get("admissionNonce", "")
    nonce_hash = hashlib.sha256(nonce.encode()).hexdigest()
    if not re.fullmatch(r"[a-fA-F0-9]{64}", nonce) or not hmac.compare_digest(nonce_hash, server.get("admissionNonceHash", "")):
        raise ValueError("Protected request nonce differs from the real server-approved admission")
    if (ready.get("schema"), ready.get("attemptId"), ready.get("releaseId"), ready.get("version"), ready.get("confirmationId")) != (
            "netratel.update.ready.v2", attempt, release, version, confirmation):
        raise ValueError("Protected ready marker differs from the server confirmation")
    if (activation.get("schema"), activation.get("attemptId"), activation.get("releaseId"), activation.get("targetVersion"),
            activation.get("currentVersion"), activation.get("runtimeId"), activation.get("processId"), activation.get("connectionEpoch")) != (
            "netratel.update.activation.v1", attempt, release, version, version, "linux-x64", pid, server.get("gatewayConnectionEpoch")):
        raise ValueError("Actual candidate activation is not bound to the admitted process/connection")
    times = {name: activation.get(name) for name in ("requestLoadedAtUtc", "activationContextAttachedAtUtc", "connectAcceptedAtUtc",
        "firstHeartbeatSentAtUtc", "heartbeatAcceptedAtUtc", "confirmationReceivedAtUtc", "readyMarkerWrittenAtUtc")}
    if not all(times.values()):
        raise ValueError("Native candidate did not complete its actual Hello/heartbeat/confirmation/ready transition")
    for value in times.values():
        utc(value)
    return {"attemptId": attempt, "releasePublicId": release, "confirmationId": confirmation, "admissionNonceSha256": nonce_hash,
        "gatewayConnectionId": server["gatewayConnectionId"], "gatewayConnectionEpoch": server["gatewayConnectionEpoch"],
        "tenantId": server["tenantId"], "agentId": server["agentId"], "serverReadmittedAtUtc": server["readmittedAtUtc"],
        "serverConfirmedAtUtc": server["confirmedAtUtc"], "transitionTimes": times}


def validate_restart(previous, current):
    if previous["pid"] == current["pid"] or previous["invocationId"] == current["invocationId"]:
        raise ValueError("Native restart did not create a new real process/invocation")
    if utc(current["acknowledgedAtUtc"]) <= utc(previous["acknowledgedAtUtc"]):
        raise ValueError("Native restart reused a stale acknowledgement")
    old, new = previous["activation"], current["activation"]
    for name in ("attemptId", "releasePublicId", "confirmationId", "admissionNonceSha256", "tenantId", "agentId"):
        if old[name] != new[name]:
            raise ValueError("Native restart changed the approved update or enrolled identity")
    if new["gatewayConnectionEpoch"] <= old["gatewayConnectionEpoch"] or new["gatewayConnectionId"] == old["gatewayConnectionId"]:
        raise ValueError("Native restart lacks a fresh authoritative gateway admission")
    if current["executableSha256"] != previous["executableSha256"] or current["files"]["request.json"]["sha256"] != previous["files"]["request.json"]["sha256"]:
        raise ValueError("Native restart changed original candidate/request bytes")
    if previous["policyRevision"] is not None and (current["policyRevision"] is None or current["policyRevision"] < previous["policyRevision"]):
        raise ValueError("Native restart lost its persisted policy revision")


def collect(args, receipt):
    properties = dict(line.split("=", 1) for line in subprocess.check_output(["systemctl", "show", "netratel-client.service",
        "--property=ActiveState,SubState,MainPID,FragmentPath,InvocationID,Restart,RestartUSec,RestartPreventExitStatus,StartLimitIntervalUSec"], text=True).splitlines())
    pid, service_uid = int(properties["MainPID"]), pwd.getpwnam("netratel").pw_uid
    process_root = Path("/proc") / str(pid)
    environment = dict(item.split(b"=", 1) for item in (process_root / "environ").read_bytes().split(b"\0") if b"=" in item)
    value = lambda name: environment.get(name.encode(), b"").decode()
    process = {"pid": pid, "uid": int(next(line.split()[2] for line in (process_root / "status").read_text().splitlines() if line.startswith("Uid:"))),
        "executable": os.readlink(process_root / "exe"), "arguments": [item.decode() for item in (process_root / "cmdline").read_bytes().split(b"\0")[1:] if item],
        "invocationId": value("INVOCATION_ID"), "mode": value("NetRatelCLIENT__Client__AutoUpdate__Mode"),
        "container": value("DOTNET_RUNNING_IN_CONTAINER").lower() == "true" or Path("/.dockerenv").exists()}
    executable = (args.root / "current/NetRatel.Client").resolve(strict=True)
    validate_service(properties, process, str(executable), service_uid)
    receipt.update(pid=pid, uid=service_uid, invocationId=process["invocationId"], service=properties,
        executable=str(executable), executableSha256=digest(executable), files={})
    presence_path = args.state / "presence.json"
    receipt["files"]["presence.json"] = protected_file(presence_path, service_uid)
    presence = read_json(presence_path)
    if presence.get("version") != args.version or utc(presence["acknowledgedAtUtc"]) < utc(args.since):
        raise ValueError("Native service has no fresh acknowledgement for the actual running version")
    receipt["acknowledgedAtUtc"] = presence["acknowledgedAtUtc"]
    policy_path = args.state / "policy.json"
    receipt["policyRevision"] = None
    if policy_path.exists():
        receipt["files"]["policy.json"] = protected_file(policy_path, service_uid)
        receipt["policyRevision"] = read_json(policy_path)["revision"]
    logs = "\n".join(path.read_text(errors="replace") for path in args.logs.glob("*.log")
        if re.fullmatch(r"netratel-client-service-.*-" + str(pid) + r"-\d{8}\.log", path.name))
    if "[ServicePolicy] recoveryPolicy=AlreadyManaged" not in logs:
        raise ValueError("The actual process did not recognize its owned canonical service policy")
    receipt["ownedServicePolicy"] = "AlreadyManaged"
    if args.stage != "prior":
        candidate = read_json(args.inputs)
        if receipt["executableSha256"] != candidate["executableSha256"]:
            raise ValueError("Actual native process differs from original candidate archive bytes")
        manifest = read_json(executable.parent / "netratel-client-manifest.json")
        if manifest != candidate["manifest"]:
            raise ValueError("Actual installed manifest differs from the authenticated archive")
        for relative, expected in candidate["archiveFiles"].items():
            path = executable.parent / relative
            if path.is_symlink() or not path.is_file() or path.stat().st_size != expected["bytes"] or digest(path) != expected["sha256"]:
                raise ValueError("Actual installed support file differs from original archive bytes: " + relative)
        receipt["originalArchiveFilesMatched"] = len(candidate["archiveFiles"])
        for name in ("request.json", "state.json", "ready.json", "activation.json"):
            receipt["files"][name] = protected_file(args.state / name, service_uid)
        server = read_json(args.server)
        receipt["activation"] = validate_activation(read_json(args.state / "request.json"), read_json(args.state / "ready.json"),
            read_json(args.state / "activation.json"), server, args.version, pid)
        marker = "Recovery consumed the matching pending update attempt " + receipt["activation"]["attemptId"] + "; outage history is retained."
        receipt["pendingCooldownConsumptionObserved"] = marker in logs
        receipt["pendingReaderLimit"] = "Healthy activation proves request/confirmation reads; cooldown-only pending-reader consumption is unproven unless naturally logged."
        if args.stage == "restarted":
            validate_restart(read_json(args.evidence / "service-activated.json"), receipt)
    receipt["passed"] = True


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--stage", choices=("prior", "activated", "restarted"), required=True)
    for name in ("root", "state", "logs", "evidence", "inputs"):
        parser.add_argument("--" + name, type=Path, required=True)
    parser.add_argument("--server", type=Path)
    parser.add_argument("--version", required=True)
    parser.add_argument("--since", required=True)
    args = parser.parse_args()
    receipt = {"stage": args.stage, "passed": False, "expectedVersion": args.version}
    try:
        collect(args, receipt)
    except Exception as error:
        receipt.update(errorType=type(error).__name__, error=str(error))
        raise
    finally:
        (args.evidence / ("service-" + args.stage + ".json")).write_text(json.dumps(receipt, indent=2) + "\n")


if __name__ == "__main__":
    main()
