#!/usr/bin/env python3
"""Compare normalized layers with fixed companions or two complete image sets."""
import argparse
import gzip
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import tempfile


ROLES = {'api', 'web', 'migrations', 'mcp-http', 'client'}
FORMAT = 'Docker-save layers normalized to gzip level 6, mtime 0, no filename; excludes image config/manifests; not registry pull bytes'


def inventory(root):
    root = root.resolve(strict=True)
    spec = importlib.util.spec_from_file_location('scanner', Path(__file__).with_name('scan-saved-image.py'))
    scanner = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(scanner)
    layers, images, configs, verified = {}, {}, {}, {}
    for image in json.loads((root / 'manifest.json').read_text()):
        content = scanner.contained(root, image['Config']).read_bytes()
        config = json.loads(content)
        identities = config['rootfs']['diff_ids']
        if not identities or len(identities) != len(image['Layers']):
            raise ValueError('Image layer/config inventory differs')
        for identity, reference in zip(identities, image['Layers']):
            path = scanner.contained(root, reference)
            if path in verified:
                if verified[path] != identity:
                    raise ValueError('Saved layer does not match its uncompressed image identity')
                continue
            with path.open('rb') as source:
                compressed = source.read(2) == b'\x1f\x8b'
            with (gzip.open(path, 'rb') if compressed else path.open('rb')) as source, tempfile.TemporaryFile() as exported:
                digest = hashlib.sha256()
                expanded = 0
                with gzip.GzipFile(filename='', mode='wb', fileobj=exported, compresslevel=6, mtime=0) as destination:
                    while chunk := source.read(1024 * 1024):
                        digest.update(chunk)
                        expanded += len(chunk)
                        destination.write(chunk)
                if 'sha256:' + digest.hexdigest() != identity:
                    raise ValueError('Saved layer does not match its uncompressed image identity')
                verified[path] = identity
                layers.setdefault(identity, {'gzipBytes': exported.tell(), 'tarBytes': expanded})
        for name in image.get('RepoTags') or []:
            images[name] = identities
        configs['sha256:' + hashlib.sha256(content).hexdigest()] = identities
    return layers, images, configs


def total(layers, identities):
    return sum(layers[identity]['gzipBytes'] for identity in set(identities))


def compare(root, candidate_name, control_name, companion_names):
    layers, images, _ = inventory(root)
    if len(set(companion_names)) != 4 or set(companion_names) & {candidate_name, control_name}:
        raise ValueError('Exactly four distinct fixed companion images are required')
    candidate, control = images[candidate_name], images[control_name]
    other = set().union(*(set(images[name]) for name in companion_names))

    return {
        'format': FORMAT,
        'controlStandaloneLayerGzipBytes': total(layers, control),
        'candidateStandaloneLayerGzipBytes': total(layers, candidate),
        'controlFiveImageUniqueLayerGzipBytes': total(layers, other | set(control)),
        'candidateFiveImageUniqueLayerGzipBytes': total(layers, other | set(candidate)),
        'layers': layers,
        'images': images,
    }


def compare_stacks(root, candidate, control):
    """Compare complete role maps of inspected image/config IDs, without retagging."""
    for images in (candidate, control):
        if set(images) != ROLES or len(set(images.values())) != 5:
            raise ValueError('Exactly five distinct role-keyed images are required per stack')
    layers, _, configs = inventory(root)
    result = {'format': FORMAT, 'registryTransferBytes': None, 'layers': layers}
    for side, images in (('candidate', candidate), ('control', control)):
        if any(image not in configs for image in images.values()):
            raise ValueError('An inspected image/config identity is missing from the export')
        selected = {role: configs[image] for role, image in images.items()}
        result[side] = {
            'images': images,
            'standaloneLayerGzipBytes': {role: total(layers, ids) for role, ids in selected.items()},
            'fiveImageUniqueLayerGzipBytes': total(layers, set().union(*map(set, selected.values()))),
        }
    return result


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--directory', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--source-sha', required=True)
    parser.add_argument('--candidate', required=True)
    parser.add_argument('--control', required=True)
    parser.add_argument('--companion', action='append', required=True)
    args = parser.parse_args()
    record = {
        'sourceSha': args.source_sha,
        'sdk': subprocess.check_output(['dotnet', '--version'], text=True).strip(),
        'runner': {key: os.environ.get(key) for key in ('RUNNER_OS', 'RUNNER_ARCH', 'ImageOS', 'ImageVersion')},
        **compare(args.directory, args.candidate, args.control, args.companion),
    }
    args.output.write_text(json.dumps(record, indent=2) + '\n')
    print(json.dumps({key: value for key, value in record.items() if key not in ('layers', 'images')}, indent=2))
