#!/usr/bin/env python3
"""Focused HTTPS Final Save fixture; reuse normal bootstrap and positive cleanup."""
import argparse
import hashlib
import http.client
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import os
from pathlib import Path
import ssl
import subprocess
import threading
import urllib.request

import native_pair as native

BASELINE = Path(os.environ['NETRATEL_PAIRING_BASELINE_SOURCE_ROOT'])
METRICS = native.FIXTURE / 'save-frontdoor-observations.json'


def capture_api(source, label):
    runtime = native.FIXTURE / 'netratel' / (label + '-api-runtime')
    project = source / 'src/NetRatel/NetRatel.API/NetRatel.API.csproj'
    native.command([str(native.DOTNET), 'publish', str(project), '--configuration', 'Debug',
        '--no-build', '--no-restore', '--output', str(runtime), '-m:1', '-nr:false'],
        cwd=source, log_name=label + '-api-debug-publish.log')
    return runtime / 'NetRatel.API.dll'


def start_api(state, dll):
    entry = state['products']['netratel']
    environment = native.environment_for(state, 'netratel', 'api')
    environment['ASPNETCORE_CONTENTROOT'] = str(dll.parent)
    with (native.FIXTURE / 'netratel-api.log').open('ab') as output:
        process = subprocess.Popen([str(native.DOTNET), str(dll)], cwd=dll.parent,
            env=environment, stdout=output, stderr=subprocess.STDOUT, start_new_session=True)
    entry['pids']['api'] = process.pid
    entry['runtimeAssemblySha256']['api'] = hashlib.sha256(dll.read_bytes()).hexdigest()
    native.private_json(native.STATE, state)
    native.wait_host(f"http://127.0.0.1:{entry['apiPort']}/api/v2/setup/status", process, 'NetRatel API')


def start(mode):
    baseline_identity = native.source_identity(BASELINE)
    expected = os.environ['PAIRING_EXPECTED_BASELINE_SHA']
    if baseline_identity['dirty'] or baseline_identity['sha'] != expected:
        raise RuntimeError('Baseline source changed; no fixture may start')
    original_create = native.create_state
    original_publish = native.publish_runtime
    original_request_ok = native.request_ok

    def create():
        state = original_create()
        state['saveJourney'] = mode
        state['baselineSource'] = baseline_identity
        trust = state['products']['netratel']['clientTrustBundle']
        for entry in state['products'].values():
            entry['environment'] = {'SSL_CERT_FILE': trust}
        # Default HTTPS port is deliberate: the real production receiver handler
        # and its service-discovery wrappers must see this exact guarded journey.
        rd = state['products']['rateldesk']
        rd['proxyPort'] = 443
        rd['proxyApiOrigin'] = 'https://127.0.0.1'
        rd['environment'].update(ServiceIdentity__ApiBaseUrl='https://127.0.0.1',
            ServiceIdentity__Issuer='https://127.0.0.1/services',
            StorageOptions__PublicApiBaseUrl='https://127.0.0.1')
        # Keep exception details private; the driver projects only fixed codes.
        state['products']['netratel']['environment']['Logging__LogLevel__System.Net.Http.HttpClient'] = 'Debug'
        native.private_json(native.STATE, state)
        return state

    def publish(product, role):
        if product == 'netratel' and role == 'api' and mode == 'retained':
            runtime = native.FIXTURE / product / 'baseline-api-runtime' / 'NetRatel.API.dll'
            return runtime if runtime.is_file() else capture_api(BASELINE, 'baseline')
        return original_publish(product, role)

    def request_ok(url):
        if not url.startswith('https://127.0.0.1/'):
            return original_request_ok(url)
        state = json.loads(native.STATE.read_text())
        context = ssl.create_default_context(cafile=state['products']['netratel']['clientTrustBundle'])
        opener = urllib.request.build_opener(urllib.request.ProxyHandler({}), urllib.request.HTTPSHandler(context=context))
        try:
            with opener.open(url, timeout=2) as response:
                return response.status == 200
        except OSError:
            return False

    def proxy(state):
        with (native.FIXTURE / 'save-frontdoor.log').open('ab') as output:
            process = subprocess.Popen(['python3', str(Path(__file__).resolve()), 'frontdoor'],
                env=native.tool_env(), stdout=output, stderr=subprocess.STDOUT, start_new_session=True)
        state.setdefault('helperPids', {})['saveFrontdoor'] = process.pid
        native.private_json(native.STATE, state)
        native.wait_host('https://127.0.0.1/__fixture/health', process, 'Trusted default-port HTTPS front door')

    native.create_state = create
    native.publish_runtime = publish
    native.request_ok = request_ok
    native.start_proxy = proxy
    native.start()


def frontdoor():
    state = json.loads(native.STATE.read_text())
    stages = {'/connect/token': 'token', '/api/v1/integrations/netratel/capabilities': 'capabilities',
        '/api/v1/integrations/netratel/targets/validate': 'targetValidation'}
    observations = []
    lock = threading.Lock()

    class Forward(BaseHTTPRequestHandler):
        def log_message(self, *_):
            pass

        def do_GET(self):
            self.forward()

        def do_POST(self):
            self.forward()

        def do_PUT(self):
            self.forward()

        def do_DELETE(self):
            self.forward()

        def forward(self):
            if self.path == '/__fixture/health':
                self.send_response(200)
                self.end_headers()
                self.wfile.write(b'{"ready":true}')
                return
            connection = None
            try:
                size = int(self.headers.get('Content-Length', '0'))
                if not 0 <= size <= 256 * 1024:
                    raise ValueError('Fixture request exceeded bound')
                body = self.rfile.read(size)
                headers = {k: v for k, v in self.headers.items() if k.lower() not in ('host', 'connection', 'transfer-encoding')}
                connection = http.client.HTTPConnection('127.0.0.1', state['products']['rateldesk']['apiPort'], timeout=30)
                connection.request(self.command, self.path, body, headers)
                reply = connection.getresponse()
                payload = reply.read(512 * 1024 + 1)
                if len(payload) > 512 * 1024:
                    raise ValueError('Fixture response exceeded bound')
                if self.path in stages:
                    with lock:
                        observations.append({'stage': stages[self.path], 'status': reply.status})
                        native.private_json(METRICS, observations[-32:])
                self.send_response(reply.status)
                for key, value in reply.getheaders():
                    if key.lower() not in ('transfer-encoding', 'connection', 'content-length'):
                        self.send_header(key, value)
                self.send_header('Content-Length', str(len(payload)))
                self.end_headers()
                self.wfile.write(payload)
            except (OSError, ValueError, http.client.HTTPException):
                self.send_response(502)
                self.end_headers()
                self.wfile.write(b'{"code":"fixture-upstream-failure"}')
            finally:
                if connection is not None:
                    connection.close()

    directory = native.FIXTURE / 'netratel' / 'keys'
    server = ThreadingHTTPServer(('127.0.0.1', 443), Forward)
    context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
    context.load_cert_chain(str(directory / 'gateway.pem'), str(directory / 'gateway-key.pem'))
    server.socket = context.wrap_socket(server.socket, server_side=True)
    server.serve_forever()


def switch():
    state = json.loads(native.STATE.read_text())
    if state['saveJourney'] != 'retained':
        raise RuntimeError('Only a retained failed-draft journey may replace its API')
    captured = state['sources']['netratel']
    current = native.source_identity(native.REPOS['netratel'])
    if current != captured:
        raise RuntimeError('Corrected source changed during the retained-draft journey')
    native.stop_process(state['products']['netratel']['pids']['api'])
    state['products']['netratel']['pids'].pop('api')
    native.private_json(native.STATE, state)
    dll = capture_api(native.REPOS['netratel'], 'corrected')
    start_api(state, dll)


def baseline_observation():
    state = json.loads(native.STATE.read_text())
    log = (native.FIXTURE / 'netratel-api.log').read_text(errors='replace')
    observed = json.loads(METRICS.read_text()) if METRICS.exists() else []
    return {'sourceSha': state['baselineSource']['sha'],
        'guardCodeObserved': 'receiver-endpoint-outside-approved-api-base' in log,
        'token200Observed': any(x == {'stage': 'token', 'status': 200} for x in observed),
        'capabilitiesReached': any(x['stage'] == 'capabilities' for x in observed)}


def finalize():
    pending = native.ROOT / 'pending-save-receipt.json'
    receipt = json.loads(pending.read_text())
    cleanup = json.loads((native.ROOT / 'cleanup-receipt.json').read_text())
    if cleanup['fixtureId'] != receipt['fixtureId'] or not all(cleanup[k] is True for k in
        ('processesStopped', 'containersRemoved', 'privateCredentialsRemoved')) or native.FIXTURE.exists():
        raise RuntimeError('Focused Save receipt requires positive fixture purge')
    if any(native.source_identity(source) != receipt['sources'][product] for product, source in native.REPOS.items()) or \
        native.source_identity(BASELINE) != receipt['baselineSource']:
        raise RuntimeError('Focused Save source identities changed before finalization')
    if receipt['saveAttempts'] != (2 if receipt['journey'] == 'retained' else 1):
        raise RuntimeError('Focused Save attempt counts do not match the selected journey')
    receipt['cleanupComplete'] = True
    native.private_json(native.ROOT / 'save-acceptance-receipt.json', receipt)
    pending.unlink()


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('action', choices=['start', 'frontdoor', 'switch-api', 'baseline-observation', 'finalize'])
    parser.add_argument('--journey', choices=['retained', 'fresh'], default='fresh')
    args = parser.parse_args()
    if args.action == 'start': start(args.journey)
    elif args.action == 'frontdoor': frontdoor()
    elif args.action == 'switch-api': switch()
    elif args.action == 'baseline-observation': print(json.dumps(baseline_observation()))
    else: finalize()
