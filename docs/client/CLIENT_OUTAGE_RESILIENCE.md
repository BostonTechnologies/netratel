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

Bare gRPC `Unknown` follows backend recovery. Typed TLS/trust, permission and
nonrecoverable authentication causes still require attention, including when
wrapped by `Unknown`. A backend interruption does not justify deleting the
identity, enrolling again, or stopping the service.

## Update outcomes and result delivery

The native updater's installation result and the API's acknowledgement are
separate records. Protected `result-delivery.json` retains the original result,
attempt/release identity, delivery status, last bounded error code and next
retry eligibility beside the existing update-state files. An unresolved result
fences a newer activation so its evidence cannot be overwritten. A matching
authoritative Resume can release a delivered conflict's fence after its original
result and receipt have been archived, while retaining the unresolved
reconciliation status. Delivery never
launches the native updater again.

One delivery worker uses bounded requests and capped backoff with jitter for
transport failures, HTTP 429/5xx and unclassified plain 404 responses. Plain 404
can indicate an older Web route rather than a missing domain attempt. A rejected
access token permits one refresh within an operation. Structured permanent
rejections and authority conflicts retain the evidence and require inspection;
they do not generate a request every 30 seconds indefinitely.

The result API accepts only client-reportable progress/failure states. Native
validation or service-stop failure after `Activating` can truthfully report
`FailedPreActivation` because cutover has not happened. Only bound authoritative
gateway confirmation can establish `Accepted`. An identical committed result is
an acknowledgement replay: it does not rewrite outcome timestamps, repeat
activity events, increment policy/catalog revisions or restore a suspension
cleared by operator Resume. Failed-release suppression remains effective.

A confirmation committed before its response or local readiness marker is lost
can be replayed for the matching candidate and current presence owner. The
attempt, release, version, nonce and connection epoch remain bound. If the
native updater physically rolls back after server confirmation, Activity retains
the immutable confirmation and separate rollback reconciliation evidence. The
attempt's target and last observed running version are separate. Online alone
does not verify installation of the target.

Before taking operator action, inspect the protected request/state/activation,
ready/result/delivery/presence/policy/suspension records, sanitized
`UpdateManagement`/`UpdateService` logs, service journal and actual executable or
`current` link. Do not publish admission nonces, access/refresh tokens, enrollment
data or raw credential stores. Preserve unresolved evidence while repairing an
identified routing, authentication or installation problem; Resume is a policy
action and does not prove installation or reconcile contradictory results.

### Recovering a replaced local result

`identity_conflict` with `local_result_replaced` means an unresolved delivery
record and the current native result contain different bytes. The original
attempt remains in `result-delivery.json`; the replacement's attempt is not the
identity to resume. Recovery requires an explicit operator action after the
client has durably observed the conflict.

1. Deploy the API and client that support this recovery, then inspect the
   protected state directory used by the running service. Retain the original
   delivery record, its embedded native result, the replacement result,
   request/ready/suspension records and both server attempts. Let the result
   worker record `identityConflict.detectedAtUtc` and its observed policy
   revision. Verify client and server UTC clocks before comparing these times.
2. Review the actual installed version, native activation state and server
   confirmation separately. Issue a fresh **Resume** for that agent through
   the suspended-client administration control, or the existing authenticated
   administrative `POST /api/v1/client-updates/tenants/{tenantId}/agents/{agentId}/resume`
   endpoint when a local conflict has no server suspension. The endpoint uses
   the existing `ArtifactPublisher` authorization. It can create the initial
   policy state for an existing tenant agent; it does not create an update
   attempt. Scoped automatic-update Resume retains its suspension requirement.
3. The client binds Resume to the original delivery attempt. Recovery requires
   both a revision newer than the conflict observation and the server's actual
   `ResumedAtUtc` later than detection. Receipt time is insufficient. Missing
   timestamps from an older server, stale Resume, mismatching identities and
   changed archive bytes leave the fence in place. A newly changed result
   requires another observation and fresh operator action.
4. Verify protected archives and the final delivery status. Recovery preserves
   the embedded original payload, exact replacement bytes including any BOM,
   delivery metadata and available request/ready/suspension/policy snapshots in
   files it never overwrites. The original raw result may already be unavailable;
   its embedded payload is preserved without claiming byte-for-byte recovery of
   that lost file. An atomic move into a durably identified `.retained.json`
   archive prevents a concurrent native writer or process interruption from
   discarding unseen replacement bytes. `archived_unresolved` releases this
   local fence while retaining `reconciliationState=required`, the original
   installation outcome and unchanged server attempt history. Explicit Resume
   clears the active suspension through its normal policy path; archived
   suspension evidence and failed-release suppression remain.

An `Accepted` native result without `result-delivery.json` needs its own
diagnosis. Confirm the service's effective `AutoUpdate.StateDirectory`, loaded
client version, file protection and matching request/ready identities, then
inspect result-worker startup and local-state diagnostics. `Mode` defaults to
`Service`; offer eligibility depends on enabled Windows/Linux service mode,
while the result monitor itself starts independently of offer eligibility.
Windows PowerShell UTF-8 BOM output is supported. These facts do not identify
the cause of a particular missing receipt, and an unbound `Accepted` payload
must retain `confirmation_missing` rather than manufacture confirmation.

For campaign closure, record recovery of each affected original client
separately from a fresh automatic update on an owned test client. The latter
must observe offer, download, activation, matching readiness and authoritative
gateway confirmation, plus its durable terminal delivery status. A manual
upgrade or successful local archive is not that acceptance result.

### Owner retest for issue #250

After an approved deployment, record the API/Web/client versions and inspect
each reported client individually: its actual executable/current
link, observed version, historical attempt, verification time and suspension
reason. Keep the 13 October rollback outcomes separate from the 14 October
gateway interruption. Resolve the unnamed second log's agent identity before
assigning it to a host, and verify the deployed UI timezone before comparing
UI timestamps with UTC logs.

For attempt `414118ca-148f-45ef-9e76-67bd6ae594fe`, retrieve the original
notification Error/StackTrace and inner exception, submitted state, actual HTTP
response, persisted attempt/release/agent/policy records and request traces.
Check that delivery is durably acknowledged or has one explicit reconciliation
outcome, with repeated fault occurrences retained. Confirm ordinary recovery
through an observed backend return without false authentication attention.
Production outage injection, fleet restarts, blind Resume, identity/journal
deletion and release/rollout are outside this source-fix authorization. A later
canary update requires separate approval.

The source fixes and deterministic regressions establish the contract defects;
they do not identify the deployed exception, ingress failure or original native
rollback cause. Those incident attributions and installed-host acceptance remain
pending production evidence. Tracking: [issue #250](https://github.com/BostonTechnologies/netratel/issues/250).

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
