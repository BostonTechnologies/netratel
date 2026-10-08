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
[`docs/deployment/traefik-gateway-static.yaml`](deployment/traefik-gateway-static.yaml).
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

### Previously installed prerelease updater

The published `v0.1.0-rc.7` Linux updater rejects a newer prerelease with
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
