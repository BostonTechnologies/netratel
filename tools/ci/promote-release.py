#!/usr/bin/env python3
"""Explicit owner-operated promotion. Never invoked by PR or rehearsal workflows."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import stat
import subprocess
import tarfile
import tempfile
import zipfile

ROOT = Path(__file__).resolve().parents[2]
REPOSITORY = "BostonTechnologies/netratel"
COMPONENTS = ("api", "web", "migrations", "mcp-http", "client")
IMAGE_VARIABLES = {name: "NETRATEL_" + name.upper().replace("-", "_") + "_IMAGE" for name in COMPONENTS}
# Container package repositories are a public compatibility contract. A release
# version is represented only by an immutable tag and digest, never by a new
# package name or a CI/test identity.
PACKAGE_NAMES = {name: f"netratel-{name}" for name in COMPONENTS}
IMAGE_REPOSITORIES = {
    name: f"ghcr.io/bostontechnologies/{package_name}"
    for name, package_name in PACKAGE_NAMES.items()
}


def release_image_tag(component, version):
    """Return the sole release tag permitted for an approved image repository."""
    return f"{IMAGE_REPOSITORIES[component]}:{version}"


def run(*command, env=None):
    try:
        return subprocess.run(command, cwd=ROOT, env=env, check=True, text=True, capture_output=True).stdout.strip()
    except subprocess.CalledProcessError as error:
        detail = (error.stderr or error.stdout or "").strip()[-1600:]
        detail = re.sub(r"(?i)(password|secret|token|authorization)([=:]\s*)\S+", r"\1\2[redacted]", detail)
        raise ValueError(f"Command failed ({' '.join(command[:3])}, exit {error.returncode}): {detail or 'no captured diagnostics'}") from error


def json_pages(value):
    """Parse the consecutive JSON values emitted by ``gh api --paginate``."""
    decoder = json.JSONDecoder()
    pages = []
    remaining = value.lstrip()
    while remaining:
        page, end = decoder.raw_decode(remaining)
        if not isinstance(page, list):
            raise ValueError("Paginated GitHub API response must contain JSON arrays")
        pages.append(page)
        remaining = remaining[end:].lstrip()
    return pages


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
    if not inputs.is_dir() or any(path.is_symlink() for path in inputs.rglob("*")):
        raise ValueError("Input artifacts must not contain symlinks")
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
    atomic_write(directory / "SHA256SUMS", "".join(f"{sha256(path)}  {path.name}\n" for path in files))


def atomic_write(path, text):
    path.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.NamedTemporaryFile("w", dir=path.parent, prefix=f".{path.name}.", delete=False) as target:
        target.write(text)
        temporary = Path(target.name)
    os.replace(temporary, path)


def atomic_json(path, value):
    atomic_write(path, json.dumps(value, indent=2) + "\n")


def verify_staged(directory, version, allow_candidate=False):
    if not directory.is_dir() or any(path.is_symlink() or not path.is_file() for path in directory.iterdir()):
        raise ValueError("Staged output must contain only direct regular files")
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


def verify_pristine_staged(directory, version, input_receipt):
    """Verify every staged byte against its authenticated workflow receipt."""
    hashes = verify_staged(directory, version)
    expected = {name: item["sha256"] for name, item in input_receipt["files"].items()}
    if hashes != expected:
        raise ValueError("Staged artifact differs from the authenticated input receipt")
    return hashes


def safe_artifact_path(value):
    if not isinstance(value, str) or not value or "\\" in value:
        raise ValueError("Input receipt has an unsafe artifact-relative path")
    path = Path(value)
    if path.is_absolute() or any(part in {"", ".", ".."} for part in path.parts):
        raise ValueError("Input receipt has an unsafe artifact-relative path")
    return path


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
    artifact_paths = set()
    for name, item in files.items():
        if not isinstance(item, dict) or not isinstance(item.get("artifact"), str) or \
                not isinstance(item.get("artifactId"), int) or item["artifactId"] <= 0 or \
                not re.fullmatch("sha256:[a-f0-9]{64}", item.get("artifactDigest", "")) or \
                not re.fullmatch("[a-f0-9]{64}", item.get("sha256", "")):
            raise ValueError(f"Input receipt has an invalid identity for {name}")
        relative = safe_artifact_path(item.get("path"))
        identity = (item["artifactId"], relative.as_posix())
        if identity in artifact_paths:
            raise ValueError("Input receipt selects the same artifact path more than once")
        artifact_paths.add(identity)
    return files


def download_artifact(artifact_id, destination):
    """Download an immutable Actions artifact ZIP without name-based selection."""
    with destination.open("wb") as output:
        try:
            subprocess.run(
                ("gh", "api", f"repos/{REPOSITORY}/actions/artifacts/{artifact_id}/zip"),
                cwd=ROOT, stdout=output, stderr=subprocess.PIPE, check=True)
        except subprocess.CalledProcessError as error:
            detail = (error.stderr or b"").decode(errors="replace").strip()[-1600:]
            detail = re.sub(r"(?i)(password|secret|token|authorization)([=:]\s*)\S+", r"\1\2[redacted]", detail)
            raise ValueError(f"Artifact ZIP download failed ({artifact_id}): {detail or 'no captured diagnostics'}") from error


def extract_artifact(zip_path, destination):
    """Safely extract one Actions ZIP under its own authenticated artifact root."""
    with zipfile.ZipFile(zip_path) as archive:
        seen = set()
        members = archive.infolist()
        for member in members:
            relative = safe_artifact_path(member.filename.rstrip("/")) if not member.is_dir() else Path(member.filename.rstrip("/"))
            if not member.is_dir() and relative.as_posix() in seen:
                raise ValueError("Authenticated artifact ZIP has duplicate file paths")
            if not member.is_dir():
                seen.add(relative.as_posix())
            if stat.S_ISLNK(member.external_attr >> 16):
                raise ValueError("Authenticated artifact ZIP contains a symlink")
        for member in members:
            if member.is_dir():
                continue
            relative = safe_artifact_path(member.filename)
            target = destination / relative
            target.parent.mkdir(parents=True, exist_ok=True)
            with archive.open(member) as source, target.open("wb") as output:
                shutil.copyfileobj(source, output)


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

    artifact_metadata = json.loads(run(
        "gh", "api", f"repos/{REPOSITORY}/actions/runs/{receipt['runId']}/artifacts?per_page=100"))
    artifacts = {item.get("id"): item for item in artifact_metadata.get("artifacts", [])}
    for item in files.values():
        metadata = artifacts.get(item["artifactId"])
        if not metadata or metadata.get("expired") or (metadata.get("name"), metadata.get("digest")) != \
                (item["artifact"], item["artifactDigest"]):
            raise ValueError("Input receipt artifact ID, name, or download digest is not from the approved run attempt")

    with tempfile.TemporaryDirectory(prefix="netratel-release-receipt-") as temporary:
        root = Path(temporary)
        for name, item in files.items():
            if sha256(found[name]) != item["sha256"]:
                raise ValueError(f"Supplied input digest differs from its receipt for {name}")
        downloaded = {}
        for artifact_id, item in {item["artifactId"]: item for item in files.values()}.items():
            archive = root / f"{artifact_id}.zip"
            download_artifact(artifact_id, archive)
            if sha256(archive) != item["artifactDigest"].removeprefix("sha256:"):
                raise ValueError("Authenticated artifact download digest differs from its receipt")
            destination = root / str(artifact_id)
            destination.mkdir()
            extract_artifact(archive, destination)
            downloaded[artifact_id] = destination
        for name in expected:
            item = files[name]
            selected = downloaded[item["artifactId"]] / safe_artifact_path(item["path"])
            if selected.is_symlink() or not selected.is_file() or sha256(selected) != sha256(found[name]):
                raise ValueError(f"Authenticated workflow download differs from supplied input: {name}")

    return {
        "repository": REPOSITORY,
        "workflow": ".github/workflows/release-build.yml",
        "runId": receipt["runId"],
        "attempt": receipt["attempt"],
        "headSha": revision,
        "productVersion": version,
        "files": {name: {key: files[name][key] for key in ("artifact", "artifactId", "artifactDigest", "path")} |
                  {"sha256": sha256(found[name])} for name in expected}
    }


def resume_state(path, version, revision, input_receipt):
    state = json.loads(path.read_text()) if path.exists() else {
        "version": version, "revision": revision, "packageNames": dict(PACKAGE_NAMES),
        "inputReceipt": input_receipt, "images": {}}
    if (state.get("version"), state.get("revision"), state.get("packageNames"), state.get("inputReceipt")) != \
            (version, revision, PACKAGE_NAMES, input_receipt):
        raise ValueError("Resume journal belongs to a different approved source or package contract")
    if not isinstance(state.get("images"), dict) or not set(state["images"]).issubset(COMPONENTS):
        raise ValueError("Resume journal contains unknown components")
    for name, reference in state["images"].items():
        expected = IMAGE_REPOSITORIES[name] + "@sha256:"
        if not reference.startswith(expected) or not re.fullmatch("[a-f0-9]{64}", reference.removeprefix(expected)):
            raise ValueError("Resume journal contains an invalid digest")
    return state


def validate_registry_packages(inventory):
    """Fail closed unless every fixed public package is the linked NetRatel package."""
    for component, package_name in PACKAGE_NAMES.items():
        package = inventory.get(package_name)
        if not package:
            raise ValueError(f"Approved registry package is missing: {package_name}")
        if package.get("visibility") != "public":
            raise ValueError(f"Approved registry package is not public: {package_name}")
        repository = package.get("repository")
        if not isinstance(repository, dict) or repository.get("full_name") != REPOSITORY:
            raise ValueError(f"Approved registry package is not linked to {REPOSITORY}: {package_name}")


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
    atomic_json(directory / "publication.candidate.json", record)
    checksums(directory)


def complete_bundle(directory, version, revision, images, input_receipt):
    candidate = directory / "publication.candidate.json"
    final = directory / "publication.json"
    if not candidate.is_file() or final.exists():
        raise ValueError("A single uncompleted candidate publication record is required")
    validate_candidate_bundle(directory, version, revision, images, input_receipt)
    record = json.loads(candidate.read_text())
    record["verification"] = {"state": "complete", "requiredSmokes": ["oidc-compose", "mcp-http-image"]}
    atomic_json(final, record)
    candidate.unlink()
    checksums(directory)


def validate_candidate_bundle(directory, version, revision, images, input_receipt):
    candidate = directory / "publication.candidate.json"
    if not candidate.exists():
        return False
    record = json.loads(candidate.read_text())
    hashes = verify_staged(directory, version, allow_candidate=True)
    archive = directory / f"netratel-compose-{version}.tar.gz"
    if (record.get("productVersion"), record.get("publicCommit"), record.get("images"), record.get("inputReceipt"),
            record.get("verification", {}).get("state")) != (version, revision, images, input_receipt, "candidate"):
        raise ValueError("Candidate publication does not match the approved resumed promotion")
    derived = record.get("derivedBundle", {})
    if derived.get("sourceSha256") != input_receipt["files"][archive.name]["sha256"] or \
            derived.get("finalSha256") != hashes[archive.name]:
        raise ValueError("Candidate publication bundle changed after it was prepared")
    unchanged = {name: digest for name, digest in hashes.items() if name != archive.name}
    receipt_hashes = {name: item["sha256"] for name, item in input_receipt["files"].items() if name != archive.name}
    if unchanged != receipt_hashes:
        raise ValueError("Candidate publication contains an artifact not matching the authenticated input receipt")
    if record.get("artifacts") != hashes:
        raise ValueError("Candidate publication does not bind the complete staged artifact map")
    return True


def promote(args):
    version = json.loads((ROOT / "release/release-manifest.json").read_text())["version"]
    revision = run("git", "rev-parse", "HEAD")
    if version != "0.1.0-rc.6" or args.approve != f"{version}@{revision}":
        raise ValueError("Explicit --approve VERSION@PUBLIC_SHA for rc.6 is required")
    if run("git", "status", "--porcelain"):
        raise ValueError("Promotion requires a clean checkout")
    run("git", "fetch", "origin", "main")
    run("git", "merge-base", "--is-ancestor", revision, "origin/main")
    if run("git", "rev-list", "-n", "1", "v" + version) != revision:
        raise ValueError("Owner-created release tag must already identify the approved commit")
    input_receipt = verified_input_receipt(args.inputs, args.receipt, version, revision)
    inventory = json_pages(run("gh", "api",
                              "orgs/BostonTechnologies/packages?package_type=container&per_page=100", "--paginate"))
    inventory = {package["name"]: package for page in inventory for package in page}
    validate_registry_packages(inventory)
    state = resume_state(args.state, version, revision, input_receipt)
    if not args.output.exists():
        stage(args.inputs, args.output, version)
    elif not args.state.exists():
        raise ValueError("Existing output requires its matching resume journal")
    if (args.output / "publication.json").exists():
        raise ValueError("Promotion output already has a verified completion record")
    candidate_exists = (args.output / "publication.candidate.json").exists()
    if candidate_exists:
        validate_candidate_bundle(args.output, version, revision, state["images"] if state["images"] else {}, input_receipt) if state["images"] else verify_staged(args.output, version, allow_candidate=True)
    else:
        verify_pristine_staged(args.output, version, input_receipt)
    args.state.parent.mkdir(parents=True, exist_ok=True)
    atomic_json(args.state, state)
    # Each component is journaled only after an immutable digest is obtained.
    for component in COMPONENTS:
        repository = IMAGE_REPOSITORIES[component]
        if component not in state["images"]:
            # Recheck before every registry write; a resumed output is not trusted by
            # its local checksum file alone.
            if candidate_exists:
                validate_candidate_bundle(args.output, version, revision, state["images"], input_receipt)
            else:
                verify_pristine_staged(args.output, version, input_receipt)
            tag = release_image_tag(component, version)
            existing = inventory[PACKAGE_NAMES[component]]
            if existing:
                versions = json_pages(run("gh", "api",
                    f"orgs/BostonTechnologies/packages/container/{PACKAGE_NAMES[component]}/versions?per_page=100", "--paginate"))
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
            atomic_json(args.state, state)
        elif not state["images"][component].startswith(repository + "@sha256:"):
            raise ValueError("Resume journal package contract differs")
    validate_digests(state["images"])
    with tempfile.TemporaryDirectory(prefix="netratel-anonymous-docker-") as temporary:
        anonymous = {**os.environ, "DOCKER_CONFIG": temporary}
        for image in state["images"].values():
            run("docker", "pull", image, env=anonymous)
            run("bash", "tools/ci/scan-public-image.sh", "--image", image, "--version", version, "--revision", revision)
    # Public access is proven before a consumer bundle can claim usable images.
    if candidate_exists:
        validate_candidate_bundle(args.output, version, revision, state["images"], input_receipt)
    else:
        verify_pristine_staged(args.output, version, input_receipt)
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
    complete_bundle(args.output, version, revision, state["images"], input_receipt)
    print(f"Promotion verified for {version}@{revision}. Flat assets: {args.output}")
    print("Owner may now create the prerelease from these exact assets; no stable/latest alias is produced.")


def preflight(args):
    """Rehearse authenticated receipt, flat staging, and resume identity without registry writes."""
    version = json.loads((ROOT / "release/release-manifest.json").read_text())["version"]
    revision = run("git", "rev-parse", "HEAD")
    if version != args.version:
        raise ValueError("Preflight version must match the checked-out release manifest")
    receipt = verified_input_receipt(args.inputs, args.receipt, version, revision)
    state = resume_state(args.state, version, revision, receipt)
    if not args.output.exists():
        stage(args.inputs, args.output, version)
    elif not args.state.exists():
        raise ValueError("Existing preflight output requires its matching resume journal")
    verify_pristine_staged(args.output, version, receipt)
    atomic_json(args.state, state)
    print(f"Non-publishing receipt/staging/resume preflight passed for {version}@{revision}.")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    staging = commands.add_parser("stage", help="Non-publishing flat artifact staging")
    staging.add_argument("--inputs", required=True, type=Path)
    staging.add_argument("--output", required=True, type=Path)
    staging.add_argument("--version", required=True)
    preflight_parser = commands.add_parser("preflight", help="Non-publishing authenticated receipt and staging rehearsal")
    preflight_parser.add_argument("--inputs", required=True, type=Path)
    preflight_parser.add_argument("--receipt", required=True, type=Path)
    preflight_parser.add_argument("--output", required=True, type=Path)
    preflight_parser.add_argument("--state", required=True, type=Path)
    preflight_parser.add_argument("--version", required=True)
    promotion = commands.add_parser("promote", help="PUSH images only after explicit owner approval")
    promotion.add_argument("--approve", required=True)
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
        elif args.command == "preflight":
            preflight(args)
        else:
            promote(args)
    except (OSError, ValueError, KeyError, subprocess.CalledProcessError) as error:
        parser.exit(1, f"Release operation stopped: {error}\n")
