#!/usr/bin/env python3
"""Opt-in hosted comparison of the current Client image and an unbundled shared-runtime candidate."""
import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import shutil
import tempfile
import tarfile
import time
import uuid


ROOT = Path(__file__).resolve().parents[2]


def command(*args):
    return subprocess.check_output(args, cwd=ROOT, text=True).strip()


def logged(args, path):
    with path.open("wb") as output:
        subprocess.run(args, cwd=ROOT, stdout=output, stderr=subprocess.STDOUT, check=True)


def probe(image, backend, directory):
    cache = directory / (backend + "-cache")
    cache.mkdir(mode=0o777)
    cache.chmod(0o777)  # Disposable host fixture is writable by the image's normal UID.
    container = "netratel-client-probe-" + uuid.uuid4().hex
    args = ["docker", "run", "--rm", "--name", container, "--mount", f"type=bind,src={cache},dst=/tmp/netratel-extraction",
            "--env", "DOTNET_BUNDLE_EXTRACT_BASE_DIR=/tmp/netratel-extraction",
            image, "--terminal-pty-self-test-" + backend]
    started = time.perf_counter()
    record = {"command": args}
    try:
        result = subprocess.run(args, cwd=ROOT, capture_output=True, timeout=90)
        record["exitCode"] = result.returncode
        (directory / (backend + ".stdout")).write_bytes(result.stdout)
        (directory / (backend + ".stderr")).write_bytes(result.stderr)
        records = []
        for line in result.stdout.splitlines():
            if line.lstrip().startswith(b"{"):
                value = json.loads(line)
                if isinstance(value, dict) and "stage" in value:
                    records.append(value)
        record.update(exitCode=result.returncode,
                      passed=result.returncode == 0 and bool(records)
                      and records[-1].get("stage") == "passed" and records[-1].get("reaped") is True)
    except subprocess.TimeoutExpired as error:
        (directory / (backend + ".stdout")).write_bytes(error.stdout or b"")
        (directory / (backend + ".stderr")).write_bytes(error.stderr or b"")
        record.update(exitCode=None, passed=False, timedOut=True)
    except (ValueError, TypeError, AttributeError) as error:
        record.update(exitCode=result.returncode, passed=False, error=str(error))
    finally:
        subprocess.run(["docker", "rm", "-f", container], stdout=subprocess.DEVNULL,
                       stderr=subprocess.DEVNULL, timeout=15)
    record["seconds"] = round(time.perf_counter() - started, 3)
    # The normal image UID creates private bundle directories. Measure those
    # disposable files as root, then return ownership solely for fixture cleanup.
    cache_script = """
import os
from pathlib import Path
import sys
root = Path('/cache')
paths = list(root.rglob('*'))
print(sum(path.stat().st_size for path in paths if path.is_file()))
for path in paths + [root]:
    os.chown(path, int(sys.argv[1]), int(sys.argv[2]), follow_symlinks=False)
"""
    record["bundleExtractionBytes"] = int(command(
        "docker", "run", "--rm", "--user", "0:0", "--mount", f"type=bind,src={cache},dst=/cache",
        "--entrypoint", "python3", image, "-c", cache_script, str(os.getuid()), str(os.getgid())))
    shutil.rmtree(cache)
    return record


def compare_layers(record, args, results):
    spec = importlib.util.spec_from_file_location("prior", ROOT / "tools/ci/select-prior-release.py")
    prior = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(prior)
    selected = prior.select(None)
    (results / "selected-companion-release.json").write_text(json.dumps(selected, indent=2) + "\n")
    companions = []
    record["companions"] = {}
    for component in ("api", "web", "migrations", "mcp-http"):
        reference = selected["images"][component]
        alias = "netratel-client-stack-" + component + ":comparison"
        logged(["docker", "pull", reference], results / (component + "-pull.log"))
        subprocess.run(["docker", "tag", reference, alias], cwd=ROOT, check=True)
        info = json.loads(command("docker", "image", "inspect", alias))[0]
        record["companions"][component] = {
            "release": selected["tag"], "reference": reference, "imageId": info["Id"],
            "sourceSha": info["Config"]["Labels"]["org.opencontainers.image.revision"],
            "productVersion": info["Config"]["Labels"]["org.opencontainers.image.version"]}
        companions.append(alias)
    with tempfile.TemporaryDirectory(prefix="netratel-client-layers-") as temporary:
        directory = Path(temporary)
        archive = directory / "images.tar"
        subprocess.run(["docker", "image", "save", args.control, args.candidate, *companions,
                        "--output", str(archive)], cwd=ROOT, check=True)
        layers = directory / "layers"
        layers.mkdir()
        with tarfile.open(archive) as exported:
            exported.extractall(layers, filter="data")
        comparison = ["python3", "tools/ci/compare-stack-image-layers.py",
                      "--directory", str(layers), "--output", str(results / "layer-comparison.json"),
                      "--source-sha", record["sourceSha"], "--candidate", args.candidate, "--control", args.control]
        for companion in companions:
            comparison.extend(("--companion", companion))
        logged(comparison, results / "layer-comparison.log")
    record["layerComparison"] = json.loads((results / "layer-comparison.json").read_text())


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--control", required=True)
    parser.add_argument("--candidate", required=True)
    parser.add_argument("--results", required=True, type=Path)
    args = parser.parse_args()
    if os.environ.get("GITHUB_ACTIONS") != "true" or os.environ.get("RUNNER_ENVIRONMENT") != "github-hosted":
        parser.error("Release-style image comparisons require a GitHub-hosted Actions runner.")
    results = args.results.resolve()
    results.mkdir(parents=True)
    revision = command("git", "rev-parse", "HEAD")
    version = command("python3", "tools/ci/product-version.py")
    record = {"sourceSha": revision, "productVersion": version, "dotnetSdkVersion": command("dotnet", "--version"),
              "runtime": "linux-x64",
              "runner": {key: os.environ.get(key) for key in ("RUNNER_OS", "RUNNER_ARCH", "ImageOS", "ImageVersion")},
              "measurement": "Image Size/app/cache bytes are distinct. Layer comparison uses normalized gzip exports with four fixed published companions; not registry-transfer bytes.",
              "arms": {}, "exitCode": 1}
    try:
        subprocess.run(["git", "diff", "--exit-code"], cwd=ROOT, check=True)
        logged(["bash", "tools/ci/build-public-image.sh", "--dockerfile", "docker/client/Dockerfile.public",
                "--image", args.candidate, "--version", version, "--revision", revision,
                "--build-arg", "CLIENT_SELF_CONTAINED=false", "--build-arg", "CLIENT_SINGLE_FILE=false",
                "--build-arg", "CLIENT_RUNTIME_IMAGE=mcr.microsoft.com/dotnet/runtime:10.0-noble"],
               results / "candidate-build.log")
        logged(["bash", "tools/ci/scan-public-image.sh", "--image", args.candidate, "--version", version,
                "--revision", revision], results / "candidate-scan.log")
        for arm, image, expected_mode in (("control", args.control, True), ("candidate", args.candidate, False)):
            directory = results / arm
            directory.mkdir()
            info = json.loads(command("docker", "image", "inspect", image))[0]
            (directory / "image.json").write_text(json.dumps(info, indent=2) + "\n")
            labels = info["Config"]["Labels"]
            if labels["org.opencontainers.image.revision"] != revision or labels["org.opencontainers.image.version"] != version:
                raise ValueError("Image source/version differs from the selected comparison.")
            if info["Config"]["User"] != "netratel":
                raise ValueError("Client image changed its normal nonroot identity.")
            uid = command("docker", "run", "--rm", "--entrypoint", "id", image, "-u")
            if uid != "10001":
                raise ValueError("Client image changed its persisted-state UID.")
            with tempfile.TemporaryDirectory(prefix="netratel-client-image-") as temporary:
                app = Path(temporary) / "out"
                container = command("docker", "create", image)
                try:
                    subprocess.run(["docker", "cp", container + ":/app", str(app)], check=True)
                finally:
                    subprocess.run(["docker", "rm", container], check=True, stdout=subprocess.DEVNULL)
                manifest = json.loads((app / "netratel-client-manifest.json").read_text(encoding="utf-8-sig"))
                if (manifest["schema"], manifest["version"], manifest["executable"], manifest["commitSha"],
                        manifest["runtimeId"], manifest["selfContained"], manifest["singleFile"]) != (
                        "netratel.client.manifest.v1", version, "NetRatel.Client", revision,
                        "linux-x64", expected_mode, expected_mode):
                    raise ValueError("Actual Client manifest differs from its comparison arm.")
                shutil.copyfile(app / "runtime.spdx.json", directory / "runtime.spdx.json")
                logged(["python3", "tools/ci/verify-runtime-sbom.py", "--sbom", str(app / "runtime.spdx.json"),
                        "--root", temporary], directory / "sbom-check.log")
                (directory / "manifest.json").write_text(json.dumps(manifest, indent=2) + "\n")
                (directory / "runtimeconfig.json").write_text(
                    (app / "NetRatel.Client.runtimeconfig.json").read_text() if not expected_mode else
                    json.dumps({"location": "embedded in the control single-file executable"}) + "\n")
                files = [{"path": path.relative_to(app).as_posix(), "bytes": path.stat().st_size,
                          "sha256": hashlib.sha256(path.read_bytes()).hexdigest()}
                         for path in sorted(app.rglob("*")) if path.is_file()]
                (directory / "app-files.json").write_text(json.dumps(files, indent=2) + "\n")
            arm_record = record["arms"][arm] = {
                "imageId": info["Id"], "uid": uid, "uncompressedImageBytes": info["Size"],
                "rootfsDiffIds": info["RootFS"]["Layers"], "appFileBytes": sum(item["bytes"] for item in files),
                "manifest": manifest, "probes": {}}
            logged(["docker", "run", "--rm", image, "--version"], directory / "version.log")
            reported_version = (directory / "version.log").read_text().strip()
            if reported_version not in (version, version + "+" + revision):
                raise ValueError("Actual Client product version/source differs from the selected comparison.")
            arm_record["reportedVersion"] = reported_version
            for backend in ("native", "python"):
                arm_record["probes"][backend] = probe(image, backend, directory)
        if not all(probe["passed"] for arm in record["arms"].values() for probe in arm["probes"].values()):
            raise ValueError("An actual-image PTY probe failed; both backends' evidence is retained.")
        if any(probe["bundleExtractionBytes"] for probe in record["arms"]["candidate"]["probes"].values()):
            raise ValueError("The unbundled candidate unexpectedly created a single-file extraction cache.")
        compare_layers(record, args, results)
        record["exitCode"] = 0
    except (OSError, ValueError, KeyError, subprocess.SubprocessError) as error:
        record["error"] = str(error)
        print(str(error), flush=True)
    finally:
        (results / "comparison.json").write_text(json.dumps(record, indent=2) + "\n")
        print(json.dumps(record, indent=2), flush=True)
    return record["exitCode"]


if __name__ == "__main__":
    raise SystemExit(main())
