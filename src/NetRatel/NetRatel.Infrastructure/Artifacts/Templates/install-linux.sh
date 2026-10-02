#!/usr/bin/env bash
set -euo pipefail
umask 077

# The renderer supplies complete shell literals, or explicit seed environment inputs.
API_BASE=@@API_BASE_URL@@
GATEWAY_ENDPOINT=@@GATEWAY_ENDPOINT@@
TENANT_ID=@@TENANT_ID@@
ENROLLMENT_CODE=@@ENROLLMENT_CODE@@
RUNTIME=@@RUNTIME_ID@@
VERSION=@@ARTIFACT_VERSION@@
EXPECTED_SHA=@@ARTIFACT_SHA256@@
VALID_TO=@@VALID_TO_UTC@@
SERVICE_MODE=@@INSTALL_AS_SERVICE@@
SILENT=@@SILENT_INSTALL@@

fail() { printf '%s\n' "$*" >&2; exit 1; }
phase() { printf 'Phase: %s\n' "$*"; }
if [ "$SERVICE_MODE" = true ] && [ "$EUID" -ne 0 ] && [ "${NetRatel_TEST_ALLOW_NONROOT:-false}" != true ]; then
  fail 'Run the systemd installer as root, or request a non-service installer.'
fi
for command_name in curl python3 flock mktemp; do
  command -v "$command_name" >/dev/null || fail "Required command is unavailable: $command_name"
done
if [ "$SERVICE_MODE" = true ]; then
  for command_name in systemctl timeout journalctl; do
    command -v "$command_name" >/dev/null || fail "Required command is unavailable: $command_name"
  done
fi

TMP_DIR=$(mktemp -d)
STAGE_DIR=''
TXN_DIR=''
TARGET_DIR=''
MUTATED=false
TARGET_INSTALLED=false
TARGET_BACKED_UP=false
SERVICE_STOPPED=false
SERVICE_WAS_ACTIVE=false
SERVICE_WAS_ENABLED=false
SUCCESS=false

journal_tail() {
  printf '%s\n' 'Recent netratel-client journal (credential lines redacted):' >&2
  journalctl -u netratel-client.service -n 60 --no-pager 2>/dev/null | python3 -c '
import re, sys
secret = re.compile(r"bearer|token|secret|password|enroll|authorization|capability|grant|private.?key", re.I)
for line in sys.stdin:
    print("[redacted credential line]" if secret.search(line) else re.sub(r"(https?://[^/\s?#]+)[^\s]*", r"\1/[redacted path]", line.rstrip()))
' >&2 || true
}
inactive() {
  local status=0
  systemctl is-active --quiet netratel-client.service || status=$?
  [ "$status" -eq 3 ]
}
restore_file() {
  local destination="$1" name="$2"
  if [ -f "$TXN_DIR/$name" ]; then
    cp -p -- "$TXN_DIR/$name" "$destination"
  else
    rm -f -- "$destination"
  fi
}
finish() {
  local status=$? rollback_failed=false
  trap - EXIT
  if [ "$SUCCESS" != true ] && [ "$MUTATED" = true ]; then
    journal_tail
    printf '%s\n' 'Local activation failed; restoring the previous installation.' >&2
    if [ "$SERVICE_MODE" = true ]; then
      if ! timeout --foreground 60s systemctl stop netratel-client.service || ! inactive; then
        printf '%s\n' "Rollback cannot replace a package while the service is active or its status is unknown. Recovery files: $TXN_DIR" >&2
        exit 70
      fi
    fi
    if [ "$TARGET_INSTALLED" = true ]; then rm -rf -- "$TARGET_DIR" || rollback_failed=true; fi
    if [ "$TARGET_BACKED_UP" = true ]; then mv -- "$TXN_DIR/package" "$TARGET_DIR" || rollback_failed=true; fi
    if [ "$SERVICE_MODE" = true ]; then
      if [ -n "$PREVIOUS_LINK" ]; then
        ln -s -- "$PREVIOUS_LINK" "$TXN_DIR/current" && mv -Tf -- "$TXN_DIR/current" "$ROOT_DIR/current" || rollback_failed=true
      else
        rm -f -- "$ROOT_DIR/current" || rollback_failed=true
      fi
      restore_file "$CLIENT_UNIT" client.service || rollback_failed=true
      restore_file "$UPDATE_UNIT" update.service || rollback_failed=true
      restore_file "$LAUNCHER" launcher.sh || rollback_failed=true
      restore_file "$UPDATER" updater.sh || rollback_failed=true
      systemctl daemon-reload || rollback_failed=true
      if [ "$SERVICE_WAS_ENABLED" != true ]; then systemctl disable netratel-client.service || rollback_failed=true; fi
    fi
    if [ "$rollback_failed" = false ]; then printf '%s\n' 'The previous installation was restored.' >&2; fi
  fi
  if [ "$SUCCESS" != true ] && [ "$SERVICE_STOPPED" = true ] && [ "$SERVICE_WAS_ACTIVE" = true ]; then
    if ! timeout --foreground 60s systemctl start netratel-client.service || ! systemctl is-active --quiet netratel-client.service; then
      rollback_failed=true
      printf '%s\n' 'The previous client service could not be restarted.' >&2
      journal_tail
    fi
  fi
  if [ -n "$STAGE_DIR" ]; then rm -rf -- "$STAGE_DIR"; fi
  rm -rf -- "$TMP_DIR"
  if [ "$rollback_failed" = true ]; then
    printf 'Rollback is incomplete. Recovery files: %s\n' "$TXN_DIR" >&2
    exit 70
  fi
  if [ -n "$TXN_DIR" ]; then rm -rf -- "$TXN_DIR"; fi
  exit "$status"
}
trap finish EXIT

phase 'validating request and existing installation'
export API_BASE GATEWAY_ENDPOINT TENANT_ID ENROLLMENT_CODE RUNTIME VERSION EXPECTED_SHA VALID_TO SERVICE_MODE
python3 - "$TMP_DIR/config.json" <<'PY'
import json, os, re, shlex, stat, subprocess, sys
from urllib.parse import urlsplit

def reject(message):
    raise SystemExit(message)

def endpoint_origin(value, gateway=False):
    url = urlsplit(value)
    allowed_paths = ('', '/') if gateway else ('', '/', '/api', '/api/')
    if url.scheme not in (('https',) if gateway else ('http', 'https')) or not url.netloc or url.username or url.password or url.query or url.fragment or url.path not in allowed_paths:
        reject('An existing endpoint override is not a supported public origin; reconcile it before repair.')
    return url.scheme.lower() + '://' + url.netloc.lower()

def path(value, label):
    if not value or not os.path.isabs(value) or any(c in value for c in '\r\n\x00%'):
        reject(label + ' must be an absolute path without control characters or systemd specifiers.')
    return os.path.normpath(value)

def trusted(value, directory=False):
    # Check existing components before creating anything; never follow replacement links.
    current = '/'
    for part in value.strip('/').split('/'):
        current = os.path.join(current, part)
        if not os.path.lexists(current):
            continue
        info = os.lstat(current)
        if stat.S_ISLNK(info.st_mode) or info.st_uid not in (0, os.geteuid()):
            reject('Untrusted owner or symlink in managed path: ' + current)
        if info.st_mode & 0o022 and not (stat.S_ISDIR(info.st_mode) and info.st_mode & stat.S_ISVTX):
            reject('Managed path is writable by another identity: ' + current)
        if current != value and not stat.S_ISDIR(info.st_mode):
            reject('Managed path ancestor is not a directory: ' + current)
    if os.path.lexists(value):
        kind = os.lstat(value).st_mode
        if not (stat.S_ISDIR(kind) if directory else stat.S_ISREG(kind)):
            reject('Managed path has an unsupported file type: ' + value)

def query(unit, property_name):
    result = subprocess.run(['systemctl', 'show', '-p', property_name, '--value', unit], capture_output=True, text=True, timeout=10)
    if result.returncode:
        reject('systemctl could not read ' + property_name + ' for ' + unit + '; no installed files were changed.')
    return result.stdout.strip()

def read_unit(filename, environment=None, settings=None):
    environment = {} if environment is None else environment
    settings = {} if settings is None else settings
    lines, environment_files = [], []
    if not filename or not os.path.isfile(filename):
        return environment, settings, lines, environment_files
    trusted(filename)
    section = ''
    allowed = {'ExecStart', 'WorkingDirectory', 'Environment', 'EnvironmentFile', 'User', 'Group', 'Type',
               'Restart', 'RestartSec', 'RestartPreventExitStatus', 'TimeoutStartSec', 'TimeoutStopSec',
               'KillMode', 'StandardOutput', 'StandardError', 'SyslogIdentifier'}
    with open(filename, encoding='utf-8') as stream:
        for raw in stream:
            line = raw.strip()
            if not line or line.startswith(('#', ';')):
                lines.append(raw.rstrip('\n'))
                continue
            if line.startswith('[') and line.endswith(']'):
                section = line[1:-1]
            elif section == 'Service':
                key, separator, value = line.partition('=')
                if not separator or key not in allowed or '\\' in value or '%' in value:
                    reject('Unsupported systemd Service directive or escaping in ' + filename + ': ' + key)
                if key == 'Environment':
                    if not value:
                        reject('Environment reset customization would erase installer settings; reconcile it before repair: ' + filename)
                    for item in shlex.split(value):
                        name, separator, setting = item.partition('=')
                        if not separator or not re.fullmatch(r'[A-Za-z_][A-Za-z0-9_]*', name):
                            reject('Unsupported systemd Environment assignment in ' + filename)
                        environment[name] = setting
                elif key == 'EnvironmentFile':
                    if not value:
                        reject('EnvironmentFile reset customization is unsupported; reconcile it before repair: ' + filename)
                    for item in shlex.split(value):
                        optional = item.startswith('-')
                        source = path(item[1:] if optional else item, 'EnvironmentFile')
                        trusted(source)
                        if not os.path.exists(source) and not optional:
                            reject('Required systemd EnvironmentFile is missing: ' + source)
                        environment_files.append(source)
                else:
                    settings[key] = value
            lines.append(raw.rstrip('\n'))
    if settings.get('User', 'root') not in ('', 'root', '0') or settings.get('Group', 'root') not in ('', 'root', '0'):
        reject('The existing service identity is not root; this installer cannot change its credential identity.')
    if settings.get('Type', 'simple') not in ('simple', 'exec', 'oneshot'):
        reject('Unsupported existing systemd service Type; reconcile it before repair.')
    return environment, settings, lines, environment_files

def effective_unit(unit, filename):
    environment, settings, lines, environment_files = read_unit(filename)
    overridden = {}
    for dropin in shlex.split(query(unit, 'DropInPaths')):
        dropin = path(dropin, 'Systemd drop-in')
        environment, settings, dropin_lines, files = read_unit(dropin, environment, settings)
        for line in dropin_lines:
            if line.strip().startswith('Environment='):
                for item in shlex.split(line.strip().split('=', 1)[1]):
                    name = item.partition('=')[0]
                    if name in environment:
                        overridden[name] = environment[name]
        environment_files.extend(files)
    # systemd applies EnvironmentFile values after inline Environment, in file order.
    for source in environment_files:
        if not os.path.exists(source):
            continue
        with open(source, encoding='utf-8') as stream:
            for raw in stream:
                line = raw.strip()
                if not line or line.startswith(('#', ';')):
                    continue
                name, separator, value = line.partition('=')
                if not separator or not re.fullmatch(r'[A-Za-z_][A-Za-z0-9_]*', name) or '\\' in value:
                    reject('Unsupported EnvironmentFile assignment or continuation in ' + source)
                value = value.strip()
                if value.startswith(('"', "'")):
                    words = shlex.split(value)
                    if len(words) != 1:
                        reject('Unsupported EnvironmentFile quoting in ' + source)
                    value = words[0]
                environment[name] = overridden[name] = value
    # The client accepts prefixed and ordinary .NET environment names. Ordinary
    # names have precedence because their configuration provider is applied last.
    for mapping in (environment, overridden):
        for canonical in ('NetRatelCLIENT__Client__ApiBaseUrl', 'NetRatelCLIENT__Gateway__Endpoint',
                          'NetRatelCLIENT__Client__AutoUpdate__StateDirectory', 'NetRatelCLIENT__Client__AutoUpdate__RequestPath',
                          'NetRatelCLIENT__Client__AutoUpdate__ReadyPath', 'NetRatelCLIENT__Client__AutoUpdate__Mode',
                          'NetRatel_CLIENT_LOG_DIR', 'NetRatel_UPDATE_ROOT', 'NetRatel_UPDATE_STATE', 'NetRatel_UPDATE_REQUEST'):
            short = canonical[len('NetRatelCLIENT__'):] if canonical.startswith('NetRatelCLIENT__') else canonical
            matches = [key for key in mapping if key.lower() == canonical.lower()]
            direct = [key for key in mapping if short != canonical and key.lower() == short.lower()]
            values = {mapping[key] for key in direct or matches}
            if len(values) > 1:
                reject('Ambiguous environment casing for ' + canonical)
            value = next(iter(values), None)
            for key in matches + direct:
                del mapping[key]
            if value is not None:
                mapping[canonical] = value
    return environment, settings, lines, overridden

service = os.environ['SERVICE_MODE'] == 'true'
for name in ('API_BASE', 'GATEWAY_ENDPOINT', 'TENANT_ID', 'ENROLLMENT_CODE', 'RUNTIME', 'VERSION', 'EXPECTED_SHA', 'VALID_TO'):
    if any(c in os.environ[name] for c in '\r\n\x00'):
        reject('Installer input contains a control character: ' + name)
semver = r'(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z.-]+)?(\+[0-9A-Za-z.-]+)?'
if not re.fullmatch(r'[1-9][0-9]*', os.environ['TENANT_ID']) or int(os.environ['TENANT_ID']) > 2147483647:
    reject('Tenant ID is invalid.')
if os.environ['RUNTIME'] not in ('linux-x64', 'linux-arm64') or (os.environ['VERSION'] != 'latest' and not re.fullmatch(semver, os.environ['VERSION'])):
    reject('The requested Linux runtime or version is invalid.')
if not os.environ['ENROLLMENT_CODE'].strip() or (os.environ['EXPECTED_SHA'] and not re.fullmatch(r'[0-9a-fA-F]{64}', os.environ['EXPECTED_SHA'])):
    reject('Enrollment input or expected SHA-256 is invalid.')
for name in ('API_BASE', 'GATEWAY_ENDPOINT'):
    if not os.environ[name] and name == 'GATEWAY_ENDPOINT':
        continue
    url = urlsplit(os.environ[name])
    if url.scheme not in ('http', 'https') or not url.netloc or url.username or url.password or url.query or url.fragment or url.path not in ('', '/'):
        reject(name + ' must be a public origin without credentials, query, or capability path.')
home = os.path.expanduser('~')
root = path(os.environ.get('NetRatel_ROOT') or ('/opt/netratel/client' if service else home + '/.local/share/netratel/client'), 'NetRatel_ROOT')
state = path(os.environ.get('NetRatel_STATE') or ('/var/lib/netratel/update' if service else home + '/.local/state/netratel/update'), 'NetRatel_STATE')
unit_dir = path(os.environ.get('NetRatel_SYSTEMD_UNIT_DIR') or '/etc/systemd/system', 'Systemd unit directory')
environment, settings, lines = {}, {}, []
update_environment, update_lines, overrides = {}, [], {}
if service:
    for name in ('netratel-client.service', 'netratel-update.service'):
        fragment = query(name, 'FragmentPath')
        expected = os.path.join(unit_dir, name)
        if fragment and fragment != expected:
            reject('The existing unit is outside the requested systemd unit directory: ' + name)
    environment, settings, lines, overrides = effective_unit('netratel-client.service', os.path.join(unit_dir, 'netratel-client.service'))
    update_environment, update_settings, update_lines, _ = effective_unit('netratel-update.service', os.path.join(unit_dir, 'netratel-update.service'))
    for key, requested in (('NetRatelCLIENT__Client__ApiBaseUrl', os.environ['API_BASE']), ('NetRatelCLIENT__Gateway__Endpoint', os.environ['GATEWAY_ENDPOINT'])):
        if requested and key in overrides and endpoint_origin(overrides[key], '__Gateway__' in key) != endpoint_origin(requested, '__Gateway__' in key):
            reject('The requested endpoint conflicts with ' + key + ' in an EnvironmentFile or drop-in; reconcile that override before repair.')
    if settings:
        working = shlex.split(settings.get('WorkingDirectory', ''))
        if len(working) != 1 or not working[0].endswith('/current'):
            reject('The existing service does not use the supported NetRatel current layout.')
        registered_root = path(working[0][:-len('/current')], 'Existing service root')
        if os.environ.get('NetRatel_ROOT') and root != registered_root:
            reject('NetRatel_ROOT conflicts with the existing service root.')
        root = registered_root
        command = shlex.split(settings.get('ExecStart', ''))
        if command not in ([root + '/netratel-client-start.sh'], [root + '/current/NetRatel.Client', '--service']):
            reject('The existing ExecStart is outside the supported NetRatel package layout.')
    if update_environment.get('NetRatel_UPDATE_ROOT', root) != root:
        reject('The updater root conflicts with the client service root.')
    if update_settings and shlex.split(update_settings.get('ExecStart', '')) != [root + '/updater/netratel-update.sh']:
        reject('The existing updater ExecStart is outside the owned layout.')
else:
    unit = os.path.join(unit_dir, 'netratel-client.service')
    if os.path.isfile(unit):
        with open(unit, encoding='utf-8') as stream:
            for line in stream:
                if line.strip().startswith('WorkingDirectory=') and shlex.split(line.strip().split('=', 1)[1]) == [root + '/current']:
                    reject('This package is owned by a systemd service; request its service repair installer.')
for value in (root, root + '/versions', root + '/staging', root + '/updater') + ((unit_dir,) if service else ()):
    trusted(value, directory=True)
for value in (root + '/netratel-client-start.sh', root + '/updater/netratel-update.sh'):
    trusted(value)
current, previous, link = root + '/current', '', ''
if os.path.lexists(current):
    if not os.path.islink(current):
        reject('The managed current path is not a symlink.')
    link = os.readlink(current)
    previous = os.path.realpath(current)
    if os.path.dirname(previous) != root + '/versions' or not re.fullmatch(semver, os.path.basename(previous)):
        reject('The current symlink is outside the owned version layout.')
if not service and not previous and os.environ['VERSION'] != 'latest':
    existing = root + '/versions/' + os.environ['VERSION']
    if os.path.lexists(existing):
        previous = existing
if previous:
    trusted(previous, directory=True)
    for name in ('NetRatel.Client', 'netratel-client-manifest.json', 'clientsettings.json'):
        trusted(previous + '/' + name)
    try:
        with open(previous + '/netratel-client-manifest.json', encoding='utf-8-sig') as stream:
            manifest = json.load(stream)
    except (OSError, ValueError):
        reject('The current package has no valid manifest; repair its owned layout first.')
    if manifest.get('product') != 'NetRatel.Client' or manifest.get('version') != os.path.basename(previous) or manifest.get('runtimeId') != os.environ['RUNTIME'] or manifest.get('executable') != 'NetRatel.Client':
        reject('The current manifest does not identify the expected NetRatel package.')
auto_update = {}
if previous:
    for name in ('appsettings.json', 'clientsettings.json'):
        filename = previous + '/' + name
        trusted(filename)
        if os.path.isfile(filename):
            with open(filename, encoding='utf-8-sig') as stream:
                data = json.load(stream)
            client = next((v for k, v in data.items() if k.lower() == 'client'), data)
            configured = next((v for k, v in client.items() if k.lower() == 'autoupdate'), {})
            auto_update.update({k.lower(): v for k, v in configured.items()})
client_state = environment.get('NetRatelCLIENT__Client__AutoUpdate__StateDirectory') or auto_update.get('statedirectory')
states = {v for v in (client_state, update_environment.get('NetRatel_UPDATE_STATE')) if v}
if len(states) > 1 or (os.environ.get('NetRatel_STATE') and states and state not in states):
    reject('The requested, client, and updater state directories conflict.')
state = path(next(iter(states), state), 'Update state directory')
request = path(environment.get('NetRatelCLIENT__Client__AutoUpdate__RequestPath') or auto_update.get('requestpath') or update_environment.get('NetRatel_UPDATE_REQUEST') or state + '/request.json', 'Update request path')
if update_environment.get('NetRatel_UPDATE_REQUEST', request) != request:
    reject('The client and updater request paths conflict.')
ready = path(environment.get('NetRatelCLIENT__Client__AutoUpdate__ReadyPath') or auto_update.get('readypath') or state + '/ready.json', 'Update ready path')
if service and auto_update.get('mode'):
    environment.setdefault('NetRatelCLIENT__Client__AutoUpdate__Mode', auto_update['mode'])
if request == ready or root == '/' or state == '/' or unit_dir == '/':
    reject('Managed installation paths overlap or target the filesystem root.')
for managed in (root + '/versions', root + '/staging', root + '/updater'):
    if any(os.path.commonpath((managed, value)) == managed for value in (state, request, ready)):
        reject('Updater state/request/ready paths must be outside replaceable package and staging directories.')
trusted(state, directory=True)
for value in (state + '/update.lock', request, ready):
    trusted(value)
credential = '/var/lib/netratel/agent.dat'
if not service and not os.access('/var/lib/netratel' if os.path.isdir('/var/lib/netratel') else '/var/lib', os.W_OK):
    credential = home + '/.local/share/netratel/agent.dat'
trusted(credential)
if service and os.path.isfile(os.path.join(unit_dir, 'netratel-client.service')):
    result = subprocess.run(['systemctl', 'is-active', '--quiet', 'netratel-client.service'])
    if result.returncode not in (0, 3):
        reject('The service state could not be verified; no installed files were changed.')
    active = result.returncode == 0
    result = subprocess.run(['systemctl', 'is-enabled', '--quiet', 'netratel-client.service'])
    if result.returncode not in (0, 1):
        reject('The service enablement state could not be verified.')
    enabled = result.returncode == 0
else:
    active = enabled = False
with open(sys.argv[1], 'w', encoding='utf-8') as stream:
    json.dump(dict(root=root, state=state, unit_dir=unit_dir, request=request, ready=ready, previous=previous,
                   link=link, environment=environment, lines=lines, update_environment=update_environment, update_lines=update_lines,
                   credential=credential, active=active, enabled=enabled), stream)
PY
read_config() { python3 -c 'import json,sys; v=json.load(open(sys.argv[1]))[sys.argv[2]]; print(str(v).lower() if isinstance(v,bool) else v)' "$TMP_DIR/config.json" "$1"; }
ROOT_DIR=$(read_config root)
STATE_DIR=$(read_config state)
SYSTEMD_UNIT_DIR=$(read_config unit_dir)
PREVIOUS_LINK=$(read_config link)
SERVICE_WAS_ACTIVE=$(read_config active)
SERVICE_WAS_ENABLED=$(read_config enabled)
CLIENT_UNIT="$SYSTEMD_UNIT_DIR/netratel-client.service"
UPDATE_UNIT="$SYSTEMD_UNIT_DIR/netratel-update.service"
LAUNCHER="$ROOT_DIR/netratel-client-start.sh"
UPDATER="$ROOT_DIR/updater/netratel-update.sh"
mkdir -p -- "$ROOT_DIR/versions" "$ROOT_DIR/staging" "$ROOT_DIR/updater" "$STATE_DIR"
if [ "$SERVICE_MODE" = true ]; then mkdir -p -- "$SYSTEMD_UNIT_DIR"; fi
exec 9>"$STATE_DIR/update.lock"
flock -n 9 || { printf '%s\n' 'Another NetRatel installer or updater is running.' >&2; exit 75; }
python3 - "$TMP_DIR/config.json" <<'PY'
import json, os, sys
with open(sys.argv[1], encoding='utf-8') as stream:
    config = json.load(stream)
current = config['root'] + '/current'
if (os.readlink(current) if os.path.islink(current) else '') != config['link']:
    raise SystemExit('The installed package changed before the updater lock was acquired; retry the installer.')
PY

phase "downloading requested version $VERSION"
curl --silent --show-error --fail --max-redirs 0 --connect-timeout 15 --max-time 120 \
  -H "X-NetRatel-Tenant-Id: $TENANT_ID" -H "X-NetRatel-Enrollment-Code: $ENROLLMENT_CODE" \
  -D "$TMP_DIR/headers" -o "$TMP_DIR/package" \
  "$API_BASE/api/v1/client-artifacts/$RUNTIME/$VERSION/onboarding-download"
STAGE_DIR=$(mktemp -d "$ROOT_DIR/staging/.candidate.XXXXXX")
phase 'verifying artifact and preparing supported configuration'
python3 - "$TMP_DIR" "$STAGE_DIR" <<'PY'
import hashlib, json, os, re, shlex, shutil, stat, sys, tarfile, zipfile
temporary, stage = sys.argv[1:]
with open(temporary + '/config.json', encoding='utf-8') as stream:
    config = json.load(stream)
def reject(message):
    raise SystemExit(message)
headers = {}
with open(temporary + '/headers', encoding='iso-8859-1') as stream:
    for line in stream:
        if line.startswith('HTTP/'):
            headers = {}
            status = line.split()[1]
        elif ':' in line:
            key, value = line.split(':', 1)
            key = key.lower()
            if key in headers:
                reject('Artifact response has ambiguous duplicate metadata.')
            headers[key] = value.strip()
version = headers.get('x-netratel-artifact-version', '')
sha = headers.get('x-netratel-artifact-sha256', '').lower()
size = headers.get('x-netratel-artifact-size', '')
if (locals().get('status') != '200' or headers.get('x-netratel-artifact-rid') != os.environ['RUNTIME'] or
    not re.fullmatch(r'(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z.-]+)?(\+[0-9A-Za-z.-]+)?', version) or
    (os.environ['VERSION'] != 'latest' and version != os.environ['VERSION']) or not re.fullmatch(r'[0-9a-f]{64}', sha) or
    not re.fullmatch(r'[1-9][0-9]*', size) or (os.environ['EXPECTED_SHA'] and sha != os.environ['EXPECTED_SHA'].lower())):
    reject('Authorized artifact metadata does not match the requested package.')
archive_path = temporary + '/package'
digest = hashlib.sha256()
with open(archive_path, 'rb') as stream:
    for chunk in iter(lambda: stream.read(1024 * 1024), b''):
        digest.update(chunk)
actual_sha = digest.hexdigest()
if os.path.getsize(archive_path) != int(size) or actual_sha != sha:
    reject('Downloaded artifact failed size or SHA-256 verification.')
names = set()
def contained(name, regular, directory):
    normalized = name.rstrip('/')
    if not normalized or '\\' in name or '\x00' in name or name.startswith('/') or re.match(r'^[A-Za-z]:', name) or any(p in ('', '.', '..') for p in normalized.split('/')):
        reject('Archive contains an unsafe path.')
    if normalized.casefold() in names or not (regular or directory):
        reject('Archive contains a duplicate, symlink, or special entry.')
    names.add(normalized.casefold())
    return os.path.join(stage, normalized)
if zipfile.is_zipfile(archive_path):
    with zipfile.ZipFile(archive_path) as archive:
        for item in archive.infolist():
            kind = stat.S_IFMT(item.external_attr >> 16)
            contained(item.filename, not item.is_dir() and kind in (0, stat.S_IFREG), item.is_dir() and kind in (0, stat.S_IFDIR))
        archive.extractall(stage)
else:
    try:
        with tarfile.open(archive_path, 'r:gz') as archive:
            for item in archive.getmembers():
                contained(item.name, item.isfile(), item.isdir())
            # Extract only files/directories; never honor archive ownership, links, or special modes.
            for item in archive.getmembers():
                destination = os.path.join(stage, item.name.rstrip('/'))
                if item.isdir():
                    os.makedirs(destination, mode=0o700, exist_ok=True)
                else:
                    os.makedirs(os.path.dirname(destination), mode=0o700, exist_ok=True)
                    with archive.extractfile(item) as source, open(destination, 'xb') as output:
                        shutil.copyfileobj(source, output)
    except tarfile.TarError:
        reject('The artifact must be a supported ZIP or tar.gz archive.')
if not os.path.isfile(stage + '/netratel-client-manifest.json'):
    wrapper = stage + '/netratel-client-' + os.environ['RUNTIME']
    if os.listdir(stage) != [os.path.basename(wrapper)] or not os.path.isdir(wrapper):
        reject('Artifact must contain the client files at its root or in the supported runtime wrapper.')
    for name in os.listdir(wrapper):
        shutil.move(os.path.join(wrapper, name), os.path.join(stage, name))
    os.rmdir(wrapper)
try:
    with open(stage + '/netratel-client-manifest.json', encoding='utf-8-sig') as stream:
        manifest = json.load(stream)
except (OSError, ValueError):
    reject('Artifact manifest is missing or malformed.')
if manifest.get('schema') != 'netratel.client.manifest.v1' or manifest.get('product') != 'NetRatel.Client' or manifest.get('version') != version or manifest.get('runtimeId') != os.environ['RUNTIME'] or manifest.get('executable') != 'NetRatel.Client' or not re.fullmatch(r'[0-9a-fA-F]{40}', str(manifest.get('commitSha', ''))):
    reject('Artifact manifest does not match its authorized runtime, version, or executable.')
for name in ('NetRatel.Client',) + (('updater/netratel-update.sh',) if os.environ['SERVICE_MODE'] == 'true' else ()):
    filename = stage + '/' + name
    if not os.path.isfile(filename):
        reject('Artifact is missing the required executable: ' + name)
    os.chmod(filename, 0o755)
target = config['root'] + '/versions/' + version
if os.path.lexists(target):
    if os.path.islink(target) or not os.path.isdir(target) or os.stat(target).st_uid != os.geteuid() or os.stat(target).st_mode & 0o022:
        reject('The existing target version has an untrusted type, owner, or mode.')
    for name in ('NetRatel.Client', 'netratel-client-manifest.json', 'clientsettings.json'):
        filename = target + '/' + name
        if os.path.lexists(filename) and (os.path.islink(filename) or not os.path.isfile(filename) or os.stat(filename).st_uid != os.geteuid() or os.stat(filename).st_mode & 0o022):
            reject('The existing target version contains an untrusted owned file.')
    try:
        with open(target + '/netratel-client-manifest.json', encoding='utf-8-sig') as stream:
            installed = json.load(stream)
    except (OSError, ValueError):
        reject('The existing target version has no verified manifest.')
    if installed.get('schema') != 'netratel.client.manifest.v1' or installed.get('product') != 'NetRatel.Client' or installed.get('version') != version or installed.get('runtimeId') != os.environ['RUNTIME'] or installed.get('executable') != 'NetRatel.Client':
        reject('The existing target manifest does not identify the intended package.')
settings_path = (config['previous'] or (target if os.path.isdir(target) else stage)) + '/clientsettings.json'
settings = {}
if os.path.isfile(settings_path):
    with open(settings_path, encoding='utf-8-sig') as stream:
        settings = json.load(stream)
if not isinstance(settings, dict):
    reject('clientsettings.json must contain a JSON object.')
def section(name):
    key = next((key for key in settings if key.lower() == name.lower()), name)
    settings.setdefault(key, {})
    if not isinstance(settings[key], dict):
        reject('Unsupported configuration section: ' + name)
    return settings[key]
def set_value(mapping, name, value):
    for key in list(mapping):
        if key.lower() == name.lower():
            del mapping[key]
    mapping[name] = value
client = settings if any(k.lower() == 'apibaseurl' for k in settings) and not any(k.lower() == 'client' for k in settings) else section('Client')
gateway = section('Gateway')
old_api = next((v for k, v in client.items() if k.lower() == 'apibaseurl'), '')
settings_gateway = next((v for k, v in gateway.items() if k.lower() == 'endpoint'), '')
def origin(value):
    value = str(value).rstrip('/').lower()
    return value[:-4] if value.endswith('/api') else value
if settings_gateway and old_api and origin(settings_gateway) == origin(old_api):
    settings_gateway = ''
    for key in list(gateway):
        if key.lower() == 'endpoint':
            del gateway[key]
set_value(client, 'ApiBaseUrl', os.environ['API_BASE'])
gateway_key = 'NetRatelCLIENT__Gateway__Endpoint'
effective_gateway = os.environ['GATEWAY_ENDPOINT'] or config['environment'].get(gateway_key) or settings_gateway
gateway_source = 'request' if os.environ['GATEWAY_ENDPOINT'] else ('existing service environment' if config['environment'].get(gateway_key) else ('installed settings' if effective_gateway else 'API origin fallback'))
if not effective_gateway and os.path.isfile(stage + '/appsettings.json'):
    with open(stage + '/appsettings.json', encoding='utf-8-sig') as stream:
        defaults = json.load(stream)
    packaged_gateway = defaults.get('Gateway', {}).get('Endpoint', '')
    packaged_api = defaults.get('Client', {}).get('ApiBaseUrl', '')
    if packaged_gateway and origin(packaged_gateway) != origin(packaged_api):
        effective_gateway, gateway_source = packaged_gateway, 'packaged defaults'
if effective_gateway:
    from urllib.parse import urlsplit
    url = urlsplit(effective_gateway)
    if url.scheme != 'https' or not url.netloc or url.username or url.password or url.query or url.fragment or url.path not in ('', '/'):
        reject('The existing gateway override is not a supported public origin; specify GatewayEndpoint explicitly.')
    set_value(gateway, 'Endpoint', effective_gateway)
with open(stage + '/clientsettings.json', 'w', encoding='utf-8') as stream:
    json.dump(settings, stream, indent=2)
if os.environ['SERVICE_MODE'] == 'true' and not os.path.exists(config['credential']):
    with open(stage + '/netratel.enroll.json', 'w', encoding='utf-8') as stream:
        import datetime
        json.dump(dict(schema='netratel.enroll.v1', tenantId=int(os.environ['TENANT_ID']), enrollmentCode=os.environ['ENROLLMENT_CODE'], issuer=os.environ['API_BASE'], createdAtUtc=datetime.datetime.now(datetime.timezone.utc).isoformat(), validToUtc=os.environ['VALID_TO']), stream)
    os.chmod(stage + '/netratel.enroll.json', 0o600)
elif os.path.exists(stage + '/netratel.enroll.json'):
    os.remove(stage + '/netratel.enroll.json')
def quote(value):
    return '"' + value.replace('\\', '\\\\').replace('"', '\\"').replace('%', '%%') + '"'
if os.environ['SERVICE_MODE'] == 'true':
    root = config['root']
    environment = config['environment']
    environment.update({'NetRatelCLIENT__Client__ApiBaseUrl': os.environ['API_BASE'],
                        'NetRatelCLIENT__Client__AutoUpdate__StateDirectory': config['state'],
                        'NetRatelCLIENT__Client__AutoUpdate__RequestPath': config['request'],
                        'NetRatelCLIENT__Client__AutoUpdate__ReadyPath': config['ready']})
    environment.setdefault('NetRatelCLIENT__Client__AutoUpdate__Mode', 'Service')
    environment.setdefault('NetRatel_CLIENT_LOG_DIR', '/var/lib/netratel/logs')
    if effective_gateway:
        environment[gateway_key] = effective_gateway
    lines = config['lines'] or ['[Unit]', 'Description=NetRatel Client', 'After=network-online.target', '[Service]', 'Restart=always', 'RestartPreventExitStatus=78', '[Install]', 'WantedBy=multi-user.target']
    output, in_service = [], False
    for line in lines:
        if line.strip().startswith('['):
            in_service = line.strip() == '[Service]'
            output.append(line)
            if in_service:
                output.extend(['WorkingDirectory=' + quote(root + '/current'), 'ExecStart=' + quote(root + '/netratel-client-start.sh')])
                output.extend('Environment=' + quote(k + '=' + v) for k, v in environment.items())
        elif not (in_service and line.strip().split('=', 1)[0] in ('ExecStart', 'WorkingDirectory', 'Environment')):
            output.append(line)
    with open(temporary + '/client.service', 'w', encoding='utf-8') as stream:
        stream.write('\n'.join(output) + '\n')
    updater_environment = config['update_environment']
    updater_environment.update(NetRatel_UPDATE_ROOT=root, NetRatel_UPDATE_STATE=config['state'], NetRatel_UPDATE_REQUEST=config['request'], NetRatel_CLIENT_SERVICE='netratel-client.service')
    with open(temporary + '/update.service', 'w', encoding='utf-8') as stream:
        in_service = False
        for line in config['update_lines'] or ['[Unit]', 'Description=NetRatel Client Updater', '[Service]', 'Type=oneshot']:
            if line.strip().startswith('['):
                in_service = line.strip() == '[Service]'
                stream.write(line + '\n')
                if in_service:
                    stream.write('ExecStart=' + quote(root + '/updater/netratel-update.sh') + '\n')
                    stream.write('\n'.join('Environment=' + quote(k + '=' + v) for k, v in updater_environment.items()) + '\n')
            elif not (in_service and line.strip().split('=', 1)[0] in ('ExecStart', 'Environment')):
                stream.write(line + '\n')
    with open(temporary + '/launcher.sh', 'w', encoding='utf-8') as stream:
        stream.write('#!/usr/bin/env bash\nset -euo pipefail\nexec ' + shlex.quote(root + '/current/NetRatel.Client') + ' --service\n')
    os.chmod(temporary + '/launcher.sh', 0o755)
with open(temporary + '/version', 'w', encoding='utf-8') as stream:
    stream.write(version)
print('Resolved version: ' + version)
print('API: ' + os.environ['API_BASE'] + ' (request)')
print('Gateway: ' + (effective_gateway or os.environ['API_BASE']) + ' (' + gateway_source + ')')
print('Credential store: ' + config['credential'] + ' (existing identity preserved)')
print('Client logs (configured): ' + config['environment'].get('NetRatel_CLIENT_LOG_DIR', '/var/lib/netratel/logs' if os.environ['SERVICE_MODE'] == 'true' else target + '/logs'))
PY
RESOLVED_VERSION=$(cat "$TMP_DIR/version")
TARGET_DIR="$ROOT_DIR/versions/$RESOLVED_VERSION"
if [ "$SERVICE_MODE" != true ]; then
  phase 'checking or enrolling the user client'
  enrollment_args=(--enroll "$ENROLLMENT_CODE" --api "$API_BASE")
  if [ "$SILENT" = true ]; then enrollment_args+=(--silent); fi
  "$STAGE_DIR/NetRatel.Client" "${enrollment_args[@]}"
fi
TXN_DIR=$(mktemp -d "$ROOT_DIR/staging/.rollback.XXXXXX")
if [ "$SERVICE_MODE" = true ]; then
  for entry in "$CLIENT_UNIT:client.service" "$UPDATE_UNIT:update.service" "$LAUNCHER:launcher.sh" "$UPDATER:updater.sh"; do
    destination=${entry%:*}
    if [ -f "$destination" ]; then cp -p -- "$destination" "$TXN_DIR/${entry##*:}"; fi
  done
  phase 'stopping the owned service before package replacement'
  if [ "$SERVICE_WAS_ACTIVE" = true ]; then
    SERVICE_STOPPED=true
    timeout --foreground 60s systemctl stop netratel-client.service || { journal_tail; fail 'The client service did not stop; no installed files were changed.'; }
    inactive || { journal_tail; fail 'The client service could not be verified stopped; no installed files were changed.'; }
  fi
fi
MUTATED=true
if [ -d "$TARGET_DIR" ]; then mv -- "$TARGET_DIR" "$TXN_DIR/package"; TARGET_BACKED_UP=true; fi
mv -- "$STAGE_DIR" "$TARGET_DIR"
STAGE_DIR=''
TARGET_INSTALLED=true
if [ "$SERVICE_MODE" = true ]; then
  phase 'switching the package and starting the service'
  ln -s -- "$TARGET_DIR" "$TXN_DIR/current"
  mv -Tf -- "$TXN_DIR/current" "$ROOT_DIR/current"
  cp -- "$TMP_DIR/launcher.sh" "$LAUNCHER"
  cp -- "$TARGET_DIR/updater/netratel-update.sh" "$UPDATER"
  chmod 0755 "$LAUNCHER" "$UPDATER"
  cp -- "$TMP_DIR/client.service" "$CLIENT_UNIT"
  cp -- "$TMP_DIR/update.service" "$UPDATE_UNIT"
  chmod 0644 "$CLIENT_UNIT" "$UPDATE_UNIT"
  systemctl daemon-reload
  systemctl enable netratel-client.service
  timeout --foreground 60s systemctl start netratel-client.service
  systemctl is-active --quiet netratel-client.service || fail 'The installed client service is not active.'
  printf 'Service: root, active. Executable: %s/current/NetRatel.Client\n' "$ROOT_DIR"
  printf '%s\n' 'Installation and local service startup completed. Enrollment and online gateway state remain unverified.'
else
  printf 'Client installed at %s. Online gateway state remains unverified.\n' "$TARGET_DIR/NetRatel.Client"
fi
SUCCESS=true
