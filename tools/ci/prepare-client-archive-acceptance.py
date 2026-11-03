#!/usr/bin/env python3
"""Verify an original hosted Linux trim assessment and wrap its exact archive."""
import argparse
from datetime import datetime, timezone
import importlib.util
import json
import os
from pathlib import Path
import re
import shutil
import stat
import subprocess
import sys
import tarfile
import time
import zipfile

TOOLS = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location("measurement", TOOLS / "measure-client-publish.py")
measurement = importlib.util.module_from_spec(spec)
spec.loader.exec_module(measurement)


def read_json(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def download_original(repository, run_id, evidence, work):
    def api(endpoint):
        return json.loads(subprocess.check_output(["gh", "api", endpoint], text=True))

    run = api(f"repos/{repository}/actions/runs/{run_id}")
    (evidence / "original-run.json").write_text(json.dumps(run, indent=2) + "\n")
    if (str(run.get("id")), run.get("status"), run.get("conclusion"), run.get("event"), run.get("path"),
            run.get("repository", {}).get("full_name"), run.get("head_repository", {}).get("full_name")) != (
            run_id, "completed", "success", "workflow_dispatch", ".github/workflows/integration-validation.yml", repository, repository):
        raise ValueError("Original run must be a completed successful optional integration run in this repository")
    listing = api(f"repos/{repository}/actions/runs/{run_id}/artifacts?per_page=100")
    (evidence / "original-artifacts.json").write_text(json.dumps(listing, indent=2) + "\n")
    if listing["total_count"] != len(listing["artifacts"]):
        raise ValueError("Original artifact listing is incomplete")
    original = evidence / "original"
    original.mkdir()
    for name in ("client-measurement-evidence-linux-x64", "client-measurement-archives-linux-x64"):
        matches = [item for item in listing["artifacts"] if item["name"] == name]
        if len(matches) != 1 or matches[0].get("expired") is not False:
            raise ValueError("Original run must retain exactly one unexpired artifact: " + name)
        artifact = matches[0]
        archive = work / (name + ".zip")
        with archive.open("wb") as output:
            subprocess.run(["gh", "api", f"repos/{repository}/actions/artifacts/{artifact['id']}/zip"], stdout=output, check=True)
        if archive.stat().st_size != artifact["size_in_bytes"] or "sha256:" + measurement.digest(archive) != artifact.get("digest"):
            raise ValueError("Downloaded Actions artifact differs from its authenticated receipt: " + name)
        with zipfile.ZipFile(archive) as bundle:
            for entry in bundle.infolist():
                destination = original / entry.filename
                if (not destination.resolve().is_relative_to(original.resolve()) or
                        stat.S_ISLNK(entry.external_attr >> 16) or (destination.exists() and not entry.is_dir())):
                    raise ValueError("Unsafe or duplicate original evidence entry")
                bundle.extract(entry, original)
    return run, original / "trimmed/linux-x64"


def verify_original(original, run, source_sha, version, sdk):
    metrics_path = original / "metrics.json"
    metrics = read_json(metrics_path)
    if (metrics.get("sourceSha"), metrics.get("runtime"), metrics.get("mode"), metrics.get("productVersion"),
            metrics.get("dotnetSdkVersion"), metrics.get("exitCode"), metrics.get("runner", {}).get("GITHUB_RUN_ID"),
            metrics.get("runner", {}).get("GITHUB_RUN_ATTEMPT")) != (
            source_sha, "linux-x64", "trimmed", version, sdk, 0, str(run["id"]), str(run["run_attempt"])):
        raise ValueError("Original measurement source/SDK/version/RID/mode/run identity or outcome differs")
    for stage in ("publish", "package", "verify", "verify-content", "launch-fresh", "launch-cached"):
        if metrics.get("stages", {}).get(stage, {}).get("exitCode") != 0:
            raise ValueError("Original assessment did not complete: " + stage)
    for backend in ("native", "python"):
        probe = metrics["stages"].get("pty-" + backend, {})
        if (probe.get("exitCode"), probe.get("finalStage"), probe.get("passed"), probe.get("archiveSha256")) != (
                0, "passed", True, metrics.get("archiveSha256")):
            raise ValueError("Original packaged PTY probe did not pass on these archive bytes: " + backend)
    properties = read_json(original / "evaluated-project.json")["Properties"]
    for key, expected in {"RuntimeIdentifier": "linux-x64", "SelfContained": "true", "PublishSingleFile": "true",
                          "PublishTrimmed": "true", "PublishAot": "false", "TrimMode": "full"}.items():
        if properties.get(key, "").lower() != expected:
            raise ValueError("Original evaluated publish property differs: " + key)
    name = f"netratel-client-{version}-linux-x64.tar.gz"
    if metrics.get("archiveName") != name:
        raise ValueError("Original archive name differs from the selected version/RID")
    packages = original / "artifacts"
    archive = packages / name
    sbom = packages / f"netratel-client-{version}-linux-x64.spdx.json"
    if archive.stat().st_size != metrics["archiveBytes"] or measurement.digest(archive) != metrics["archiveSha256"]:
        raise ValueError("Original native archive differs from measured bytes")
    checksums = {}
    for line in (packages / "SHA256SUMS").read_text().splitlines():
        match = re.fullmatch(r"([a-f0-9]{64})  ([^/\\]+)", line)
        if not match or match[2] in checksums:
            raise ValueError("Original checksum inventory is malformed or duplicated")
        checksums[match[2]] = match[1]
    if checksums != {archive.name: measurement.digest(archive), sbom.name: measurement.digest(sbom)}:
        raise ValueError("Original checksum inventory differs from archive/SBOM bytes")
    return metrics, archive, sbom


def extract_distribution(archive, destination):
    destination.mkdir()
    with tarfile.open(archive) as bundle:
        names = set()
        for entry in bundle.getmembers():
            path = destination / entry.name
            if (entry.name in names or not path.resolve().is_relative_to(destination.resolve()) or
                    entry.name.split("/")[0] != "netratel-client-linux-x64" or not (entry.isdir() or entry.isfile())):
                raise ValueError("Unsafe or duplicate native archive entry")
            names.add(entry.name)
        bundle.extractall(destination, filter="data")
    return destination / "netratel-client-linux-x64"


def probe_wrapper(receipt, evidence, image):
    failures = []
    for backend in ("native", "python"):
        name = "pty-" + backend
        container = f"netratel-archive-{os.environ['GITHUB_RUN_ID']}-{os.environ.get('GITHUB_RUN_ATTEMPT', '1')}-{backend}"
        command = ["docker", "run", "--name", container, "--network", "none", image, "--terminal-pty-self-test-" + backend]
        started = time.perf_counter()
        record = receipt["stages"][name] = {"command": command}
        try:
            result = subprocess.run(command, capture_output=True, text=True, timeout=100)
            record["exitCode"] = result.returncode
            stdout, stderr = result.stdout, result.stderr
        except subprocess.TimeoutExpired as error:
            record.update(exitCode=None, timedOut=True)
            stdout, stderr = (value.decode(errors="replace") if isinstance(value, bytes) else value or "" for value in (error.stdout, error.stderr))
        finally:
            subprocess.run(["docker", "rm", "-f", container], capture_output=True, check=False)
        (evidence / (name + ".stdout.log")).write_text(stdout)
        (evidence / (name + ".stderr.log")).write_text(stderr)
        events = []
        for line in stdout.splitlines():
            try:
                event = json.loads(line)
                if isinstance(event, dict) and "stage" in event:
                    events.append(event)
            except json.JSONDecodeError:
                pass
        final = events[-1] if events else {}
        record.update(seconds=round(time.perf_counter() - started, 3), finalStage=final.get("stage"), reaped=final.get("reaped"))
        record["passed"] = record["exitCode"] == 0 and final.get("stage") == "passed" and final.get("reaped") is True
        if not record["passed"]:
            failures.append(name)
    if failures:
        receipt["failureStages"] = failures
        raise ValueError("Archived client wrapper PTY acceptance failed: " + ", ".join(failures))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--run-id", required=True)
    parser.add_argument("--source", required=True, type=Path)
    parser.add_argument("--work", required=True, type=Path)
    parser.add_argument("--evidence", required=True, type=Path)
    parser.add_argument("--image", required=True)
    args = parser.parse_args()
    if os.environ.get("GITHUB_ACTIONS") != "true" or os.environ.get("RUNNER_ENVIRONMENT") != "github-hosted":
        parser.error("Archive wrappers and Release acceptance run only on hosted Actions (AGENTS.md)")
    if not re.fullmatch(r"[1-9][0-9]*", args.run_id):
        parser.error("An original client-measurement run ID is required")
    source, work, evidence = args.source.resolve(), args.work.resolve(), args.evidence.resolve()
    work.mkdir(parents=True, exist_ok=False)
    evidence.mkdir(parents=True, exist_ok=False)
    receipt = {"originalRunId": args.run_id, "startedUtc": datetime.now(timezone.utc).isoformat(), "stages": {},
               "toolingSourceSha": os.environ.get("GITHUB_WORKFLOW_SHA"), "functionalAcceptance": "pending deployment",
               "wrapperMeaning": "Disposable runtime-deps wrapper of exact archived /app bytes; not an optimized distribution"}
    exit_code = 1
    try:
        source_sha = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=source, text=True).strip()
        version = subprocess.check_output([sys.executable, source / "tools/ci/product-version.py"], cwd=source, text=True).strip()
        sdk = subprocess.check_output(["dotnet", "--version"], cwd=source, text=True).strip()
        if sdk != read_json(source / "global.json")["sdk"]["version"]:
            raise ValueError("Acceptance SDK differs from selected source's pinned SDK")
        receipt.update(serviceSourceSha=source_sha, dotnetSdkVersion=sdk)
        run, original = download_original(os.environ["GITHUB_REPOSITORY"], args.run_id, evidence, work)
        metrics, archive, sbom = verify_original(original, run, source_sha, version, sdk)
        receipt["original"] = {key: metrics[key] for key in ("sourceSha", "dotnetSdkVersion", "runtime", "mode", "productVersion",
                                                           "archiveName", "archiveBytes", "archiveSha256", "runner")}
        receipt["originalMetricsSha256"] = measurement.digest(original / "metrics.json")
        distribution = extract_distribution(archive, work / "extracted")
        manifest = read_json(distribution / "netratel-client-manifest.json")
        if (manifest.get("schema"), manifest.get("commitSha"), manifest.get("version"), manifest.get("runtimeId"), manifest.get("executable")) != (
                "netratel.client.manifest.v1", source_sha, version, "linux-x64", "NetRatel.Client"):
            raise ValueError("Original manifest differs from selected source/version/RID/executable")
        expected = read_json(original / "distribution-inventory.json")
        if measurement.inventory(distribution) != expected or not os.access(distribution / "NetRatel.Client", os.X_OK):
            raise ValueError("Original archive contents differ from measured inventory or executable mode")

        def stage(name, command):
            measurement.run_stage(receipt, evidence, name, command, source)

        stage("verify", measurement.artifact_verifier_command(source, original / "artifacts", version, "linux-x64"))
        stage("verify-content", [sys.executable, TOOLS / "verify-client-runtime-content.py", "--sbom", sbom,
              "--runtime-graph", original / "publish.deps.json", "--publish-inputs", original / "publish-inputs.txt", "--distribution", distribution])
        context = work / "context"
        context.mkdir()
        shutil.copytree(distribution, context / "app")
        shutil.copyfile(TOOLS.parents[1] / "docker/client/Dockerfile.archive-acceptance", context / "Dockerfile")
        stage("wrapper-build", ["docker", "build", "--pull", "--tag", args.image, context])
        inspection = json.loads(subprocess.check_output(["docker", "image", "inspect", args.image], text=True))[0]
        if (inspection["Config"].get("User"), inspection["Config"].get("WorkingDir"), inspection["Config"].get("Entrypoint")) != (
                "netratel", "/app", ["/app/NetRatel.Client"]):
            raise ValueError("Wrapper changed the normal client user/work directory/entrypoint")
        receipt["wrapperImageId"] = inspection["Id"]
        uid = subprocess.check_output(["docker", "run", "--rm", "--network", "none", "--entrypoint", "id", args.image, "-u"], text=True).strip()
        if uid != "10001":
            raise ValueError("Wrapper changed the normal client UID")
        receipt["wrapperUid"] = int(uid)
        # Inspect the final image as its normal UID: extra/fallback files fail equality.
        inventory_code = "import hashlib,json,pathlib; r=pathlib.Path('/app'); print(json.dumps([{'path':p.relative_to(r).as_posix(),'bytes':p.stat().st_size,'sha256':hashlib.sha256(p.read_bytes()).hexdigest()} for p in sorted(r.rglob('*')) if p.is_file()]))"
        actual = json.loads(subprocess.check_output(["docker", "run", "--rm", "--network", "none", "--entrypoint", "python3", args.image, "-c", inventory_code], text=True))
        (evidence / "wrapper-app-inventory.json").write_text(json.dumps(actual, indent=2) + "\n")
        if actual != expected:
            raise ValueError("Final wrapper /app contains different or additional files")
        probe_wrapper(receipt, evidence, args.image)
        with Path(os.environ["GITHUB_ENV"]).open("a") as env:
            env.write(f"NETRATEL_CLIENT_SMOKE_IMAGE={args.image}\nNETRATEL_CLIENT_ARCHIVE_EVIDENCE={evidence}\n")
        exit_code = 0
    except (ValueError, KeyError, OSError, subprocess.CalledProcessError) as error:
        receipt["error"] = str(error)
        print(str(error), file=sys.stderr)
    finally:
        receipt.update(exitCode=exit_code, finishedUtc=datetime.now(timezone.utc).isoformat())
        (evidence / "archive-acceptance.json").write_text(json.dumps(receipt, indent=2) + "\n")
    return exit_code


if __name__ == "__main__":
    raise SystemExit(main())
