#!/usr/bin/env python3
"""Compare the actual native PTY backend in two exact-source macOS Debug builds."""
import argparse
import ctypes
from datetime import datetime, timezone
import errno
import json
import os
from pathlib import Path
import re
import runpy
import shutil
import subprocess
import sys
import tarfile

TOOLS = Path(__file__).resolve().parent
MEASURE = runpy.run_path(str(TOOLS / "measure-client-publish.py"))
PROJECT = "src/NetRatel/NetRatel.Client/NetRatel.Client.csproj"
LIBRARY = "libnetratel_terminal_pty_resize.dylib"


def events(path):
    result = []
    for line in path.read_text().splitlines():
        try:
            item = json.loads(line)
        except json.JSONDecodeError:
            continue
        if isinstance(item, dict) and "stage" in item:
            result.append(item)
    return result


def compare_variant(label, source, state, evidence, expected_sdk):
    sha = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=source, text=True).strip()
    if not re.fullmatch(r"[a-f0-9]{40}", sha):
        raise ValueError("Debug source must resolve to an exact commit")
    sdk = subprocess.check_output(["dotnet", "--version"], cwd=source, text=True).strip()
    if sdk != expected_sdk or sdk != json.loads((source / "global.json").read_text())["sdk"]["version"]:
        raise ValueError("Baseline and candidate must use the same pinned SDK")
    evidence.mkdir(parents=True)
    state.mkdir(parents=True)
    metrics = {"sourceSha": sha, "runtime": "osx-arm64", "mode": "Debug", "dotnetSdkVersion": sdk,
               "stages": {}, "startedUtc": datetime.now(timezone.utc).isoformat()}
    code = 0
    try:
        properties = {"Configuration": "Debug", "RuntimeIdentifier": "osx-arm64", "SelfContained": "false",
                      "PublishSingleFile": "false", "PublishTrimmed": "false", "PublishAot": "false",
                      "SourceRevisionId": sha, "PublicSourceRevision": sha}
        flags = [f"-p:{key}={value}" for key, value in properties.items()]
        build = state / "build"
        def run(stage, command):
            return MEASURE["run_stage"](metrics, evidence, stage, command, source)
        run("restore", ["dotnet", "restore", PROJECT, "--runtime", "osx-arm64", "--artifacts-path", build,
                        "--disable-parallel", "-m:1", *flags])
        run("build", ["dotnet", "build", PROJECT, "--configuration", "Debug", "--no-restore", "--runtime", "osx-arm64",
                      "--artifacts-path", build, "-m:1", *flags])
        evaluation = subprocess.check_output(["dotnet", "msbuild", PROJECT, *flags, f"-p:ArtifactsPath={build}",
            "-p:UseArtifactsOutput=true", "-getProperty:TargetPath,Configuration,RuntimeIdentifier,SelfContained,PublishSingleFile,PublishTrimmed,PublishAot"],
            cwd=source, text=True, encoding="utf-8-sig")
        (evidence / "evaluated-project.json").write_text(evaluation)
        evaluated = json.loads(evaluation)["Properties"]
        for key in ("Configuration", "RuntimeIdentifier", "SelfContained", "PublishSingleFile", "PublishTrimmed", "PublishAot"):
            if evaluated[key].lower() != properties[key].lower():
                raise ValueError("Evaluated Debug mode differs: " + key)
        output = Path(evaluated["TargetPath"]).parent
        version = subprocess.check_output([sys.executable, source / "tools/ci/product-version.py"], cwd=source, text=True).strip()
        metrics["productVersion"] = version
        if label == "candidate":
            library = output / LIBRARY
            identity = MEASURE["binary_identity"](library)
            if identity != {"format": "Mach-O", "machine": "0x100000c"}:
                raise ValueError("Debug native resize helper must be Mach-O arm64")
            run("native-exports", ["xcrun", "nm", "-gU", library])
            run("native-libraries", ["xcrun", "otool", "-L", library])
            run("native-deployment", ["xcrun", "vtool", "-show-build", library])
            if "_netratel_set_window_size" not in (evidence / "native-exports.log").read_text():
                raise ValueError("Debug native resize helper export is missing")
            native = ctypes.CDLL(str(library), use_errno=True)
            resize = native.netratel_set_window_size
            resize.argtypes, resize.restype = [ctypes.c_int, ctypes.c_ushort, ctypes.c_ushort], ctypes.c_int
            ctypes.set_errno(0)
            result = resize(-1, 17, 77)
            error = ctypes.get_errno()
            metrics["nativeHelper"] = {"sha256": MEASURE["digest"](library), "binary": identity,
                                       "invalidFdResult": result, "invalidFdErrno": error,
                                       "deployment": MEASURE["macho_deployment"](library)}
            MEASURE["validate_darwin_deployment"](MEASURE["macho_deployment"](output / "NetRatel.Client"),
                                                   metrics["nativeHelper"]["deployment"])
            if result != -1 or error != errno.EBADF:
                raise ValueError("The native resize shim did not preserve ioctl errno")
        (evidence / "distribution-inventory.json").write_text(json.dumps(MEASURE["inventory"](output), indent=2) + "\n")
        # Internal Debug transport only: reuse the existing exact-byte version and
        # PTY runners, without a Release publish or a second probe implementation.
        archive = state / "debug-client.tar"
        with tarfile.open(archive, "w") as bundle:
            bundle.add(output, arcname="netratel-client-osx-arm64")
        MEASURE["run_packaged_version_probes"](metrics, evidence, state, archive, "netratel-client-osx-arm64", "osx-arm64", version)
        try:
            MEASURE["run_packaged_pty_probes"](metrics, evidence, state, archive, "netratel-client-osx-arm64")
        except subprocess.CalledProcessError:
            if label != "baseline":
                raise
        if label == "baseline":
            native_events = events(evidence / "pty-native.stdout.log")
            final = native_events[-1] if native_events else {}
            native = metrics["stages"]["pty-native"]
            known_failure = (native["exitCode"] == 1 and not native.get("timedOut")
                and metrics.get("failureStages") == ["pty-native"]
                and final.get("stage") == "failed"
                and "Timed out waiting for terminal output '17 77'" in final.get("error", "")
                and any(item.get("stage") == "started" and item.get("backend") == "unix-pty-native" for item in native_events)
                and any(item.get("stage") == "input-output" and item.get("result") == "passed" for item in native_events)
                and any(item.get("stage") == "resize" and item.get("result") == "applied"
                        and item.get("requestedRows") == 17 and item.get("requestedCols") == 77 for item in native_events)
                and metrics["stages"]["pty-python"]["passed"])
            metrics["expectedBaselineResizeFailure"] = known_failure
            if not known_failure:
                raise ValueError("Baseline did not reproduce the specific native resize defect with Python still passing")
    except (ValueError, OSError, KeyError, subprocess.CalledProcessError) as error:
        code = 1
        metrics["error"] = str(error)
    finally:
        metrics.update(exitCode=1 if metrics.get("expectedBaselineResizeFailure") else code,
                       comparisonExitCode=code, finishedUtc=datetime.now(timezone.utc).isoformat())
        (evidence / "metrics.json").write_text(json.dumps(metrics, indent=2) + "\n")
    if code:
        raise ValueError(label + " Debug comparison failed: " + metrics["error"])
    return metrics


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--baseline", type=Path, required=True)
    parser.add_argument("--state-root", type=Path, required=True)
    parser.add_argument("--evidence", type=Path, required=True)
    args = parser.parse_args()
    if os.environ.get("GITHUB_ACTIONS") != "true" or os.environ.get("RUNNER_ENVIRONMENT") != "github-hosted":
        parser.error("Native Debug comparison requires a matching GitHub-hosted macOS arm64 runner")
    host = MEASURE["validate_runtime_host"]("osx-arm64")
    # Framework-dependent Debug apphosts must use the same installed runtime as
    # the pinned CLI, rather than a different machine-wide dotnet installation.
    dotnet_root = str(Path(shutil.which("dotnet")).resolve().parent)
    os.environ["DOTNET_ROOT"] = dotnet_root
    os.environ["DOTNET_ROOT_ARM64"] = dotnet_root
    state, evidence = args.state_root.resolve(), args.evidence.resolve()
    if state.exists() or evidence.exists():
        parser.error("Debug state/evidence must be fresh")
    evidence.mkdir(parents=True)
    sdk = json.loads((args.source / "global.json").read_text())["sdk"]["version"]
    result = {"sourceSha": subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=args.source, text=True).strip(),
              "runtime": "osx-arm64", "mode": "Debug comparison", "nativeHost": host, "stages": {},
              "dotnetSdkVersion": sdk, "dotnetRoot": dotnet_root, "runner": {key: os.environ.get(key) for key in
              ("ImageOS", "ImageVersion", "GITHUB_RUN_ID", "GITHUB_RUN_ATTEMPT")},
              "startedUtc": datetime.now(timezone.utc).isoformat(), "functionalAcceptance": "pending owner testing"}
    code = 0
    try:
        for stage, command in (("dotnet-toolchain", ["dotnet", "--info"]),
                               ("native-toolchain", ["xcrun", "clang", "--version"]),
                               ("native-sdk", ["xcrun", "--show-sdk-version"])):
            MEASURE["run_stage"](result, evidence, stage, command, args.source.resolve())
        for label, source in (("baseline", args.baseline), ("candidate", args.source)):
            result.setdefault("debugComparison", {})[label] = compare_variant(label, source.resolve(), state / label, evidence / label, sdk)
    except (ValueError, OSError, KeyError, subprocess.CalledProcessError) as error:
        code = 1
        result.update(error=str(error), failureKind="native Debug comparison")
    finally:
        result.update(exitCode=code, finishedUtc=datetime.now(timezone.utc).isoformat())
        (evidence / "metrics.json").write_text(json.dumps(result, indent=2) + "\n")
        print("DARWIN_PTY_DEBUG_COMPARISON=" + json.dumps(result, separators=(",", ":")), flush=True)
    return code


if __name__ == "__main__":
    raise SystemExit(main())
