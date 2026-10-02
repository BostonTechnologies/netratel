# RC.16 client reliability findings

Starting source is freshly fetched `origin/main` at `8e7b7ceb860c448c2cff81c358d2ad8e8ca4a2cc`, the reviewed PR #135 merge. The actual RC.15 publication record binds that commit to build [36996510014](https://github.com/BostonTechnologies/netratel/actions/runs/36996510014) and publication [36998581685](https://github.com/BostonTechnologies/netratel/actions/runs/36998581685), with complete verification and five immutable image digests. RC.16 was unused when checked. The single product version source is `Directory.Build.props`; implementation issues are [#136](https://github.com/BostonTechnologies/netratel/issues/136) and [#137](https://github.com/BostonTechnologies/netratel/issues/137), release tracking is [#71](https://github.com/BostonTechnologies/netratel/issues/71).

| Item | Production evidence/change | Validation and disposition |
| --- | --- | --- |
| A | `install.ps1.Initialize-ServiceController` explicitly loads the framework assembly before preflight mutation; `Set-ServiceState` uses fully qualified types. Outer failure preserves the original exception. Both direct and seeded scripts share this path. | Offline missing-assembly probes; clean native Windows PowerShell 5.1 normal/seed type resolution in the existing Windows package lane. No service installation, enrollment or network operation. |
| B | `Get-LegacyAclPlan` / `Invoke-LegacyAclRepair`, `WindowsAgentDataDirectory.EnsureForPath`, earliest `LogManager.TrySetLogFolder` and credential writer. Details below. | Synthetic decisions locally; native temporary ACL trees in the same Windows package lane. Actual owner ACL captures unavailable, so only the recognized canonical pattern is eligible. |
| C | `ClientInstallEndpointResolver.Resolve` drives authorized read-only preview, link creation and direct scripts. Grant snapshot columns retain effective gateway and provenance; historical unknowns remain null. | Real PostgreSQL preview/no-mutation, auth, configuration-change and immutable replay cases; real-loader precedence; generation/summary components. Existing explicit installed gateway overrides remain effective on repair. |
| D | `GatewayHttpDiagnosticsHandler` observes only response headers; `GatewaySessionDiagnostics` classifies and bounds nonsecret fields. Presence backoff, identity, deadlines and validation are retained. | Fake HTTP handlers and an in-memory real gRPC server cover HTML 403, genuine denial trailers, malformed metadata, resets/TLS/cancellation, streaming passthrough and retries. |
| E | No inspected product session-duration limit explains the reported 60-second reset. Existing ingress coverage retained. | Insufficient external evidence; [#138](https://github.com/BostonTechnologies/netratel/issues/138) remains open. Diagnostics and operator procedure below; sustained deployed-path acceptance is unrun. |
| F | `RemoteDesktopUserHelperTask.BuildTaskXml` uses the documented group principal; unsafe account-default fallback removed; structured registration arguments and launcher read access. | Production principal schema and escaping/argument tests; native interactive registration, consent, capture and input remain owner acceptance. |
| Additions | Heartbeat prior-ACK RTT uses a monotonic timer, correlated sequence/epoch and Akka projection; existing 15-second bulk directory refresh updates both client views. Compact code blocks/copy icons, read-only Monaco preview and top-aligned script tree. | Gateway/actor/component cases cover automatic updates, reconnect/offline clearing and stale values. Old agents need upgrade for the new optional RTT report. Browser geometry checks the actual global/scoped CSS cascade and scroll reachability. |

Identity, tenant/proof-of-possession, immutable grants, source versus normalized archive checksum boundaries, updater activation/rollback and approval policy, real heartbeat fencing, consent and least privilege are preserved. Linux/macOS installer resources and the macOS atomic rollback fix are unchanged. No production deployment, DNS/proxy/WAF change, owner enrollment or automatic update rollout is performed.

## Installer size

Counts use the same synthetic tenant/grant/hash/origin, candidate version and fixed validity time before/after; UTF-8 bytes and logical lines. Windows service output grows from **529 lines / 37,102 bytes / 14 functions** to **648 lines / 46,843 bytes / 21 functions**; non-service differs by one byte. Linux remains 683 lines / 40,654 bytes / 7 functions; macOS service remains 303 lines / 15,236 bytes / 4 functions. The maintained renderer/request/seed/Windows/Linux resources plus the new canonical-root creation helper total **2,402 lines / 141,701 bytes → 2,586 lines / 155,473 bytes**. The two existing runtime call sites add two lines. Growth is the explicit assembly preflight and narrow readable ACL preview/repair/diagnostics, not encoded payloads or relocated installer complexity; unchanged actual updater implementations and tests are excluded consistently.

## Validation and release disposition

Pinned SDK 10.0.401; local Debug builds only. PowerShell 7.6.6 function probes establish offline logic/parsing, not Windows PowerShell 5.1 execution. Final local serial Debug build passed with zero warnings/errors; focused Main **208/208**, Web components **240/240**, browser **41/41**, all zero skipped. Version authority covers 20 projects; existing **57/57** release/source guards and both public-disclosure gates passed. The initial PostgreSQL pull was Docker Hub rate-limited; the public mirror image was cached locally before the real database cases passed. Exact commands/results and hosted final-head/native receipts are recorded in the PR; release remains source preparation until that PR, merged-main validation, tag build and publication verification succeed.

The initial hosted PR run [37022987821](https://github.com/BostonTechnologies/netratel/actions/runs/37022987821) passed clean Windows PowerShell 5.1 normal/seed probes, generic build/tests, all five image checks, PostgreSQL, migration and OIDC gates. Native ACL mutation exposed a .NET Framework inherited-ACE conversion bug: protection preserving inheritance left inherited flags in memory, so an explicit-only scan missed unsafe rights before persistence. The correction snapshots inherited rules, removes inheritance, and reconstructs those rules explicitly in one write, reducing only eligible Users Allow write rights and retaining denies and unrelated access. Native assertions additionally preserve an inherited Users deny and Everyone read. The fresh-root test now snapshots the persisted fixture descriptor, keeping its exact ancestor-unchanged assertion.

All three local-first Compose failures waited for the removed URL textbox. The corrected journeys read the requested code blocks, check the complete live Monaco model against the anonymously fetched script, test read-only keyboard behavior and viewport bounds, and retain download/enrollment/revocation/native-install assertions. First corrected-head local validation: serial Debug build with zero errors; affected offline installer tests **62/62**, browser **41/41**, release/source guards **57/57**, zero failed/skipped. Hosted native ACL and real Compose/Monaco receipts remain pending the corrected head.

## Gateway HTTP and transport diagnostics (D)

`AgentGatewayPresenceClient` observes response headers through a small handler
around its existing gRPC HTTP transport. It distinguishes observed HTTP/non-gRPC
responses from a protocol-valid gRPC `PermissionDenied` or `Unauthenticated`
response. An HTML HTTP 403 is an HTTP/proxy/origin rejection; it does not prove
that NetRatel denied the agent. A genuine gRPC denial requires HTTP/2, HTTP 200,
gRPC content type and matching observed `grpc-status`. Missing or conflicting
metadata remains unverified. Cloudflare is named only when the observed response
identifies `Server: cloudflare`; other responses retain an unknown edge.

The bounded diagnostic includes UTC time, a credential-free origin, RPC family,
locally generated attempt correlation ID and accepted server connection ID,
observed HTTP status/media type/version,
gRPC status, typed transport code, session lifetime, last validated heartbeat ACK
age and the unchanged retry delay. Timing is recorded before optional extension
shutdown. An observable HTTP/2 reset code is reported without guessing its sender;
reset direction remains unknown. Repeated identical failures are logged at most
once per 30 seconds while reconnects retain their existing 1–30 second backoff.
The observer never reads/buffers successful streaming bodies, prints bodies or
headers containing credentials, or emits arbitrary exception details. It does
not change admission, identity, enrollment, deadline or heartbeat fencing.

`GatewaySessionDiagnosticsTests` uses real `GrpcChannel` calls with fake HTTP
handlers and an in-memory gRPC server for HTML 403, header/trailer gRPC denial,
incomplete metadata, TLS/socket/reset/cancellation, streaming passthrough,
redaction and retry/identity checks. These are simulated transport checks, not
evidence that the owner's public route or sustained connectivity is fixed.

[Cloudflare's gRPC documentation](https://developers.cloudflare.com/network/grpc-connections/)
(reviewed 2026-10-02) supports proxied gRPC endpoints and states that a zone with
gRPC disabled returns HTTP 403. Its supported configuration requires the gRPC
endpoint on port 443, TLS, HTTP/2 advertised through ALPN, gRPC content type,
a proxied hostname and at least Full SSL/TLS mode, with the zone's gRPC setting
enabled. WAF inspection covers connection headers; managed rules do not inspect
gRPC stream content. Cloudflare Access does not support gRPC through its reverse
proxy and ignores that traffic when gRPC is enabled, so NetRatel's authentication
must remain enforced at the origin. Cloudflare Tunnel supports gRPC through
private subnet routing; public hostname deployments are currently unsupported.
The owner's DNS-only change resolved one observed HTML 403; DNS-only is not a
general NetRatel requirement. No DNS, certificate, proxy, Access or WAF change
was performed by this task.

## Gateway resets near 60 seconds (E)

Disposition: **not reproduced; insufficient external evidence**. Track the
remaining observation in [#138](https://github.com/BostonTechnologies/netratel/issues/138).
The attached brief reports HTTP/2 `INTERNAL_ERROR` after admission but supplies
no client/API/proxy logs, active routing configuration, deployed Traefik version
or image digest. The Windows investigation files named in that brief were not
attached. The terminating hop and cause remain unknown. This is separate from
the earlier HTML HTTP 403 observation; neither a reconnect nor gateway admission
proves sustained connectivity or working desktop capture.

Source evidence retained for this candidate:

- [PR #124](https://github.com/BostonTechnologies/netratel/pull/124) fixed an
  NGINX request-body idle timeout. `release/nginx.public-https.conf` still has
  finite `client_body_timeout`, `grpc_read_timeout` and `grpc_send_timeout` of
  3600 seconds on `/netratel.gateway.v1.`. The existing
  `tools/ci/tests/nginx-gateway-idle.sh` regression keeps one bidirectional
  HTTP/2 stream open across 65 seconds of request-body silence. That regression
  covers this NGINX configuration; it does not reproduce the reported Traefik path.
- `tools/ci/tests/traefik-gateway-routing.sh` uses **fixture** version v3.7.13.
  It checks the dotted namespace, complete method path, TLS-to-h2c route and
  REST fallback with synthetic responders. Its short probes establish route
  precedence, not a sustained production gateway session or the deployed version.
- `NetRatel.API/Program.cs` separates REST HTTP/1 on port 9222 from gateway h2c
  on port 9223 by default. `AgentGatewayService.Connect` follows RPC cancellation,
  frame validation and presence fencing. `GatewayDuplexSession.RunAsync` joins
  workers when the call or registration ends; its five-second shutdown bound is
  not a session lifetime. No 60-second session-duration limit was identified in
  these inspected paths. Existing heartbeat and identity validation remain intact.

[Traefik entrypoint documentation](https://doc.traefik.io/traefik/reference/install-configuration/entrypoints/)
and its [v3.7.13 source](https://github.com/traefik/traefik/blob/v3.7.13/docs/content/reference/install-configuration/entrypoints.md)
define `transport.respondingTimeouts.readTimeout` as the maximum duration for
reading the **entire request, including its body** (documented default 60s).
An open gRPC request body makes this a plausible lead. It is not merely a
between-heartbeats idle timer; additional heartbeats do not prove that an
absolute request deadline has been removed. A documented default is not
evidence of the owner's active setting.

Check `writeTimeout` separately: it bounds response writes from the end of
request-header reading through the end of response writing (documented default
0s). `idleTimeout` concerns an idle keep-alive connection (default 180s).
`keepAliveMaxRequests` and `keepAliveMaxTime` can cause HTTP/2 GOAWAY when
configured. [Upstream serversTransport timeouts](https://github.com/traefik/traefik/blob/v3.7.13/docs/content/reference/routing-configuration/http/load-balancing/serverstransport.md)
control a different hop: dialing, waiting for response headers, idle connections
and HTTP/2 ping health. Changing them is not an entrypoint request-body fix.
Backend restarts, cancellation, presence replacement and token expiry require
their own correlation rather than assuming every 60-second event has one cause.

After the owner deploys the matching candidate, collect a bounded observation
across several former 60-second windows:

1. Record UTC start/end timestamps, client/server versions and image digests,
   actual public origin, REST/gRPC router and service mapping, deployed Traefik
   version, active static entrypoint settings and upstream transport settings.
   Preserve `/netratel.gateway.v1.` and the complete method path to the private
   h2c listener. Record proxy/backend restarts and the token expiry time without
   recording the token.
2. Correlate client session/admission identifiers, last acknowledged heartbeat
   age, session lifetime, observed HTTP/gRPC/transport status and retry delay
   with API admission/disconnection and Traefik request duration/status. Record
   which hop sent a reset or GOAWAY and its code when observable; otherwise
   leave direction unknown. Report reconnects and how long each stream lasted.
   Share only sanitized diagnostic fields, never response bodies, credentials,
   capability install URLs or unfiltered production logs.
3. If the collected evidence demonstrates an entrypoint body deadline, perform
   an owner-controlled validation using the documentation for that deployed
   Traefik version and an approved finite streaming duration. These are
   **static entrypoint settings**, applied to every router on that listener,
   requiring a proxy restart. A shared HTTPS entrypoint also serves Web/REST;
   an individual gRPC router cannot override its deadline. Select a dedicated
   gateway entrypoint or account explicitly for that shared scope. Do not
   disable all timeouts globally. Confirm the same routing and stream behavior
   after the change, including streams exceeding the old deadline.

No live proxy/DNS/WAF setting, retry policy, keepalive interval or heartbeat
validation was changed to chase this unproven observation. The diagnostics and
confirmed fixes may ship as a candidate for testing while #138 remains open.
A demonstrated unresolved supported-path stability or identity/security
regression still blocks release.

## Interactive desktop helper task (F)

`RemoteDesktopUserHelperTask.BuildTaskXml` emitted `LogonType=Group`. Microsoft's
[XML logonType enumeration](https://learn.microsoft.com/en-us/windows/win32/taskschd/taskschedulerschema-logontype-simpletype)
contains `S4U`, `Password`, `InteractiveToken` and
`InteractiveTokenOrPassword`; the Task Scheduler **API** `TASK_LOGON_GROUP`
value is not an XML value. The documented
[group principal example](https://learn.microsoft.com/en-us/windows/win32/taskschd/taskschedulerschema-principal-principaltype-element)
uses `GroupId` without `LogonType`.

The corrected task retains numeric BUILTIN Users SID `S-1-5-32-545`,
`LeastPrivilege`, the group action context, logon trigger and targeted-session
`RunEx` behavior. XML registration passes each schtasks argument separately.
The previous CLI fallback omitted `/RU`, which
[schtasks documents](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/schtasks-create)
as the registering account, potentially SYSTEM when invoked by the service.
That fallback is removed. XML registration failures remain explicit; the
independent HKLM interactive-logon bootstrap is retained, and no substitute
execution account or password is introduced.

The protected root grants interactive users traversal. The helper launcher
directory additionally grants Users read/execute so interactive startup can
read its launchers without write access. The isolated
`Client/logs/remote-desktop-helper` directory retains its deliberate Users
Modify permission. Those user-writable logs are not trusted service-health
evidence or permission to write credential/control/executable paths.

`RemoteDesktopUserHelperTaskTests` runs the production XML builder and checks
its principal against the unchanged Microsoft `principalType` and dependency
definitions in `Client/Fixtures/TaskSchedulerPrincipal.xsd`. Restoring the old
`Group` value fails schema validation; corrected group/least-privilege XML,
escaping and structured registration arguments are covered offline. The
excerpt validates only the principal; it does not claim full Task v1.4 schema
or native registration acceptance.

Residual account/SID mapping warnings need their exact sanitized error and
registration/session context. Native Windows interactive registration, helper
connection, capture, input, reconnect and consent remain separate owner tests.
An online client or schema-valid task is not desktop acceptance. Confirm these
operations in the selected interactive session with existing remote-support
authorization and consent; no capture or customer-machine change ran in CI.

## Canonical Windows legacy ACL recovery (B)

The owner-machine ACL SID/rights/inheritance captures named in the brief were
not attached. The repair therefore accepts only a narrow reproducible pattern:
a trusted-owned canonical `C:\ProgramData\NetRatel` tree inheriting an allow ACE
for numeric BUILTIN Users SID `S-1-5-32-545` whose rights fit Write/Modify,
ReadAndExecute and Synchronize, with no ChangePermissions/TakeOwnership or
unknown SID writes. Root names are restricted to the actual credential, updater,
service logs, desktop launchers and diagnostic paths present in the client
source. Explicit untrusted writes, unknown owners/layouts, conflicting trusted
account denies and reparse points refuse repair. Rejections report the sanitized
path, leaf/ancestor role, numeric owner/ACE SIDs, rights, allow/deny and inheritance
flags. Custom installations continue to require manual review.

In an elevated Windows PowerShell 5.1 process, use a freshly generated installer
file without changing its embedded request values:

```powershell
$env:NetRatel_LEGACY_ACL_REPAIR = 'preview'
& .\install.ps1
# Inspect the bounded preview. Stop the client/updater using the ordinary
# approved operator procedure before applying this offline recovery.
$env:NetRatel_LEGACY_ACL_REPAIR = 'apply'
& .\install.ps1
Remove-Item Env:\NetRatel_LEGACY_ACL_REPAIR
```

Preview exits before installation and makes no ACL, file, lock or enrollment
changes. Apply requires the existing service stopped and no client/updater
process. It never opens an unsafe updater lock. It takes a protected recovery
lock under the fixed Program Files product recovery directory, rechecks the
complete eligible plan, then saves the exact original owner/group/DACL/SACL
security descriptors in a private JSON snapshot before changing any ACL. The
recovery directory itself must pass owned-path checks. The process preserves
unrelated ACEs and existing read/traverse rights, removes only the eligible
inherited write permissions, and revalidates the tree. Applying an already safe
tree is a no-op. Permission recovery is independent of later binary rollback;
the installer does not restore a known-insecure descriptor if installation fails.
It never resets an ancestor ACL, takes ownership or changes credential bytes.

`Program.RunClientAsync` initializes logging before obtaining credentials. Its
service log creation could therefore create the common product root with
ProgramData inheritance before `AgentCredentialStore.WritePayloadAsync` did.
Both proven creation paths now invoke one small canonical-root helper: missing
roots are created atomically with protected SYSTEM/Administrators full control
and Users directory traversal, and existing unsafe roots fail without mutation.
Explicit custom paths and non-Windows behavior are unchanged. Desktop launcher
directories retain explicit Users ReadAndExecute; the isolated
`Client\logs\remote-desktop-helper` directory retains intentional Users Modify.
User-writable log contents are excluded from repair scanning and never become
trusted credential/control or service-health evidence.

`WindowsLegacyAclRepairTests` covers decision semantics with synthetic numeric
ACL fixtures locally, and runs native temporary product-shaped trees in the
existing Windows package lane. Native probes cover inherited-write repair,
preview/no mutation, exact protected snapshots, credential preservation, no
ancestor change, helper access, idempotence, explicit-write refusal and fresh
root creation. Test-only root injection is confined to isolated fixture
functions/the private creation core; production exposes no arbitrary repair
target. These offline probes do not enroll clients, contact a backend or
install/start/stop services. Native Windows results must come from hosted
Windows execution; Linux PowerShell parsing is not that evidence.

## Owner testing handoff

After public publication verification, use the matching RC.16 digest-pinned Compose/server bundle and client archives from the release; import that same version into Agent Installers. Deployment and import are owner operations. Verify `ClientArtifacts__PublicBaseUrl` and `ClientArtifacts__PublicGatewayBaseUrl` against the actual REST/gRPC listener routing (examples in `CONFIGURATION.md`), while installer links remain on public Web.

1. Preview the three public origins, then generate a **fresh** immutable link and confirm its actual stored summary. Old links retain old template/configuration snapshots. Copy the generated command unchanged into clean elevated Windows PowerShell 5.1; keep capability URLs private.
2. Exercise a fresh canonical install and ordinary repair, then the eligible legacy ACL preview/offline apply documented above. Confirm refusal diagnostics on unknown owners, explicit untrusted writes, reparse/custom layouts; retain existing AgentId, device key, refresh credentials and tenant. Inspect root/service versus isolated helper-log permissions separately.
3. Check installed files, local startup, enrollment and gateway online status separately. Restart and compare effective API/gateway configuration, including preserved explicit installed/deployment overrides. A completed installer is not proof of enrollment or connectivity.
4. Observe multiple former 60-second reset windows using the UTC correlation procedure for #138; record session lifetimes and reconnects. Upgraded agents should continuously refresh per-client heartbeat RTT in /clients without clicking ping, with accurate offline/stale presentation.
5. Validate helper registration and intended interactive account/session, then remote-support consent, connection, capture, input and reconnect separately. Copy controls, full command/URL content, Monaco scroll/view behavior and top-left library tree are UI acceptance items.

No owner-machine service lifecycle, live ingress change or native desktop capture/input ran in this task. The native hosted tests are bounded offline type/ACL/package checks, not this deployment acceptance.
