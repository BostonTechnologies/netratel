#!/usr/bin/env python3
"""Verify the bounded HTML package-ownership delta in actual migrations payloads."""
import argparse
import hashlib
import json
from pathlib import Path


REMOVED_FILES = {'AngleSharp.dll', 'AngleSharp.Css.dll', 'HtmlAgilityPack.dll', 'HtmlSanitizer.dll'}
REMOVED_PACKAGES = {'AngleSharp', 'AngleSharp.Css', 'HtmlAgilityPack', 'HtmlSanitizer'}
CHANGED_FILES = {
    'NetRatel.Infrastructure.dll', 'NetRatel.Infrastructure.pdb',
    'NetRatel.Migrations.dll', 'NetRatel.Migrations.pdb',
    'NetRatel.Migrations.deps.json', 'THIRD-PARTY-NOTICES.txt', 'runtime.spdx.json',
}


def compare(results):
    inventories = {arm: json.loads((results / (arm + '-file-hashes.json')).read_text())
                   for arm in ('candidate', 'control')}
    candidate, control = inventories['candidate'], inventories['control']
    removed = set(control) - set(candidate)
    changed = {name for name in candidate.keys() & control.keys() if candidate[name] != control[name]}
    if removed != REMOVED_FILES or set(candidate) - set(control):
        raise ValueError('HTML comparison must remove exactly the four HTML DLLs and add no files')
    if not changed <= CHANGED_FILES or 'NetRatel.Infrastructure.dll' not in changed:
        raise ValueError('HTML comparison changed an unexpected retained payload file')
    if (results / 'candidate-runtimeconfig.json').read_bytes() != (results / 'control-runtimeconfig.json').read_bytes():
        raise ValueError('HTML ownership changed the runtime configuration')
    if (results / 'candidate-runtime.txt').read_bytes() != (results / 'control-runtime.txt').read_bytes():
        raise ValueError('HTML ownership comparison requires identical runtimes, native packages and UID')

    documents = {arm: json.loads((results / (arm + '-runtime.spdx.json')).read_text())
                 for arm in ('candidate', 'control')}
    package_maps = {arm: {item['SPDXID']: item for item in document['packages']}
                    for arm, document in documents.items()}
    candidate_packages, control_packages = package_maps['candidate'], package_maps['control']
    removed_package_ids = control_packages.keys() - candidate_packages.keys()
    if (len(removed_package_ids) != len(REMOVED_PACKAGES)
            or {control_packages[key]['name'] for key in removed_package_ids} != REMOVED_PACKAGES
            or candidate_packages.keys() - control_packages.keys()
            or any(item != control_packages[key] for key, item in candidate_packages.items())):
        raise ValueError('HTML comparison changed a retained package or removed an unexpected package')
    removed_file_ids = {item['SPDXID'] for item in documents['control']['files']
                        if item['fileName'].removeprefix('./out/') in REMOVED_FILES}
    removed_ids = removed_package_ids | removed_file_ids

    def relationships(document):
        return {tuple(sorted(item.items())) for item in document['relationships']
                if item['spdxElementId'] not in removed_ids and item['relatedSpdxElement'] not in removed_ids}

    if relationships(documents['candidate']) != relationships(documents['control']):
        raise ValueError('HTML comparison changed an unrelated SPDX relationship')
    for document in documents.values():
        namespace_hash = hashlib.sha256(json.dumps(document['files'], sort_keys=True).encode()).hexdigest()
        if document['documentNamespace'].rsplit('/', 1)[-1] != namespace_hash:
            raise ValueError('SPDX namespace does not bind its actual file inventory')
    record = {
        'comparison': 'html-ownership',
        'controlApplicationBytes': sum(item['bytes'] for item in control.values()),
        'candidateApplicationBytes': sum(item['bytes'] for item in candidate.values()),
        'removedFiles': {name: control[name] for name in sorted(removed)},
        'changedFiles': {name: {arm: inventories[arm][name] for arm in inventories} for name in sorted(changed)},
        'byteIdenticalRetainedFiles': len(candidate) - len(changed),
        'removedPackages': [control_packages[key] for key in sorted(removed_package_ids)],
        'packageCounts': {arm: len(document['packages']) for arm, document in documents.items()},
        'spdxFileCounts': {arm: len(document['files']) for arm, document in documents.items()},
        'generatedMetadata': {
            arm: {key: document[key] for key in ('documentNamespace', 'creationInfo')}
            for arm, document in documents.items()
        },
    }
    return record


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--results', required=True, type=Path)
    args = parser.parse_args()
    record = compare(args.results)
    (args.results / 'payload-comparison.json').write_text(json.dumps(record, indent=2) + '\n')
    print(json.dumps({key: value for key, value in record.items() if key not in ('changedFiles', 'removedPackages', 'generatedMetadata')}, indent=2))
