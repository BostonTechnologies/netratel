#!/usr/bin/env python3
"""Prepare ordinary Actions archives and source images for the existing native upgrade smoke."""
import argparse
from datetime import datetime
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import platform
import re
import subprocess
import tarfile
import tempfile

TOOLS = Path(__file__).resolve().parent


def module(name):
    spec = importlib.util.spec_from_file_location(name, TOOLS / (name + ".py"))
    result = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(result)
    return result


promotion = module("promote-release")


def write_json(path, value):
    path.write_text(json.dumps(value, indent=2) + "\n")


def command(*args, cwd=None):
    return subprocess.check_output(args, cwd=cwd, text=True, encoding="utf-8-sig").strip()


def api(endpoint):
    return json.loads(command("gh", "api", "repos/" + promotion.REPOSITORY + "/" + endpoint))


def validate_run(run, run_id, source):
    if (run.get("id"), run.get("status"), run.get("conclusion"), run.get("head_sha"), run.get("path"),
            run.get("repository", {}).get("full_name"), run.get("head_repository", {}).get("full_name")) != (
            int(run_id), "completed", "success", source, ".github/workflows/release-build.yml",
            promotion.REPOSITORY, promotion.REPOSITORY):
        raise ValueError("Candidate must come from one successful exact-source release-build in this repository")
    if run.get("event") not in ("push", "workflow_dispatch") or type(run.get("run_attempt")) is not int or run["run_attempt"] < 1:
        raise ValueError("Candidate release-build event or attempt is invalid")


def select_artifact(listing, name, run):
    if listing.get("total_count") != len(listing.get("artifacts", [])):
        raise ValueError("Candidate artifact listing is incomplete")
    matches = [item for item in listing["artifacts"] if item.get("name") == name]
    if len(matches) != 1:
        raise ValueError("Candidate artifact upload is absent or ambiguous: " + name)
    item = matches[0]
    if (item.get("expired") is not False or type(item.get("id")) is not int or item["id"] < 1 or
            not re.fullmatch(r"sha256:[a-f0-9]{64}", item.get("digest", "")) or
            type(item.get("size_in_bytes")) is not int or item["size_in_bytes"] <= 0 or
            (item.get("workflow_run", {}).get("id"), item.get("workflow_run", {}).get("head_sha")) !=
            (run["id"], run["head_sha"])):
        raise ValueError("Candidate artifact identity differs from the selected run: " + name)
    created = datetime.fromisoformat(item["created_at"].replace("Z", "+00:00"))
    started = datetime.fromisoformat(run["run_started_at"].replace("Z", "+00:00"))
    ended = datetime.fromisoformat(run["updated_at"].replace("Z", "+00:00"))
    if not started <= created <= ended:
        raise ValueError("Candidate artifact does not belong to the successful run attempt")
    return item


def verify_checksums(directory, expected):
    values = {}
    for line in (directory / "SHA256SUMS").read_text().splitlines():
        match = re.fullmatch(r"([a-f0-9]{64}) [ *]([^/\\]+)", line)
        if not match or match[2] in values or match[2] in (".", ".."):
            raise ValueError("Candidate checksum inventory is malformed or duplicated")
        path = directory / match[2]
        if not path.is_file() or path.is_symlink() or promotion.sha256(path) != match[1]:
            raise ValueError("Candidate checksum differs: " + match[2])
        values[match[2]] = match[1]
    if set(values) != set(expected):
        raise ValueError("Candidate checksum inventory differs from the shipping artifact set")
    return values


def inspect_client(archive, version, source):
    prefix = "netratel-client-linux-x64/"
    with tarfile.open(archive) as bundle:
        files = {}
        for entry in bundle.getmembers():
            if entry.isdir():
                continue
            if not entry.isfile() or not entry.name.startswith(prefix):
                raise ValueError("Candidate Client must contain regular files under its RID directory")
            relative = entry.name[len(prefix):]
            promotion.safe_artifact_path(relative)
            if relative in files:
                raise ValueError("Candidate Client archive has duplicate files")
            with bundle.extractfile(entry) as contents:
                files[relative] = {"bytes": entry.size, "sha256": hashlib.file_digest(contents, "sha256").hexdigest()}
        manifest = json.load(bundle.extractfile(prefix + "netratel-client-manifest.json"))
        if (manifest.get("schema"), manifest.get("product"), manifest.get("version"), manifest.get("runtimeId"),
                manifest.get("commitSha"), manifest.get("executable"), manifest.get("selfContained"), manifest.get("singleFile")) != (
                "netratel.client.manifest.v1", "NetRatel.Client", version, "linux-x64", source, "NetRatel.Client", True, True):
            raise ValueError("Candidate Client manifest differs from the ordinary exact-source Linux archive")
        executable = bundle.extractfile(prefix + "NetRatel.Client")
        header = executable.read(20)
        if header[:6] != b"\x7fELF\x02\x01" or int.from_bytes(header[18:20], "little") != 62:
            raise ValueError("Candidate Client executable must be Linux x86-64")
        executable.seek(0)
        executable_hash = hashlib.file_digest(executable, "sha256").hexdigest()
    return {"manifest": manifest, "executableSha256": executable_hash, "archiveSha256": promotion.sha256(archive), "archiveFiles": files}


def prepare_candidate(source, run_id, work, evidence, version, revision):
    run = api("actions/runs/" + run_id)
    validate_run(run, run_id, revision)
    attempt = api(f"actions/runs/{run_id}/attempts/{run['run_attempt']}")
    validate_run(attempt, run_id, revision)
    if attempt["run_attempt"] != run["run_attempt"]:
        raise ValueError("Candidate release-build attempt changed")
    write_json(evidence / "candidate-release-run.json", attempt)
    listing = api(f"actions/runs/{run_id}/artifacts?per_page=100")
    write_json(evidence / "candidate-artifacts.json", listing)
    directories, authenticated = {}, []
    for kind, name in (("client", f"netratel-client-{version}-linux-x64"), ("package", f"netratel-{version}-linux-x64")):
        artifact = select_artifact(listing, name, attempt)
        archive = work / (str(artifact["id"]) + ".zip")
        promotion.download_artifact(artifact["id"], archive)
        if archive.stat().st_size != artifact["size_in_bytes"] or "sha256:" + promotion.sha256(archive) != artifact["digest"]:
            raise ValueError("Original candidate Actions ZIP size or digest differs")
        directory = work / name
        directory.mkdir()
        promotion.extract_artifact(archive, directory)
        directories[kind] = directory
        authenticated.append({key: artifact[key] for key in ("id", "name", "size_in_bytes", "digest", "created_at")})
    client_archive = f"netratel-client-{version}-linux-x64.tar.gz"
    client_sums = verify_checksums(directories["client"], (client_archive, f"netratel-client-{version}-linux-x64.spdx.json"))
    package_sums = verify_checksums(directories["package"], (f"netratel-cli-{version}-linux-x64.tar.gz", f"NetRatel.Cli.{version}.nupkg",
        f"netratel-mcp-stdio-{version}-linux-x64.tar.gz", f"netratel-compose-{version}.tar.gz", f"netratel-{version}.spdx.json"))
    for name, args in (("client", ("verify-client-release-artifact.sh", "--runtime", "linux-x64", "--extension", "tar.gz", "--integrity-only")),
                       ("package", ("verify-release-artifacts.sh", "--source-revision", revision))):
        with (evidence / (name + "-integrity.log")).open("wb") as log:
            subprocess.run(["bash", str(source / "tools/ci" / args[0]), "--artifacts", str(directories[name]), "--version", version, *args[1:]],
                           cwd=source, stdout=log, stderr=subprocess.STDOUT, check=True)
    archive = directories["client"] / client_archive
    result = inspect_client(archive, version, revision)
    bundle = directories["package"] / f"netratel-compose-{version}.tar.gz"
    with tarfile.open(bundle) as contents:
        manifest = json.load(contents.extractfile("./release-manifest.json"))
    expected_manifest = json.loads((source / "release/release-manifest.json").read_text())
    expected_manifest.update(version=version, prerelease="-" in version)
    if manifest != expected_manifest:
        raise ValueError("Candidate Compose bundle manifest differs from the selected source/version")
    properties = json.loads(command("dotnet", "msbuild", str(source / "src/NetRatel/NetRatel.Client/NetRatel.Client.csproj"),
        "-p:Configuration=Release", "-p:RuntimeIdentifier=linux-x64", "-p:SelfContained=true",
        "-getProperty:PublishTrimmed,PublishAot,PublishSingleFile,SelfContained,RuntimeIdentifier", cwd=source))["Properties"]
    if any(properties.get(key, "").lower() != value for key, value in
           {"PublishTrimmed": "false", "PublishSingleFile": "true", "SelfContained": "true", "RuntimeIdentifier": "linux-x64"}.items()) or properties.get("PublishAot", "").lower() not in ("", "false"):
        raise ValueError("Selected source no longer uses the ordinary untrimmed shipping Client mode")
    result.update(runId=int(run_id), attempt=run["run_attempt"], sourceSha=revision, productVersion=version,
        artifacts=authenticated, checksums={"client": client_sums, "package": package_sums}, evaluatedShippingProperties=properties,
        clientArchive=str(archive), composeBundle=str(bundle), publicationState="unpublished Actions candidate")
    write_json(evidence / "candidate-inputs.json", result)
    return result


def prepare_prior(version, work, evidence):
    selected = module("select-prior-release").select(version)
    write_json(evidence / "selected-prior-release.json", selected)
    metadata = api("releases/tags/" + selected["tag"])
    write_json(evidence / "prior-release-metadata.json", metadata)
    if (metadata.get("draft"), bool(metadata.get("published_at")), metadata.get("tag_name")) != (False, True, selected["tag"]):
        raise ValueError("Selected prior release is not a completed public release")
    assets = [item for item in metadata["assets"] if item.get("state") == "uploaded"]
    by_name = {item["name"]: item for item in assets}
    if len(assets) != len(by_name):
        raise ValueError("Selected prior release has duplicate public assets")
    directory = work / "prior"
    directory.mkdir()
    for name in ("publication.json", "SHA256SUMS"):
        if not re.fullmatch(r"sha256:[a-f0-9]{64}", by_name[name].get("digest", "")):
            raise ValueError("Prior publication metadata must bind the downloaded asset digest")
        module("fetch-published-client-pack").checked_download(selected["tag"], name, by_name[name], directory)
    record = json.loads((directory / "publication.json").read_text())
    prior_version, revision = selected["tag"][1:], record.get("publicCommit")
    if (record.get("productVersion"), record.get("images"), record.get("verification", {}).get("state")) != (prior_version, selected["images"], "complete"):
        raise ValueError("Prior publication differs from the selected completed release")
    promotion.validate_input_receipt_identity(record["inputReceipt"], prior_version, revision)
    promotion.validate_digests(record["images"])
    if api("commits/" + selected["tag"])["sha"] != revision:
        raise ValueError("Prior public tag differs from its recorded build source")
    receipt = record["inputReceipt"]
    prior_run = api(f"actions/runs/{receipt['runId']}/attempts/{receipt['attempt']}")
    validate_run(prior_run, receipt["runId"], revision)
    if (prior_run["run_attempt"], prior_run.get("event"), prior_run.get("head_branch")) != (receipt["attempt"], "push", selected["tag"]):
        raise ValueError("Prior publication does not identify its successful tagged build attempt")
    sums = {}
    for line in (directory / "SHA256SUMS").read_text().splitlines():
        match = re.fullmatch(r"([a-f0-9]{64}) [ *]([^/\\]+)", line)
        if not match or match[2] in sums:
            raise ValueError("Prior publication checksums are malformed or duplicated")
        sums[match[2]] = match[1]
    if set(record["artifacts"]) != set(promotion.required_artifacts(prior_version)) or set(sums) != set(record["artifacts"]) | {"publication.json"}:
        raise ValueError("Prior publication artifact/checksum inventory differs")
    if sums["publication.json"] != promotion.sha256(directory / "publication.json"):
        raise ValueError("Prior publication checksum differs")
    for name, digest in record["artifacts"].items():
        if by_name[name].get("digest") != "sha256:" + digest or sums[name] != digest:
            raise ValueError("Prior published asset identity differs: " + name)
    for role, image in record["images"].items():
        if not image.startswith(promotion.IMAGE_REPOSITORIES[role] + "@sha256:"):
            raise ValueError("Prior published image repository differs")
    write_json(evidence / "prior-publication.json", record)
    write_json(evidence / "prior-release-build.json", prior_run)
    return selected


def build_source_images(source, revision, version, evidence):
    roles = {}
    with tempfile.TemporaryDirectory(prefix="netratel-native-upgrade-source-") as temporary:
        context = Path(temporary)
        archive = subprocess.Popen(["git", "archive", revision], cwd=source, stdout=subprocess.PIPE)
        try:
            subprocess.run(["tar", "-x", "-C", str(context)], stdin=archive.stdout, check=True)
            if archive.wait() != 0:
                raise ValueError("Unable to stage exact source images")
        finally:
            archive.stdout.close()
            if archive.poll() is None:
                archive.kill(); archive.wait()
        for role in ("api", "web", "migrations"):
            reference = f"netratel-{role}:native-upgrade-candidate"
            with (evidence / (role + "-build.log")).open("wb") as log:
                subprocess.run(["bash", "tools/ci/build-public-image.sh", "--dockerfile", f"docker/{role}/Dockerfile.public",
                    "--image", reference, "--version", version, "--revision", revision], cwd=context,
                    stdout=log, stderr=subprocess.STDOUT, check=True)
            image = json.loads(command("docker", "image", "inspect", reference))[0]
            labels = image["Config"]["Labels"]
            if (labels.get("org.opencontainers.image.revision"), labels.get("org.opencontainers.image.version")) != (revision, version):
                raise ValueError("Built source image identity differs: " + role)
            roles[role] = {"reference": reference, "id": image["Id"], "sourceSha": revision, "productVersion": version}
    write_json(evidence / "source-images.json", roles)
    return roles


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--run-id", required=True)
    parser.add_argument("--work", type=Path, required=True)
    parser.add_argument("--evidence", type=Path, required=True)
    args = parser.parse_args()
    if (os.environ.get("GITHUB_ACTIONS"), os.environ.get("RUNNER_ENVIRONMENT"), platform.system(), platform.machine().lower()) not in (
            ("true", "github-hosted", "Linux", "x86_64"), ("true", "github-hosted", "Linux", "amd64")) or Path("/.dockerenv").exists():
        parser.error("Native service acceptance requires a matching non-container GitHub-hosted Linux x64 runner")
    if not re.fullmatch(r"[1-9][0-9]*", args.run_id):
        parser.error("An explicit successful release-build run ID is required")
    source, work, evidence = args.source.resolve(), args.work.resolve(), args.evidence.resolve()
    work.mkdir(parents=True)
    evidence.mkdir(parents=True, exist_ok=True)
    revision = command("git", "rev-parse", "HEAD", cwd=source)
    tooling_revision = command("git", "rev-parse", "HEAD", cwd=TOOLS)
    if not re.fullmatch(r"[a-f0-9]{40}", tooling_revision) or tooling_revision != os.environ.get("GITHUB_WORKFLOW_SHA"):
        raise ValueError("Native acceptance tooling must match the separately pinned workflow source")
    version = command("python3", str(source / "tools/ci/product-version.py"), cwd=source)
    sdk = command("dotnet", "--version", cwd=source)
    if sdk != json.loads((source / "global.json").read_text())["sdk"]["version"]:
        raise ValueError("Hosted SDK differs from the exact candidate source pin")
    candidate = prepare_candidate(source, args.run_id, work, evidence, version, revision)
    prior = prepare_prior(version, work, evidence)
    images = build_source_images(source, revision, version, evidence)
    values = {"NETRATEL_UPGRADE_CURRENT_CLIENT_ARCHIVE": candidate["clientArchive"], "NETRATEL_UPGRADE_COMPOSE_BUNDLE": candidate["composeBundle"],
        "NETRATEL_UPGRADE_PRIOR_VERSION": prior["tag"], "NETRATEL_NATIVE_UPGRADE_EVIDENCE": str(evidence),
        "NETRATEL_NATIVE_UPGRADE_INPUTS": str(evidence / "candidate-inputs.json"),
        "NETRATEL_UPGRADE_SOURCE_ROOT": str(source), "NETRATEL_NATIVE_UPGRADE_TOOLING_SHA": tooling_revision}
    for role in ("api", "web", "migrations"):
        values["NETRATEL_UPGRADE_CURRENT_" + role.upper() + "_IMAGE"] = images[role]["reference"]
    for role in ("api", "web", "migrations", "client"):
        values["NETRATEL_UPGRADE_PRIOR_" + role.upper() + "_IMAGE"] = prior["images"][role]
    with Path(os.environ["GITHUB_ENV"]).open("a") as environment:
        for key, value in values.items():
            if any(character in value for character in "\r\n"):
                raise ValueError("Native acceptance environment value contains a newline")
            environment.write(key + "=" + value + "\n")
    write_json(evidence / "preparation.json", {"sourceSha": revision, "toolingSha": tooling_revision, "productVersion": version, "dotnetSdkVersion": sdk,
        "candidateRunId": int(args.run_id), "candidateAttempt": candidate["attempt"], "priorTag": prior["tag"],
        "shippingMode": "self-contained, single-file, untrimmed JIT", "candidatePublication": "not published", "sourceImages": images})


if __name__ == "__main__":
    main()
