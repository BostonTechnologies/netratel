#!/usr/bin/env python3
"""Explicit owner-operated promotion. Never invoked by PR or rehearsal workflows."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import tarfile
import tempfile

ROOT = Path(__file__).resolve().parents[2]
REPOSITORY = "BostonTechnologies/netratel"
COMPONENTS = ("api", "web", "migrations", "mcp-http", "client")
IMAGE_VARIABLES = {name: "NETRATEL_" + name.upper().replace("-", "_") + "_IMAGE" for name in COMPONENTS}


def run(*command, env=None):
    return subprocess.run(command, cwd=ROOT, env=env, check=True, text=True, capture_output=True).stdout.strip()


def sha256(path):
    digest = hashlib.sha256()
    with path.open("rb") as source:
        for block in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def required_artifacts(version):
    return [
        f"netratel-cli-{version}-linux-x64.tar.gz", f"NetRatel.Cli.{version}.nupkg",
        f"netratel-mcp-stdio-{version}-linux-x64.tar.gz", f"netratel-compose-{version}.tar.gz",
        f"netratel-{version}.spdx.json",
        *[f"netratel-client-{version}-{runtime}.{extension}" for runtime, extension in
          (("linux-x64", "tar.gz"), ("win-x64", "zip"), ("osx-arm64", "tar.gz"))],
        *[f"netratel-client-{version}-{runtime}.spdx.json" for runtime in ("linux-x64", "win-x64", "osx-arm64")]
    ]


def stage(inputs, output, version):
    if output.exists():
        raise ValueError("Staging output must be a new directory")
    found = {}
    # Verify the original per-job checksums before flattening their downloadable assets.
    for sums in inputs.rglob("SHA256SUMS"):
        for line in sums.read_text().splitlines():
            expected, name = line.split(maxsplit=1)
            name = name.lstrip("*")
            if Path(name).name != name or not re.fullmatch("[a-f0-9]{64}", expected):
                raise ValueError("Input checksums must contain flat safe basenames")
            path = sums.parent / name
            if not path.is_file() or sha256(path) != expected:
                raise ValueError(f"Missing/corrupt input artifact: {name}")
            if name in found and sha256(found[name]) != expected:
                raise ValueError(f"Conflicting input artifact: {name}")
            found[name] = path
    expected_names = required_artifacts(version)
    missing = set(expected_names) - found.keys()
    if missing:
        raise ValueError(f"Missing required artifacts: {', '.join(sorted(missing))}")
    output.mkdir(parents=True)
    for name in expected_names:
        shutil.copy2(found[name], output / name)
    checksums(output)


def checksums(directory):
    files = sorted(path for path in directory.iterdir() if path.is_file() and path.name != "SHA256SUMS")
    (directory / "SHA256SUMS").write_text("".join(f"{sha256(path)}  {path.name}\n" for path in files))


def verify_staged(directory, version, allow_candidate=False):
    listed = set()
    for line in (directory / "SHA256SUMS").read_text().splitlines():
        expected, name = line.split(maxsplit=1)
        name = name.lstrip("*")
        if Path(name).name != name or not re.fullmatch("[a-f0-9]{64}", expected) or name in listed:
            raise ValueError("Malformed staged checksum entry")
        path = directory / name
        if not path.is_file() or sha256(path) != expected:
            raise ValueError(f"Staged artifact changed or missing: {name}")
        listed.add(name)
    if not set(required_artifacts(version)).issubset(listed):
        raise ValueError("Staged checksums omit required artifacts")
    allowed = set(required_artifacts(version)) | {"SHA256SUMS"}
    if allow_candidate:
        allowed.add("publication.candidate.json")
    unexpected = {path.name for path in directory.iterdir() if path.is_file()} - allowed
    if unexpected:
        raise ValueError(f"Staged output contains unexpected files: {', '.join(sorted(unexpected))}")
    return {name: sha256(directory / name) for name in required_artifacts(version)}


def validate_input_receipt_identity(receipt, version, revision):
    expected = required_artifacts(version)
    if (receipt.get("repository"), receipt.get("workflow"), receipt.get("productVersion"), receipt.get("headSha")) != (
            REPOSITORY, ".github/workflows/release-build.yml", version, revision):
        raise ValueError("Input receipt does not identify the approved repository, workflow, version, and commit")
    if not isinstance(receipt.get("runId"), int) or not isinstance(receipt.get("attempt"), int):
        raise ValueError("Input receipt must identify a workflow run and attempt")
    files = receipt.get("files")
    if not isinstance(files, dict) or set(files) != set(expected):
        raise ValueError("Input receipt must bind every required artifact exactly once")
    for name, item in files.items():
        if not isinstance(item, dict) or not isinstance(item.get("artifact"), str) or \
                not re.fullmatch("[a-f0-9]{64}", item.get("sha256", "")):
            raise ValueError(f"Input receipt has an invalid identity for {name}")
    return files


def verified_input_receipt(inputs, receipt_path, version, revision):
    """Bind local inputs to exact files downloaded from a successful trusted run."""
    receipt = json.loads(receipt_path.read_text())
    expected = required_artifacts(version)
    files = validate_input_receipt_identity(receipt, version, revision)

    # This is authoritative GitHub metadata, rather than a caller-provided
    # checksum claim. It must succeed before staging, journaling, image builds,
    # or any registry write.
    run_details = json.loads(run(
        "gh", "api", f"repos/{REPOSITORY}/actions/runs/{receipt['runId']}/attempts/{receipt['attempt']}"))
    if (run_details.get("conclusion"), run_details.get("head_sha"), run_details.get("path")) != (
            "success", revision, ".github/workflows/release-build.yml"):
        raise ValueError("Trusted workflow run was not successful for the approved source")

    found = {}
    for sums in inputs.rglob("SHA256SUMS"):
        for line in sums.read_text().splitlines():
            expected_digest, name = line.split(maxsplit=1)
            name = name.lstrip("*")
            path = sums.parent / name
            if name in files and path.is_file() and sha256(path) == expected_digest:
                found[name] = path
    if set(found) != set(expected):
        raise ValueError("Input receipt does not match the supplied flat artifact files")

    downloaded = {}
    with tempfile.TemporaryDirectory(prefix="netratel-release-receipt-") as temporary:
        root = Path(temporary)
        for name in expected:
            item = files[name]
            if sha256(found[name]) != item["sha256"]:
                raise ValueError(f"Supplied input digest differs from its receipt for {name}")
            downloaded.setdefault(item["artifact"], root / item["artifact"])
        for artifact, destination in downloaded.items():
            run("gh", "run", "download", str(receipt["runId"]), "--repo", REPOSITORY,
                "--name", artifact, "--dir", str(destination))
        for name in expected:
            candidates = [path for path in root.rglob(name) if path.is_file()]
            if len(candidates) != 1 or sha256(candidates[0]) != sha256(found[name]):
                raise ValueError(f"Authenticated workflow download differs from supplied input: {name}")

    return {
        "repository": REPOSITORY,
        "workflow": ".github/workflows/release-build.yml",
        "runId": receipt["runId"],
        "attempt": receipt["attempt"],
        "headSha": revision,
        "productVersion": version,
        "files": {name: {"artifact": files[name]["artifact"], "sha256": sha256(found[name])} for name in expected}
    }


def resume_state(path, version, revision, prefix, input_receipt):
    state = json.loads(path.read_text()) if path.exists() else {
        "version": version, "revision": revision, "packagePrefix": prefix,
        "inputReceipt": input_receipt, "images": {}}
    if (state.get("version"), state.get("revision"), state.get("packagePrefix"), state.get("inputReceipt")) != \
            (version, revision, prefix, input_receipt):
        raise ValueError("Resume journal belongs to a different approved source or package prefix")
    if not isinstance(state.get("images"), dict) or not set(state["images"]).issubset(COMPONENTS):
        raise ValueError("Resume journal contains unknown components")
    for name, reference in state["images"].items():
        expected = f"ghcr.io/bostontechnologies/{prefix}-{name}@sha256:"
        if not reference.startswith(expected) or not re.fullmatch("[a-f0-9]{64}", reference.removeprefix(expected)):
            raise ValueError("Resume journal contains an invalid digest")
    return state


def validate_digests(images):
    if set(images) != set(COMPONENTS):
        raise ValueError("All five immutable image outputs are required")
    if any(not re.fullmatch(r"ghcr\.io/[a-z0-9_./-]+@sha256:[a-f0-9]{64}", value) for value in images.values()):
        raise ValueError("Invalid/unresolved immutable image digest")


def finalize_bundle(directory, version, revision, images, input_receipt):
    validate_digests(images)
    source_archive = directory / f"netratel-compose-{version}.tar.gz"
    source_archive_digest = sha256(source_archive)
    with tempfile.TemporaryDirectory(prefix="netratel-promoted-bundle-") as temporary:
        bundle = Path(temporary)
        archive = source_archive
        with tarfile.open(archive) as source:
            source.extractall(bundle, filter="data")
        for required in ("INSTALL.md", "compose.images.yaml", "compose.mcp-http.yaml", ".env.images.example", "LICENSE", "NOTICE"):
            if not (bundle / required).is_file():
                raise ValueError(f"Bundle lacks {required}")
        lines = (bundle / ".env.images.example").read_text().splitlines()
        for component, variable in IMAGE_VARIABLES.items():
            lines = [line for line in lines if not line.startswith(variable + "=")]
            lines.append(f"{variable}={images[component]}")
        text = "\n".join(lines) + "\n"
        if "REPLACE_AFTER_APPROVED_PUBLIC_RELEASE" in text:
            raise ValueError("Bundle retains unresolved image placeholders")
        (bundle / ".env.images.example").write_text(text)
        manifest = json.loads((bundle / "release-manifest.json").read_text())
        if manifest["version"] != version:
            raise ValueError("Bundle product version differs from approved promotion")
        manifest.update({"publicCommit": revision, "images": images})
        (bundle / "release-manifest.json").write_text(json.dumps(manifest, indent=2) + "\n")
        with tarfile.open(archive, "w:gz") as target:
            for path in sorted(bundle.iterdir()):
                target.add(path, arcname=path.name)
    record = {"productVersion": version, "publicCommit": revision, "images": images,
              "inputReceipt": input_receipt,
              "derivedBundle": {
                  "sourceSha256": source_archive_digest,
                  "finalSha256": sha256(source_archive),
                  "transformation": "Inserted verified immutable image digests into .env.images.example."
              },
              "verification": {"state": "candidate", "requiredSmokes": ["oidc-compose", "mcp-http-image"]},
              "artifacts": {path.name: sha256(path) for path in sorted(directory.iterdir())
                            if path.is_file() and path.name not in {"SHA256SUMS", "publication.json", "publication.candidate.json"}}}
    (directory / "publication.candidate.json").write_text(json.dumps(record, indent=2) + "\n")
    checksums(directory)


def complete_bundle(directory):
    candidate = directory / "publication.candidate.json"
    final = directory / "publication.json"
    if not candidate.is_file() or final.exists():
        raise ValueError("A single uncompleted candidate publication record is required")
    record = json.loads(candidate.read_text())
    record["verification"] = {"state": "complete", "requiredSmokes": ["oidc-compose", "mcp-http-image"]}
    final.write_text(json.dumps(record, indent=2) + "\n")
    candidate.unlink()
    checksums(directory)


def validate_candidate_bundle(directory, revision, images, input_receipt):
    candidate = directory / "publication.candidate.json"
    if not candidate.exists():
        return False
    record = json.loads(candidate.read_text())
    archive = directory / f"netratel-compose-{record.get('productVersion')}.tar.gz"
    if (record.get("publicCommit"), record.get("images"), record.get("inputReceipt"),
            record.get("verification", {}).get("state")) != (revision, images, input_receipt, "candidate"):
        raise ValueError("Candidate publication does not match the approved resumed promotion")
    if record.get("derivedBundle", {}).get("finalSha256") != sha256(archive):
        raise ValueError("Candidate publication bundle changed after it was prepared")
    return True


def promote(args):
    version = json.loads((ROOT / "release/release-manifest.json").read_text())["version"]
    revision = run("git", "rev-parse", "HEAD")
    if version != "0.1.0-rc.2" or args.approve != f"{version}@{revision}":
        raise ValueError("Explicit --approve VERSION@PUBLIC_SHA for rc.2 is required")
    if run("git", "status", "--porcelain"):
        raise ValueError("Promotion requires a clean checkout")
    run("git", "fetch", "origin", "main")
    run("git", "merge-base", "--is-ancestor", revision, "origin/main")
    if run("git", "rev-list", "-n", "1", "v" + version) != revision:
        raise ValueError("Owner-created release tag must already identify the approved commit")
    if not re.fullmatch(r"[a-z0-9-]+", args.package_prefix):
        raise ValueError("Package prefix must be a simple lowercase name")
    input_receipt = verified_input_receipt(args.inputs, args.receipt, version, revision)
    inventory = json.loads(run("gh", "api", "--paginate", "--slurp",
                              "orgs/BostonTechnologies/packages?package_type=container&per_page=100"))
    inventory = {package["name"]: package for page in inventory for package in page}
    for component in COMPONENTS:
        existing = inventory.get(f"{args.package_prefix}-{component}")
        if existing and existing["visibility"] != "public":
            raise ValueError("Proposed package name collides with a non-public package; choose a new reviewed prefix")
    state = resume_state(args.state, version, revision, args.package_prefix, input_receipt)
    if not args.output.exists():
        stage(args.inputs, args.output, version)
    elif not args.state.exists():
        raise ValueError("Existing output requires its matching resume journal")
    if (args.output / "publication.json").exists():
        raise ValueError("Promotion output already has a verified completion record")
    verify_staged(args.output, version, allow_candidate=True)
    args.state.parent.mkdir(parents=True, exist_ok=True)
    args.state.write_text(json.dumps(state, indent=2) + "\n")
    # Each component is journaled only after an immutable digest is obtained.
    for component in COMPONENTS:
        repository = f"ghcr.io/bostontechnologies/{args.package_prefix}-{component}"
        if component not in state["images"]:
            tag = f"{repository}:{version}-{revision[:12]}"
            existing = inventory.get(f"{args.package_prefix}-{component}")
            if existing:
                versions = json.loads(run("gh", "api", "--paginate", "--slurp",
                    f"orgs/BostonTechnologies/packages/container/{args.package_prefix}-{component}/versions?per_page=100"))
                if any(tag.rsplit(":", 1)[1] in item["metadata"]["container"]["tags"] for page in versions for item in page):
                    raise ValueError("Tag already exists without this journal; inspect and recover its digest explicitly, never overwrite it")
            dockerfile = "docker/client/Dockerfile.public" if component == "client" else f"docker/{component}/Dockerfile"
            run("bash", "tools/ci/build-public-image.sh", "--dockerfile", dockerfile, "--image", tag, "--version", version, "--revision", revision)
            run("bash", "tools/ci/scan-public-image.sh", "--image", tag, "--version", version, "--revision", revision)
            run("docker", "push", tag)
            digest = run("docker", "buildx", "imagetools", "inspect", tag, "--format", "{{json .Manifest.Digest}}").strip('"')
            if not re.fullmatch(r"sha256:[a-f0-9]{64}", digest):
                raise ValueError("Registry did not return a valid digest")
            state["images"][component] = f"{repository}@{digest}"
            args.state.parent.mkdir(parents=True, exist_ok=True)
            args.state.write_text(json.dumps(state, indent=2) + "\n")
        elif not state["images"][component].startswith(repository + "@sha256:"):
            raise ValueError("Resume journal package prefix differs")
    validate_digests(state["images"])
    with tempfile.TemporaryDirectory(prefix="netratel-anonymous-docker-") as temporary:
        anonymous = {**os.environ, "DOCKER_CONFIG": temporary}
        for image in state["images"].values():
            run("docker", "pull", image, env=anonymous)
            run("bash", "tools/ci/scan-public-image.sh", "--image", image, "--version", version, "--revision", revision)
    # Public access is proven before a consumer bundle can claim usable images.
    if not validate_candidate_bundle(args.output, revision, state["images"], input_receipt):
        finalize_bundle(args.output, version, revision, state["images"], input_receipt)
    environment = {**os.environ, **{IMAGE_VARIABLES[name]: value for name, value in state["images"].items()},
                   "NETRATEL_COMPOSE_SMOKE_MODE": "release-images",
                   "NETRATEL_CLIENT_SMOKE_IMAGE": state["images"]["client"],
                   "NETRATEL_MCP_HTTP_SMOKE_IMAGE": state["images"]["mcp-http"],
                   "NETRATEL_CLI_SMOKE_ARCHIVE": str(args.output / f"netratel-cli-{version}-linux-x64.tar.gz"),
                   "NETRATEL_MCP_STDIO_SMOKE_ARCHIVE": str(args.output / f"netratel-mcp-stdio-{version}-linux-x64.tar.gz"),
                   "NETRATEL_COMPOSE_SMOKE_BUNDLE": str(args.output / f"netratel-compose-{version}.tar.gz")}
    run("bash", "tools/ci/smoke-oidc-compose.sh", env=environment)
    run("bash", "tools/ci/smoke-mcp-http-image.sh", env=environment)
    complete_bundle(args.output)
    print(f"Promotion verified for {version}@{revision}. Flat assets: {args.output}")
    print("Owner may now create the prerelease from these exact assets; no stable/latest alias is produced.")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    staging = commands.add_parser("stage", help="Non-publishing flat artifact staging")
    staging.add_argument("--inputs", required=True, type=Path)
    staging.add_argument("--output", required=True, type=Path)
    staging.add_argument("--version", required=True)
    promotion = commands.add_parser("promote", help="PUSH images only after explicit owner approval")
    promotion.add_argument("--approve", required=True)
    promotion.add_argument("--package-prefix", required=True)
    promotion.add_argument("--inputs", required=True, type=Path)
    promotion.add_argument("--receipt", required=True, type=Path,
                           help="Trusted release-workflow receipt; verified against GitHub before writes")
    promotion.add_argument("--output", required=True, type=Path)
    promotion.add_argument("--state", required=True, type=Path)
    args = parser.parse_args()
    for field in ("inputs", "output", "state", "receipt"):
        if hasattr(args, field):
            setattr(args, field, getattr(args, field).resolve())
    try:
        if args.command == "stage":
            stage(args.inputs, args.output, args.version)
        else:
            promote(args)
    except (OSError, ValueError, KeyError, subprocess.CalledProcessError) as error:
        parser.exit(1, f"Release operation stopped: {error}\n")
