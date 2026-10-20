# HTTP MCP verification and troubleshooting

Check each boundary separately. A valid configuration, responding route, authenticated initialize and authorized business read are different outcomes. Record completed checks and unavailable or denied checks separately; a 401/403 is not successful onboarding.

If you are deciding between client and server modes or setting up a externally managed Compose stack, start with [the server-first setup guide](setup.md). Server setup connects MCP to the API; client setup connects a consumer to that configured MCP endpoint. Switching modes cannot supply missing deployment storage.

## Bounded verification

1. **Configuration:** run the explicit `config validate` command for the chosen surface. It returns redacted target/auth/source information and exits nonzero for invalid or conflicting values. Render Compose with `config --quiet`; do not print secret-bearing configuration.
2. **Reachability:** confirm DNS, certificates and shared Docker network reachability. Web's private API/MCP HTTP destinations are separate from the host's selected trusted HTTPS outbound API URL.
3. **API routing:** fetch `/api/docs/` and `/api/openapi/v1.json` at the public origin. Expect the Web Scalar page and API JSON, respectively.
4. **MCP routing:** submit an unauthenticated MCP request. Inspect HTTP status, content type and challenge; verify the response is from MCP rather than Blazor HTML.
5. **Discovery:** external OIDC only: fetch exact `/.well-known/oauth-protected-resource/mcp` and verify resource/authority. Local mode must not advertise OAuth metadata.
6. **Authentication:** use an approved client to initialize with its proper HttpMcp-purpose credential or explicit OIDC token; check negotiated protocol/server information and errors.
7. **Backend health:** invoke `netratel_health` with `operation: "get"` through the saved client. Its bounded component results distinguish API liveness/readiness from protected health-route authentication admission. A failed readiness probe retains the liveness result. This observation covers `HealthRead` routing/admission only; use `netratel_auth/status` for delegated authentication and a permitted tenant read for business admission.
8. **Authorized read:** call `netratel_capabilities` with `operation: "get"` to inspect the catalog, then one permitted business read such as `netratel_clients` `get` for an approved tenant/AgentId. Use the returned closed schema, actual IDs and policy; success of the catalog call alone does not prove delegated tenant access.

An unauthenticated route probe needs no secret:

```sh
curl --silent --show-error --include \
  --header 'Accept: application/json, text/event-stream' \
  --header 'Content-Type: application/json' \
  --data '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"operator-route-check","version":"1"}}}' \
  https://netratel.example.invalid/mcp
```

A 401/403 can establish that an auth boundary responded, but it does not prove credentials, delegation or allowed tools work. For authenticated probes, use an MCP client configured with a protected secret reference; do not paste bearers into committed examples or diagnostics. Keep mutating tasks out of diagnostics. Observe incremental streaming through the actual outer ingress when that behavior matters.

## Published tool arguments

`tools/list` publishes a closed envelope for each selected operation. Supply `operation` and only its documented `request` and confirmation fields. Unexpected outer fields, unknown nested fields, missing required fields and invalid types return `error.code: "validation_error"`, `failure.layer: "input"` and bounded `data.fieldPaths` before operation authorization exchange or backend dispatch. Credential authentication still occurs at the HTTP boundary. Correct the fields using the selected operation's schema; the error never repeats input values. Typed parameter dictionaries remain open to their documented keys and value types.

Where the schema offers both forms, `tenantId: 17` and `tenantId: "17"` identify the same tenant. Int32 identifiers range through `2147483647`, Int64 identifiers through `9223372036854775807`; each field's published schema defines its own range. Decimal strings use ASCII digits without signs, spaces, fractions or leading zeroes. Preserve returned long IDs as strings: `"9007199254740993"` must never pass through a JavaScript number.

For `netratel_search`, omitted `q`, `q: ""` and whitespace-only `q` all mean an unfiltered bounded search. Nonempty Unicode text is retained. Explicit `offset: 0` and valid `false` values remain meaningful, and existing pagination limits and business admission still apply.

## Common failures

Local delegation errors include a bounded `failure.code`, `failure.layer`, `failure.retryable`, safe remediation and `correlationId`. `local_credential_permission_denied` identifies an active identity missing an application grant; check the capability's named permission and reviewed target. `local_delegation_target_invalid` requires correcting the published target shape. `local_delegation_binding_mismatch` requires refreshing the permitted persisted reference and target; ownership is never disclosed. Neither denial calls for credential replacement. Known previously issued executions distinguish `local_credential_expired` from `delegated_identity_revoked`; incoming unknown credentials do not disclose lifecycle details. Scope, role and missing/denied policy errors remain the receiving authority's separate classifications.

Permanent denials are non-retryable. Credential-service throttling preserves a bounded retry delay; timeout and unavailability retain dependency recovery guidance. Reconcile an uncertain mutation before resubmission. Connectivity preview is safe inspection; a confirmed probe requires the additional test grant and explicit operator review.

Read `delegationContext` and evaluation `interpretation` before using a diagnostic as evidence about another operation. A local `netratel_policy/evaluate` assertion is Admin-only: inspecting an Observe operation such as `netratel_terminal/availability` can return `oauth_scope_missing` for that inspection context. The unchanged denial does not show what scope a separate flow read receives. Status likewise reports its current assertion, not all configured grants. External OAuth context reports its supplied scopes. Use the owned `netratel_access/credential` admission or reviewed `receipt` for the exact operation, then a supported permitted read for current dispatch evidence. If the read instead returns `tenant_not_authorized`, verify matching active policies in the current environment through reviewed access; configured application grants alone do not supply tenant admission. Layers labeled `not_checked` remain unassessed. See [delegated status and policy evaluation](authentication.md#interpreting-delegated-status-and-policy-evaluation).

At initial native HTTP ingress, a full hash/purpose/resource match under the valid paired-resource proof permits `local_credential_expired` or `local_credential_revoked` with safe correlation. A wrong token, wrong purpose or wrong resource remains generic `local_credential_invalid`; no unrelated identity or credential identifier is disclosed. Update approved expired/revoked access through the reviewed lifecycle, then reconnect through the supported client transport.

| Observation | Check |
| --- | --- |
| `No persistent setup mount` or missing client storage | Run the custom-stack launcher on the **deployment host** using the actual Compose file/project, or use the matching extracted release bundle. Keep host setup and client storage separate; do not run the server as root. |
| `compose.setup.yaml` / `.yml` not found | This file belongs to the release bundle. An externally managed stack’s saved Compose files is a different supported path; use its launcher. Changing the extension cannot create an absent file. |
| Existing host identity/settings detected, but setup never completed | Reuse the supported effective config/environment generation through server setup. Config values are supplied settings, not proof of administrator approval or a verified connection. Do not rotate or manually copy keys to bypass the gate. |
| API/Web are separate hosts or instance shows `dev` | Retain the effective instance and enter the actual HTTPS API destination in server setup. A Web hostname is not automatically the separate API hostname. |
| Authentication choice required while Local credential text is visible | Select and confirm the actual supported mode in the explicit choice prompt. A default label is not proof that discovery or effective configuration established that mode. |
| `/mcp` returns Web HTML | MCP overlay selected **and** Web route enabled; exact `/mcp` route; correct destination; no fallback path rewrite |
| 502/504 or connection refused | Configured service DNS/network/port and backend readiness; do not switch targets automatically |
| Works directly, fails through ingress | Forwarded Authorization/protocol headers, trusted proxy/host, disabled buffering/caching, timeout budgets and exact metadata route |
| Local mode 401 | HttpMcp purpose, exact canonical resource, expiry/revocation/owner state, selected HTTPS API target and delegation authenticate route |
| Health reports `unmapped` | Verify the deployed API version and both exact `/api/v2/mcp/operator/health/live` and `/api/v2/mcp/operator/health/ready` paths through the selected HTTPS destination and its outer ingress. Deploy the alias-capable API before or with MCP; preserve the existing operator-prefix forwarding and API authorization. The source already retained protected root health routes, so a deployed 404 alone does not identify which hop failed. A readable MCP health resource establishes MCP host state only, and probes never fall back to another health surface. |
| Health reports `unready`, `unauthorized`, `unavailable`, `invalid_response` or `timed_out` | Review the named component and correlation ID. Readiness 503 with the API's fixed `api_readiness_unhealthy` code denotes an unready probe; a generic gateway 503 remains unavailable with an unknown backend cause; 401/403 denotes protected-route rejection; malformed JSON or an unexpected successful body is an invalid response. Unavailable/timeout requires restoring the configured destination. Responses intentionally omit dependency exceptions and upstream bodies. |
| Initialize works, business call denied | Current tenant permission, operation scope/target policy, matched delegation and independent gateway identity |
| OIDC metadata missing | OIDC mode selected; exact `/.well-known/oauth-protected-resource/mcp` routed to MCP; local mode deliberately advertises none |
| OIDC 401/403 | Actual authority/issuer, audience/resource, signing key/lifetime, required group/scope and provider registration |
| Redirect response from API/token endpoint | Automatic redirects are disabled. Verify the intended HTTPS origin/issuer, review the new endpoint and explicitly rebind/reauthorize; credentials are not automatically replayed |
| Outbound target mismatch | `ApiBaseUrl` equals the selected dev/prod allowlist HTTPS URL; no stale profile credential target; no HTTP container URL |
| Secret-file startup error | Mounted path exists, readable by serving UID, no symlinked path/parents, size/permissions valid, one secret source |
| Settings disappear after recreation | Persistent profile/output/key mounts and explicit file references; no setup output saved only in the container layer |
| `.env` value ignored | Compose interpolation mapping versus actual injected `env_file` key; use canonical .NET `__` key or implemented dedicated alias |
| `restart` does not apply environment | Recreate the affected services with the same approved Compose files/env input |
| stdio emits banners or ANSI | Run ordinary stdio protocol mode with pipes; explicit setup alone uses a TTY; diagnostics belong on stderr |

## Distinguish host service checks from client checks

**MCP container terminal:** bare `dotnet /app/NetRatel.Mcp.Http.dll doctor` diagnoses the server's backend identity. In API M2M mode it reports **token acquisition** separately from **service API authorization**. A rejected token request/invalid token response stops before the API probe. A later 403 identifies the API validation/caller/authorization stage; it does not establish that the secret was wrong. A missing service-status endpoint requires a compatible API upgrade alongside MCP. Diagnostics redact credentials and token responses.

Passing the service probe establishes that backend service identity only. It does not establish client tenant grants, MCP initialization or delegated business reads. Complete server setup's real verification. To inspect a separately saved HTTP-client profile instead, run `dotnet /app/NetRatel.Mcp.Http.dll doctor --transport http --profile default`; this exercises that client's credential and MCP path. A browser approval, a file write or account-only endpoint rejection cannot substitute for either result.

The [routing guide](../deployment/web-gateway.md), [complete configuration reference](configuration.md), [shared profile rules](../integrations/configuration.md) and [host setup](setup.md) explain the exact settings. Logs should identify the failed stage using redacted metadata, never device capabilities, user codes, bearer values or complete secret-bearing configuration.

Focused unit/component/configuration tests and optional HTTP image smoke exist in the repository. Full external ingress, browser, terminal/container and recreation acceptance remains opt-in or owner-run under [AGENTS.md](../../AGENTS.md). Preserve the actual status of checks that could not execute; historical evidence is not a current pass.

## Prompt arguments and client discovery

`prompts/get` requires `inspect_client.arguments.client` to be a nonblank string of at most 512 characters without control characters. Unicode names retain their exact text. `diagnose_job_run.arguments.jobRunId` requires a positive decimal-string Int64 identifier without whitespace, signs or exponent notation; retain large IDs as strings, for example `"9007199254740993"`. Missing or invalid required arguments return JSON-RPC `-32602` with the argument name and correction. Extra prompt arguments are ignored for compatibility; they do not expand authority or change the selected target. Check `prompts/list` for required argument metadata.

For `inspect_client`, first read `netratel://capabilities`, then use the published `netratel_search/tenants` and `netratel_search/clients` operations with `request.q`. Continue using the returned `hasMore`/`nextOffset` fields and the published `offset`/`limit` request fields. Retain the exact returned `tenantId`/`agentId` pair, then inspect `netratel_access/target` and `effective` before the approved read. No match, ambiguous names across tenants or denied discovery requires clarification or further authorized discovery. Never reuse another tenant's agent ID or guess a target. Names and returned text are data, not permission to mutate or expand scope.

Protocol fixtures cover these contracts. Native AI prompt workflows and health through the owner's actual selected deployment remain owner acceptance checks after reviewed admission.

## Client bridge streaming and recovery

The protected Windows launcher reports bounded `stage` and `pathRole` diagnostics. An installation, encrypted-source, reference, pending-state or temporary-directory failure requires selecting an existing local path with safe complete ancestry, then using the [checked acquisition and preparation commands](../integrations/http-mcp-bridge.md#retain-an-existing-windows-dpapi-credential). Preparation checks the effective temporary directory and returns `-TemporaryDirectory` in the launch entry; retain that argument when configuring the consumer or using `-AccessPrepare`. A successful preparation followed by a path rejection can mean that the consumer dropped the argument or the selected ACLs/ancestry changed. Runtime revalidates them. Create suitable new private directories; leave existing ancestry, original credential ACLs, global environment and machine execution policy unchanged. An existing profile remains compatible, and rollback restores the saved consumer entry and encrypted reference. These diagnostics contain no decrypted value, arbitrary exception or remote response.

For stdio-only consumers use the [versioned HTTP MCP bridge](../integrations/http-mcp-bridge.md), acquired from the verified stdio archive with its own protected HTTP-client profile. Confirm the actual launch entry and `http-bridge --version` before attributing buffering or cancellation behavior to the server. The campaign adapter simulation established external local adapter behavior; original source/installed provenance and live server attribution remain unproven.

`rate_limited` preserves typed Retry-After information; `transport_timeout` means a distinct total-budget expiry and requires state reconciliation before retry. Cancellation forwards the exact string or numeric `requestId` while the request is pending. `client_busy` means the local bounded outstanding limit was reached. Malformed stdin returns `-32700` with `id:null` and processing continues. Notifications receive no request-style response. An interrupted mutation or expired session is never automatically replayed. Keep supported installed transport/version, migration and live streaming/cancellation results in the private owner receipt; synthetic loopback results alone cannot mark those owner stages complete.

For `protectedSetupRequired`, prepare a create request with the supported client command in [reviewed setup](setup.md#reviewed-client-access-and-exact-expiry); never place its device capability in an MCP argument. For `reviewedRevision`, `reviewedProposal` or a denied resume, inspect the same request and credential first: an expiry/grant, target-profile, policy, environment or operation-mapping revision changed, or approver authority was withdrawn. Submit the newly reviewed exact proposal under a fresh request ID. Reuse the saved exact UTC expiry unless the operator explicitly reviews another instant. A policy-only allowed receipt still leaves runtime readiness and capability checks to actual dispatch.


## Configured grants with missing Production policies

If authentication succeeds but an exact tenant read returns `tenant_not_authorized`, open credential details and refresh **Configured grants and current admission**. For synthetic tenants 17 and 29, configured `flow.read` grants do not by themselves create matching Production `netratel_flows/list` policies. Inspect the current resource/instance/environment, exact expiry, durable owner permission, tenant visibility and matching policy facts separately. The bounded default samples do not assess every configured permission; unselected operations and runtime readiness remain not checked.

Use the [reviewed admission workflow](local-credential-mode.md#reviewed-admission) to reconcile the existing credential with exact tenant/operation targets. Retain the original grant set and expiry intent, obtain an authorized operator's approval and resume the same saved request ID. An owner with current policy administration authority may self-approve; inspect the explicit receipt mode. Refresh details afterward. Setup and doctor may show completed authentication with `admissionStatus=blocked`, `not_checked` or `unavailable`; those fields deliberately preserve a separate business-admission outcome. Do not widen scopes, replace the credential or install policies as a setup side effect. Confirm selected native reads only after admission, through the owner acceptance path.

## Compound job execution and classification-bound policy changes

A local `netratel_job_runs/start` assertion remains Execute-only. Starting a job
requires current `job.manage` and `script.edit` application entitlements in the
same tenant, including when an external OAuth identity enters the API. Internal
validation of the job's pinned scripts is part of that exact start contract; it
does not grant a reusable `netratel_scripts/get` Read assertion. Review the current
job and script ownership, version/hash, target profile and confirmation when a
start is rejected. Reconcile the actual run before retrying an uncertain start.
Confirmation binds the complete job definition and reviewed script contents and
parameter types. Every step uses that stored execution snapshot. Editing the
current job or script cannot change an already confirmed run's executable content.

Policy mutation preview and confirm both evaluate the canonical action below.
Use those canonical actions in exact operation selectors, rather than the names
of the transport's preview/confirm commands.

| Requested mutation | Canonical admission action |
| --- | --- |
| Create policy | `netratel_policy/create` |
| Replace policy | `netratel_policy/replace` |
| Disable policy | `netratel_policy/disable` |
| Revoke policy | `netratel_policy/revoke` |
| Create or update target profile | `netratel_policy/upsert_target_profile` |

For an exact-agent policy change, classification and tags come from its persisted
operator target profile. Caller-provided classification or the proposed profile's
new classification cannot substitute for current trusted state. A missing required
profile returns `target_profile_missing`; an independently authorized administrator
must first complete the supported reviewed profile bootstrap. Profile revision,
classification or tags changing after preview requires a fresh preview and
confirmation. Preserve the original intended classification selectors when
retesting; a policy with those selectors removed does not prove the protection.
The accepted audit and write hold the same profile guard, so a concurrent profile
update cannot change the classification between admission and persistence.

## Secret-designated task output and historical results

Library-script execution captures secret and `secret_reference` parameter
classification from the server's script definition. The native agent resolves
client-local secret references and masks declared values before output capture,
truncation, streaming and final results. Classification metadata contains parameter
names and revision evidence, not resolved environment values. Ordinary output
retains useful nonsecret values, including false, zero, empty strings and Unicode.

Update both the server and executing native agent for the complete path. If a
secret-bearing task returns no matching protection receipt, the server withholds
arbitrary output while retaining typed lifecycle facts such as exit code and
timeout state. Older agents can therefore produce masked output until upgraded.
Missing classification on historical library tasks is treated as unknown; editing
or deleting the current script never reclassifies their retained output as safe.

The schema migration adds output-classification and protected-history fields.
Ordinary task projections immediately withhold unclassified historical output.
Task detail/log inspection archives the original stored result, error, logs and
operator summary under the existing durable Data Protection key ring before
replacing visible output columns with masks. It preserves task and log IDs,
status, timestamps and cursors. This is application-assisted remediation, not an
eager rewrite of every historical row during schema migration. Retain the key ring
with deployment backups. A schema rollback is blocked once protected archives
exist, to prevent silently dropping preserved evidence.

The masking contract covers declared values, their JSON encodings and multiline
fragments. It does not claim to recognize every arbitrary transformation a script
could compute from a secret. No new ordinary-reader reveal path is introduced.
Retest fresh standalone scripts and jobs through task detail, logs, streams and
MCP projections with the intended ordinary identities; an earlier permission
denial does not establish that the output projection was reached and protected.

## Native service-operation time windows

Service-operation issue and expiry times are sent over the authenticated native
gateway with the current tenant/session and replay fencing. The payload has no
separate per-operation signature; signed HTTP MCP delegation is a different
boundary. The native client enforces a positive issuance lifetime of at most two
minutes and allows the server's issuance clock to be at most two seconds ahead.
It still rejects at or after actual expiry before native mutation. Queue or
inventory time never refreshes expiry; native controller execution remains bounded
by its existing 45-second timeout.

`service_operation_clock_skew` identifies an issuance time beyond that allowance.
`invalid_service_operation_window` identifies a nonpositive or overlong issuance
lifetime. `service_operation_expired` retains its genuine expiry meaning. Verify
normal clock synchronization and current server/agent versions; do not repeatedly
adjust clocks or extend the allowed lifetime to force an acceptance result. New
servers emit the persisted original issue time; updated clients also handle older
payloads without that field using a bounded remaining-window check. The fixed
native agent is required for the new validation behavior.

Use only a registered inert service for Start/Stop acceptance and read its actual
state afterward. The MCP bridge's product version is not the native agent version.
