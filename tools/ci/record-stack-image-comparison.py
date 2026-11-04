#!/usr/bin/env python3
"""Optional source OIDC image receipts; builds locally on the hosted runner only."""
import argparse
from datetime import datetime, timezone
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import re
import shutil
import stat
import statistics
import subprocess
import tarfile
import tempfile
import time


ROOT = Path(__file__).resolve().parents[2]
REPOSITORY = 'BostonTechnologies/netratel'
PROJECTS = {'api': 'NetRatel.API.dll', 'web': 'NetRatel.Web.dll',
            'migrations': 'NetRatel.Migrations.dll', 'mcp-http': 'NetRatel.Mcp.Http.dll',
            'client': 'NetRatel.Client'}
DOCKERFILES = {role: f'docker/{role}/Dockerfile' for role in PROJECTS}
DOCKERFILES['client'] = 'docker/client/Dockerfile.public'


def module(name):
    spec = importlib.util.spec_from_file_location(name, ROOT / 'tools/ci' / (name + '.py'))
    result = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(result)
    return result


def command(*args):
    return subprocess.check_output(args, cwd=ROOT, text=True, encoding='utf-8').strip()


def write(path, value):
    path.write_text(json.dumps(value, indent=2) + '\n')


def identity(info, source, version, reference=None):
    labels = info.get('Config', {}).get('Labels') or {}
    expected = {'org.opencontainers.image.title': 'NetRatel',
                'org.opencontainers.image.source': 'https://github.com/' + REPOSITORY,
                'org.opencontainers.image.version': version,
                'org.opencontainers.image.revision': source}
    if (not re.fullmatch('[a-f0-9]{40}', source) or not re.fullmatch('sha256:[a-f0-9]{64}', info.get('Id', ''))
            or any(labels.get(key) != value for key, value in expected.items())):
        raise ValueError('Image source/version/config identity differs')
    if (info.get('Os'), info.get('Architecture')) != ('linux', 'amd64'):
        raise ValueError('Stack comparison requires actual linux/amd64 images')
    if reference and reference not in info.get('RepoDigests', []):
        raise ValueError('Pulled image does not retain its selected publication digest')
    return {key: info[key] for key in ('Id', 'Os', 'Architecture', 'Size', 'RootFS')} | {
        'labels': labels, 'entrypoint': info['Config'].get('Entrypoint'),
        'command': info['Config'].get('Cmd'), 'user': info['Config'].get('User')}


def save_images(images, destination):
    """Stream Docker's outer archive; never extract the opaque filesystem layers."""
    process = subprocess.Popen(['docker', 'image', 'save', *sorted(set(images))], stdout=subprocess.PIPE)
    try:
        with tarfile.open(fileobj=process.stdout, mode='r|') as archive:
            for entry in archive:
                relative = Path(entry.name)
                if relative.is_absolute() or '..' in relative.parts or not relative.parts:
                    raise ValueError('Unsafe Docker-save member')
                target = destination / relative
                if entry.isdir():
                    target.mkdir(parents=True, exist_ok=True)
                elif entry.isfile() and not target.exists():
                    target.parent.mkdir(parents=True, exist_ok=True)
                    with archive.extractfile(entry) as source, target.open('wb') as output:
                        shutil.copyfileobj(source, output)
                else:
                    raise ValueError('Docker-save member must be a unique regular file or directory')
        if process.wait() != 0:
            raise ValueError('Docker image export failed')
    finally:
        process.stdout.close()
        if process.poll() is None:
            process.kill()
            process.wait()


def prepare(results, source, version):
    started = time.monotonic()
    roles = {}
    with tempfile.TemporaryDirectory(prefix='netratel-stack-source-') as temporary:
        context = Path(temporary)
        # The immutable archive excludes changing logs/results and local obj files.
        archive = subprocess.Popen(['git', 'archive', source], cwd=ROOT, stdout=subprocess.PIPE)
        try:
            subprocess.run(['tar', '-x', '-C', str(context)], stdin=archive.stdout, check=True)
            if archive.wait() != 0:
                raise ValueError('Unable to stage the selected source commit')
        finally:
            archive.stdout.close()
            if archive.poll() is None:
                archive.kill(); archive.wait()
        for role, dockerfile in DOCKERFILES.items():
            role_started = time.monotonic()
            reference = f'netratel-{role}:manual-deployment'
            log = results / f'{role}-build.log'
            with log.open('wb') as output:
                subprocess.run(['bash', 'tools/ci/build-public-image.sh', '--dockerfile', dockerfile,
                                '--image', reference, '--version', version, '--revision', source],
                               cwd=context, env={**os.environ, 'BUILDKIT_PROGRESS': 'plain'},
                               stdout=output, stderr=subprocess.STDOUT, check=True)
            info = json.loads(command('docker', 'image', 'inspect', reference))[0]
            bases = sorted(set(re.findall(r'\bFROM (mcr\.microsoft\.com/dotnet/[a-z-]+:[A-Za-z0-9_.-]+@sha256:[a-f0-9]{64})', log.read_text())))
            if not bases:
                raise ValueError('Actual resolved build bases are missing from the build log')
            roles[role] = {'reference': reference, **identity(info, source, version),
                           'dockerfile': dockerfile, 'dockerfileSha256': hashlib.sha256((context / dockerfile).read_bytes()).hexdigest(),
                           'resolvedBuildBases': bases, 'buildLog': log.name,
                           'buildSeconds': round(time.monotonic() - role_started, 3)}
    record = {'sourceSha': source, 'productVersion': version, 'roles': roles,
              'prepareSeconds': round(time.monotonic() - started, 3)}
    write(results / 'source-images.json', record)
    with Path(os.environ['GITHUB_ENV']).open('a') as environment:
        environment.write(f'NETRATEL_SOURCE_STACK_IMAGES={results / "source-images.json"}\n')
    print('Prepared five production images from the selected source; no registry write.')


def published(results):
    selected = module('select-prior-release').select(None)
    metadata = json.loads(command('gh', 'api', f'repos/{REPOSITORY}/releases/tags/{selected["tag"]}'))
    if metadata.get('draft') is not False or not metadata.get('published_at') or metadata.get('tag_name') != selected['tag']:
        raise ValueError('Selected release is not published')
    with tempfile.TemporaryDirectory(prefix='netratel-stack-release-') as temporary:
        subprocess.run(['gh', 'release', 'download', selected['tag'], '--repo', REPOSITORY,
                        '--pattern', 'publication.json', '--dir', temporary], check=True)
        content = (Path(temporary) / 'publication.json').read_bytes()
    asset = next(item for item in metadata['assets'] if item['name'] == 'publication.json' and item.get('state') == 'uploaded')
    if (asset.get('digest'), asset.get('size')) != ('sha256:' + hashlib.sha256(content).hexdigest(), len(content)):
        raise ValueError('Publication asset digest/size differs')
    publication = json.loads(content)
    source = publication['publicCommit']
    if (publication['productVersion'], publication['images'], publication['verification']['state']) != (
            selected['tag'][1:], selected['images'], 'complete'):
        raise ValueError('Publication differs from the selected completed release')
    if command('gh', 'api', f'repos/{REPOSITORY}/commits/{selected["tag"]}', '--jq', '.sha') != source:
        raise ValueError('Published source, original input receipt and release tag differ')
    receipt = publication['inputReceipt']
    module('promote-release').validate_input_receipt_identity(receipt, publication['productVersion'], source)
    build = json.loads(command('gh', 'api', f'repos/{REPOSITORY}/actions/runs/{receipt["runId"]}/attempts/{receipt["attempt"]}'))
    if (build.get('id'), build.get('run_attempt'), build.get('status'), build.get('conclusion'),
            build.get('head_sha'), build.get('path'), build.get('repository', {}).get('full_name'),
            build.get('head_repository', {}).get('full_name')) != (
            receipt['runId'], receipt['attempt'], 'completed', 'success', source,
            '.github/workflows/release-build.yml', REPOSITORY, REPOSITORY):
        raise ValueError('Publication receipt does not identify the successful exact-source release-build attempt')
    write(results / 'selected-release.json', selected)
    write(results / 'selected-release-metadata.json', metadata)
    write(results / 'selected-release-build.json', build)
    (results / 'publication.json').write_bytes(content)
    return publication


def app_inventory(directory, role):
    files = {}
    for path in sorted(directory.rglob('*')):
        if stat.S_ISREG(path.lstat().st_mode):
            with path.open('rb') as stream:
                files[path.relative_to(directory).as_posix()] = {'bytes': path.stat().st_size,
                    'sha256': hashlib.file_digest(stream, 'sha256').hexdigest()}
    for name in (PROJECTS[role], 'licenses/LICENSE', 'licenses/NOTICE', 'THIRD-PARTY-NOTICES.txt', 'runtime.spdx.json'):
        if name not in files or files[name]['bytes'] == 0:
            raise ValueError('Image role/application/legal inventory differs: ' + name)
    return {'applicationBytes': sum(item['bytes'] for item in files.values()), 'files': files}


def parse_status(status):
    name = re.search(r'^Name:\s+(.+)$', status, re.M)
    pid = re.search(r'^Pid:\s+(\d+)$', status, re.M)
    rss = re.search(r'^VmRSS:\s+(\d+) kB$', status, re.M)
    if not name or not pid or pid[1] != '1' or not rss:
        raise ValueError('PID 1 process RSS is unavailable or malformed')
    return {'processName': name[1], 'namespacePid': 1, 'rssBytes': int(rss[1]) * 1024}


def memory(results, containers):
    prepared = json.loads((results / 'source-images.json').read_text())
    records = {}
    for role, container in containers.items():
        info = json.loads(command('docker', 'inspect', container))[0]
        if info['Image'] != prepared['roles'][role]['Id'] or not info['State']['Running']:
            raise ValueError('RSS container does not run the prepared image: ' + role)
        records[role] = {'containerId': info['Id'], 'imageId': info['Image'],
                         'hostPid': info['State']['Pid'], 'samples': []}
    started = time.monotonic()
    for index in range(5):
        if index:
            time.sleep(2.5)
        for role, container in containers.items():
            observed = datetime.now(timezone.utc).isoformat()
            status = subprocess.check_output(['docker', 'exec', container, 'cat', '/proc/1/status'], text=True, encoding='utf-8')
            sample = {'observedAtUtc': observed, 'secondsSinceWindowStart': round(time.monotonic() - started, 3),
                      'rawProcStatus': status, **parse_status(status)}
            records[role]['samples'].append(sample)
    for record in records.values():
        values = [sample['rssBytes'] for sample in record['samples']]
        record.update(minRssBytes=min(values), medianRssBytes=statistics.median(values), maxRssBytes=max(values))
    for role, container in containers.items():
        info = json.loads(command('docker', 'inspect', container))[0]
        if (not info['State']['Running'] or info['State']['Pid'] != records[role]['hostPid'] or
                info['Id'] != records[role]['containerId'] or info['Image'] != records[role]['imageId']):
            raise ValueError('Sampled process exited or restarted: ' + role)
    write(results / 'process-rss.json', {'sourceSha': prepared['sourceSha'], 'productVersion': prepared['productVersion'],
          'scope': 'PID 1 process RSS during a short post-workload idle window; not container memory or a baseline/performance comparison.',
          'windowSeconds': round(time.monotonic() - started, 3), 'roles': records,
          'mcp-http': {'measured': False, 'reason': 'Measurement image is unstarted.'},
          'migrations': {'measured': False, 'reason': 'One-shot migration has completed.'},
          'apiWebReadinessDurationSeconds': None, 'readinessReason': 'API/Web initialized-service boundaries are not recorded; client ordered-stage timing is separate.'})


def readiness(results, finish):
    path = results / 'client-readiness.json'
    observed = {'utc': datetime.now(timezone.utc).isoformat(), 'monotonicSeconds': time.monotonic()}
    if not finish:
        prepared = json.loads((results / 'source-images.json').read_text())
        record = {'sourceSha': prepared['sourceSha'], 'imageId': prepared['roles']['client']['Id'], 'start': observed,
                  'boundary': 'Immediately before the existing enrollment process through the existing authenticated API check and positive authoritative gateway presence query. Includes intervening ordered smoke stages; an upper bound on readiness, not the earliest ready instant.'}
    else:
        record = json.loads(path.read_text())
        record.update(end=observed, elapsedSeconds=observed['monotonicSeconds'] - record['start']['monotonicSeconds'],
                      authenticatedApiCheckPassed=True, authoritativeGatewayPresencePassed=True)
        if record['elapsedSeconds'] < 0:
            raise ValueError('Client readiness clock moved backwards')
    write(path, record)


def capture(results, source, version):
    started = time.monotonic()
    free_before = shutil.disk_usage(results).free
    minimum_free = free_before
    current = json.loads((results / 'source-images.json').read_text())
    if (current['sourceSha'], current['productVersion'], set(current['roles'])) != (source, version, set(PROJECTS)):
        raise ValueError('Source preparation identity differs')
    publication = published(results)
    records = {'candidate': {}, 'control': {}}
    with tempfile.TemporaryDirectory(prefix='netratel-stack-capture-') as temporary:
        temporary = Path(temporary)
        for side in records:
            for role in PROJECTS:
                if side == 'candidate':
                    reference = current['roles'][role]['Id']
                    expected_source, expected_version, digest = source, version, None
                else:
                    reference = publication['images'][role]
                    subprocess.run(['docker', 'pull', '--platform', 'linux/amd64', reference], check=True)
                    expected_source, expected_version, digest = publication['publicCommit'], publication['productVersion'], reference
                info = json.loads(command('docker', 'image', 'inspect', reference))[0]
                record = identity(info, expected_source, expected_version, digest)
                record['reference'] = reference
                record['baseProvenance'] = ({'resolvedBuildBases': current['roles'][role]['resolvedBuildBases'],
                    'buildLog': current['roles'][role]['buildLog']} if side == 'candidate' else
                    {'resolvedBuildBases': None, 'reason': 'Publication record does not attest original build bases; floating tags are not substituted.'})
                prefix = 'publish' if role in ('api', 'web') else 'out'
                app = temporary / prefix
                app.mkdir()
                container = command('docker', 'create', '--network', 'none', '--entrypoint', '/bin/true', record['Id'])
                try:
                    subprocess.run(['docker', 'cp', container + ':/app/.', str(app)], check=True)
                finally:
                    subprocess.run(['docker', 'rm', '-f', container], check=True, stdout=subprocess.DEVNULL)
                record.update(app_inventory(app, role))
                minimum_free = min(minimum_free, shutil.disk_usage(results).free)
                subprocess.run(['docker', 'run', '--rm', '--network', 'none', '--entrypoint', '/bin/sh', record['Id'], '-ceu',
                                'test "$(id -u)" != 0; test -r /app/licenses/LICENSE; test -r /app/licenses/NOTICE; '
                                'test -s /app/THIRD-PARTY-NOTICES.txt; test -s /app/runtime.spdx.json'], check=True)
                module('verify-runtime-sbom').verify(json.loads((app / 'runtime.spdx.json').read_text()), temporary)
                shutil.copyfile(app / 'runtime.spdx.json', results / f'{side}-{role}-runtime.spdx.json')
                for graph in app.rglob('*.deps.json'):
                    if graph.is_symlink() or not graph.is_file():
                        raise ValueError('Dependency graph must be a regular image file')
                    target = results / f'{side}-{role}-graphs' / graph.relative_to(app)
                    target.parent.mkdir(parents=True, exist_ok=True)
                    shutil.copyfile(graph, target)
                records[side][role] = record
                shutil.rmtree(app)
        exported = temporary / 'layers'
        exported.mkdir()
        save_images([record['Id'] for side in records.values() for record in side.values()], exported)
        minimum_free = min(minimum_free, shutil.disk_usage(results).free)
        module('scan-saved-image').scan(exported)
        measured = module('compare-stack-image-layers').compare_stacks(exported,
            {role: record['Id'] for role, record in records['candidate'].items()},
            {role: record['Id'] for role, record in records['control'].items()})
    write(results / 'image-inventories.json', records)
    process_rss = json.loads((results / 'process-rss.json').read_text())
    client_readiness = json.loads((results / 'client-readiness.json').read_text())
    if (process_rss['sourceSha'] != source or process_rss['productVersion'] != version or
            set(process_rss['roles']) != {'api', 'web', 'client'} or process_rss['windowSeconds'] < 10 or
            any(value['imageId'] != records['candidate'][role]['Id'] or len(value['samples']) != 5
                for role, value in process_rss['roles'].items()) or
            client_readiness['sourceSha'] != source or client_readiness['imageId'] != records['candidate']['client']['Id'] or
            client_readiness.get('authenticatedApiCheckPassed') is not True or client_readiness.get('authoritativeGatewayPresencePassed') is not True):
        raise ValueError('Runtime measurement identity or completion differs')
    report = {'sourceSha': source, 'productVersion': version, 'baselineSourceSha': publication['publicCommit'],
              'baselineVersion': publication['productVersion'], 'comparison': 'Separate-run current production images versus completed published release; not isolated change attribution.',
              'baselineBuildRunId': publication['inputReceipt']['runId'], 'baselineBuildAttempt': publication['inputReceipt']['attempt'],
              'functionalScope': 'Source OIDC deployment passed; MCP HTTP is build/inventory measurement only.',
              'runner': {key: os.environ.get(key) for key in ('RUNNER_OS', 'RUNNER_ARCH', 'ImageOS', 'ImageVersion')},
              'runnerSdkVersion': command('dotnet', '--version'), 'captureSeconds': round(time.monotonic() - started, 3),
              'prepareSeconds': current['prepareSeconds'],
              'applicationBytes': {side: {role: record['applicationBytes'] for role, record in values.items()} for side, values in records.items()},
              'processRssReceipt': 'process-rss.json', 'clientReadinessReceipt': 'client-readiness.json',
              'apiWebReadinessDurationSeconds': None,
              'clientObservedReadinessUpperBoundSeconds': client_readiness['elapsedSeconds'],
              'processRssBytes': {role: {key: value[key] for key in ('minRssBytes', 'medianRssBytes', 'maxRssBytes')} for role, value in process_rss['roles'].items()},
              'diskFreeBytesBeforeCapture': free_before, 'diskFreeBytesAfterCapture': shutil.disk_usage(results).free,
              'minimumSampledDiskFreeBytesDuringCapture': minimum_free,
              **measured}
    write(results / 'comparison.json', report)
    print(json.dumps({key: value for key, value in report.items() if key != 'layers'}, indent=2))


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('phase', choices=('prepare', 'memory', 'readiness-start', 'readiness-end', 'capture'))
    parser.add_argument('--results', type=Path, required=True)
    for role in ('api', 'web', 'client'):
        parser.add_argument('--' + role + '-container')
    args = parser.parse_args()
    if os.environ.get('GITHUB_ACTIONS') != 'true' or os.environ.get('RUNNER_OS') != 'Linux' or os.environ.get('RUNNER_ARCH') != 'X64':
        parser.error('Use the optional source OIDC deployment on the hosted linux/amd64 runner.')
    source = command('git', 'rev-parse', 'HEAD')
    version = command('python3', str(ROOT / 'tools/ci/product-version.py'))
    if source != os.environ.get('SOURCE_SHA') or version != os.environ.get('VERSION'):
        parser.error('Checked-out source/version differs from the workflow identity.')
    args.results.mkdir(parents=True, exist_ok=True)
    if args.phase.startswith('readiness-'):
        readiness(args.results.resolve(), args.phase == 'readiness-end')
    elif args.phase == 'memory':
        containers = {role: getattr(args, role + '_container') for role in ('api', 'web', 'client')}
        if not all(containers.values()):
            parser.error('Actual API/Web/client containers are required for process RSS sampling.')
        memory(args.results.resolve(), containers)
    else:
        (prepare if args.phase == 'prepare' else capture)(args.results.resolve(), source, version)
