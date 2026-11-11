#!/usr/bin/env python3
"""Produce a safe acceptance receipt only after genuine stages and owned cleanup."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess

root = Path(os.environ['NETRATEL_PAIRING_RUNTIME_ROOT'])
fixture = root / 'actual-pair'

def read(path):
    return json.loads(path.read_text())

def require(condition, message):
    if not condition:
        raise RuntimeError(message)

def write(path, data):
    path.write_text(json.dumps(data, indent=2))
    path.chmod(0o600)

def identity(name):
    source = Path(os.environ[name])
    sha = subprocess.check_output(['git', '-C', str(source), 'rev-parse', 'HEAD'], text=True).strip()
    require(len(sha) == 40 and all(char in '0123456789abcdef' for char in sha), 'Actual source identity is missing')
    dirty = bool(subprocess.check_output(['git', '-C', str(source), 'status', '--porcelain'], text=True).strip())
    return sha, dirty

def prepare():
    browser = read(fixture / 'browser-evidence' / 'receipt.json')
    business = read(fixture / 'business-evidence' / 'receipt.json')
    require(browser['fixtureId'] == business['fixtureId'], 'Stage receipts belong to different fixtures')
    statuses = {row['stage']: row['status'] for row in browser['results']}
    for stage in ['rateldesk-to-netratel-rendered-pair-and-single-save',
                  'netratel-to-rateldesk-rendered-pair-and-single-save',
                  'side-effect-free-test-current-counts', 'deleteWhilePeerOffline']:
        require(statuses.get(stage) == 'passed', 'A real rendered acceptance stage has not passed: ' + stage)
    for stage in ['incidentReceiptReplay', 'nativeAutomationResult',
                  'nativeAggregateAutomationResult-explicit', 'nativeAggregateAutomationResult-ha',
                  'renderedSelfService-self-success', 'renderedSelfService-self-failure',
                  'renderedSelfService-on-behalf', 'renderedSelfService-lost-callback']:
        require(business['stages'].get(stage) == 'passed', 'A real native business stage has not passed: ' + stage)
    require(business['historyPreserved'] is True and business['resultReceived'] is True,
            'Durable real execution/result history was not verified')
    require(bool(str(business.get('executionId', ''))), 'An actual execution identity is missing')
    require(business.get('incidentCount') == 2 and business.get('receiptCount') == 1,
            'The original receiver receipt and its incident plus one self-service failure incident must be retained')
    from native_pair import source_identity
    private_state = read(fixture / 'private-state.json')
    current = {product: source_identity(Path(os.environ[name])) for product, name in [
        ('netratel', 'NETRATEL_PAIRING_SOURCE_ROOT'), ('rateldesk', 'RATELDESK_PAIRING_SOURCE_ROOT')]}
    nr_sha, nr_dirty = current['netratel']['sha'], current['netratel']['dirty']
    rd_sha, rd_dirty = current['rateldesk']['sha'], current['rateldesk']['dirty']
    clean_required = os.environ.get('PAIRING_REQUIRE_CLEAN_SOURCE') == '1'
    for product in current:
        started = private_state['sources'][product]
        require(started['sha'] == current[product]['sha'], 'A source commit changed while the fixture ran')
        if clean_required:
            require(not started['dirty'] and not current[product]['dirty'] and started['workingTreeSha256'] == current[product]['workingTreeSha256'],
                    'Commit-bound acceptance requires unchanged clean reviewed sources')
    client_hash = business.get('clientBinarySha256', '')
    require(len(client_hash) == 64 and all(char in '0123456789abcdef' for char in client_hash),
            'An actual copied native Client binary hash was not verified')
    write(fixture / 'browser-evidence' / 'source-manifests.json',
          {'captured': private_state['sources'], 'current': current, 'netratelClientBinarySha256': client_hash})
    def bounded_identity(identity):
        return {key: value for key, value in identity.items() if key != 'dirtyFiles'} | {
            'dirtyFileCount': len(identity['dirtyFiles']),
            'dirtyFilesSha256': hashlib.sha256(json.dumps(identity['dirtyFiles'], separators=(',', ':')).encode()).hexdigest()}
    captured_summary = {name: bounded_identity(value) for name, value in private_state['sources'].items()}
    current_summary = {name: bounded_identity(value) for name, value in current.items()}
    require(business['sources']['netratel'] == nr_sha and business['sources']['rateldesk'] == rd_sha,
            'Source identity changed during real native acceptance')
    self_executions = []
    for mode in ['self-success', 'self-failure', 'on-behalf', 'lost-callback']:
        rows = [row for row in business['results'] if row.get('stage') == 'renderedSelfService-' + mode]
        require(len(rows) == 1 and rows[0].get('status') == 'passed', 'A rendered native result is missing or repeated')
        row = rows[0]
        ids = {}
        for key in ['requestId', 'customerId', 'organizationId', 'taskId', 'connectionId', 'jobId',
                    'providerRequestId', 'providerRunId', 'correlationId']:
            value = row.get(key)
            require(isinstance(value, str) and 0 < len(value) <= 128 and
                    all(char.isascii() and (char.isalnum() or char in '_.:-') for char in value), 'A bounded actual correlation identity is missing')
            ids[key] = value
        require(row.get('admissionCount') == 1 and row.get('submissionRetryRetained') is True,
                'Actual admission/retry uniqueness is not proven')
        require(row.get('nativeExitCode') == (42 if mode == 'self-failure' else 0), 'The intended native command outcome is missing')
        require(row.get('localStatus') == ('failed' if mode == 'self-failure' else 'succeeded'), 'The actual local terminal outcome is missing')
        expected_parent = 'Failed' if mode == 'self-failure' else 'InProgress' if mode == 'lost-callback' else 'Completed'
        require(row.get('parentStatus') == expected_parent, 'The intended parent workflow outcome is missing')
        require(row.get('requesterKind') in ['ordinary-local', 'local-administrator'], 'The rendered actor kind is missing')
        require(mode != 'self-success' or row.get('ordinaryMyRequestsVerified') is True, 'Ordinary request ownership is unproven')
        require(mode != 'on-behalf' or row.get('selectedPersonPreserved') is True, 'A distinct selected requester is unproven')
        latency = row.get('liveConvergenceMilliseconds')
        require(mode == 'lost-callback' and latency is None or isinstance(latency, int) and 0 <= latency <= 2000,
                'Actual commit-to-render convergence is not proven')
        self_executions.append({'stage': 'renderedSelfService-' + mode, **ids,
            'requesterKind': row['requesterKind'], 'ordinaryMyRequestsVerified': row.get('ordinaryMyRequestsVerified') is True,
            'selectedPersonPreserved': row.get('selectedPersonPreserved') is True,
            'nativeExitCode': row['nativeExitCode'], 'admissionCount': 1, 'submissionRetryRetained': True,
            'localStatus': row['localStatus'], 'parentStatus': row['parentStatus'], 'liveConvergenceMilliseconds': latency})
    data = {'fixtureId': browser['fixtureId'], 'stages': {
        'pairedFromRatelDesk': 'passed', 'pairedFromNetRatel': 'passed',
        'incidentReceiptReplay': 'passed', 'nativeAutomationResult': 'passed',
        'nativeAggregateAutomationResult-explicit': 'passed', 'nativeAggregateAutomationResult-ha': 'passed',
        'sideEffectFreeTest': 'passed', 'deleteWhilePeerOffline': 'passed',
        'renderedSelfService-self-success': 'passed', 'renderedSelfService-self-failure': 'passed',
        'renderedSelfService-on-behalf': 'passed', 'renderedSelfService-lost-callback': 'passed'},
        'sources': {'netratelSha': nr_sha, 'rateldeskSha': rd_sha,
                    'netratelDirty': nr_dirty, 'rateldeskDirty': rd_dirty,
                    'captured': captured_summary, 'current': current_summary,
                    'runtimeAssemblySha256': {name: entry['runtimeAssemblySha256'] for name, entry in private_state['products'].items()},
                    'netratelClientBinarySha256': client_hash, 'cleanSourceRequired': clean_required},
        'result': {'incidentCount': business['incidentCount'], 'receiptCount': business['receiptCount'], 'executionId': str(business['executionId']),
                   'resultReceived': True, 'historyPreserved': True, 'cleanupComplete': False,
                   'selfServiceExecutions': self_executions,
                   'executions': [{key: row[key] for key in ['stage', 'executionId', 'nativeExitCode', 'callbackReplayHarmless']}
                                  for row in business['results'] if row.get('stage') in [
                                      'nativeAutomationResult', 'nativeAggregateAutomationResult-explicit', 'nativeAggregateAutomationResult-ha']]}}
    write(root / 'pending-receipt.json', data)

def finalize():
    data = read(root / 'pending-receipt.json')
    cleanup = read(root / 'cleanup-receipt.json')
    require(data['fixtureId'] == cleanup['fixtureId'], 'Cleanup belongs to another fixture')
    require(all(cleanup.get(stage) is True for stage in ['processesStopped', 'containersRemoved', 'privateCredentialsRemoved'])
            and not fixture.exists(), 'Owned fixture cleanup is incomplete')
    data['result']['cleanupComplete'] = True
    write(root / 'acceptance-receipt.json', data)
    (root / 'pending-receipt.json').unlink()
    print('Actual two-source pairing acceptance passed; safe receipt written after cleanup')

if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('action', choices=['prepare', 'finalize'])
    args = parser.parse_args()
    try:
        prepare() if args.action == 'prepare' else finalize()
    except (RuntimeError, KeyError, FileNotFoundError) as error:
        print(str(error))
        raise SystemExit(1)
