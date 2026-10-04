# Gateway connectivity deployment and acceptance

Related to [#138](https://github.com/BostonTechnologies/netratel/issues/138).
Keep [Windows helper #156](https://github.com/BostonTechnologies/netratel/issues/156)
acceptance separate from core presence, terminal and file connectivity.

## Product lifetime and renewal behavior

Each native presence attempt owns its physical call and pending I/O. The client
bootstrap budget defaults to 15 seconds, server admission to 30 seconds, and
teardown to 5 seconds. After admission, the advertised heartbeat policy bounds
blocked writes and ACK waits. Accepted heartbeats renew server inactivity;
duplicate frames and late frames from an offline owner cannot renew it.
The existing supported heartbeat configuration range remains compatible.
There is no fixed overall presence-stream deadline. Actor admission is provisional
until the first validated heartbeat commits ownership. An absolute admission
deadline, exact-attempt cancellation and bounded pending state prevent a late
cancelled admission from displacing its successor.

Token renewal has an independent timer. Negotiated `presence-auth-renewal-v1`
reauthenticates the same logical owner using a fresh signed Agent JWT, the same
tenant/agent/fence, a new ordered operation, strict credential expiry, and current
enabled/unrevoked enrollment. Akka commits only the validated expiry and sequence;
JWT bytes never enter actor state. The ACK confirms credential rotation before
later capability reconnects use it. The native cache uses the earlier of the
response lifetime hint and readable JWT expiry, so fractional timestamps cannot
return the predecessor credential at the next acknowledged renewal boundary.
The renewal timer rounds up to milliseconds and rechecks its absolute due time
before authentication, including after a backward wall-clock adjustment.
The active terminal keeps its shell process,
generation and registration through successful routine renewal. Mixed-version
peers keep the existing replacement/fencing outcome. A blocked ACK deliberately
retires its owned call rather than treating a coincident reset as successful renewal.
A timer becoming due during healthy I/O allows the single pending exchange to
finish within the original watchdog, one heartbeat interval, and half the
remaining authenticated lifetime; renewal then has priority over another heartbeat.

Authentication expiry is serializable actor state with an exact-owner keyed timer.
Capability streams on another API replica verify the authoritative snapshot,
fence and expiry through a bounded monitor at most five seconds apart, with
shorter intervals for short leases. Exact-expiry timers fail closed; a local
exact-key lease only accelerates retirement and creates no replica-affinity or
numeric-epoch authority assumption. A new capability RPC must also present a
currently valid JWT; HTTP clock-skew acceptance cannot adopt renewed authority.

Retry history resets after 120 seconds of validated health, so the recorded
60-second flap remains unhealthy. Failed admission and flapping use exponential
delay with injected randomness, 20% jitter and a 30-second ceiling. Planned
renewal has one owner; child retries stop with the parent. File workers are
tracked, cancelled and joined before their resources are disposed. If synchronous
filesystem work outlives the five-second cleanup budget, the capability reports
unavailable and a successor is gated until that work exits. Such a dependency
is observed rather than forcibly terminated. Terminal teardown aborts stalled
HTTP I/O and fences late delivery callbacks. Safe folder listing may recover
after fresh readiness; uncertain uploads, terminal input, commands and jobs are
not automatically replayed.

## Evidence and deployment inventory

The October 2026 handoff records 1,464 unplanned presence failures across
Windows/hv01/hv02, with median lifetimes of 60.334/60.024/60.022 seconds and
median last-ACK age of 14.994 seconds. Several independently admitted child
RPCs reached their own approximately 60-second boundary before presence-owner
cancellation. That favors a whole-request deadline; the recorded reset origin
is still unknown. Do not add subordinate `Cancelled`/`Internal` messages to
the primary incident count or infer downtime from the disconnect-only CSV.

RC.19 already contains the Traefik v3.7.13 A/B proof and reference setting.
[Its evidence](RC19_INSTALLER_GATEWAY_RELIABILITY.md) distinguishes the default
60-second body-read deadline from a corrected/direct approximately 195-second
continuous stream. This PR's product lifecycle changes and further functional
tests must be assessed separately from that existing proof.

The 4 October cloud preflight found the strict `global.json` SDK 10.0.401
available after one supported installation, authenticated GitHub CLI access,
and a working managed Docker daemon. The authorized workspace contained the
repository's reference configuration and disposable test fixtures only. No
running production ingress, owner-host access, deployed gateway URL or scoped
production proxy/API logs were supplied. No live configuration was changed.

Before applying an ingress change, the deployment owner must complete this
record from the **running deployment**, rather than from a fixture:

| Required evidence | Record |
| --- | --- |
| Effective native `Gateway:Endpoint` / installer Gateway URL | `<actual HTTPS origin, including public port>` |
| Every TLS/LB/proxy hop and forwarding order | `<hop identities and termination points>` |
| Ingress image version and immutable digest | `<running image digest; not the fixture tag>` |
| Actual gRPC router, listener and public/private port mapping | `<names and mapping>` |
| Effective incoming read/write/idle limits at each hop | `<explicit values or version-confirmed defaults>` |
| Backend protocol/port and relevant upstream deadlines | `<private h2c:9223 if confirmed; actual transport settings>` |
| Configuration source and overrides | `<static file/CLI/Helm values and effective precedence>` |
| Scoped proxy and API logs for the incident below | `<UTC window, matched IDs, origin or unknown>` |

Correlate `2026-10-03T18:07:53.1680920Z`,
`correlation=b00e5d88-4528-46e7-bd98-7e6119458500`,
`serverConnection=700ad4d9-112a-451d-a417-23d740c64d06` across the actual
hops. Preserve a small redacted window recording reset/deadline/cancellation
and restart direction. Read only the needed configuration fields; do not dump
tokens, credentials, entire environments, or complete secret-bearing configs.
If direct API traffic also fails, investigate application cancellation first.

## Conditional operator ingress patch

Apply this patch only after the inventory establishes Traefik and the
conflicting incoming whole-request read limit on the actual gateway listener.
Replace both placeholders with captured deployment values:

```diff
 entryPoints:
   <ACTUAL_GATEWAY_ENTRYPOINT>:
     transport:
       respondingTimeouts:
-        readTimeout: <CAPTURED_EFFECTIVE_READ_TIMEOUT>
+        readTimeout: 0s
```

When the running version supplies the conflicting default implicitly, add the
`readTimeout: 0s` key instead of deleting an absent key. For a CLI-managed
listener the equivalent static argument is
`--entryPoints.<ACTUAL_GATEWAY_ENTRYPOINT>.transport.respondingTimeouts.readTimeout=0s`.
Change the winning configuration source; a lower-precedence file is insufficient.
Inspect a finite `writeTimeout` independently before claiming sustained streaming.
Leave other limits intact unless a separate reproduced conflict justifies a change.

The [reference fragment](deployment/traefik-gateway-static.yaml) reserves a
dedicated listener on `:8443`. Its name/port are examples, not the running
deployment. If the listener also serves Web/REST, use the documented
[dedicated-listener design](CLIENT.md#sustained-traefik-gateway-streams) and
record its public HTTPS URL explicitly. Keep TLS and hostname validation,
trusted-proxy rules, agent authentication, the exact `/netratel.gateway.v1.`
route boundary and the confirmed private API backend. A new hostname on the
same listener, a dynamic router label, or an upstream idle timeout does not
override this incoming static deadline.

1. Save the current configuration revision/hash and running image digest.
   Prepare a minimal diff, validate it with the deployment's supported parser,
   and retain the original configuration as the rollback input.
2. Apply through the existing ingress deployment procedure. A static Traefik
   setting requires restart/recreation. For an operator-confirmed Compose
   deployment, fill in the three captured selectors before running:

   ```sh
   ingress_project='<actual Compose project>'
   ingress_compose='<actual operator Compose file>'
   ingress_service='<actual Traefik service>'
   docker compose --project-name "$ingress_project" -f "$ingress_compose" \
     config --quiet
   docker compose --project-name "$ingress_project" -f "$ingress_compose" \
     up --detach --no-deps --force-recreate "$ingress_service"
   ```

   For another orchestrator, use its recorded rollout procedure. Capture the
   effective setting from the replacement process and confirm that the actual
   gRPC router is attached to that listener.
3. Check HTTPS/TLS, authenticated admission, Web/REST routing and sustained
   terminal/file operation on the effective gateway URL. Record any expected
   interruption caused by the ingress rollout separately.
4. On regression, restore the exact saved configuration revision and repeat
   the same restart/recreation command. Check the restored effective setting,
   router and digest. Preserve failure logs and the reason for rollback; do
   not overwrite the evidence with a successful laboratory result.

## Candidate and functional acceptance record

Start a fresh receipt for each candidate; unfilled fields mean acceptance is
pending. Obtain review binaries from hosted Actions for the exact source head.
Keep the current product version and existing enrolled identities/credentials.
An API image change alone does not apply operator-owned ingress configuration.

```text
candidate_source_sha=<full PR source head SHA>
tested_merge_sha=<full SHA reported by final-head Actions>
actions_run_url=<URL for this candidate, not an earlier successful run>
api_binary_or_image=<artifact checksum or immutable image digest + source revision>
windows_client=<artifact checksum + source revision>
hv01_client=<artifact checksum + source revision>
hv02_client=<artifact checksum + source revision>
gateway_origin=<effective HTTPS origin>
ingress_image_digest=<running immutable digest>
ingress_configuration_revision=<applied operator revision/hash>
token_policy=<expiry/renewal margin; UTC timestamps>
acceptance_started_utc=<timestamp>
acceptance_ended_utc=<timestamp>
```

Run a 30–45 minute functional check first, extending it until **two normal
authenticated renewals** have occurred on each host. Keep one authorized
terminal open on each host; record its logical session/generation and process
identity, exchange benign stdin/stdout and resize before/across/after both
renewals, and verify that the same shell remains usable. Opening a replacement
shell is a different outcome. Record explicit close/process exit separately.

List an allowed folder and read a small known file periodically and near
renewal boundaries; compare SHA-256 with the original. Record request/attempt
IDs and capability readiness. Following one explicitly controlled interruption,
wait for fresh authorization/readiness and issue a new safe listing/read.
Record reconnection bounds, stale-owner rejection and terminal recovery outcome.
Do not automatically replay terminal input, commands/jobs or file mutations
whose execution is unconfirmed; report interrupted/unknown outcomes.

The file cleanup enqueue has a one-second bound; saturation retires the old
file owner and exposes unavailable readiness while retaining the original
caller outcome. The browser's safe folder retry waits/rechecks authoritative
readiness for at most 30 seconds and stops on disposal. Check those bounds
explicitly during the controlled interruption; upload/mutation is not replayed
by this recovery path.

Then run the same Windows/hv01/hv02 hosts for one approximately 12–13 hour
steady-state soak with lightweight functional probes and capped logs. Require
no unexplained transport/presence disconnects, continuous successful probes
in expected connected periods, and normal renewal retaining the active
terminal. Exclude the deliberately injected interruption from steady-state
counts while verifying its documented recovery. A different reset interval
does not satisfy acceptance.

Keep process uptime, authoritative presence availability, per-capability
readiness and successful terminal/file operations as separate measurements.
Record primary failures/rate, admissions, connection/ACK ages, reconnect-gap
percentiles, refresh count/reason, readiness transitions and task/session/queue/
timer/resource counts. Capture complete timestamped admission/refresh/failure
events to calculate gaps; disconnect-only CSV data cannot establish them.

The bounded diagnostics command remains:

```sh
python3 tools/ci/summarize-gateway-log.py < scoped-client.log
```

Use `python3 tools/ci/summarize-gateway-log.py --metrics < scoped-client.log`
for aggregate allowlisted events and timestamped log extent. Run it separately
for each host. It does not establish live-hop cause, exact downtime, process uptime
or operation success. Rows lacking owner identity remain uncorrelated; primary
presence incidents and subordinate cancellations are separate totals.
Reported log-suppressed failures are a separate metric because their individual
identities and reasons are unavailable. `correlation_complete=false` reports
exhausted bounded identity storage; missing identities are counted separately.

## Repository validation commands

`global.json` requires SDK **10.0.401** with roll-forward disabled and
Microsoft.Testing.Platform. The authoritative invocation and receipt verifier
are in [.github/workflows/public-pr-validation.yml](../.github/workflows/public-pr-validation.yml).
After coordinated integration, retain that workflow's sequence and all gates:

```sh
dotnet restore NetRatel.sln
tools/ci/verify-product-version.sh
python3 tools/ci/test-release-validation.py
bash tools/ci/tests/nginx-gateway-idle.sh
dotnet build NetRatel.sln --configuration Release --no-restore
NETRATEL_GATEWAY_TEST_ASSEMBLY="$PWD/src/NetRatel/NetRatel.Tests/bin/Release/net10.0/NetRatel.Tests.dll" \
  bash tools/ci/tests/traefik-gateway-routing.sh
pwsh src/NetRatel/NetRatel.Web.PlaywrightTests/bin/Release/net10.0/playwright.ps1 install --with-deps chromium
dotnet test --solution NetRatel.sln --configuration Release --no-build \
  --max-parallel-test-modules 1 --filter-not-trait category=compose category=hosted \
  --results-directory TestResults --report-trx \
  --report-trx-filename 'netratel-tests-{asm}_{tfm}_{arch}.trx'
```

Set the workflow's `NETRATEL_REVIEW_SOURCE_SHA`,
`NETRATEL_REVIEW_TEST_MERGE_SHA`, `NETRATEL_PLAYWRIGHT_ARTIFACT_ROOT` and
`NETRATEL_DOTNET_SDK_VERSION` to the actual candidate/runner values for visual
receipts. Require successful nonempty receipts for `NetRatel.Tests`,
`NetRatel.API.IntegrationTests`, `NetRatel.Web.ComponentTests` and
`NetRatel.Web.PlaywrightTests`, using the workflow's generic receipt-verification
step. Report counts from those receipts, never a historical test total.

The existing transport lane checks exact routing, TLS-to-private-h2c mapping,
default negative and corrected/direct approximately 195-second profiles, then
requires a native gateway TRX receipt. Extend that same fixture for new
functional coverage; its historic placeholder extension alone does not prove
terminal/file renewal continuity. `--native-only` is a diagnostic rerun option,
not a replacement for the full hosted gate.

Retain the workflow's Windows offline installer/ACL/Task Scheduler/process
selection and `verify-mtp-trx.py --expected-executed 21`, and its native macOS
installer/command selection with `--expected-executed 3`, on their respective
hosted runners. Linux cannot establish their native execution. Preserve all
other `PR validation` dependencies: disclosure, review artifacts, client packages,
images, source/release/local-first Compose, PostgreSQL regressions/upgrades and
MCP HTTP image smoke. Release publication/tagging/registry writes are outside
this PR. Laboratory and CI success leave the owner deployment/soak record above
pending until actual receipts exist.
