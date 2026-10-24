# Native Client

For enrolled-client backend outage recovery, native refresh-rotation
compatibility and backend-first canary rollout, see
[Native client outage recovery](CLIENT_OUTAGE_RESILIENCE.md).

Install the Native Client only from an owner-approved release artifact and its
matching `netratel-client-manifest.json`. The release rehearsal verifies the
manifest, required sidecars, and archive integrity; Linux packages additionally
exercise the native PTY helper.

For a first enrollment, obtain a short-lived enrollment code from an authorized
operator and run the packaged executable with the deployment API URL:

```sh
NetRatel.Client --enroll replace-with-short-lived-code --api https://netratel.example.invalid
```

Treat enrollment codes and persisted agent credentials as secrets. Do not copy
them between machines or use an operator OIDC credential in their place. A
Client that has not enrolled exits rather than starting an unauthenticated
service.

## Disposable executors for owner acceptance

Use an independently enrolled disposable Windows VM or separate Windows host
when fixed-native execution is required while existing agents remain protected.
Create it from an image that has never enrolled in NetRatel. A clone containing
an existing installation's credential store also contains that installation's
device identity. Use a fresh Linux VM, or the exact owner-designated Linux
container and its separately registered persistent volume, for Linux cases.
Keep the original tenant/agent bindings, credentials, update intent and runtime
data intact. Register each fixture and its eventual cleanup under
[#304](https://github.com/BostonTechnologies/netratel/issues/304) and the
[owner campaign procedure](../mcp-http/campaign-retest.md).

### Windows identity boundary

The packaged Windows client reads its credential and device-key store from
`%ProgramData%\NetRatel\agent.dat`. Its pending native-refresh exchange is beside
that file. The runtime does not expose a supported command-line or configuration
option selecting a different credential store. Another executable directory,
Windows account, `--agent-id`, log directory or `Client:AutoUpdate:StateDirectory`
does not establish a second installation identity. Do not launch a purported
second agent on a protected host or reset that host's identity to create a test
fixture. Native updater, service and offboarding state also belong to the
installed host; changing only a credential filename would not isolate them.

Open an **administrator PowerShell terminal inside the new disposable Windows
VM**. PowerShell and the Windows service/CIM commands are supplied by Windows.
Before installing, verify that this VM has no existing installation:

```powershell
$ErrorActionPreference = 'Stop'
$dataRoot = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'NetRatel'
$installRoot = Join-Path ([Environment]::GetFolderPath('ProgramFiles')) 'NetRatel\Client'
$existingService = Get-CimInstance Win32_Service -Filter "Name='NetRatel.Client'"
if ($existingService -or (Test-Path -LiteralPath $dataRoot) -or (Test-Path -LiteralPath $installRoot)) {
    throw 'This VM already has NetRatel installation state. Select an unused disposable VM.'
}
```

Select the exact completed release tag from
[GitHub Releases](https://github.com/BostonTechnologies/netratel/releases), retain
its publication record and checksums, and use
[GitHub release import](#release-import-and-instance-automation) to import the
matching verified client pack. Generate a **fresh Windows installer** for the
designated fixture tenant and that pack after the corrected server is deployed.
In **Agent Installers & Updates**, choose **Generate script** on the matching
artifact row. Verify **Selected artifact** shows the expected version and
`win-x64`, choose the fixture tenant, and select **Install as Service**. Use one
enrollment use for this one fixture, then **Generate link** and **Download
script**. Existing generated links retain their original installer snapshot.
Save the script in a private directory in this VM and run its downloaded local
file under the normal script-review and execution-policy process:

```powershell
& 'C:\absolute\private-fixture-install.ps1' # Replace with the fresh generated installer path.
```

Native client archives contain their .NET runtime. Shell execution still needs
the [interpreter appropriate to the test](#script-interpreters); install
[PowerShell 7](https://learn.microsoft.com/powershell/scripting/install/installing-powershell-on-windows)
inside the fixture when testing `Pwsh`. Use only the fixture's fresh short-lived
enrollment grant. Leave instance-wide automatic publication/deployment settings
and protected agents' update policies unchanged. Inspect the resulting distinct
agent ID and tenant before granting exact fixture operations or executing a
task.

### Prove the native executable actually serving the fixture

In the **same VM's administrator PowerShell terminal**, inspect the running
service process, its installed manifest and its executable version. Replace the
release-tag placeholder with the tag selected above:

```powershell
$releaseTag = 'REPLACE_WITH_PUBLISHED_RELEASE_TAG'
$expectedVersion = $releaseTag.TrimStart('v')
$service = Get-CimInstance Win32_Service -Filter "Name='NetRatel.Client'"
if (-not $service -or $service.State -ne 'Running' -or $service.ProcessId -le 0) {
    throw 'The fixture native service is not running.'
}
$clientProcess = Get-CimInstance Win32_Process -Filter "ProcessId=$($service.ProcessId)"
if (-not $clientProcess.ExecutablePath) { throw 'The running fixture executable could not be identified.' }
$clientExecutable = $clientProcess.ExecutablePath
$manifestPath = Join-Path (Split-Path -Parent $clientExecutable) 'netratel-client-manifest.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$nativeVersion = (& $clientExecutable --version | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $nativeVersion.Split('+')[0] -cne $expectedVersion -or
    $manifest.version -cne $expectedVersion -or $manifest.runtimeId -cne 'win-x64') {
    throw 'The running fixture package does not match the selected Windows release.'
}
[ordered]@{
    selectedRelease = $releaseTag
    version = $nativeVersion
    manifestSourceRevision = $manifest.commitSha
    processId = $clientProcess.ProcessId
    startedAtUtc = $clientProcess.CreationDate.ToUniversalTime().ToString('o')
    executable = $clientExecutable
    executableSha256 = (Get-FileHash -LiteralPath $clientExecutable -Algorithm SHA256).Hash
    serviceAccount = $service.StartName
} | ConvertTo-Json
```

Compare the manifest source revision and executable checksum with the matching
verified pack. Import can normalize an archive: use the source archive checksum
for the GitHub download and the imported artifact/manifest receipt for the local
pack. Retain the installation directory unchanged during this readback. If the
service restarts or changes executable during inspection, repeat for its current
PID. In the platform, read the exact fixture's durable tenant/agent binding,
current gateway presence, reported native version and required capability. A
running service or a local `--version` result alone does not establish that the
expected identity connected or that execution was admitted.

For a disposable Linux VM installed through the generated systemd installer,
run these **read-only commands in that VM's administrator shell**. The commands
use the systemd and GNU coreutils supplied by the supported Linux distribution:

```sh
set -eu
fixture_pid=$(systemctl show netratel-client.service --property MainPID --value)
test "$fixture_pid" -gt 0
fixture_executable=$(readlink -e "/proc/$fixture_pid/exe")
printf '%s\n' "$fixture_executable"
"$fixture_executable" --version
sha256sum "$fixture_executable"
cat "$(dirname "$fixture_executable")/netratel-client-manifest.json"
```

Match that executable/manifest to the selected Linux release and the same
fixture's current gateway identity. For an existing registered container,
inspect its exact container ID, image digest and mounted credential volume
before an owner-authorized update; changing an image tag does not prove the
running digest changed. Recover an unavailable runtime with its established
host recovery procedure and preserve the historical volume. A new VM or
container is a new fixture and cannot substitute for recovery or cleanup of
the original one.

Run native secret/service acceptance only after these identity, version and
capability checks and the exact reviewed fixture policies succeed. Windows
service tests require an explicitly designated inert service on the Windows
fixture; retain its initial and final status/start type. Server, bridge and
native-agent versions are separate evidence. Keep fixture cleanup and retained
audit readback in the campaign ledger.

## Client labels and offboarding

On **Clients**, the compact **Edit client** action beside Services edits the
friendly name and comma-separated tags. For example, set `Dispatch Server`
with `Prod, Factory1`. The reported hostname remains read-only and appears
once; an empty friendly name, or one equal to the hostname, does not add a
second name. Tags can appear without a friendly name. Names are limited to
128 characters, with up to 20 tags of 48 characters each; surrounding spaces
and duplicate tags are normalized. These labels belong to the client record
and can be edited while it is offline. Editing and removal require client
management permission for that client's tenant.

Cards and the table display the client's reported network address, supporting
IPv4 or IPv6. This is the native client's selected local interface address,
so a NAT or reverse proxy address need not match it. An omitted address in a
later identity report preserves the last known value, including while the
client is offline; **Not reported** means no valid address has been recorded.
Search includes the friendly name, tags and reported address. Updating Web/API
can expose an address already reported by an existing client; it does not
require that client to support uninstall.

### Choose the removal action

Use **Remove client** to review the tenant, hostname, client ID and last-seen
time before removing a duplicate. A shared hostname does not establish which
enrolled identity is the obsolete record.

| Action | Effect on the computer | When the record disappears |
| --- | --- | --- |
| **Remove from platform** | Leaves installed software in place. Revokes the client's platform access. Available for offline records too. | After the removal is saved. |
| **Uninstall and remove** | An independent native helper stops and removes the managed service, installation files and installation credentials. Client logs are retained. | After the API accepts a successful helper receipt confirming both service and file removal. |

Platform removal retains an auditable tombstone, the operator and the supplied
reason. It revokes access and refresh credentials, invalidates connection
ownership and cancels pending connection admissions in the same database
transaction. The removed identity cannot reconnect or recover enrollment with
its existing device key. Repeating record removal is idempotent.

Uninstall requires an enabled, online client with a current control connection
advertising `uninstall-v1`. Deploying Web/API alone cannot add that capability
to older native binaries: install an approved native build that implements it.
The requested operation is saved before dispatch, and its helper can continue
independently of the service or browser. Going offline is not completion
evidence. A confirmed failure or missing confirmation retains the client record
and its operation status for investigation.

For a pending operation, **Retry request** reuses the same operation identity
while the client is eligible and its confirmation window remains open. The
initial removal must start within its short dispatch deadline; retry can renew
that deadline without replacing the operation. The helper persists its result
and retries delivery, including after a restart, for up to the operation's
24-hour confirmation window. Expiry does not delete the record or prove that
the local installation remains intact. Inspect the computer and the saved
failure reference before taking further action. **Remove record only…** is an
explicit fallback when confirmation remains pending or has expired; it revokes
platform access without waiting for a helper that may still be running.

### Native uninstall eligibility and acceptance

The initial implementation verifies the installed manifest and an owned service
running with the following default layouts:

| Runtime | Service identity | Installation and state |
| --- | --- | --- |
| Windows x64 | `NetRatel.Client` running as LocalSystem | `%ProgramFiles%\NetRatel\Client\versions\<version>`; credentials and update state under `%ProgramData%\NetRatel` |
| Linux x64 | `netratel-client.service` running as root under systemd | `/opt/netratel/client/versions/<version>`; credentials and update state under `/var/lib/netratel`; generated units under `/etc/systemd/system` |

The gateway must use HTTPS. Custom installation roots or updater state
directories, a custom credential machine identity, interactive/user installs,
containers, macOS and other runtime IDs do not advertise this capability.
Unprotected paths, unexpected links, changed device identity or service files,
and unsupported service overrides can prevent preparation or removal. The
helper uses the existing installer/updater lock and verifies ownership before
deleting files; it does not provide a general-purpose remote uninstall command.

**Manual Windows and Linux acceptance is pending.** Before declaring this
capability ready for deployment, validate on disposable machines with the
supported native build:

1. Edit/clear labels, search tags and IPs, and remove an obsolete offline record
   without changing the other enrolled identity.
2. Uninstall an online service; verify the service, installation and credential
   files are gone, then confirm the record is removed only after the receipt.
3. Interrupt receipt delivery and restart the helper host; verify the durable
   operation remains visible and the same result is retried without repeating
   a completed removal.
4. Exercise a changed installation, pending/expired confirmation and explicit
   record-only fallback; verify failures preserve the record and audit trail.

Enhancement tracking: [client labels #190](https://github.com/BostonTechnologies/netratel/issues/190),
[client offboarding #191](https://github.com/BostonTechnologies/netratel/issues/191),
[reported addresses #192](https://github.com/BostonTechnologies/netratel/issues/192).

## Script interpreters

Commands and library scripts run in external processes under the Client's
executing account. PowerShell scripts require an installed interpreter:
`Pwsh` selects `pwsh`, and `WindowsPowerShell` selects Windows PowerShell on
Windows. An explicit selection fails if that interpreter is unavailable.
For a PowerShell script, `Auto` prefers `pwsh` and can fall back to Windows
PowerShell on Windows; Linux and macOS require `pwsh`. General shell commands
retain the platform's existing automatic shell selection.

The Client reports installed shell capabilities and uses the same executable
resolution for command and library-script execution. Service accounts can use
standard Windows interpreter locations even with a restricted `PATH`.
Script `#requires` directives remain enforced by the selected PowerShell
interpreter. The Client does not provision interpreters. A missing interpreter
fails that task while the Client continues accepting other work.

Installed settings may still contain `UseInProcPowerShell`; that retired key
is ignored. Legacy `RunPowerShell` and `ExecPs` dispatches use external
PowerShell while retaining their result array. Canonical command and library
tasks retain their exit-code/stdout/stderr result envelope.

## API and gateway endpoints

Set `Client:ApiBaseUrl` to the public HTTPS origin used for enrollment and HTTP
API calls. The Client uses that same URL for its gRPC gateway when
`Gateway:Endpoint` is absent or empty in an existing/manual configuration.
New public installers require explicit Site URL and Gateway URL values on
Branding, or the API deployment options `Branding__SiteUrl` and
`Branding__GatewayUrl`. Windows, Linux and macOS installers persist both
addresses, including an explicit same-host gateway. For native Client Docker
deployments, set `NetRatelCLIENT__Client__ApiBaseUrl` and
`NetRatelCLIENT__Gateway__Endpoint`. Gateway operations use the configured endpoint. Remote Support
V2 follows the enrolled Client's advertised capabilities and platform; provider
handover remains governed by the `RemoteSupport:Handover` policy. Terminal
support depends on the Client host's available shell/PTY capabilities.

For a shared public host, route the gateway's protobuf service namespace to the
API's private h2c gateway listener. The connection path is:

`Native Client -- HTTPS / HTTP/2 --> reverse proxy -- h2c --> API gateway listener`

Keep the existing Web and REST routers separate. This compact Traefik
file-provider fragment shows only the gateway router and service:

```yaml
http:
  routers:
    netratel-grpc:
      rule: "Host(`netratel.example.com`) && PathPrefix(`/netratel.gateway.v1.`)"
      entryPoints: [websecure]
      priority: 100
      service: netratel-grpc
      tls: {}
  services:
    netratel-grpc:
      loadBalancer:
        servers:
          - url: "h2c://api:9223"
```

The trailing dot in the gRPC `PathPrefix` keeps this router scoped to the
protobuf package. The proxy terminates public TLS and uses h2c only on its
private hop to the API listener. Do not add path rewriting. Set the entrypoint,
TLS certificate configuration, backend address, and router priority for your
deployment. Forwarding this package prefix does not bypass per-RPC
authentication or the API's authenticated-peer, tenant, current-session, and
protocol checks. If the gateway is instead on a dedicated public host, a
Host-only router can send that host to the same h2c listener; set
`Gateway:Endpoint` to its public HTTPS URL, for example
`https://grpc.example.com`.

### Sustained Traefik gateway streams

`Connect` keeps one authenticated HTTP/2 request body open while exchanging
heartbeats and acknowledgments. In Traefik v3.7.13,
`entryPoints.<name>.transport.respondingTimeouts.readTimeout` defaults to 60s
and bounds reading the **entire request body**. A heartbeat every 15 seconds
does not extend that absolute deadline. Admission and a successful short RPC
therefore do not establish that a gateway stream can remain connected.

For an unbounded gateway stream, use a dedicated gateway entrypoint with
`readTimeout: 0s`, as shown in
[`docs/deployment/traefik-gateway-static.yaml`](../deployment/traefik-gateway-static.yaml).
This changes only the conflicting body-read deadline. Merge that fragment into
the operator-owned **static** configuration and restart Traefik; retain the
deployment's other entrypoints, timeouts, trusted forwarded headers, TLS,
network boundaries and authentication controls. The fragment reserves port
8443 for the gateway. Bind and expose it according to the deployment's policy,
and set the native `Gateway:Endpoint`/installer Gateway URL to its actual public
HTTPS origin, for example `https://grpc.example.com:8443`.

An existing streaming entrypoint can retain its validated setting. For a
CLI-managed `websecure` entrypoint the equivalent setting is
`--entryPoints.websecure.transport.respondingTimeouts.readTimeout=0s`.
This is separate from investigating renewal `DataLoss`; neither the proxy change
nor healthy heartbeats establishes the cause of that status. Source
implementation does not alter the live proxy or remove the client
outage-recovery policy.

Attach the gateway router to that listener, keeping the exact namespace and
private h2c service mapping:

```yaml
http:
  routers:
    netratel-grpc:
      rule: "Host(`grpc.example.com`) && PathPrefix(`/netratel.gateway.v1.`)"
      entryPoints: [netratel-gateway]
      priority: 100
      service: netratel-grpc
      tls: {}
  services:
    netratel-grpc:
      loadBalancer:
        servers:
          - url: "h2c://api:9223"
```

A separate hostname on the same `websecure` listener does **not** isolate this
setting: an entrypoint timeout applies to every router on that listener,
including Web and REST. Dynamic router configuration and Docker labels cannot
override the static body-read timeout per gRPC route. A dedicated listener or
gateway-only proxy provides that isolation. Do not apply this reference fragment
as a replacement for the whole static configuration or relax all proxy timeouts.

Review `writeTimeout` independently: a finite value must accommodate the
intended response stream lifetime. `idleTimeout` describes idle keep-alive
connections; upstream `serversTransport.forwardingTimeouts`, HTTP/2 ping
settings and keep-alive GOAWAY policies concern other boundaries. Preserve
those settings unless evidence identifies a separate conflict. The gateway
still enforces agent authentication, tenant/agent/current-session identity,
frame sequence and heartbeat acknowledgment validation. Streaming-compatible
transport does not extend an expiring credential or bypass refresh/readmission.

The generic `tools/ci/tests/traefik-gateway-routing.sh` regression pins
**fixture** Traefik v3.7.13 and keeps the original exact gRPC route, dotted
namespace near-miss, REST fallback and TLS-to-h2c assertions. It sends framed
synthetic heartbeat requests with ACKs every 15s on one request per profile:
the default entrypoint ends near 60s, while changing only `readTimeout` to zero
and a direct h2c control remain open for 195s with no reconnect. The native
authenticated-handler probe is an additional hosted test using that same
fixture; synthetic responders alone do not establish product admission,
credential renewal or presence behavior.

For a supported deployment, record actual proxy/client/API versions and
digests, effective static and dynamic configuration, the intended session
lifetime, token expiry time, and sanitized ACK/session/disconnection evidence
across several former timeout windows. Record reset/GOAWAY direction only when
observed. The fixture version and its reproduction do not identify the
reported deployment's version or prove the cause of its disconnects. Keep
credentials, request/response bodies and capability install URLs out of logs.
See the version-specific
[entrypoint reference](https://github.com/traefik/traefik/blob/v3.7.13/docs/content/reference/install-configuration/entrypoints.md)
and
[upstream transport reference](https://github.com/traefik/traefik/blob/v3.7.13/docs/content/reference/routing-configuration/http/load-balancing/serverstransport.md)
before applying the profile to another version.

Generated install links are immutable snapshots. After deploying an updated
candidate, create a fresh link for validation; an existing link does not pick
up a newer installer script. Never reuse an exposed or consumed enrollment
grant.

The package includes runtime-specific update helpers and a manifest. Preserve
the existing installation identity and rollback material during an update; do
not bypass ordinary downgrade protection or point a Client at an unapproved
feed. Rc.1 is historical. Install an rc.3 Client only from the matching
prerelease asset after verifying its checksum, manifest and supported runtime;
neither prerelease establishes an unattended update channel.

## Release import and instance automation

An instance administrator can review GitHub client packs under **Agent Installers
& Updates → GitHub releases**. The catalogue is a read-only view of official
releases. Import downloads every runtime in a pack, verifies the source
publication record and SHA256 checksums, adapts the archive to the local
manifest format, and activates the complete local pack together. The source
archive SHA256 and local normalized artifact SHA256 can differ; each is checked
at its own boundary. Importing leaves the currently offered update releases
unchanged. The separate **Approve and publish for deployment** action records
the operator and offers the pack only through existing enabled tenant update
policies. Legacy offline upload remains available, but it publishes immediately
after manifest validation and bypasses GitHub publication verification and the
saved automation policy. Use GitHub import when those checks and the
instance-wide automation controls are required.

Automation is instance-wide and starts **off**. Choose 12-hour or 24-hour UTC
checks, then opt in to stable downloads. Prerelease downloads, automatic
publication, and automatic prerelease deployment each require separate choices.
The last attempt, last success, last error and next check are shown in UTC;
**Check now** schedules one check. After a restart, the durable schedule
coalesces missed checks. Only the newest eligible pack per selected channel is
considered. A database lease prevents two API replicas from checking the same
due schedule. Publication rechecks the policy before offering a release, so
turning it off during a download prevents that automatic rollout. Existing
tenant channel settings, disabled releases and suspended agents still apply.
Use the update attempts view to investigate rollout failures. Failed imports
retain their operation status and can be retried from the management page.

The GitHub source is fixed to this repository's official releases. An optional
read-only GitHub token increases API limits; the API instance needs outbound
access to GitHub API and release assets, including through any configured
outbound proxy. Catalogue responses are cached, and GitHub rate limits and
partial publication evidence are shown instead of treating an incomplete pack
as installable. A client receives updates from its NetRatel instance; it does
not need direct GitHub access.

### Automatic update routing and recovery

The configured REST origin may be the Web hostname. Web forwards
`/api/v2/agent-updates/releases/{releaseId}/claim` and
`/api/v2/agent-updates/attempts/{attemptId}/result` to the API without rewriting
the path or body. The explicit agent bearer token remains the API's identity;
these calls do not require a browser session. Artifact downloads continue using
the same-origin v1 client-artifact route and retain size, checksum, manifest,
runtime and version verification.

The gateway evaluates the offer on every accepted presence heartbeat (15 seconds
by default). The recovery coordinator retries transport/routing failures only on
a fresh eligible offer after cooldowns of 30, 60, 120, 240 and then 300 seconds.
The 300-second cap continues while eligibility remains; there is no exhausted
budget that suppresses recovery permanently. Withdrawing an offer, disabling
updates, suspending/suppressing the agent, or selecting another release cancels
stale work. Shutdown cancels active work. One reader serializes staging, and a
handover fence prevents another updater launch, including after an uncertain
launch outcome. Invalid artifacts and terminal policy rejection are not staged
again automatically.

Claim retries retain their admission nonce. Progress-report retries resume the
same phase without repeating verified downloads; a failed failure report retains
the original error. Existing authority rules still govern failed pre-activation
attempts, interrupted downloads and five-minute stale-claim recovery. Tenant
enablement, channel and target pins determine eligibility; the client's packaged
channel is advisory. Suspended agents, suppressed releases and incompatible or
older versions remain excluded. Diagnostics identify current/target version,
runtime, release/attempt, stage, safe route template, failure code and retry delay
without logging tokens, nonces, HTTP bodies or download query strings.

For later owner deployment validation, record the eligible catalog offer and its
target version, a successful authenticated claim through Web, verified artifact
staging, updater handover, gateway activation/readmission and confirmation, the
accepted result, and the running executable reporting the target version. An
unauthenticated 401 only proves authentication reachability. Preserve agent ID
and tenant binding throughout.

Already installed beta.2/beta.6 binaries retain their old in-memory offer
suppression; deploying Web alone does not give them coordinator recovery. After
the Web fix is deployed and the route and eligibility are verified, the owner can
restart an affected client service once to clear that suppression and receive a
fresh offer. If a claim response was lost, first inspect the attempt: a fresh
`Claimed` record with a different nonce is fenced for five minutes; wait for that
existing recovery window before restarting. Staged/activating attempts require
inspection of their existing lifecycle rather than clearing records or bypassing
nonce checks. A routing failure before claiming needs no re-enrollment or
identity reset. These are later owner actions, not deployment evidence.

### Recover an enrolled Windows client from a verified archive

The published Windows client archive includes
`updater/repair-windows-client.ps1`. This entry point uses the same protected
transaction as the generated installer, accepts a local archive instead of an
enrollment download, and requires an existing LocalSystem service and protected
`agent.dat`. It preserves the installed API/gateway configuration, enrollment,
tenant binding, administrator suspension, pending update result and delivery
metadata. It does not create another client or report an installation as Accepted.

On the affected Windows host, download the approved Windows archive and
`SHA256SUMS` from the same completed publication. Verify its SHA256 before
extracting the recovery script. Open **Windows PowerShell 5.1 as Administrator**
and use the verified archive's actual local path and checksum:

```powershell
$archive = 'D:\NetRatelRecovery\netratel-client-<approved-version>-win-x64.zip'
$sha256 = '<64 hexadecimal characters from the release SHA256SUMS>'
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $sha256) {
    throw 'Archive does not match the approved publication.'
}
Expand-Archive -LiteralPath $archive -DestinationPath 'D:\NetRatelRecovery\verified-package'
$repair = 'D:\NetRatelRecovery\verified-package\netratel-client-win-x64\updater\repair-windows-client.ps1'
& $repair -ArchivePath $archive -Sha256 $sha256 -Mode Preview
& $repair -ArchivePath $archive -Sha256 $sha256 -Mode Apply
```

The preview validates the existing registration, protected paths, exact archive
checksum, runtime, version and source. It performs no service, file, lock or ACL
write. Apply repeats verification, takes the existing activation lock, retains
the native state bytes below the protected package `failed/repair-state-*`
directory, and uses the installer's guarded service cutover and rollback. Native
result/request/ready/delivery records remain in place for authenticated delivery;
the installer cutover record has a retained predecessor copy. Re-running an
apply preserves the enrollment and uses a new protected transaction. An
administrator-disabled service remains a prerequisite to reconcile separately.

Both the installer and runtime accept only the exact registered NetRatel
service SID, SYSTEM, Administrators and their existing bounded trusted-owner
rules. A different service SID, untrusted writer, unexpected registration or
reparse point remains rejected. The existing `NetRatel_LEGACY_ACL_REPAIR=preview`
and offline `apply` handle only the documented inherited Users-write pattern;
they do not authorize a broad ACL reset. ACL backups remain protected.
Use those modes with the generated installer as a separate operation and clear
the environment variable before invoking archive recovery. Archive recovery
rejects an inherited legacy ACL mode, including during Preview.

For an **automatic progression test**, use the recovery script from the new
archive to recover to a fixed, earlier completed publication, then let the server
offer the new release normally. Pass that earlier archive's checksum, exact
version and source together:

```powershell
& $repair -ArchivePath $earlierArchive -Sha256 $earlierSha256 `
    -PackageVersion $earlierVersion -PackageSourceRevision $earlierSource -Mode Preview
& $repair -ArchivePath $earlierArchive -Sha256 $earlierSha256 `
    -PackageVersion $earlierVersion -PackageSourceRevision $earlierSource -Mode Apply
```

Both metadata overrides are mandatory together and must match the selected
archive. Older-than-installed packages are refused. The supported earlier
package must already contain the result-reader correction required by the
affected installation. Verify the same enrolled identity, actual running hash,
authenticated result acknowledgement or supported conflict reconciliation, and
then the distinct automatic offer/claim/activation/Accepted chain to the newer
release. A repaired service reaching Running proves local startup only.

Enterprise deployment can run the same checked script once as LocalSystem from
a protected local directory. Record the actual execution identity, preserve its
exit status and protected state receipt, and remove only that owned deployment
task afterwards. The archive is protected by release checksum and publication
attestation; this entry point does not claim an Authenticode signature.

### Repair an older installed Linux updater

An installation from beta.35 or beta.36 can retain an updater that calls
`unzip`, even after installing a newer executable. On minimal Ubuntu this
fails with exit 127 / `unzip: command not found` and gateway
`FailedPreActivation`. The verified incoming archive cannot repair the helper
before the old helper runs. Use the one-time repair below with Python 3
(`sudo apt-get install python3` on Ubuntu, if absent); installing `unzip` is
not required. Stop any scheduled retry while inspecting the failed attempt,
then repair and allow a fresh normal offer. Do not delete attempt state.

The published `v0.1.0-rc.7` Linux updater also rejects a newer prerelease with
the same `0.1.0` core before it can read the candidate package. The generic
Linux client archive now includes `updater/repair-linux-updater.py` for this
one-time installed-script repair. It works with any verified candidate Linux
archive and has no release-version pin. It checks the archive checksum and
manifest, takes the updater's activation lock, saves the installed script,
and atomically replaces only that script while preserving ownership and mode.
It does not install or enroll a Client. The existing agent ID, credential,
tenant and active Client symlink stay intact.

On an affected host, download the approved Linux archive and its release
`SHA256SUMS` from the same completed publication. Verify the checksum before
extracting and running the utility. Confirm the installed updater's path with
`systemctl cat netratel-update.service`; its default is
`/opt/netratel/client/updater/netratel-update.sh`. Then run:

```sh
archive='netratel-client-<approved-version>-linux-x64.tar.gz'
sha256sum --ignore-missing --check SHA256SUMS
sha="$(awk -v name="$archive" '$2 == name { print $1 }' SHA256SUMS)"
test "${#sha}" -eq 64
tar -xOzf "$archive" netratel-client-linux-x64/updater/repair-linux-updater.py > repair-linux-updater.py
sudo python3 repair-linux-updater.py --archive "$archive" --sha256 "$sha"
```

For a nondefault installed updater or state directory, pass `--updater` and
`--lock` with the paths from that host's service configuration. If an update
attempt holds the lock, the utility stops without changing the script. A
repeat against the same candidate reports that the repair is already done.
After repair, approve the candidate through the ordinary instance policy and
verify the update attempt, readmission, agent ID and tenant binding. Keep the
reported backup until that check succeeds; restore it if the replacement
fails before activation. No Client reinstall or re-enrollment is needed.

Hosted acceptance extracts the actual published updater and records its version
gate result. For rc.7, that result is rejection. It runs the packaged repair
utility with the built candidate archive, and then activates that verified
candidate. The OIDC
previous-release upgrade also runs the enrolled published Client under its
original credential owner, repairs its updater, and checks the server-offered
update, readmission, confirmation, and original identity.

## Public install links

An authorized operator can generate a time-limited install link for one tenant,
runtime, immutable version and set of install options. The result shows the
script, command and URL; copying, previewing or downloading that same result
does not issue another enrollment code. For Linux or macOS, use the command
shown for that runtime, for example:

```sh
bash -o pipefail -c "curl -fsSL 'https://netratel.example/clients/install/replace-with-issued-token.sh' | bash"
```

For Windows, use the generated PowerShell command:

```powershell
Invoke-WebRequest -UseBasicParsing -ErrorAction Stop 'https://netratel.example/clients/install/replace-with-issued-token.ps1' | Invoke-Expression
```

Inspect or download the script before execution if required by your operating
procedure. Anyone holding an active URL can use its remaining enrollment
allowance. The maximum uses count is **successful enrollments**, not HTTP
fetches: preview, HEAD, GET and retries do not consume a use. The public script
stops being served when the grant expires, is revoked or is exhausted. Revoking
also blocks enrollment from previously downloaded copies while leaving already
enrolled machines intact. Configure a trusted HTTPS public Web origin and the
public API/download origins before generating a link; a missing or unsafe
public URL is reported as a configuration error. Issued scripts are protected
snapshots: a later template or package change does not rewrite an old link or
archive. For recovery, revoke the old link from the management page (or the
management revoke endpoint), confirm the tenant/runtime/origin/options, and
generate a new link. Old packages remain supported until their link expires,
is revoked, or is exhausted; do not edit a capability URL or downloaded script.
Keep capability URLs and generated script contents out of external reverse-proxy
access logs.
