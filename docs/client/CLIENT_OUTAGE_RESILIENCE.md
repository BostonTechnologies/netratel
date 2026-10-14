# Native client outage recovery

The enrolled operational client keeps its process and installation identity
while it waits for the backend. `WaitingForBackend` and
`AuthenticationAttention` describe a live recovery worker. Only validated
authoritative presence establishes `Online`; process liveness, a token response,
a ping, or an open stream does not establish backend presence.

## Retry contract

One operational presence/authentication owner schedules an outage episode. A
fresh start receives one immediate bounded attempt. After each failed attempt
finishes, elapsed outage time, including its I/O, selects the next wait:

| Elapsed outage | Independently sampled wait |
| --- | --- |
| Below 5 minutes | Equal jitter between half and all of exponential bases 2, 4, 8, 16, then 30 seconds |
| 5 minutes to below 30 minutes | Uniform 60–120 seconds |
| At least 30 minutes | Uniform 300–600 seconds, indefinitely |

Phase boundaries do not interrupt an attempt or create additional attempts.
An accepted `Retry-After` delta/date from an applicable 429/503 response raises
the wait floor. The scheduled wait is capped at 600 seconds; excessive hints
are reported as capped. Malformed or past hints are ignored. Recovery latency
is the remaining scheduled wait plus bounded authentication, admission and
first-heartbeat work, rather than an absolute ten-minute deadline.

Restored authoritative presence reports Online immediately. Retry history
resets only after 120 seconds of continuous validated heartbeat progress under
the current owner. Brief flaps retain the episode. Token responses, renewal
acknowledgements and stalled streams do not reset it. Child capability failures
continue to use their own scoped retry rules.

The strongly typed local configuration section is `Client:OutageRecovery`.
Existing flat client configuration uses `OutageRecovery` at the root instead.
All values are integer seconds; no new environment variable is required.

| JSON property | Default | Accepted values |
| --- | --- | --- |
| `AttemptTimeoutSeconds` | 30 | 1–300 |
| `FastPhaseSeconds` | 300 | 1–86,400 |
| `ExtendedPhaseSeconds` | 1,800 | Greater than FastPhaseSeconds and at most 604,800 |
| `HeartbeatStabilitySeconds` | 120 | 1–3,600 |
| `MaximumScheduledWaitSeconds` | 600 | 30–600 |

For a nested deployment, environment names are
`Client__OutageRecovery__<property>` or the existing prefixed alias
`NetRatelCLIENT__Client__OutageRecovery__<property>`, including when the
installed JSON uses the legacy flat client shape. Configuration precedence remains
the existing packaged defaults, installed settings, deployment environment,
then command line. Validation is local. The table above describes adjustable
limits; the exact 5/30-minute policy and 120-second history reset use defaults.

At defaults, a disconnected attempt has a 30-second whole budget for
authentication, admission and the first validated heartbeat, followed by the
applicable five-second physical I/O abort/join and bounded child shutdown.
Connected renewal has one whole budget capped by 30 seconds, the negotiated
watchdog and the remaining current authorization. A validated healthy presence
session has no artificial 30-second lifetime. These bounds are deterministic
test evidence; actual installed-host timing remains owner acceptance.

The nonsecret retry record is advisory, versioned and atomic. Corrupt or
unwritable state must not stop recovery. It retains the established phase
across unexpected restarts without becoming a durable scheduler. A matching,
still-pending controlled update can consume one immediate bounded attempt
ahead of an inherited slow deadline; repeated starts of the same candidate
cannot consume it again or bypass an active Retry-After floor.

## Native authentication and rollback

Deploy the backend before the client for the complete refresh-rotation
guarantee. The native token extension is separate from helpdesk M2M and its
OIDC discovery document. The client negotiates native support before recording
an exchange. A pending exchange retains its immutable parent credential,
exchange ID, scopes and registered device binding in protected storage outside
replaceable version directories and the older client's fixed-field writer.

`GET /api/v1/agents/token-capabilities` advertises
`refreshExchangeVersion: 1` and `rotationEnabled`. The additive token request
uses `exchangeVersion: 1` and a UUID `exchangeId`, echoed by the response.
The registered-device signature covers the versioned canonical request
projection, including that exchange ID and the exact requested scope array.
Optional cached proof JWTs are omitted on acquisition. Every real token POST
has a new device signature, nonce and timestamp. A new pending
exchange requires both version 1 support and `rotationEnabled: true`; default
nonrotating backends use the legacy wire without pending state. The first
negotiated logical attempt can send a capability GET followed by one token
POST. After positive support/rotation negotiation, ordinary token attempts send
one token POST, including exact pending retries. Known nonrotating or old APIs
are probed again before later legacy acquisition to detect upgrades or enabled
rotation. A new process renegotiates before a new exchange, while an existing
pending exchange is resumed directly. The existing narrowly authorized
`agent_not_found` recovery may additionally enroll and retry when its enrollment
contract permits. A known old API may use legacy before an extension starts.
An outage does not erase negotiated support.

The API's existing `DataProtection:KeysDirectory` must remain persistent and
shared across replicas, as for existing protected features. No new protection
deployment setting is introduced. Turning rotation off with an outstanding
recorded exchange returns `refresh_exchange_policy_changed` attention and
retains pending state; it does not silently revive the parent.

Exact retries use fresh device proof with the same exchange binding. The API
consumes a parent transactionally once and retains one protected successor
result. Repeated response loss returns that same refresh successor with a
fresh access token when authorized. It does not create successive rotations.
Current tenant, agent, key, scopes, mTLS policy, natural expiry and successor
status are checked again. Revocation and natural expiry remain effective.

An ambiguous extended exchange cannot silently fall back to legacy, change its
ID or restore a stale parent. A legitimate newer credential save wins over
old pending material. Later authenticated successor use acknowledges the
protected result. Nonexpiring credentials may retain this one outstanding
result until acknowledgement or revocation.

If an older client resumes on the new API after an extended response/save
loss, a narrow compatibility bridge can return the already-recorded immediate
successor. It requires fresh registered-device proof, the recorded key/scope
binding, current authorization and original expiry, and an unacknowledged live
successor. It cannot create another successor, claim another exchange, follow
arbitrary descendants, or enlarge the generic legacy grace period. This bridge
does not retrofit resilience into independent later legacy rotations or an
older API. Keep the existing 180-second update rollback contract and never
restore an acknowledged credential from a rollback snapshot.

## Service-manager fallback

Backend outages are recovered inside the running process. OS restart is a
fallback for actual process failure. Owned Linux service policy is:

```ini
[Unit]
StartLimitIntervalSec=0
[Service]
Restart=always
RestartSec=30s
RestartPreventExitStatus=78
```

The configuration/enrollment exit remains meaningful. Intentional
`systemctl stop` remains an intentional stop. Windows failure actions restart
after 30, 60 and 300 seconds, repeat the last restart, and reset the failure
count after 86,400 seconds. Intentional stop/shutdown cancels and joins work;
unexpected worker termination reports a real service failure.

Existing installations need the owned-policy retrofit executed by the new
candidate before backend-dependent startup, including a candidate launched by
an older updater. Only recognized product-managed settings on a verified
owned service are eligible. Administrator overrides, custom paths, accounts,
environment, permissions and rollback remain preserved. Permission or override
failures are reported and in-process recovery continues. Freshly generated
installer links contain the current template; older immutable links do not.

## Backend-first owner rollout

Publication distributes artifacts; it does not activate installed clients.

1. Verify the completed publication record, checksums, source, runtime and
   immutable image digests. Deploy the API/migration using the normal approved
   procedure. Keep the stable channel policy separate from prerelease approval.
2. Import the verified native pack through **Agent Installers & Updates →
   GitHub releases**, then use **Approve and publish for deployment** and the
   existing tenant update policy. Retain agent credentials and device keys.
   Use the existing installer repair route when a managed policy cannot be
   retrofitted under the service account; a repair does not require identity
   replacement or re-enrollment.
3. On each approved canary, inspect its actual running executable path,
   version/source, agent ID, PID/process start and effective service recovery
   policy. Historical incident versions and service states are not current
   observations. Distinguish local Running from authenticated update
   activation; matching attempt/release/version/confirmation and the
   180-second timeout/rollback remain required.
4. In an agreed isolated environment, begin with a short backend outage and
   restoration. Correlate UTC API/gateway telemetry, sanitized local retry
   diagnostics, authoritative presence and newly admitted capabilities.
5. Then observe a 24-hour outage on the approved Windows/hv01/hv02 canaries.
   Record continuing phases, randomized slow retries, unchanged process and
   identity, and bounded tasks/sockets/queues/logs. Restore the backend and
   record authentication, first authoritative heartbeat, Online and capability
   recovery without any client-side intervention.
6. Separately test actual process-crash/service-manager recovery and native
   update/rollback when desired. A backend outage should not itself restart
   the client.

Do not replay old terminal/job commands or fabricate missed monitoring samples.
Legitimate persisted Flow/outbox deliveries retain their durable action and
receipt identity and follow the existing retry/deduplication business rules.

## Evidence and optional functional checks

Required CI runs source/disclosure checks, one Release build, and the existing
fast Core/API/component selection. The excluded categories are `compose`,
`hosted` and `manual-integration`. Deterministic virtual-time tests establish
policy behavior; real PostgreSQL tests establish database guarantees; offline
adapter fixtures establish policy decisions. None is an installed-host or
physical overnight soak observation.

The owner can choose these existing optional Linux workflows after publication:

```sh
gh workflow run integration-validation.yml --repo BostonTechnologies/netratel \
  -f suite=gateway -f source_ref=<approved-source-sha>
gh workflow run integration-validation.yml --repo BostonTechnologies/netratel \
  -f suite=native-client -f source_ref=<approved-source-sha>
gh workflow run integration-validation.yml --repo BostonTechnologies/netratel \
  -f suite=upgrade-local -f source_ref=<approved-source-sha> -f release_tag=<approved-release-tag>
```

Release-image and upgrade suites use `release_tag`; ordinary suites use
`source_ref`. This workflow does not establish native Windows SCM or macOS
installed-host acceptance. Distribution verification can be complete with
scope `build-distribution-integrity` and no required runtime smokes while
functional acceptance remains pending owner testing. Unexecuted optional
checks and owner acceptance must not be reported as passed.
