#!/usr/bin/env python3
"""One hosted, client-only publish using release archive/SBOM/integrity conventions."""
import argparse
from collections import Counter
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import time

TOOLS = Path(__file__).resolve().parent
PROJECT = "src/NetRatel/NetRatel.Client/NetRatel.Client.csproj"


def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def inventory(directory):
    return [{"path": path.relative_to(directory).as_posix(), "bytes": path.stat().st_size,
             "sha256": digest(path)} for path in sorted(directory.rglob("*")) if path.is_file()]


def failure_kind(stage, log):
    if stage == "publish":
        if re.search(r"(?:gcc|clang|link\.exe).*(?:not found|not recognized|cannot find)|Native compilation requires.*(?:toolchain|Visual Studio)", log, re.I):
            return "missing toolchain"
        if re.search(r"error (?:CS|MSB|NETSDK)\d+", log):
            return "compilation/build"
        if re.search(r"error IL\d+|ILLink.*failed|Optimizing assemblies.*failed", log):
            return "linking"
    return {"restore": "restore", "launch-fresh": "runtime behavior", "launch-cached": "runtime behavior",
            "verify": "artifact integrity", "verify-content": "artifact content"}.get(stage, stage)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", required=True, type=Path)
    parser.add_argument("--runtime", required=True, choices=("win-x64", "linux-x64", "osx-arm64"))
    parser.add_argument("--mode", required=True, choices=("untrimmed", "trimmed"))
    parser.add_argument("--state-root", required=True, type=Path)
    parser.add_argument("--evidence", required=True, type=Path)
    parser.add_argument("--verify-content", action="store_true", help="Candidate-only removed-content gate")
    args = parser.parse_args()
    if os.environ.get("GITHUB_ACTIONS") != "true" or os.environ.get("RUNNER_ENVIRONMENT") != "github-hosted":
        parser.error("Release-style measurements must run on a GitHub-hosted Actions runner (AGENTS.md).")
    if args.mode == "trimmed" and args.runtime != "linux-x64":
        parser.error("The initial bounded trimming assessment is linux-x64 only.")
    source, evidence = args.source.resolve(), args.evidence.resolve()
    sha = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=source, text=True).strip()
    if not re.fullmatch(r"[a-f0-9]{40}", sha):
        parser.error("Source checkout must resolve to a commit SHA.")
    state = args.state_root.resolve() / sha / args.mode / args.runtime
    if state.exists() or evidence.exists():
        parser.error("State and evidence directories must be fresh for this source/mode/RID.")
    state.mkdir(parents=True)
    evidence.mkdir(parents=True)
    build = state / "build"
    packages = evidence / "artifacts"
    packages.mkdir()
    distribution = packages / f"netratel-client-{args.runtime}"
    metrics = {"sourceSha": sha, "mode": args.mode, "runtime": args.runtime,
               "startedUtc": datetime.now(timezone.utc).isoformat(), "stages": {},
               "runner": {key: os.environ.get(key) for key in
                          ("RUNNER_OS", "RUNNER_ARCH", "ImageOS", "ImageVersion", "GITHUB_RUN_ID", "GITHUB_RUN_ATTEMPT")},
               "cacheConditions": "Fresh source/mode/RID intermediates; shared runner NuGet cache, sequential A/B/C order.",
               "agentInitialization": "not measured", "gatewayReady": "not measured", "residentMemory": "not measured",
               "functionalAcceptance": "pending owner testing"}

    def run(stage, command, cwd=source, env=None):
        command = [str(value) for value in command]
        started = time.perf_counter()
        record = metrics["stages"][stage] = {"command": command, "cwd": str(cwd)}
        with (evidence / f"{stage}.log").open("w", encoding="utf-8") as log:
            log.write(json.dumps({"command": command, "cwd": str(cwd)}) + "\n")
            log.flush()
            process = subprocess.Popen(command, cwd=cwd, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                       text=True, encoding="utf-8", errors="replace", env=env)
            for line in process.stdout:
                log.write(line)
                print(line, end="", flush=True)
            result = process.wait()
        record.update(exitCode=result, seconds=round(time.perf_counter() - started, 3))
        if result:
            metrics["failureStage"] = stage
            metrics["failureKind"] = failure_kind(stage, (evidence / f"{stage}.log").read_text())
            raise subprocess.CalledProcessError(result, command)

    exit_code = 0
    try:
        sdk = subprocess.check_output(["dotnet", "--version"], cwd=source, text=True).strip()
        required_sdk = json.loads((source / "global.json").read_text())["sdk"]["version"]
        metrics["dotnetSdkVersion"] = sdk
        if sdk != required_sdk:
            raise ValueError(f"Required SDK {required_sdk}; measured SDK {sdk}")
        run("toolchain", ["dotnet", "--info"])
        version = subprocess.check_output([sys.executable, source / "tools/ci/product-version.py"], cwd=source, text=True).strip()
        metrics["productVersion"] = version
        properties = {"Configuration": "Release", "RuntimeIdentifier": args.runtime, "SelfContained": "true",
                      "PublishSingleFile": "true", "PublishTrimmed": str(args.mode == "trimmed").lower(),
                      "PublishAot": "false", "SourceRevisionId": sha, "PublicSourceRevision": sha,
                      "CustomAfterMicrosoftCommonTargets": str(TOOLS / "client-measurement.targets"),
                      "ClientMeasurementEvidenceDirectory": str(evidence)}
        if args.mode == "trimmed":
            properties.update(PublishProfile=str(source / "src/NetRatel/NetRatel.Client/Properties/PublishProfiles/ClientTrimDiagnostics.pubxml"),
                              TrimMode="full", EnableTrimAnalyzer="true", EnableAotAnalyzer="true", TrimmerSingleWarn="false")
        flags = [f"-p:{key}={value}" for key, value in properties.items()]
        # SDK artifacts output retains a unique per-project obj/bin path, unlike
        # assigning one BaseIntermediateOutputPath to the entire reference graph.
        run("restore", ["dotnet", "restore", PROJECT, "--runtime", args.runtime, "--artifacts-path", build,
                        "--verbosity", "normal", *flags])
        evaluation = subprocess.check_output(
            ["dotnet", "msbuild", PROJECT, *flags, f"-p:ArtifactsPath={build}", "-p:UseArtifactsOutput=true",
             "-getProperty:RuntimeIdentifier,SelfContained,PublishSingleFile,PublishTrimmed,PublishAot,TrimMode,EnableTrimAnalyzer,EnableAotAnalyzer,TrimmerSingleWarn,IncludeAllContentForSelfExtract,IncludeNativeLibrariesForSelfExtract,EnableCompressionInSingleFile,BaseIntermediateOutputPath,IntermediateOutputPath,ProjectAssetsFile,TargetPath",
             "-getItem:PackageReference,ProjectReference"], cwd=source, text=True, encoding="utf-8-sig")
        (evidence / "evaluated-project.json").write_text(evaluation, encoding="utf-8")
        evaluated = json.loads(evaluation)["Properties"]
        for key in ("RuntimeIdentifier", "SelfContained", "PublishSingleFile", "PublishTrimmed", "PublishAot"):
            if evaluated[key].lower() != properties[key].lower():
                raise ValueError(f"Evaluated {key} differs from requested restore/publish mode")
        assets = Path(evaluated["ProjectAssetsFile"])
        # Retain every restored project graph, not just declared PackageReferences.
        for item in build.rglob("project.assets.json"):
            destination = evidence / "restored-graphs" / item.relative_to(build)
            destination.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(item, destination)
        run("publish", ["dotnet", "publish", PROJECT, "--configuration", "Release", "--no-restore", "--runtime", args.runtime,
                        "--self-contained", "true", "--artifacts-path", build, "--output", distribution,
                        "--verbosity", "normal", *flags])
        graph = evidence / "publish.deps.json"
        if not graph.is_file():
            raise ValueError("Actual final prebundle publish graph was not captured")
        inputs = []
        for line in (evidence / "publish-inputs.txt").read_text(encoding="utf-8-sig").splitlines():
            path, relative, loose = line.split("|")
            path = Path(path)
            inputs.append({"path": relative, "bytes": path.stat().st_size, "sha256": digest(path),
                           "excludedFromBundle": loose.lower() == "true"})
        (evidence / "publish-input-inventory.json").write_text(json.dumps(inputs, indent=2) + "\n")
        for name in ("LICENSE", "NOTICE"):
            shutil.copy2(source / name, distribution / name)
        sbom = packages / f"netratel-client-{version}-{args.runtime}.spdx.json"
        run("sbom", [sys.executable, TOOLS / "generate-runtime-sbom.py", "--project-assets", assets,
                     "--runtime-graph", graph, "--distribution", distribution, "--runtime", args.runtime,
                     "--name", f"NetRatel-Client-{args.runtime}", "--version", version, "--output", sbom])
        manifest = json.loads((distribution / "netratel-client-manifest.json").read_text(encoding="utf-8-sig"))
        executable_name = "NetRatel.Client.exe" if args.runtime == "win-x64" else "NetRatel.Client"
        if (manifest.get("schema"), manifest.get("version"), manifest.get("runtimeId"), manifest.get("commitSha"), manifest.get("executable")) != (
                "netratel.client.manifest.v1", version, args.runtime, sha, executable_name):
            raise ValueError("Measured archive manifest differs from source/version/RID/executable identity")
        extension = "zip" if args.runtime == "win-x64" else "tar.gz"
        archive = packages / f"netratel-client-{version}-{args.runtime}.{extension}"
        if extension == "zip":
            if shutil.which("zip"):
                run("package", ["zip", "-qr", archive.name, distribution.name], cwd=packages)
            else:
                run("package", ["pwsh", "-NoLogo", "-NoProfile", "-NonInteractive", "-Command",
                                f"Compress-Archive -Path '{distribution.name}' -DestinationPath '{archive.name}' -Force"], cwd=packages)
        else:
            run("package", ["tar", "-C", packages, "-czf", archive, distribution.name])
        (packages / "SHA256SUMS").write_text("".join(f"{digest(path)}  {path.name}\n" for path in (archive, sbom)))
        files = inventory(distribution)
        (evidence / "distribution-inventory.json").write_text(json.dumps(files, indent=2) + "\n")
        metrics.update(executableBytes=(distribution / executable_name).stat().st_size,
                       extractedBytes=sum(item["bytes"] for item in files), archiveBytes=archive.stat().st_size,
                       archiveSha256=digest(archive), archiveName=archive.name)
        graph_json = json.loads(graph.read_text(encoding="utf-8-sig"))
        target = graph_json["targets"][graph_json["runtimeTarget"]["name"]]
        runtime_inventory = [{"identity": name, "type": graph_json["libraries"][name]["type"],
                              "assets": {kind: list(entry.get(kind, {})) for kind in ("runtime", "native", "resources", "runtimeTargets")}}
                             for name, entry in sorted(target.items())]
        (evidence / "runtime-package-inventory.json").write_text(json.dumps(runtime_inventory, indent=2) + "\n")
        run("verify", ["bash", source / "tools/ci/verify-client-release-artifact.sh", "--artifacts", packages,
                       "--version", version, "--runtime", args.runtime, "--extension", extension, "--integrity-only"])
        if args.verify_content:
            run("verify-content", [sys.executable, TOOLS / "verify-client-runtime-content.py", "--sbom", sbom,
                                   "--runtime-graph", graph, "--publish-inputs", evidence / "publish-inputs.txt",
                                   "--distribution", distribution])
        launch_env = os.environ.copy()
        launch_env["DOTNET_BUNDLE_EXTRACT_BASE_DIR"] = str(state / "fresh-extraction")
        for label in ("fresh", "cached"):
            run("launch-" + label, [distribution / executable_name, "--version"], env=launch_env)
        metrics["launchTimingMeaning"] = "--version only, before normal initialization; fresh then cached single-file extraction, not agent/gateway startup."
        # The native archive already retains these bytes; avoid duplicating the
        # extracted distribution in the Actions upload/storage bill.
        shutil.rmtree(distribution)
    except subprocess.CalledProcessError as error:
        exit_code = error.returncode if error.returncode > 0 else 1
        metrics["error"] = str(error)
    except (ValueError, OSError, KeyError) as error:
        exit_code = 1
        metrics["error"] = str(error)
        print(str(error), file=sys.stderr)
    finally:
        warnings = Counter()
        warning_lines = []
        for path in evidence.glob("*.log"):
            for line in path.read_text(encoding="utf-8").splitlines():
                warning = re.search(r"\bwarning (IL[23]\d{3})\b", line)
                if warning:
                    warnings[warning[1]] += 1
                    warning_lines.append(line)
        (evidence / "trim-aot-warnings.txt").write_text("\n".join(sorted(set(warning_lines))) + "\n")
        metrics.update(exitCode=exit_code, warningsByCode=dict(sorted(warnings.items())),
                       finishedUtc=datetime.now(timezone.utc).isoformat())
        (evidence / "metrics.json").write_text(json.dumps(metrics, indent=2) + "\n")
    return exit_code


if __name__ == "__main__":
    raise SystemExit(main())
