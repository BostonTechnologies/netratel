#!/usr/bin/env python3
"""Disposable native Debug products; secrets stay in a private fixture directory.

This launcher performs normal migration/bootstrap commands and never grants
product permissions directly. Use start, restart <product>, stop <product>, or
cleanup. Pairing/business/browser assertions are separate and do not imply a
pass merely because startup succeeded.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import secrets
import signal
import shutil
import socket
import subprocess
import time
import urllib.request
import uuid

ROOT = Path(os.environ['NETRATEL_PAIRING_RUNTIME_ROOT'])
FIXTURE = ROOT / 'actual-pair'
SAFE_EVIDENCE = Path(os.environ.get('NETRATEL_PAIRING_EVIDENCE_DIRECTORY', str(ROOT / 'evidence')))
STATE = FIXTURE / 'private-state.json'
DOTNET = Path(os.environ.get('DOTNET_HOST_PATH') or shutil.which('dotnet') or str(ROOT / 'dotnet' / 'dotnet'))
REPOS = {'netratel': Path(os.environ['NETRATEL_PAIRING_SOURCE_ROOT']),
         'rateldesk': Path(os.environ['RATELDESK_PAIRING_SOURCE_ROOT'])}
DOCKER = ['docker', '--host=unix:///var/run/docker.sock']


def private_json(path, value):
    path.write_text(json.dumps(value, indent=2))
    path.chmod(0o600)


def free_port():
    with socket.socket() as listener:
        listener.bind(('127.0.0.1', 0))
        return listener.getsockname()[1]


def tool_env():
    environment = dict(os.environ)
    environment.update(DOTNET_ROOT=str(DOTNET.parent),
        DOTNET_CLI_HOME=os.environ.get('DOTNET_CLI_HOME', str(ROOT / 'dotnet-cli')),
        NUGET_PACKAGES=os.environ.get('NUGET_PACKAGES', str(ROOT / 'nuget')),
        DOTNET_NOLOGO='1', DOTNET_CLI_TELEMETRY_OPTOUT='1',
        PLAYWRIGHT_BROWSERS_PATH=os.environ.get('PLAYWRIGHT_BROWSERS_PATH', str(ROOT / 'playwright-browsers')),
        PATH=str(DOTNET.parent) + ':' + environment.get('PATH', ''))
    return environment


def docker_env():
    environment = tool_env()
    for name in ['DOCKER_HOST', 'DOCKER_CONTEXT', 'DOCKER_TLS',
                 'DOCKER_TLS_VERIFY', 'DOCKER_CERT_PATH']:
        environment.pop(name, None)
    return environment


def command(args, *, environment=None, cwd=None, log_name='command.log', timeout=120):
    with (FIXTURE / log_name).open('ab') as output:
        result = subprocess.run(args, env=environment or tool_env(), cwd=cwd,
            stdout=output, stderr=subprocess.STDOUT, timeout=timeout)
    if result.returncode:
        raise RuntimeError(f'{log_name} failed with exit {result.returncode}; inspect the private log')


def source_identity(source):
    sha = subprocess.check_output(['git', '-C', str(source), 'rev-parse', 'HEAD'], text=True).strip()
    dirty_files = subprocess.check_output(['git', '-C', str(source), 'status', '--porcelain', '--untracked-files=all'], text=True).splitlines()
    tracked = subprocess.check_output(['git', '-C', str(source), 'ls-files', '-z'])
    untracked = subprocess.check_output(['git', '-C', str(source), 'ls-files', '--others', '--exclude-standard', '-z'])
    names = sorted(set(name for name in (tracked + untracked).split(b'\0') if name))
    tree = hashlib.sha256()
    for name in names:
        path = source / os.fsdecode(name)
        tree.update(name + b'\0')
        if path.is_symlink():
            tree.update(b'link\0' + os.fsencode(os.readlink(path)))
        elif path.is_file():
            tree.update(b'file\0' + hashlib.sha256(path.read_bytes()).digest())
        else:
            tree.update(b'deleted\0')
        tree.update(b'\0')
    return {'sha': sha, 'dirty': bool(dirty_files), 'dirtyFiles': dirty_files,
            'workingTreeSha256': tree.hexdigest()}


def create_state():
    identities = {name: source_identity(source) for name, source in REPOS.items()}
    if os.environ.get('PAIRING_REQUIRE_CLEAN_SOURCE') == '1':
        for name, identity in identities.items():
            expected = os.environ.get('PAIRING_EXPECTED_' + name.upper() + '_SHA')
            if not expected or identity['dirty'] or identity['sha'] != expected:
                raise RuntimeError('Reviewed source changed during build; no fixture may start')
    FIXTURE.mkdir(mode=0o700, parents=True, exist_ok=True)
    FIXTURE.chmod(0o700)
    suffix = uuid.uuid4().hex[:12]
    state = {'fixtureId': suffix, 'container': 'pairing-acceptance-' + suffix,
        'databasePort': free_port(), 'databasePassword': secrets.token_hex(32),
        'adminPassword': secrets.token_hex(32), 'products': {},
        'sources': identities}
    for product in REPOS:
        directory = FIXTURE / product
        directory.mkdir(mode=0o700)
        for child in ['bootstrap', 'data', 'keys', 'storage']:
            (directory / child).mkdir(mode=0o700)
        password_file = directory / 'admin-password'
        password_file.write_text(state['adminPassword'])
        password_file.chmod(0o600)
        state['products'][product] = {'apiPort': free_port(), 'webPort': free_port(),
            'gatewayPort': free_port(), 'database': product + '_' + suffix,
            'email': product + '.admin@example.test', 'pids': {}}
        if product == 'rateldesk':
            state['products'][product]['proxyPort'] = free_port()
            state['products'][product]['proxyApiOrigin'] = f"http://127.0.0.1:{state['products'][product]['proxyPort']}"
    prepare_gateway(state)
    private_json(STATE, state)
    return state


def prepare_gateway(state):
    directory = FIXTURE / 'netratel' / 'keys'
    entry = state['products']['netratel']
    if 'gatewayTlsPort' not in entry:
        entry['gatewayTlsPort'] = free_port()
    entry['gatewayEndpoint'] = f"https://127.0.0.1:{entry['gatewayTlsPort']}"
    pfx_password = directory / 'gateway-pfx-password'
    if not pfx_password.exists():
        pfx_password.write_text(secrets.token_hex(32))
        pfx_password.chmod(0o600)
        extensions = directory / 'gateway-extensions.cnf'
        extensions.write_text('subjectAltName=DNS:localhost,IP:127.0.0.1\n'
            'extendedKeyUsage=serverAuth\nbasicConstraints=CA:FALSE\n')
        command(['openssl', 'req', '-x509', '-newkey', 'rsa:2048', '-nodes',
            '-keyout', str(directory / 'ca-key.pem'), '-out', str(directory / 'ca.pem'),
            '-days', '2', '-subj', '/CN=Disposable pairing acceptance CA'],
            log_name='gateway-certificates.log')
        command(['openssl', 'req', '-newkey', 'rsa:2048', '-nodes',
            '-keyout', str(directory / 'gateway-key.pem'), '-out', str(directory / 'gateway.csr'),
            '-subj', '/CN=localhost'], log_name='gateway-certificates.log')
        command(['openssl', 'x509', '-req', '-in', str(directory / 'gateway.csr'),
            '-CA', str(directory / 'ca.pem'), '-CAkey', str(directory / 'ca-key.pem'),
            '-CAcreateserial', '-out', str(directory / 'gateway.pem'), '-days', '2',
            '-extfile', str(extensions)], log_name='gateway-certificates.log')
        command(['openssl', 'pkcs12', '-export', '-out', str(directory / 'gateway.pfx'),
            '-inkey', str(directory / 'gateway-key.pem'), '-in', str(directory / 'gateway.pem'),
            '-certfile', str(directory / 'ca.pem'), '-passout', 'file:' + str(pfx_password)],
            log_name='gateway-certificates.log')
    bundle = directory / 'client-trust-bundle.pem'
    system_bundle = Path(os.environ.get('SSL_CERT_FILE', '/etc/ssl/certs/ca-certificates.crt'))
    bundle.write_text(system_bundle.read_text() + '\n' + (directory / 'ca.pem').read_text())
    entry['clientTrustBundle'] = str(bundle)
    for child in directory.iterdir():
        child.chmod(0o600)


def environment_for(state, product, role):
    entry = state['products'][product]
    directory = FIXTURE / product
    api = f"http://127.0.0.1:{entry['apiPort']}"
    web = f"http://127.0.0.1:{entry['webPort']}"
    canonical_api = f"http://127.0.0.1:{entry['proxyPort']}" if product == 'rateldesk' and 'proxyPort' in entry else api
    connection = (f"Host=127.0.0.1;Port={state['databasePort']};"
        f"Database={entry['database']};Username=postgres;Password={state['databasePassword']}")
    environment = tool_env()
    environment.update(ASPNETCORE_ENVIRONMENT='Production',
        ASPNETCORE_URLS=api if role == 'api' else web,
        Authentication__Mode='Local',
        Bootstrap__StateDirectory=str(directory / 'bootstrap'),
        ApiBaseUrl=api + '/',
        ReverseProxy__Clusters__apiCluster__Destinations__api1__Address=api + '/',
        ServiceIdentity__ApiBaseUrl=canonical_api, ServiceIdentity__WebBaseUrl=web,
        ServiceIdentity__Issuer=canonical_api + '/services',
        ServiceIdentity__Audience=product + '.services')
    if product == 'netratel':
        environment.update(ConnectionStrings__NetRatelDb=connection,
            DataProtection__KeysDirectory=str(directory / 'keys'),
            NetRatel_KEYS_DIR=str(directory / 'keys'),
            Authentication__Local__AllowInsecureLocalhost='true',
            NetRatel_HTTP_PORT=str(entry['apiPort']),
            NetRatelAkka__GatewayGrpcPort=str(entry['gatewayPort']),
            Bootstrap__Unattended__PasswordFile=str(directory / 'admin-password'),
            Bootstrap__Unattended__Email=entry['email'],
            Bootstrap__Unattended__DisplayName='Disposable NetRatel Administrator',
            Bootstrap__Unattended__TenantName='Acceptance NetRatel tenant',
            Bootstrap__AllowedOrigins__0=web,
            StorageOptions__RootPath=str(directory / 'storage'),
            ClientArtifacts__StorageRoot=str(directory / 'storage' / 'artifacts'),
            AgentAuth__PrivateKeyPath=str(directory / 'keys' / 'agent-es256.pem'))
        if role == 'api' and 'gatewayEndpoint' in entry:
            environment.update(Kestrel__Endpoints__PairingAcceptance__Url=entry['gatewayEndpoint'],
                Kestrel__Endpoints__PairingAcceptance__Protocols='Http2',
                Kestrel__Endpoints__PairingAcceptance__Certificate__Path=str(directory / 'keys' / 'gateway.pfx'),
                Kestrel__Endpoints__PairingAcceptance__Certificate__Password=(directory / 'keys' / 'gateway-pfx-password').read_text(),
                HTTPS_PORT='-1')
    else:
        # A fresh bootstrap must select PostgreSQL through its normal command.
        environment.update(ConnectionStrings__HelpdeskDb='',
            DataProtection__KeyRingPath=str(directory / 'keys'),
            Bootstrap__DataDirectory=str(directory / 'data'),
            Authentication__AllowInsecureLocalhost='true',
            Bootstrap__Unattended__Provider='PostgreSql',
            Bootstrap__Unattended__PostgreSqlConnectionString=connection,
            Bootstrap__Unattended__Email=entry['email'],
            Bootstrap__Unattended__DisplayName='Disposable RatelDesk Administrator',
            Bootstrap__Unattended__Password=state['adminPassword'],
            Bootstrap__Unattended__OrganizationName='Acceptance RatelDesk organization',
            Bootstrap__Unattended__ApplicationUrl=web,
            StorageOptions__RootPath=str(directory / 'storage'),
            StorageOptions__PublicApiBaseUrl=canonical_api,
            EmailIngestion__Enabled='false')
    environment.update(entry.get('environment', {}))
    return environment


def assembly(product, role):
    if product == 'netratel':
        name = 'NetRatel.API' if role == 'api' else 'NetRatel.Web'
        return REPOS[product] / 'src' / 'NetRatel' / name / 'bin' / 'Debug' / 'net10.0' / (name + '.dll')
    name = 'Helpdesk.API' if role == 'api' else 'HelpDesk.NewWeb'
    return REPOS[product] / 'src' / name / 'bin' / 'Debug' / 'net10.0' / (name + '.dll')


def request_ok(url):
    try:
        # Loopback fixture traffic is local, while normal external tools retain proxies.
        opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
        with opener.open(url, timeout=2) as response:
            return response.status == 200
    except (OSError, urllib.error.URLError):
        return False


def wait_host(url, process, label):
    deadline = time.monotonic() + 60
    while time.monotonic() < deadline:
        if process.poll() is not None:
            raise RuntimeError(label + ' exited before health; inspect private logs')
        if request_ok(url):
            return
        time.sleep(.5)
    raise RuntimeError(label + ' did not become healthy within 60 seconds')


def publish_runtime(product, role):
    dll = assembly(product, role)
    # Ordinary Debug publication packages actual static assets for normal
    # installation hosting. It never enables development authentication.
    runtime = FIXTURE / product / (role + '-runtime')
    captured = runtime / '.capture-complete'
    if captured.exists():
        return runtime / dll.name
    if not dll.is_file():
        raise RuntimeError(f'Missing actual Debug {product} {role} assembly')
    project = dll.parent.parent.parent.parent / (dll.stem + '.csproj')
    command([str(DOTNET), 'publish', str(project), '--configuration', 'Debug',
        '--no-build', '--no-restore', '--output', str(runtime), '-m:1', '-nr:false'],
        cwd=REPOS[product], log_name=product + '-' + role + '-debug-publish.log')
    captured.write_text('Current-source Debug runtime captured once for this fixture\n')
    return runtime / dll.name


def start_product(state, product):
    entry = state['products'][product]
    for role in ['api', 'web']:
        dll = publish_runtime(product, role)
        entry.setdefault('runtimeAssemblySha256', {})[role] = hashlib.sha256(dll.read_bytes()).hexdigest()
        environment = environment_for(state, product, role)
        environment['ASPNETCORE_CONTENTROOT'] = str(dll.parent)
        with (FIXTURE / (product + '-' + role + '.log')).open('ab') as output:
            process = subprocess.Popen([str(DOTNET), str(dll)], cwd=dll.parent,
                env=environment, stdout=output, stderr=subprocess.STDOUT,
                start_new_session=True)
        entry['pids'][role] = process.pid
        private_json(STATE, state)
        route = ('/api/v2/setup/status' if product == 'netratel' else '/api/v1/setup/status') if role == 'api' else '/'
        wait_host(f"http://127.0.0.1:{entry[role + 'Port']}" + route, process,
            product + ' ' + role)
        print(product + ' ' + role + ' healthy', flush=True)


def process_alive(pid):
    try:
        # Reaped-by-init zombies cannot keep a listener alive.
        status = Path(f'/proc/{pid}/stat').read_text().split(') ', 1)[1].split()[0]
        return status != 'Z'
    except (FileNotFoundError, ProcessLookupError):
        return False


def stop_process(pid):
    if not process_alive(pid):
        return
    marker = ('NETRATEL_PAIRING_RUNTIME_ROOT=' + str(ROOT)).encode()
    try:
        environment_entries = Path(f'/proc/{pid}/environ').read_bytes().split(b'\0')
    except FileNotFoundError:
        return
    if marker not in environment_entries:
        raise RuntimeError('Recorded PID no longer belongs to this owned fixture')
    try:
        os.killpg(pid, signal.SIGTERM)
    except ProcessLookupError:
        return
    deadline = time.monotonic() + 20
    while process_alive(pid) and time.monotonic() < deadline:
        time.sleep(.2)
    if process_alive(pid):
        try:
            os.killpg(pid, signal.SIGKILL)
        except ProcessLookupError:
            pass
        deadline = time.monotonic() + 5
        while process_alive(pid) and time.monotonic() < deadline:
            time.sleep(.1)
    if process_alive(pid):
        raise RuntimeError('An owned fixture process did not stop')


def stop_product(state, product):
    for pid in state['products'][product]['pids'].values():
        stop_process(pid)
    state['products'][product]['pids'] = {}
    private_json(STATE, state)


def cleanup(state, purge=False):
    for product in REPOS:
        stop_product(state, product)
    for pid in state.get('helperPids', {}).values():
        stop_process(pid)
    # Daemon/query failure is never evidence that an owned resource is absent.
    health = subprocess.run(DOCKER + ['info', '--format', '{{.ServerVersion}}'],
        env=docker_env(), stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=10)
    if health.returncode:
        raise RuntimeError('Docker daemon is unavailable; owned cleanup cannot be verified')
    def existing_containers():
        result = subprocess.run(DOCKER + ['ps', '--all', '--format', '{{.Names}}'],
            env=docker_env(), stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, timeout=10)
        if result.returncode:
            raise RuntimeError('Docker resource query failed; owned absence cannot be verified')
        return set(result.stdout.splitlines())
    for container in [state.get('clientContainer'), state['container']]:
        if not container:
            continue
        if container in existing_containers():
            command(DOCKER + ['rm', '--force', container], environment=docker_env(),
                log_name='resource-cleanup.log')
        if container in existing_containers():
            raise RuntimeError('An owned disposable container survived cleanup')
    state['helperPids'] = {}
    state['resourcesStopped'] = True
    private_json(STATE, state)
    if purge:
        # Only masked screenshots and deliberately safe assertion summaries survive.
        source_evidence = FIXTURE / 'browser-evidence'
        if source_evidence.exists():
            destination = SAFE_EVIDENCE / state['fixtureId']
            destination.parent.mkdir(parents=True, exist_ok=True)
            shutil.copytree(source_evidence, destination, dirs_exist_ok=True)
        business_receipt = FIXTURE / 'business-evidence' / 'receipt.json'
        if business_receipt.exists():
            destination = SAFE_EVIDENCE / state['fixtureId']
            destination.mkdir(parents=True, exist_ok=True)
            shutil.copy2(business_receipt, destination / 'business-receipt.json')
        shutil.rmtree(FIXTURE)
        private_json(ROOT / 'cleanup-receipt.json', {'fixtureId': state['fixtureId'],
            'processesStopped': True, 'containersRemoved': True, 'privateCredentialsRemoved': not FIXTURE.exists()})
    print('Owned fixture resources stopped' + (' and private credentials removed' if purge else '; private diagnostics retained'), flush=True)


def counts(state):
    def query(product, sql):
        result = subprocess.run(DOCKER + ['exec', state['container'], 'psql',
            '-U', 'postgres', '-d', state['products'][product]['database'], '-tAc', sql],
            env=docker_env(), stdout=subprocess.PIPE, stderr=subprocess.PIPE,
            text=True, timeout=10)
        if result.returncode:
            raise RuntimeError('Read-only acceptance counts failed; inspect the owning database schema')
        return int(result.stdout.strip())
    return {
        'incidentCount': query('rateldesk', 'SELECT count(*) FROM "Tickets" WHERE "Discriminator"=\'Incident\''),
        'receiptCount': query('rateldesk', 'SELECT count(*) FROM "IncidentCreateReceipts"'),
        'taskCount': query('rateldesk', 'SELECT count(*) FROM "Tickets" WHERE "Discriminator"=\'RequestTask\''),
        'jobRunCount': query('netratel', 'SELECT count(*) FROM "JobRuns"'),
    }


def start_proxy(state):
    helper = Path(__file__).with_name('business_pair.js')
    with (FIXTURE / 'incident-capture-proxy.log').open('ab') as output:
        process = subprocess.Popen(['node', str(helper), 'proxy'], env=tool_env(),
            stdout=output, stderr=subprocess.STDOUT, start_new_session=True)
    state.setdefault('helperPids', {})['incidentProxy'] = process.pid
    private_json(STATE, state)
    wait_host(state['products']['rateldesk']['proxyApiOrigin'] + '/__fixture/health',
        process, 'Owned business acceptance proxy')
    print('Owned loopback business acceptance proxy healthy', flush=True)


def start():
    if STATE.exists():
        raise RuntimeError('A fixture already exists; preserve its evidence or run cleanup')
    state = create_state()
    environment = docker_env()
    environment['POSTGRES_PASSWORD'] = state['databasePassword']
    command(DOCKER + ['run', '--detach', '--rm', '--name', state['container'],
        '-e', 'POSTGRES_PASSWORD', '-p', f"127.0.0.1:{state['databasePort']}:5432",
        'postgres:16-alpine'], environment=environment, log_name='postgres-start.log')
    deadline = time.monotonic() + 30
    while time.monotonic() < deadline:
        result = subprocess.run(DOCKER + ['exec', state['container'], 'pg_isready',
            '-h', '127.0.0.1', '-U', 'postgres'], env=docker_env(),
            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        if result.returncode == 0:
            break
        time.sleep(.5)
    else:
        raise RuntimeError('Disposable PostgreSQL did not become healthy')
    for product, entry in state['products'].items():
        command(DOCKER + ['exec', state['container'], 'createdb', '-U', 'postgres', entry['database']],
            environment=docker_env(), log_name='database-create.log')
        if product == 'netratel':
            command(['openssl', 'ecparam', '-name', 'prime256v1', '-genkey', '-noout',
                '-out', str(FIXTURE / product / 'keys' / 'agent-es256.pem')],
                log_name='agent-key-generate.log')
            migration = REPOS[product] / 'src/NetRatel/NetRatel.Migrations/bin/Debug/net10.0/NetRatel.Migrations.dll'
            command([str(DOTNET), str(migration)], environment=environment_for(state, product, 'api'),
                cwd=migration.parent, log_name='netratel-migrate.log')
        dll = publish_runtime(product, 'api')
        bootstrap_environment = environment_for(state, product, 'api')
        bootstrap_environment['ASPNETCORE_CONTENTROOT'] = str(dll.parent)
        command([str(DOTNET), str(dll), '--initialize-unattended'],
            environment=bootstrap_environment, cwd=dll.parent,
            log_name=product + '-initialize.log')
        start_product(state, product)
    start_proxy(state)
    print('Actual native two-source product fixture started; no pairing acceptance claimed', flush=True)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('action', choices=['start', 'stop', 'restart', 'proxy', 'cleanup', 'counts'])
    parser.add_argument('product', nargs='?', choices=list(REPOS))
    parser.add_argument('--purge', action='store_true', help='Remove private credentials after owned resources stop')
    args = parser.parse_args()
    if args.action == 'start':
        start()
        return
    if not STATE.exists():
        return
    state = json.loads(STATE.read_text())
    if args.action == 'proxy':
        start_proxy(state)
    elif args.action == 'counts':
        print(json.dumps(counts(state)))
    elif args.action in ['stop', 'restart']:
        if not args.product:
            raise RuntimeError('Choose a fixture product')
        stop_product(state, args.product)
        if args.action == 'restart':
            start_product(state, args.product)
    else:
        cleanup(state, args.purge)


if __name__ == '__main__':
    try:
        main()
    except (RuntimeError, subprocess.TimeoutExpired) as error:
        print(str(error))
        raise SystemExit(1)
