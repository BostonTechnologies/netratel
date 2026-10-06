"""Candidate full production Client process, private installation and actual TLS.

Requires an already-built exact-current CI image. This module never builds,
pulls, publishes, updates, or removes an image and never starts the reduced
ServiceLink.NativeRunner in place of the full production telemetry producer.
"""
from __future__ import annotations

import hashlib
import json
import os
import pathlib
import re
import shutil
import ssl
import subprocess
import threading
import time
import uuid
from urllib.parse import urlsplit

from owned_loop_volume import FixturePreconditionError, OwnedLoopVolume, private_json


PROBE = '''import hashlib,json,os,pathlib,socket,ssl,sys
cfg=json.loads(pathlib.Path('/fixture/probe.json').read_text())
volume=pathlib.Path('/netratel-physical-disk')
volume_stat=volume.stat()
assert [os.major(volume_stat.st_dev),os.minor(volume_stat.st_dev)]==cfg['volumeDevice']
assert (volume/'.fixture-owner').read_text(encoding='ascii')==cfg['volumeOwner']
assert volume_stat.st_dev!=pathlib.Path('/').stat().st_dev
mounts=[line.split() for line in pathlib.Path('/proc/self/mountinfo').read_text().splitlines()]
assert any(parts[4]=='/netratel-physical-disk' and parts[parts.index('-')+1]=='ext4' for parts in mounts)
observed=os.statvfs(volume)
assert observed.f_bavail*observed.f_frsize==cfg['volumeAvailableBytes']
assert observed.f_blocks*observed.f_frsize==cfg['volumeTotalBytes']
manifest=json.loads(pathlib.Path('/app/netratel-client-manifest.json').read_text(encoding='utf-8-sig'))
assert manifest['schema']=='netratel.client.manifest.v1'
assert manifest['product']=='NetRatel.Client'
assert manifest['commitSha']==cfg['sourceSha'] and manifest['version']==cfg['version']
assert manifest['runtimeId']=='linux-x64' and manifest['executable']=='NetRatel.Client'
assert hashlib.sha256(pathlib.Path('/app/NetRatel.Client').read_bytes()).hexdigest()==cfg['executableSha256']
ctx=ssl.create_default_context(cafile='/fixture/trusted-ca.pem' if cfg['trusted'] else None)
ctx.set_alpn_protocols(['h2'])
try:
    with socket.create_connection((cfg['host'],cfg['port']),timeout=10) as raw:
        with ctx.wrap_socket(raw,server_hostname=cfg['host']) as secure:
            assert secure.selected_alpn_protocol()=='h2'
except ssl.SSLCertVerificationError:
    if cfg['trusted']: sys.exit(43)
    print('production-image-unrelated-ca-rejected'); sys.exit(42)
if not cfg['trusted']: sys.exit(43)
print('production-image-tls-alpn-h2-verified')
'''


class ProductionClientContainer:
    """Owns one full Client plus private identity, and records only safe facts.

    Run inside the future root-capable CI orchestration process. Host networking
    connects to the existing loopback-only real HTTPS/H2 listener; the process
    uses UID10001 with no capabilities, no Docker socket and no host identity.
    """

    def __init__(self, volume: OwnedLoopVolume, image_id: str, source_sha: str,
                 version: str, executable_sha256: str, trusted_ca_pem: str,
                 api_base_url: str, gateway_endpoint: str, enrollment_code: str):
        if not re.fullmatch(r"sha256:[0-9a-f]{64}", image_id) or not re.fullmatch(r"[0-9a-f]{40}", source_sha) or not re.fullmatch(r"[0-9a-f]{64}", executable_sha256):
            raise FixturePreconditionError("exact-image-source-executable-identities-required")
        api = urlsplit(api_base_url)
        gateway = urlsplit(gateway_endpoint)
        if api.scheme not in ("http", "https") or api.username or api.password or api.query or api.fragment:
            raise FixturePreconditionError("invalid-actual-rest-endpoint")
        if gateway.scheme != "https" or gateway.hostname != "127.0.0.1" or not gateway.port or gateway.username or gateway.password or gateway.query or gateway.fragment:
            raise FixturePreconditionError("actual-loopback-https-h2-listener-required")
        if not enrollment_code or len(enrollment_code) > 4096 or len(trusted_ca_pem) > 8192:
            raise FixturePreconditionError("bounded-private-enrollment-and-ca-required")
        self.volume, self.image_id, self.source_sha = volume, image_id, source_sha
        self.version, self.executable_sha256 = version, executable_sha256
        self.name = "netratel-physical-client-" + uuid.uuid4().hex
        self.root = volume.root / ("client-" + uuid.uuid4().hex)
        self.state = self.root / "state"
        self.settings = self.root / "clientsettings.json"
        self.ca = self.root / "trusted-ca.pem"
        self.probe_file = self.root / "trust_probe.py"
        self.probe_config = self.root / "probe.json"
        self.created_ids: list[str] = []
        self.pending_names: set[str] = set()
        self.main_id: str | None = None
        self.started_at: float | None = None
        self.closed = False
        self.command_marker = "physical-command-" + uuid.uuid4().hex
        self.release_file = "/var/lib/netratel/physical-command-release-" + uuid.uuid4().hex
        self.lifetime_expired = False
        self.watchdog: threading.Timer | None = None
        self.proof = {"sourceSha": source_sha, "clientImageId": image_id, "clientVersion": version,
                      "clientExecutableSha256": executable_sha256,
                      "caSha256": hashlib.sha256(ssl.PEM_cert_to_DER_cert(trusted_ca_pem)).hexdigest(),
                      "producer": "full-production-NetRatel.Client", "slowCollectionSeconds": 5,
                      "maximumClientLifetimeSeconds": 300, "credentialsExported": False, "commandMarker": self.command_marker,
                      "releaseFile": self.release_file,
                      "command": "for i in $(seq 1 25); do test -f "+self.release_file+" && { printf '%s\n' "+self.command_marker+"; exit 0; }; sleep 1; done; exit 91"}
        self._private_input = (api_base_url, gateway_endpoint, enrollment_code, trusted_ca_pem)

    def docker(self, arguments: list[str], timeout: int = 30) -> str:
        docker_env = os.environ.copy()
        for name in ("DOCKER_HOST", "DOCKER_CONTEXT", "DOCKER_TLS", "DOCKER_TLS_VERIFY", "DOCKER_CERT_PATH"):
            docker_env.pop(name, None)
        result = subprocess.run(["docker", "--host=unix:///var/run/docker.sock", *arguments],
                                capture_output=True, timeout=timeout, check=False, env=docker_env)
        if result.returncode or len(result.stdout) > 65536:
            raise FixturePreconditionError("owned-client-docker-command-failed:" + arguments[0])
        return result.stdout.decode("utf-8", "strict").strip()

    def private_file(self, path: pathlib.Path, data: str) -> None:
        fd = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o400)
        with os.fdopen(fd, "w", encoding="utf-8") as handle:
            handle.write(data)
            handle.flush()
            os.fsync(handle.fileno())
        os.chown(path, 10001, 10001)

    def inspect_image(self) -> None:
        rows = json.loads(self.docker(["image", "inspect", self.image_id]))
        if len(rows) != 1 or rows[0]["Id"] != self.image_id or rows[0]["Os"] != "linux" or rows[0]["Architecture"] != "amd64":
            raise FixturePreconditionError("same-current-ci-linux-amd64-client-image-required")
        config = rows[0]["Config"]
        labels = config.get("Labels") or {}
        if labels.get("org.opencontainers.image.revision") != self.source_sha or labels.get("org.opencontainers.image.version") != self.version:
            raise FixturePreconditionError("current-client-image-source-version-labels-not-proven")
        if config.get("Entrypoint") != ["/app/NetRatel.Client"] or config.get("User") not in ("netratel", "10001", "10001:10001"):
            raise FixturePreconditionError("production-client-entrypoint-user-not-preserved")

    def create(self, extra: list[str], probe: bool) -> str:
        mounts = ["--mount", f"type=bind,source={self.root},target=/fixture,readonly",
                  "--mount", f"type=bind,source={self.volume.mount},target=/netratel-physical-disk,readonly"]
        if not probe:
            mounts += ["--mount", f"type=bind,source={self.state},target=/var/lib/netratel",
                       "--mount", f"type=bind,source={self.settings},target=/app/clientsettings.json,readonly"]
        name = self.name + ("-probe-" + uuid.uuid4().hex if probe else "")
        self.pending_names.add(name)
        created = self.docker(["create", "--name", name, "--label", "netratel.fixture.owner=" + self.volume.owner,
                               "--network", "host", "--user", "10001:10001", "--cap-drop", "ALL",
                               "--security-opt", "no-new-privileges", "--pids-limit", "128", *mounts, *extra,
                               self.image_id, *( ["/fixture/trust_probe.py"] if probe else ["--service"] )])
        if not re.fullmatch(r"[0-9a-f]{64}", created):
            raise FixturePreconditionError("owned-client-create-did-not-return-exact-container-id")
        self.created_ids.append(created)
        self.pending_names.remove(name)
        self.volume.container_users.add(created)
        return created

    def start(self) -> None:
        try:
            self.volume.assert_owned()
            self.inspect_image()
            self.root.mkdir(mode=0o700)
            os.chown(self.root, 10001, 10001)
            self.state.mkdir(mode=0o700)
            os.chown(self.state, 10001, 10001)
            api, endpoint, code, ca = self._private_input
            self.private_file(self.settings, json.dumps({"Client": {"ApiBaseUrl": api, "EnrollmentCode": code, "StateDirectory": "/var/lib/netratel"},
                                                        "Gateway": {"Endpoint": endpoint, "TelemetrySlowIntervalSeconds": 5}}))
            self.private_file(self.ca, ca)
            self.private_file(self.probe_file, PROBE)
            address = urlsplit(endpoint)
            for trusted, expected in ((True, (0, "production-image-tls-alpn-h2-verified")),
                                      (False, (42, "production-image-unrelated-ca-rejected"))):
                private_json(self.probe_config, {"trusted": trusted, "host": address.hostname, "port": address.port,
                                                "sourceSha": self.source_sha, "version": self.version,
                                                "executableSha256": self.executable_sha256,
                                                "volumeDevice": [os.major(self.volume.mount.stat().st_dev), os.minor(self.volume.mount.stat().st_dev)],
                                                "volumeOwner": self.volume.owner,
                                                "volumeAvailableBytes": os.statvfs(self.volume.mount).f_bavail * os.statvfs(self.volume.mount).f_frsize,
                                                "volumeTotalBytes": os.statvfs(self.volume.mount).f_blocks * os.statvfs(self.volume.mount).f_frsize})
                os.chown(self.probe_config, 10001, 10001)
                os.chmod(self.probe_config, 0o400)
                probe = self.create(["--entrypoint", "python3"], probe=True)
                self.docker(["start", probe])
                status = int(self.docker(["wait", probe], timeout=15))
                output = self.docker(["logs", probe])
                if (status, output) != expected:
                    raise FixturePreconditionError("actual-production-image-tls-trust-probe-failed")
                self.proof["trustedTlsAlpnVerified" if trusted else "unrelatedCaRejected"] = True
                self.proof["daemonVisibleOwnedExt4Verified"] = True
                self.remove(probe)
            # This CA is process-local and this enrollment code never appears in argv/env/log exports.
            self.main_id = self.create(["--env", "SSL_CERT_FILE=/fixture/trusted-ca.pem",
                                       "--env", "NetRatel_CLIENT_LOG_DIR=/var/lib/netratel/logs"], probe=False)
            self.docker(["start", self.main_id])
            self.started_at = time.monotonic()
            self.watchdog = threading.Timer(300, self.stop_at_deadline)
            self.watchdog.daemon = True
            self.watchdog.start()
            self._private_input = ()
        except BaseException:
            self.close()
            raise

    def assert_alive(self) -> None:
        if not self.main_id or self.started_at is None:
            raise FixturePreconditionError("full-production-client-not-started")
        if self.lifetime_expired or time.monotonic() - self.started_at > 300:
            raise FixturePreconditionError("fixed-actual-native-client-lifetime-exceeded")
        row = json.loads(self.docker(["inspect", self.main_id]))[0]
        if not row["State"]["Running"] or row["Config"]["Image"] != self.image_id:
            raise FixturePreconditionError("full-production-client-exited-or-image-identity-changed")

    def release_command(self) -> dict:
        self.assert_alive()
        # Create only the disposable command gate after the real durable ACK.
        # The production executor, not docker exec, runs the recorded command.
        self.docker(["exec", "--user", "10001:10001", self.main_id, "python3", "-c",
                     "import os,sys; f=os.open(sys.argv[1],os.O_WRONLY|os.O_CREAT|os.O_EXCL,0o600); os.close(f)",
                     self.release_file])
        return {"ownedCommandGateReleased": True, "commandMarker": self.command_marker}

    def remove(self, container: str) -> None:
        rows = json.loads(self.docker(["inspect", container]))
        if len(rows) != 1 or rows[0]["Id"] != container or rows[0]["Config"]["Image"] != self.image_id or (rows[0]["Config"].get("Labels") or {}).get("netratel.fixture.owner") != self.volume.owner:
            raise FixturePreconditionError("owned-client-container-identity-changed")
        if rows[0]["State"]["Running"]:
            self.docker(["stop", "--time", "8", container], timeout=15)
        self.docker(["rm", container], timeout=15)  # No force, image deletion or fleet operation.
        self.created_ids.remove(container)
        self.volume.container_users.remove(container)

    def stop_at_deadline(self) -> None:
        self.lifetime_expired = True
        if self.main_id in self.created_ids:
            try:
                self.docker(["stop", "--time", "8", self.main_id], timeout=15)
            except BaseException:
                # The caller fails on lifetime_expired; normal bounded cleanup
                # must still prove the exact owned container stopped and removed.
                pass

    def close(self) -> None:
        if self.closed:
            return
        if self.watchdog:
            self.watchdog.cancel()
            self.watchdog.join(timeout=16)
            if self.watchdog.is_alive():
                raise FixturePreconditionError("owned-client-deadline-stop-still-active")
        failures = []
        # A timed-out create may have committed in the daemon. Reconcile only
        # the exact new UUID names before reporting cleanup, never all containers.
        for name in list(self.pending_names):
            try:
                ids = self.docker(["ps", "--all", "--no-trunc", "--filter", "name=^/" + name + "$", "--format", "{{.ID}}"]).splitlines()
                if len(ids) > 1 or any(not re.fullmatch(r"[0-9a-f]{64}", value) for value in ids):
                    raise FixturePreconditionError("ambiguous-owned-create-reconciliation")
                for value in ids:
                    if value not in self.created_ids:
                        self.created_ids.append(value)
                        self.volume.container_users.add(value)
                self.pending_names.remove(name)
            except BaseException as error:
                failures.append(type(error).__name__)
        for container in list(reversed(self.created_ids)):
            try:
                self.remove(container)
            except BaseException as error:
                failures.append(type(error).__name__)
        if failures:
            raise FixturePreconditionError("bounded-owned-client-cleanup-incomplete:" + ",".join(failures))
        # Keep no credential/identity/private arbitrary process diagnostics after owned teardown.
        if self.root.exists():
            shutil.rmtree(self.root)
        self._private_input = ()
        self.closed = True
