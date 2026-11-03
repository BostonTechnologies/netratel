#!/usr/bin/env python3
"""Optional same-source Linux FDD control and self-contained trimmed-JIT diagnostic."""
import argparse
from collections import Counter
from datetime import datetime, timezone
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tarfile
import time

TOOLS = Path(__file__).resolve().parent
PROJECT = "src/NetRatel/NetRatel.Mcp/NetRatel.Mcp.csproj"


def load(name):
    spec = importlib.util.spec_from_file_location(name, TOOLS / (name + ".py"))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def digest(path):
    with path.open("rb") as source:
        return hashlib.file_digest(source, "sha256").hexdigest()


def diagnostics(text):
    # Commands disable the MSBuild summary. Also exclude its recognizable echo
    # if a nested invocation emits one; distinct primary emissions stay distinct.
    records = []
    for number, line in enumerate(text.splitlines(), 1):
        line = re.sub(r"\x1b\[[0-?]*[ -/]*[@-~]", "", line)
        if line.strip() in ("Build succeeded.", "Build FAILED."):
            break
        match = re.search(r":\s*(?:Trim analysis |AOT analysis )?(warning|error) ([A-Z]+\d{3,5}):", line)
        if match:
            records.append({"line": number, "severity": match[1], "code": match[2], "text": line})
    return records


def properties(mode, sha):
    trimmed = mode == "trimmed-jit"
    result = {"Configuration": "Release", "RuntimeIdentifier": "linux-x64", "SelfContained": str(trimmed).lower(),
              "UseAppHost": "true", "PublishSingleFile": "false", "PublishTrimmed": str(trimmed).lower(),
              "PublishAot": "false", "SourceRevisionId": sha, "PublicSourceRevision": sha}
    if trimmed:
        result.update(TrimMode="full", EnableTrimAnalyzer="true", EnableAotAnalyzer="false", TrimmerSingleWarn="false")
    return result


def run(record, directory, stage, command, source):
    command = list(map(str, command))
    started = time.monotonic()
    entry = record["stages"][stage] = {"command": command, "exitCode": None}
    with (directory / (stage + ".log")).open("w") as log:
        log.write(json.dumps({"command": command}) + "\n")
        log.flush()
        process = subprocess.Popen(command, cwd=source, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                   text=True, encoding="utf-8", errors="replace")
        for line in process.stdout:
            log.write(line)
            print(line, end="", flush=True)
        status = process.wait()
    entry.update(exitCode=status, seconds=round(time.monotonic() - started, 3))
    if status:
        record["failureStage"] = stage
        raise subprocess.CalledProcessError(status, command)


def external_runtime():
    lines = subprocess.check_output(["dotnet", "--list-runtimes"], text=True).splitlines()
    choices = [line.split() for line in lines if line.startswith("Microsoft.NETCore.App 10.0.")]
    if not choices:
        raise ValueError("FDD control requires an installed .NET 10 runtime")
    selected = max(choices, key=lambda row: tuple(map(int, row[1].split("-", 1)[0].split("."))))
    directory = Path(selected[2].strip("[]")) / selected[1]
    return {"framework": selected[0], "version": selected[1], "directory": str(directory),
            "observedSharedFrameworkRegularBytes": sum(path.stat().st_size for path in directory.rglob("*") if path.is_file()),
            "scope": "Existing runner shared-framework directory only; runtime installer/download, dotnet host and hostfxr are not included or measured."}


def publish_mode(mode, source, state, evidence, sha, version, fixture):
    directory = evidence / mode
    directory.mkdir()
    build = state / mode
    build.mkdir()
    artifacts = directory / "artifacts"
    artifacts.mkdir()
    distribution = artifacts / "netratel-mcp-linux-x64"
    record = {"mode": mode, "sourceSha": sha, "productVersion": version, "runtime": "linux-x64", "stages": {},
              "passed": False, "footprintScope": "FDD application only; external runtime separate" if mode == "fdd" else
              "Self-contained trimmed JIT application including its bundled runtime; not comparable to an FDD-only download"}
    try:
        values = properties(mode, sha)
        flags = [f"-p:{key}={value}" for key, value in values.items()]
        run(record, directory, "restore", ["dotnet", "restore", PROJECT, "--runtime", "linux-x64", "--disable-parallel",
             "--artifacts-path", build, "--verbosity", "normal", "-clp:NoSummary", *flags], source)
        evaluated = subprocess.check_output(["dotnet", "msbuild", PROJECT, *flags, f"-p:ArtifactsPath={build}",
            "-p:UseArtifactsOutput=true", "-getProperty:RuntimeIdentifier,SelfContained,UseAppHost,PublishSingleFile,PublishTrimmed,PublishAot,TrimMode,EnableTrimAnalyzer,EnableAotAnalyzer,TrimmerSingleWarn,JsonSerializerIsReflectionEnabledByDefault,ProjectAssetsFile"],
            cwd=source, text=True)
        (directory / "evaluated-project.json").write_text(evaluated)
        evaluated = json.loads(evaluated)["Properties"]
        for key in ("RuntimeIdentifier", "SelfContained", "UseAppHost", "PublishSingleFile", "PublishTrimmed", "PublishAot"):
            if evaluated[key].lower() != values[key].lower():
                raise ValueError("Evaluated publish mode differs: " + key)
        assets = Path(evaluated["ProjectAssetsFile"])
        run(record, directory, "publish", ["dotnet", "publish", PROJECT, "--configuration", "Release", "--no-restore",
            "--runtime", "linux-x64", "--self-contained", values["SelfContained"], "--artifacts-path", build,
            "--output", distribution, "--verbosity", "normal", "--tl:off", "-clp:NoSummary", "-m:1", "-nr:false", *flags], source)
        for name in ("LICENSE", "NOTICE"):
            shutil.copyfile(source / name, distribution / name)
        shutil.copyfile(source / "docs/integrations/CLI_AND_MCP.md", distribution / "CLI_AND_MCP.md")
        run(record, directory, "documentation", [sys.executable, source / "tools/ci/prepare-bundle-documentation.py", "--bundle",
            distribution, "--commit", sha, "--document", "CLI_AND_MCP.md=docs/integrations/CLI_AND_MCP.md"], source)
        graph = distribution / "NetRatel.Mcp.deps.json"
        shutil.copyfile(graph, directory / "publish.deps.json")
        shutil.copyfile(distribution / "NetRatel.Mcp.runtimeconfig.json", directory / "publish.runtimeconfig.json")
        sbom = artifacts / f"netratel-mcp-stdio-{version}-linux-x64.spdx.json"
        run(record, directory, "sbom", [sys.executable, source / "tools/ci/generate-runtime-sbom.py", "--project-assets", assets,
            "--runtime-graph", graph, "--distribution", distribution, "--runtime", "linux-x64", "--name",
            "NetRatel-Mcp-linux-x64", "--version", version, "--output", sbom], source)
        archive = artifacts / f"netratel-mcp-stdio-{version}-linux-x64.tar.gz"
        run(record, directory, "archive", ["tar", "-C", artifacts, "-czf", archive, distribution.name], source)
        index = {path.name: digest(path) for path in (archive, sbom)}
        (artifacts / "SHA256SUMS").write_text("".join(f"{value}  {name}\n" for name, value in index.items()))
        inventory = [{"path": str(path.relative_to(distribution)), "bytes": path.stat().st_size, "sha256": digest(path)}
                     for path in sorted(distribution.rglob("*")) if path.is_file()]
        (directory / "file-inventory.json").write_text(json.dumps(inventory, indent=2) + "\n")
        record.update(archiveName=archive.name, archiveBytes=archive.stat().st_size, archiveSha256=index[archive.name],
                      extractedRegularBytes=sum(item["bytes"] for item in inventory), files=len(inventory),
                      symbolBytes=sum(item["bytes"] for item in inventory if item["path"].endswith(".pdb")))
        extraction = build / "original-archive"
        extraction.mkdir()
        with tarfile.open(archive) as bundle:
            bundle.extractall(extraction, filter="data")
        if any(digest(artifacts / name) != value for name, value in index.items()):
            raise ValueError("Packaged checksum identity changed")
        run(record, directory, "verify-sbom", [sys.executable, source / "tools/ci/verify-runtime-sbom.py", "--sbom", sbom,
            "--root", extraction], source)
        archived = extraction / distribution.name
        for item in inventory:
            if digest(archived / item["path"]) != item["sha256"]:
                raise ValueError("Actual archive inventory differs: " + item["path"])
        runtime_graph = json.loads(graph.read_text())
        runtime_files = set()
        for name, entry in runtime_graph["targets"][runtime_graph["runtimeTarget"]["name"]].items():
            if runtime_graph["libraries"][name]["type"] == "runtimepack":
                for kind in ("runtime", "native"):
                    runtime_files.update(Path(path).name for path in entry.get(kind, {}))
        record["bundledRuntimePackRegularBytes"] = sum(item["bytes"] for item in inventory if item["path"] in runtime_files)
        record["bundledRuntimePackAssetNames"] = sorted(runtime_files)
        probes = load("probe-stdio-assessment").probe(archived / "NetRatel.Mcp", directory / "probes", build / "probe-state",
                                                     version, sha, fixture)
        if not probes["passed"]:
            record["failureStage"] = "actual-archive-probes"
            raise ValueError("Actual archive probes failed; original stdout/stderr and stage receipt retained")
        record["passed"] = True
    except (ValueError, OSError, KeyError, subprocess.CalledProcessError) as error:
        record["error"] = type(error).__name__ + ": " + str(error)
    finally:
        emitted = [{"stage": path.stem, **item} for path in directory.glob("*.log") for item in diagnostics(path.read_text())]
        (directory / "emitted-diagnostics.json").write_text(json.dumps(emitted, indent=2) + "\n")
        record["emittedDiagnosticsByCode"] = dict(sorted(Counter(item["code"] for item in emitted).items()))
        record["emittedDiagnosticCount"] = len(emitted)
        record["diagnosticCountScope"] = "Primary compiler/linker emission lines; MSBuild summary echoes excluded, distinct emissions retained"
        (directory / "metrics.json").write_text(json.dumps(record, indent=2) + "\n")
        # The archives retain original bytes; avoid uploading extracted duplicates.
        if distribution.exists():
            shutil.rmtree(distribution)
    return record


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", required=True, type=Path)
    parser.add_argument("--state-root", required=True, type=Path)
    parser.add_argument("--evidence", required=True, type=Path)
    args = parser.parse_args()
    if os.environ.get("GITHUB_ACTIONS") != "true" or os.environ.get("RUNNER_ENVIRONMENT") != "github-hosted":
        parser.error("Publish assessments require a GitHub-hosted Actions runner (AGENTS.md)")
    source = args.source.resolve()
    sha = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=source, text=True).strip()
    if not re.fullmatch("[0-9a-f]{40}", sha):
        parser.error("A full source SHA is required")
    state = args.state_root.resolve() / sha
    evidence = args.evidence.resolve()
    if state.exists() or evidence.exists():
        parser.error("State and evidence paths must be fresh")
    state.mkdir(parents=True)
    evidence.mkdir(parents=True)
    snapshot = state / "source"
    snapshot.mkdir()
    archive = state / "source.tar"
    with archive.open("wb") as output:
        subprocess.run(["git", "archive", sha], cwd=source, stdout=output, check=True)
    with tarfile.open(archive) as bundle:
        bundle.extractall(snapshot, filter="data")
    sdk = subprocess.check_output(["dotnet", "--version"], cwd=source, text=True).strip()
    if sdk != json.loads((snapshot / "global.json").read_text())["sdk"]["version"]:
        raise ValueError("SDK differs from the selected source pin")
    version = subprocess.check_output([sys.executable, snapshot / "tools/ci/product-version.py"], cwd=snapshot, text=True).strip()
    summary = {"sourceSha": sha, "productVersion": version, "dotnetSdkVersion": sdk, "runtime": "linux-x64",
               "startedUtc": datetime.now(timezone.utc).isoformat(), "runner": {key: os.environ.get(key) for key in
               ("RUNNER_OS", "RUNNER_ARCH", "ImageOS", "ImageVersion", "GITHUB_RUN_ID", "GITHUB_RUN_ATTEMPT")},
               "comparison": "Same source and SDK, fresh per-mode intermediates, FDD then self-contained trimmed JIT; no cross-mode savings claim",
               "cacheConditions": "Fresh source snapshot and per-mode SDK artifacts; shared hosted NuGet cache; FDD runs first",
               "externalRuntime": external_runtime(), "modes": [], "passed": False}
    fixture = load("probe-stdio-assessment").Fixture(state / "fixture")
    try:
        for mode in ("fdd", "trimmed-jit"):
            record = publish_mode(mode, snapshot, state, evidence, sha, version, fixture)
            summary["modes"].append(record)
            if not record["passed"]:
                break
        if len(summary["modes"]) == 2 and all(mode["passed"] for mode in summary["modes"]):
            catalogues = [json.loads((evidence / mode / "probes/catalog.json").read_text()) for mode in ("fdd", "trimmed-jit")]
            if catalogues[0] != catalogues[1]:
                raise ValueError("Full catalog, resource/prompt descriptors, digest or input/output schemas differ")
            summary["passed"] = True
    except (ValueError, OSError, KeyError) as error:
        summary["error"] = str(error)
    finally:
        fixture.close()
        summary["finishedUtc"] = datetime.now(timezone.utc).isoformat()
        (evidence / "assessment.json").write_text(json.dumps(summary, indent=2) + "\n")
        print("STDIO_ASSESSMENT=" + json.dumps(summary), flush=True)
    return 0 if summary["passed"] else 1


if __name__ == "__main__":
    raise SystemExit(main())
