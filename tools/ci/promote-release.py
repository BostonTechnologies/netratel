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


def validate_digests(images):
    if set(images) != set(COMPONENTS):
        raise ValueError("All five immutable image outputs are required")
    if any(not re.fullmatch(r"ghcr\.io/[a-z0-9_./-]+@sha256:[a-f0-9]{64}", value) for value in images.values()):
        raise ValueError("Invalid/unresolved immutable image digest")


def finalize_bundle(directory, version, revision, images):
    validate_digests(images)
    with tempfile.TemporaryDirectory(prefix="netratel-promoted-bundle-") as temporary:
        bundle = Path(temporary)
        archive = directory / f"netratel-compose-{version}.tar.gz"
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
              "artifacts": {path.name: sha256(path) for path in sorted(directory.iterdir())
                            if path.is_file() and path.name not in {"SHA256SUMS", "publication.json"}}}
    (directory / "publication.json").write_text(json.dumps(record, indent=2) + "\n")
    checksums(directory)


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
    inventory = json.loads(run("gh", "api", "--paginate", "--slurp",
                              "orgs/BostonTechnologies/packages?package_type=container&per_page=100"))
    inventory = {package["name"]: package for page in inventory for package in page}
    for component in COMPONENTS:
        existing = inventory.get(f"{args.package_prefix}-{component}")
        if existing and existing["visibility"] != "public":
            raise ValueError("Proposed package name collides with a non-public package; choose a new reviewed prefix")
    state = json.loads(args.state.read_text()) if args.state.exists() else {"version": version, "revision": revision, "images": {}}
    if state["version"] != version or state["revision"] != revision:
        raise ValueError("Resume journal belongs to a different approved source")
    if not args.output.exists():
        stage(args.inputs, args.output, version)
    elif not args.state.exists():
        raise ValueError("Existing output requires its matching resume journal")
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
    finalize_bundle(args.output, version, revision, state["images"])
    environment = {**os.environ, **{IMAGE_VARIABLES[name]: value for name, value in state["images"].items()},
                   "NETRATEL_COMPOSE_SMOKE_MODE": "release-images",
                   "NETRATEL_CLIENT_SMOKE_IMAGE": state["images"]["client"],
                   "NETRATEL_MCP_HTTP_SMOKE_IMAGE": state["images"]["mcp-http"],
                   "NETRATEL_CLI_SMOKE_ARCHIVE": str(args.output / f"netratel-cli-{version}-linux-x64.tar.gz"),
                   "NETRATEL_MCP_STDIO_SMOKE_ARCHIVE": str(args.output / f"netratel-mcp-stdio-{version}-linux-x64.tar.gz"),
                   "NETRATEL_COMPOSE_SMOKE_BUNDLE": str(args.output / f"netratel-compose-{version}.tar.gz")}
    run("bash", "tools/ci/smoke-oidc-compose.sh", env=environment)
    run("bash", "tools/ci/smoke-mcp-http-image.sh", env=environment)
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
    promotion.add_argument("--output", required=True, type=Path)
    promotion.add_argument("--state", required=True, type=Path)
    args = parser.parse_args()
    for field in ("inputs", "output", "state"):
        if hasattr(args, field):
            setattr(args, field, getattr(args, field).resolve())
    try:
        if args.command == "stage":
            stage(args.inputs, args.output, args.version)
        else:
            promote(args)
    except (OSError, ValueError, KeyError, subprocess.CalledProcessError) as error:
        parser.exit(1, f"Release operation stopped: {error}\n")
