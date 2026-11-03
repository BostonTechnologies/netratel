#!/usr/bin/env python3
"""Compare normalized layer exports for two images and four fixed companions."""
import argparse
import gzip
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import tempfile


def compare(root, candidate_name, control_name, companion_names):
    root = root.resolve(strict=True)
    spec = importlib.util.spec_from_file_location('scanner', Path(__file__).with_name('scan-saved-image.py'))
    scanner = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(scanner)
    layers, images, verified = {}, {}, {}
    for image in json.loads((root / 'manifest.json').read_text()):
        config = json.loads(scanner.contained(root, image['Config']).read_text())
        identities = config['rootfs']['diff_ids']
        if len(identities) != len(image['Layers']):
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
        for name in image['RepoTags'] or []:
            images[name] = identities
    if len(set(companion_names)) != 4 or set(companion_names) & {candidate_name, control_name}:
        raise ValueError('Exactly four distinct fixed companion images are required')
    candidate, control = images[candidate_name], images[control_name]
    other = set().union(*(set(images[name]) for name in companion_names))

    def total(identities):
        return sum(layers[identity]['gzipBytes'] for identity in set(identities))

    return {
        'format': 'Docker-save layers normalized to gzip level 6, mtime 0, no filename; excludes image config/manifests; not registry pull bytes',
        'controlStandaloneLayerGzipBytes': total(control),
        'candidateStandaloneLayerGzipBytes': total(candidate),
        'controlFiveImageUniqueLayerGzipBytes': total(other | set(control)),
        'candidateFiveImageUniqueLayerGzipBytes': total(other | set(candidate)),
        'layers': layers,
        'images': images,
    }


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
