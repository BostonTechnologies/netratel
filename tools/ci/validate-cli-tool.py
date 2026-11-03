#!/usr/bin/env python3
"""Opt-in portable tool acceptance using original, authenticated hosted packages."""

import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import platform
import re
import shutil
import subprocess
import sys
import xml.etree.ElementTree as ET
import zipfile


def module(name):
    spec = importlib.util.spec_from_file_location(name, Path(__file__).with_name(name + ".py"))
    loaded = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(loaded)
    return loaded


fixture = module("cli-tool-fixture")
promotion = module("promote-release")
versions = module("product-version")
prior_releases = module("select-prior-release")
REPOSITORY = promotion.REPOSITORY
WORKFLOW = ".github/workflows/release-build.yml"


def api(path, field=None):
    arguments = ["gh", "api", f"repos/{REPOSITORY}/{path}"]
    if field:
        arguments += ["--paginate", "--jq", "." + field]
    output = subprocess.check_output(arguments, text=True)
    return [item for page in promotion.json_pages(output) for item in page] if field else json.loads(output)


def select_build(runs, revision):
    matching = [run for run in runs if
                (run.get("path"), run.get("head_sha"), run.get("status"), run.get("conclusion")) ==
                (WORKFLOW, revision, "completed", "success") and
                run.get("event") in {"push", "workflow_dispatch"} and
                run.get("head_repository", {}).get("full_name") == REPOSITORY]
    fixture.require(len(matching) == 1,
                    "Expected exactly one successful release-build for the full selected source SHA; "
                    "missing or ambiguous builds cannot select a portable package")
    return matching[0]


def candidate_package(root, version, revision):
    run = select_build(api(f"actions/workflows/release-build.yml/runs?head_sha={revision}&per_page=100",
                           "workflow_runs"), revision)
    attempt = api(f"actions/runs/{run['id']}/attempts/{run['run_attempt']}")
    fixture.require((attempt.get("head_sha"), attempt.get("path"), attempt.get("status"),
                     attempt.get("conclusion"), attempt.get("run_attempt")) ==
                    (revision, WORKFLOW, "completed", "success", run["run_attempt"]),
                    "Selected release-build attempt identity changed")
    name = f"netratel-{version}-linux-x64"
    artifacts = [item for item in api(f"actions/runs/{run['id']}/artifacts?per_page=100", "artifacts")
                 if item.get("name") == name and not item.get("expired")]
    fixture.require(len(artifacts) == 1, "Expected one unexpired Linux tooling artifact from the exact source build")
    artifact = artifacts[0]
    fixture.require((artifact.get("workflow_run", {}).get("id"),
                     artifact.get("workflow_run", {}).get("head_sha")) == (run["id"], revision),
                    "Artifact metadata does not identify the selected source build")
    fixture.require(re.fullmatch(r"sha256:[a-f0-9]{64}", artifact.get("digest", "")),
                    "Authoritative artifact metadata lacks its SHA-256 download digest")
    archive = root / "candidate-artifact.zip"
    promotion.download_artifact(artifact["id"], archive)
    fixture.require("sha256:" + promotion.sha256(archive) == artifact["digest"],
                    "Authenticated artifact download digest differs")
    extracted = root / "candidate-artifact"
    promotion.extract_artifact(archive, extracted)
    expected_name = f"NetRatel.Cli.{version}.nupkg"
    packages = list(extracted.rglob(expected_name))
    fixture.require(len(packages) == 1, "Authenticated artifact has a missing or ambiguous CLI package")
    package = packages[0]
    checksum = package.parent / "SHA256SUMS"
    hashes = [line.split(maxsplit=1)[0] for line in checksum.read_text().splitlines()
              if len(line.split(maxsplit=1)) == 2 and line.split(maxsplit=1)[1].lstrip("*") == expected_name]
    fixture.require(hashes == [promotion.sha256(package)], "Candidate package differs from its hosted SHA256SUMS")
    feed = root / "candidate-feed"
    feed.mkdir()
    shutil.copyfile(package, feed / expected_name)
    return feed / expected_name, {
        "repository": REPOSITORY, "workflow": WORKFLOW, "runId": run["id"],
        "attempt": run["run_attempt"], "sourceSha": revision, "productVersion": version,
        "artifactId": artifact["id"], "artifactDigest": artifact["digest"],
        "package": expected_name, "packageSha256": promotion.sha256(package)}


def prior_package(root, current_version):
    selected = prior_releases.select(current_version)
    release = api("releases/tags/" + selected["tag"])
    feed = root / "prior-feed"
    feed.mkdir()
    publication_assets = [item for item in release["assets"] if item["name"] == "publication.json"]
    fixture.require(not release.get("draft") and len(publication_assets) == 1,
                    "Prior completed publication metadata is missing or ambiguous")
    subprocess.run(["gh", "release", "download", selected["tag"], "--repo", REPOSITORY,
                    "--pattern", "publication.json", "--dir", str(feed)], check=True)
    publication_path = feed / "publication.json"
    fixture.require("sha256:" + promotion.sha256(publication_path) == publication_assets[0].get("digest"),
                    "Prior publication record differs from its public asset digest")
    record = json.loads(publication_path.read_text())
    version = record["productVersion"]
    fixture.require(record.get("verification", {}).get("state") == "complete" and
                    selected["tag"] == "v" + version and version != current_version and
                    re.fullmatch(r"[a-f0-9]{40}", record.get("publicCommit", "")),
                    "Prior package is not an independently completed different-version publication")
    name = f"NetRatel.Cli.{version}.nupkg"
    subprocess.run(["gh", "release", "download", selected["tag"], "--repo", REPOSITORY,
                    "--pattern", name, "--dir", str(feed)], check=True)
    package = feed / name
    digest = promotion.sha256(package)
    fixture.require(record.get("artifacts", {}).get(name) == digest,
                    "Prior tool package differs from its completed publication record")
    return package, {"tag": selected["tag"], "productVersion": version,
                     "sourceSha": record["publicCommit"], "packageSha256": digest}


def package_inventory(package, version):
    with zipfile.ZipFile(package) as archive:
        names = archive.namelist()
        fixture.require(len(names) == len(set(names)), "Tool package has duplicate paths")
        for name in names:
            promotion.safe_artifact_path(name.rstrip("/"))
        nuspecs = [name for name in names if name.endswith(".nuspec")]
        fixture.require(len(nuspecs) == 1, "Tool package metadata is ambiguous")
        metadata = ET.fromstring(archive.read(nuspecs[0]))
        fields = {item.tag.rsplit("}", 1)[-1]: item.text for item in metadata.iter()}
        fixture.require(fields.get("id") == "NetRatel.Cli" and fields.get("version") == version,
                        "Tool package ID/version metadata changed")
        fixture.require(any(item.tag.rsplit("}", 1)[-1] == "packageType" and item.get("name") == "DotnetTool"
                            for item in metadata.iter()), "Package no longer declares the DotnetTool type")
        base = "tools/net10.0/any/"
        settings = ET.fromstring(archive.read(base + "DotnetToolSettings.xml"))
        commands = settings.findall(".//Command")
        fixture.require(len(commands) == 1 and commands[0].get("Name") == "netratel" and
                        commands[0].get("EntryPoint") == "netratel.dll" and commands[0].get("Runner") == "dotnet",
                        "Portable tool command/entry point changed")
        legal = {}
        for notice in ("LICENSE", "NOTICE", "THIRD-PARTY-NOTICES.txt"):
            fixture.require(bool(archive.read(notice)), "Tool package lacks " + notice)
            legal[notice] = hashlib.sha256(archive.read(notice)).hexdigest()
        files = {name: hashlib.sha256(archive.read(name)).hexdigest()
                 for name in names if name.startswith("tools/") and not name.endswith("/")}
        deps = json.loads(archive.read(base + "netratel.deps.json"))
        runtime = json.loads(archive.read(base + "netratel.runtimeconfig.json"))
        fixture.require(deps["runtimeTarget"]["name"] == ".NETCoreApp,Version=v10.0" and
                        runtime["runtimeOptions"]["framework"]["name"] == "Microsoft.NETCore.App",
                        "Tool package no longer has the portable framework-dependent runtime contract")
        return {"packageId": fields["id"], "productVersion": version, "command": commands[0].attrib,
                "legalFiles": legal, "files": files, "compressedBytes": package.stat().st_size,
                "expandedBytes": sum(item.file_size for item in archive.infolist()),
                "toolPayloadBytes": sum(item.file_size for item in archive.infolist() if item.filename in files),
                "runtimeConfig": runtime,
                "fileCount": len(names), "packages": sorted(name for name, item in deps["libraries"].items()
                                                            if item["type"] == "package")}


def isolated_environment(root):
    root.mkdir()
    return dict(os.environ, DOTNET_CLI_HOME=str(root / "cli-home"), DOTNET_NOLOGO="1",
                DOTNET_CLI_TELEMETRY_OPTOUT="1", DOTNET_SKIP_FIRST_TIME_EXPERIENCE="1",
                DOTNET_ADD_GLOBAL_TOOLS_TO_PATH="0", NUGET_PACKAGES=str(root / "packages"),
                NUGET_HTTP_CACHE_PATH=str(root / "http-cache"), NUGET_PLUGINS_CACHE_PATH=str(root / "plugins-cache"))


def feed_config(root, feed, name):
    path = root / (name + ".NuGet.Config")
    document = ET.Element("configuration")
    sources = ET.SubElement(document, "packageSources")
    ET.SubElement(sources, "clear")
    ET.SubElement(sources, "add", {"key": "verified-local-package", "value": str(feed.resolve())})
    ET.SubElement(ET.SubElement(document, "fallbackPackageFolders"), "clear")
    ET.ElementTree(document).write(path, encoding="utf-8", xml_declaration=True)
    return str(path)


def installed_inventory(search_root, version, inventory):
    assemblies = [path for path in search_root.rglob("netratel.dll")
                  if path.parent.as_posix().endswith("/tools/net10.0/any") and
                  any(part.casefold() == version.casefold() for part in path.parts)]
    fixture.require(len(assemblies) == 1, "Installed entry assembly is missing or ambiguous")
    package_root = assemblies[0].parents[3]
    for relative, digest in inventory["files"].items():
        path = package_root / relative
        fixture.require(path.is_file() and promotion.sha256(path) == digest,
                        "Installed file differs from the authenticated package: " + relative)
    return {"matchedPackageFiles": len(inventory["files"]),
            "entryAssemblySha256": promotion.sha256(assemblies[0]),
            "installedToolPayloadBytes": inventory["toolPayloadBytes"],
            "installedBytes": sum(path.stat().st_size for path in package_root.rglob("*") if path.is_file())}


def exercise(root, evidence, package, identity, prior, prior_identity, inventory, prior_inventory):
    stages = []
    receipt = {"passed": False, "stages": stages, "installations": {}}
    version = identity["productVersion"]
    shim_name = "netratel.exe" if os.name == "nt" else "netratel"

    def global_case(name):
        case = root / name
        environment = isolated_environment(case)
        shim = Path(environment["DOTNET_CLI_HOME"]) / ".dotnet" / "tools" / shim_name
        return case, environment, shim

    def install(operation, environment, directory, feed, selected_version, local=False):
        config = feed_config(directory, feed, operation + "-" + selected_version)
        arguments = ["dotnet", "tool", operation, "NetRatel.Cli", "--local" if local else "--global",
                     "--version", selected_version, "--configfile", config,
                     "--disable-parallel", "--no-http-cache"]
        fixture.command(arguments, environment, directory, stages)

    def check(name, entrypoint, environment, directory, search_root):
        result = installed_inventory(search_root, version, inventory)
        result["probeStages"] = fixture.probe(entrypoint, environment, directory, version,
                                              identity["sourceSha"], evidence / (name + ".json"))
        receipt["installations"][name] = result

    try:
        case, environment, shim = global_case("global-install")
        install("install", environment, case, package.parent, version)
        check("global-install", [str(shim)], environment, case, shim.parent)

        case = root / "local-manifest"
        environment = isolated_environment(case)
        manifest = case / ".config" / "dotnet-tools.json"
        manifest.parent.mkdir()
        manifest.write_text(json.dumps({"version": 1, "isRoot": True, "tools": {}}), encoding="utf-8")
        install("install", environment, case, package.parent, version, local=True)
        saved = json.loads(manifest.read_text())
        fixture.require(saved["isRoot"] is True and saved["tools"]["netratel.cli"]["version"] == version and
                        saved["tools"]["netratel.cli"]["commands"] == ["netratel"],
                        "Local tool manifest does not pin the produced package/command")
        fixture.command(["dotnet", "tool", "restore", "--tool-manifest", str(manifest),
                         "--configfile", feed_config(case, package.parent, "restore"),
                         "--disable-parallel", "--no-http-cache"], environment, case, stages)
        check("local-manifest", ["dotnet", "tool", "run", "netratel", "--"], environment, case,
              Path(environment["NUGET_PACKAGES"]))

        case, environment, shim = global_case("previous-version-update")
        preserved = case / "preserved-profile.json"
        preserved.write_text(json.dumps({"apiBaseUrl": "https://netratel.example.invalid",
                                        "integrationCredential": "synthetic-preserved-profile"}), encoding="utf-8")
        preserved.chmod(0o600)
        original = preserved.read_bytes()
        install("install", environment, case, prior.parent, prior_identity["productVersion"])
        receipt["priorInstalled"] = installed_inventory(shim.parent, prior_identity["productVersion"], prior_inventory)
        actual = fixture.command([str(shim), "--version"], environment, case, stages)
        fixture.require(actual == prior_identity["productVersion"] + "+" + prior_identity["sourceSha"],
                        "Previous installed tool identity differs from its publication")
        fixture.command([str(shim), "--config", str(preserved), "config", "validate"], environment, case, stages)
        install("update", environment, case, package.parent, version)
        fixture.require(preserved.read_bytes() == original, "Tool update changed the existing explicit profile")
        fixture.command([str(shim), "--config", str(preserved), "config", "validate"], environment, case, stages)
        check("previous-version-update", [str(shim)], environment, case, shim.parent)
        fixture.command(["dotnet", "tool", "uninstall", "NetRatel.Cli", "--global"], environment, case, stages)
        fixture.require(not shim.exists(), "Tool uninstall left its command shim active")
        install("install", environment, case, package.parent, version)
        fixture.require(preserved.read_bytes() == original, "Tool reinstall changed the existing explicit profile")
        fixture.command([str(shim), "--config", str(preserved), "config", "validate"], environment, case, stages)
        check("reinstall", [str(shim)], environment, case, shim.parent)
        receipt["passed"] = True
    finally:
        (evidence / "installation.json").write_text(json.dumps(receipt, indent=2) + "\n", encoding="utf-8")
    return receipt


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--work-root", type=Path, required=True)
    parser.add_argument("--evidence", type=Path, required=True)
    args = parser.parse_args()
    source = args.source.resolve()
    evidence = args.evidence.resolve()
    evidence.mkdir(parents=True, exist_ok=True)
    receipt = {"passed": False, "os": platform.system(), "machine": platform.machine(),
               "scope": "portable nupkg distribution acceptance; synthetic API; live backend and interactive TTY pending"}
    try:
        receipt["toolingSha"] = subprocess.check_output(
            ["git", "rev-parse", "HEAD"], cwd=Path(__file__).resolve().parents[2], text=True).strip()
        revision = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=source, text=True).strip()
        fixture.require(re.fullmatch(r"[a-f0-9]{40}", revision), "Selected source is not a full Git commit identity")
        version, _ = versions.product_version(source)
        sdk = subprocess.check_output(["dotnet", "--version"], cwd=source, text=True).strip()
        fixture.require(sdk == json.loads((source / "global.json").read_text())["sdk"]["version"],
                        "Installed SDK differs from the selected source SDK pin")
        receipt.update(sourceSha=revision, productVersion=version, dotnetSdkVersion=sdk)
        root = args.work_root.resolve()
        root.mkdir(parents=True, exist_ok=False)
        shutil.copyfile(source / "global.json", root / "global.json")
        candidate, candidate_identity = candidate_package(root, version, revision)
        (evidence / "candidate-input.json").write_text(json.dumps(candidate_identity, indent=2) + "\n", encoding="utf-8")
        prior, prior_identity = prior_package(root, version)
        (evidence / "prior-input.json").write_text(json.dumps(prior_identity, indent=2) + "\n", encoding="utf-8")
        inventory = package_inventory(candidate, version)
        old_inventory = package_inventory(prior, prior_identity["productVersion"])
        for name, value in (("candidate-package", inventory), ("prior-package", old_inventory)):
            (evidence / (name + ".json")).write_text(json.dumps(value, indent=2) + "\n", encoding="utf-8")
        receipt["acceptance"] = exercise(root, evidence, candidate, candidate_identity, prior, prior_identity,
                                          inventory, old_inventory)["installations"]
        receipt["passed"] = True
    finally:
        (evidence / "result.json").write_text(json.dumps(receipt, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(receipt, indent=2))


if __name__ == "__main__":
    try:
        main()
    except (ValueError, KeyError, OSError, subprocess.SubprocessError) as error:
        print("Portable CLI tool acceptance failed: " + str(error), file=sys.stderr)
        raise SystemExit(1)
