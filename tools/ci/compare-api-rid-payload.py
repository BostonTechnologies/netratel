#!/usr/bin/env python3
"""Constrain the API's platform-asset and unused Identity.Web dependency deltas."""
from collections import Counter
from datetime import datetime
import hashlib
import json
import re


MOVES = {
    "liblmdb.so": "runtimes/linux-x64/native/liblmdb.so",
    "System.Drawing.Common.dll": "runtimes/unix/lib/net6.0/System.Drawing.Common.dll",
}
METADATA = {"NetRatel.API.deps.json", "runtime.spdx.json"}
LINUX_OMISSIONS = {"web.config"}
IDENTITY_ROOTS = {"Microsoft.Identity.Web", "Microsoft.Identity.Web.DownstreamApi"}
IDENTITY_PACKAGES = {
    "Azure.Core", "Azure.Identity", "Azure.Security.KeyVault.Certificates", "Azure.Security.KeyVault.Secrets",
    "Microsoft.Bcl.AsyncInterfaces", "Microsoft.Identity.Abstractions", "Microsoft.Identity.Client",
    "Microsoft.Identity.Client.Extensions.Msal", "Microsoft.Identity.Web", "Microsoft.Identity.Web.Certificate",
    "Microsoft.Identity.Web.Certificateless", "Microsoft.Identity.Web.Diagnostics", "Microsoft.Identity.Web.DownstreamApi",
    "Microsoft.Identity.Web.TokenAcquisition", "Microsoft.Identity.Web.TokenCache",
    "Microsoft.IdentityModel.LoggingExtensions", "Microsoft.IdentityModel.Validators", "System.ClientModel", "System.Memory.Data",
}
IDENTITY_FILES = {name + ".dll" for name in IDENTITY_PACKAGES}
IDENTITY_METADATA = METADATA | {"NetRatel.API.dll", "NetRatel.API.pdb", "THIRD-PARTY-NOTICES.txt"}


def compare_identity_web(control, candidate, control_root, candidate_root):
    removed = control.keys() - candidate.keys()
    changed = {path for path in candidate.keys() & control.keys() if candidate[path] != control[path]}
    if removed != IDENTITY_FILES or candidate.keys() - control.keys():
        raise ValueError("Identity.Web comparison must remove exactly its 19 DLLs and add no files")
    if not changed <= IDENTITY_METADATA:
        raise ValueError("Identity.Web comparison changed an unexpected retained payload file")
    pdbs = {path for path in control if path.endswith(".pdb")}
    if len(pdbs) != 7 or pdbs != {path for path in candidate if path.endswith(".pdb")}:
        raise ValueError("Identity.Web comparison must retain all seven PDB paths")
    if (control_root / "NetRatel.API.runtimeconfig.json").read_bytes() != (candidate_root / "NetRatel.API.runtimeconfig.json").read_bytes():
        raise ValueError("Identity.Web comparison changed the runtime configuration")
    deps = {arm: json.loads((root / "NetRatel.API.deps.json").read_text())
            for arm, root in (("control", control_root), ("candidate", candidate_root))}
    old_deps, new_deps = deps["control"], deps["candidate"]
    removed_libraries = {key for key in old_deps["libraries"] if key.rsplit("/", 1)[0] in IDENTITY_PACKAGES}
    if ({key.rsplit("/", 1)[0] for key in removed_libraries} != IDENTITY_PACKAGES or
            len(removed_libraries) != len(IDENTITY_PACKAGES) or
            any(old_deps["libraries"][key]["type"] != "package" for key in removed_libraries)):
        raise ValueError("Identity.Web control lacks the exact removable package closure")
    if ({key: value for key, value in old_deps.items() if key not in {"libraries", "targets"}} !=
            {key: value for key, value in new_deps.items() if key not in {"libraries", "targets"}} or
            new_deps["libraries"] != {key: value for key, value in old_deps["libraries"].items() if key not in removed_libraries} or
            new_deps["targets"].keys() != old_deps["targets"].keys()):
        raise ValueError("Identity.Web comparison changed a retained runtime dependency identity")
    api_targets = 0
    for target, entries in old_deps["targets"].items():
        expected = {}
        for identity, entry in entries.items():
            if identity in removed_libraries:
                continue
            if identity.startswith("NetRatel.API/"):
                api_targets += 1
                dependencies = entry["dependencies"]
                if any(dependencies.get(name) != "4.16.1" for name in IDENTITY_ROOTS):
                    raise ValueError("Identity.Web control must reconstruct only the original 4.16.1 roots")
                entry = dict(entry, dependencies={key: value for key, value in dependencies.items() if key not in IDENTITY_ROOTS})
            expected[identity] = entry
        if new_deps["targets"][target] != expected:
            raise ValueError("Identity.Web comparison changed an unrelated dependency edge or runtime asset")
    if api_targets != 1:
        raise ValueError("Identity.Web comparison requires the actual single Linux API runtime target")

    documents = {arm: json.loads((root / "runtime.spdx.json").read_text())
                 for arm, root in (("control", control_root), ("candidate", candidate_root))}
    old, new = documents["control"], documents["candidate"]
    varying = {"packages", "files", "relationships", "creationInfo", "documentNamespace"}
    if ({key: value for key, value in old.items() if key not in varying} !=
            {key: value for key, value in new.items() if key not in varying}):
        raise ValueError("Identity.Web comparison changed unrelated SPDX document metadata")
    old_creation, new_creation = dict(old["creationInfo"]), dict(new["creationInfo"])
    for creation in (old_creation, new_creation):
        datetime.strptime(creation.pop("created"), "%Y-%m-%dT%H:%M:%SZ")
    if old_creation != new_creation:
        raise ValueError("Identity.Web comparison changed SPDX creation metadata")
    package_maps = {arm: {item["SPDXID"]: item for item in document["packages"]} for arm, document in documents.items()}
    if any(len(package_maps[arm]) != len(document["packages"]) for arm, document in documents.items()):
        raise ValueError("Duplicate SPDX packages")
    removed_package_ids = package_maps["control"].keys() - package_maps["candidate"].keys()
    if (len(removed_package_ids) != len(IDENTITY_PACKAGES) or
            {package_maps["control"][key]["name"] for key in removed_package_ids} != IDENTITY_PACKAGES or
            {package_maps["control"][key]["name"] + "/" + package_maps["control"][key]["versionInfo"] for key in removed_package_ids} != removed_libraries or
            package_maps["candidate"] != {key: value for key, value in package_maps["control"].items() if key not in removed_package_ids}):
        raise ValueError("Identity.Web comparison changed a retained SPDX package identity")
    prefix = old["documentNamespace"].rsplit("/", 1)[0] + "/"
    inventories = {"control": control, "candidate": candidate}
    file_maps = {}
    for arm, document in documents.items():
        suffix = hashlib.sha256(json.dumps(document["files"], sort_keys=True).encode()).hexdigest()
        if document["documentNamespace"] != prefix + suffix:
            raise ValueError("SPDX namespace does not bind its actual file inventory")
        files = {}
        for item in document["files"]:
            path = item["fileName"].removeprefix("./publish/")
            if (item["fileName"] != "./publish/" + path or path not in inventories[arm] or path in files or
                    item["SPDXID"] != "SPDXRef-File-" + re.sub(r"[^A-Za-z0-9.-]", "-", item["fileName"]).rstrip("-") or
                    item["checksums"] != [{"algorithm": "SHA256", "checksumValue": inventories[arm][path]["sha256"]}]):
                raise ValueError("SPDX file identity does not bind the actual payload")
            files[path] = item
        # The entrypoint and root legal files are copied by the final Docker
        # stage after the publish-stage SPDX is generated; the full inventory
        # still requires their unchanged bytes above.
        if files.keys() != inventories[arm].keys() - {"runtime.spdx.json", "docker-entrypoint.sh", "licenses/LICENSE", "licenses/NOTICE"}:
            raise ValueError("SPDX does not cover every actual application file")
        file_maps[arm] = files
    for path, item in file_maps["candidate"].items():
        excluded = {"checksums"} if path in changed else set()
        if ({key: value for key, value in item.items() if key not in excluded} !=
                {key: value for key, value in file_maps["control"][path].items() if key not in excluded}):
            raise ValueError("Identity.Web comparison changed retained SPDX file metadata")
    removed_ids = removed_package_ids | {file_maps["control"][path]["SPDXID"] for path in removed}
    expected_relations = Counter(json.dumps(item, sort_keys=True) for item in old["relationships"]
                                 if item["spdxElementId"] not in removed_ids and item["relatedSpdxElement"] not in removed_ids)
    if Counter(json.dumps(item, sort_keys=True) for item in new["relationships"]) != expected_relations:
        raise ValueError("Identity.Web comparison changed an unrelated SPDX relationship")

    # Preserve every retained upstream notice verbatim. The generator's sorted
    # package headings delimit the complete license texts, including paragraphs.
    notice = (control_root / "THIRD-PARTY-NOTICES.txt").read_bytes().decode("utf-8")
    target = old_deps["targets"][old_deps["runtimeTarget"]["name"]]
    headings = [(identity, identity.rsplit("/", 1)) for identity in sorted(target)
                if not (old_deps["libraries"][identity]["type"] == "project" and identity.startswith("NetRatel."))]
    positions = []
    for identity, (name, version) in headings:
        matches = list(re.finditer(r"(?m)^" + re.escape(name + " " + version) + r"\n", notice))
        if len(matches) != 1 or notice[matches[0].start() - 2:matches[0].start()] != "\n\n":
            raise ValueError("Upstream notice package boundaries are ambiguous: " + identity)
        positions.append(matches[0].start())
    if not positions or positions != sorted(positions) or not notice.endswith("\n"):
        raise ValueError("Upstream notice package order differs from the actual dependency graph")
    sections = [notice[start:(positions[index + 1] - 2 if index + 1 < len(positions) else len(notice) - 1)]
                for index, start in enumerate(positions)]
    expected_notice = "\n\n".join([notice[:positions[0] - 2]] + [section for (identity, _), section in zip(headings, sections)
                                                                         if identity not in removed_libraries]) + "\n"
    if (candidate_root / "THIRD-PARTY-NOTICES.txt").read_bytes().decode("utf-8") != expected_notice:
        raise ValueError("Identity.Web comparison changed a retained upstream notice or license")
    return {
        "comparison": "identity-web",
        "format": "Actual uncompressed app files; diagnostic reconstruction of unused package roots, not original published or registry bytes",
        "controlAppBytes": sum(item["bytes"] for item in control.values()),
        "candidateAppBytes": sum(item["bytes"] for item in candidate.values()),
        "removedFiles": {path: control[path] for path in sorted(removed)},
        "removedPackages": [package_maps["control"][key] for key in sorted(removed_package_ids)],
        "retainedPdbPaths": sorted(pdbs),
        "byteIdenticalRetainedFiles": len(candidate) - len(changed),
        "changedPaths": sorted(changed),
        "packageCounts": {arm: len(document["packages"]) for arm, document in documents.items()},
        "generatedMetadata": {arm: {key: document[key] for key in ("documentNamespace", "creationInfo")} for arm, document in documents.items()},
        "retainedNoticeSectionsIdentical": len(headings) - len(removed_libraries),
    }


def compare(control, candidate, control_root, candidate_root):
    for destination, source in MOVES.items():
        if destination not in candidate or source not in control or candidate[destination] != control[source]:
            raise ValueError("Linux runtime asset is missing or changed: " + destination)
    if any(path.startswith("runtimes/") for path in candidate):
        raise ValueError("RID-specific publish retained a portable runtime asset tree")
    for path, value in candidate.items():
        if path in METADATA or value == control.get(path):
            continue
        if path not in MOVES or value != control.get(MOVES[path]):
            raise ValueError("Unexpected API payload change: " + path)
    removed = sorted(control.keys() - candidate.keys())
    if any(not path.startswith("runtimes/") and path not in LINUX_OMISSIONS for path in removed):
        raise ValueError("RID selection removed a non-runtime file or symbol")
    for path in MOVES.values():
        if path not in control:
            raise ValueError("Missing known portable runtime input: " + path)
    old_deps = json.loads((control_root / "NetRatel.API.deps.json").read_text())
    new_deps = json.loads((candidate_root / "NetRatel.API.deps.json").read_text())
    expected_target = dict(old_deps["runtimeTarget"])
    expected_target["name"] += "/linux-x64"
    if ("/" in old_deps["runtimeTarget"]["name"] or new_deps["runtimeTarget"] != expected_target or
            old_deps["libraries"] != new_deps["libraries"]):
        raise ValueError("RID selection changed the runtime dependency/package identities")
    old = json.loads((control_root / "runtime.spdx.json").read_text())
    new = json.loads((candidate_root / "runtime.spdx.json").read_text())
    varying = {"files", "relationships", "creationInfo", "documentNamespace"}
    if ({key: value for key, value in old.items() if key not in varying} !=
            {key: value for key, value in new.items() if key not in varying}):
        raise ValueError("RID selection changed SPDX document or package metadata")
    old_creation, new_creation = dict(old["creationInfo"]), dict(new["creationInfo"])
    for creation in (old_creation, new_creation):
        datetime.strptime(creation.pop("created"), "%Y-%m-%dT%H:%M:%SZ")
    if old_creation != new_creation:
        raise ValueError("RID selection changed SPDX creators")
    prefix = old["documentNamespace"].rsplit("/", 1)[0] + "/"
    for document, inventory in ((old, control), (new, candidate)):
        suffix = hashlib.sha256(json.dumps(document["files"], sort_keys=True).encode()).hexdigest()
        if document["documentNamespace"] != prefix + suffix:
            raise ValueError("SPDX namespace does not bind its actual file inventory")
        for item in document["files"]:
            path = item["fileName"].removeprefix("./publish/")
            if item["fileName"] != "./publish/" + path or path not in inventory:
                raise ValueError("Unexpected SPDX distribution path")
            expected_id = "SPDXRef-File-" + re.sub(r"[^A-Za-z0-9.-]", "-", item["fileName"]).rstrip("-")
            if (item["SPDXID"] != expected_id or item["checksums"] != [
                    {"algorithm": "SHA256", "checksumValue": inventory[path]["sha256"]}]):
                raise ValueError("SPDX file identity differs from actual payload")
        contained = Counter(json.dumps(item, sort_keys=True) for item in document["relationships"]
                            if item["relationshipType"] == "CONTAINS")
        expected = Counter(json.dumps({"spdxElementId": document["packages"][0]["SPDXID"],
                                     "relationshipType": "CONTAINS", "relatedSpdxElement": item["SPDXID"]},
                                    sort_keys=True) for item in document["files"])
        if contained != expected:
            raise ValueError("SPDX file relationships differ")
    old_files = {item["fileName"].removeprefix("./publish/"): item for item in old["files"]}
    new_files = {item["fileName"].removeprefix("./publish/"): item for item in new["files"]}
    expected_files = (old_files.keys() - set(removed)) | (candidate.keys() - control.keys() - METADATA)
    if new_files.keys() != expected_files:
        raise ValueError("SPDX file coverage differs from the allowed payload delta")
    for path, item in new_files.items():
        source = MOVES.get(path, path) if candidate.get(path) != control.get(path) and path in MOVES else path
        ignored = {"fileName", "SPDXID", "checksums"}
        if ({key: value for key, value in item.items() if key not in ignored} !=
                {key: value for key, value in old_files[source].items() if key not in ignored}):
            raise ValueError("RID selection changed retained SPDX file metadata")
    def dependency_relations(document):
        return Counter(json.dumps(item, sort_keys=True) for item in document["relationships"]
                       if item["relationshipType"] != "CONTAINS")
    if dependency_relations(old) != dependency_relations(new):
        raise ValueError("RID selection changed dependency relationships")
    return {
        "format": "Actual uncompressed app files; controlled source/metadata/base comparison, not registry bytes",
        "controlAppBytes": sum(item["bytes"] for item in control.values()),
        "candidateAppBytes": sum(item["bytes"] for item in candidate.values()),
        "removedPaths": removed,
        "removedRuntimePaths": [path for path in removed if path.startswith("runtimes/")],
        "runtimeAssetMoves": MOVES,
        "omittedWindowsHostingFiles": sorted(set(removed) & LINUX_OMISSIONS),
        "retainedPdbPaths": sorted(path for path in control if path.endswith(".pdb")),
        "packages": len(new["packages"]),
        "changedPaths": sorted(path for path in candidate if candidate[path] != control.get(path)),
    }
