# API reference

NetRatel serves its generated OpenAPI document at `/api/openapi/v1.json` and
its Scalar reference UI at `/api/docs/`. Both routes are anonymous so an
operator can inspect the contract before signing in; calling a protected
operation remains subject to the API's normal authorization boundary.

The UI is hosted by the Web application but reads the document through the
same-origin API proxy. It does not proxy credentials or store a server-side
secret. Scalar's **Try it** therefore uses the browser's normal same-origin
request rules, including antiforgery and an existing local or OIDC browser
session where that is the supported credential.

## Stable catalog

The release-generated document assigns every operation a deterministic ID and
one of these tags:

- Authentication/Accounts
- Setup/Instance
- Tenants/Access
- Clients/Enrollment
- Telemetry/Logs
- Scripts/Commands
- Jobs/Tasks
- Files/Terminal
- Remote Support
- Integrations/MCP
- Health/Diagnostics

Tags improve navigation only. They do not rename routes, environment-variable
aliases, cookies, MCP tool names, native Client enrollment paths, or existing
request/response contracts.

## Authentication shown by the reference

The document describes the credential boundary for each operation rather than
applying a blanket bearer requirement:

- Anonymous setup/status, discovery, branding, and explicitly public routes
  declare no credential.
- Local-account routes use the protected browser-session cookie.
- Interactive operator routes use the configured OIDC bearer boundary.
- M2M-only and delegated MCP routes use their dedicated bearer credentials.
- Native Client/agent and machine-token routes show their separate bearer
  schemes.

An integration credential is purpose-scoped. A normal API credential is not
an HTTP-MCP credential, and the HTTP-MCP exchange routes remain the only
place the delegated credential is accepted. Do not paste deployment secrets
into the reference UI.

## Client install link contract

`POST /api/v1/client-install-links` creates an idempotent, protected grant for
an existing verified local client artifact. Its JSON request names `tenantId`,
`runtimeId`, optional `artifactVersion`, `validForMinutes`, `maxUses`,
`installAsService`, `silentInstall`, and a caller-generated GUID
`idempotencyKey`. The JSON result includes the management `id`, selected
version and SHA256, expiry, remaining uses, public URL, install command, and
script preview. Repeating the same key and inputs returns the same grant;
changing inputs with that key is rejected. The route requires the
`ClientArtifactsWrite` operator policy.

`GET /api/v1/client-install-links` lists recent grant metadata, optionally
filtered by `tenantId`; `GET /api/v1/client-install-links/{id}` reads metadata;
and `POST /api/v1/client-install-links/{id}/revoke` revokes a grant and its
underlying enrollment code. These routes have the same operator policy.
Management responses should be handled as sensitive because creation returns
the protected script and capability URL.

Anonymous `GET` and `HEAD` on `/clients/install/{token}.sh` or `.ps1` return
raw script content only while the grant is active. Invalid, expired, revoked,
exhausted, extension-mismatched, and query-overridden requests fail. Fetches
do not consume enrollment uses. Clients should treat the URL as a bearer
capability and must not log it. The prior client-script file-response route
retains its existing response type for compatibility.

## Flows

`GET /api/v1/tenants/{tenantId}/flows/schema-capabilities` separates
`supportedDraftSchemas` from `executableSchemas`. Authoring and explicit draft upgrades
use the former; publication requires the latter and complete current dependency checks.
Schema-one drafts, immutable graph/configuration hashes, enum values and continuations retain
their historical path. An older writer cannot downgrade a stored newer draft.
Managed MCP Flows operations retain their schema-one input/export contract; their graphless
mutation routes cannot alter a schema-two definition.

Schema two retains `FlowGraphDto` with nullable-omitted additions. Its new kinds are execution
event (7), run library script (8), run job (9), rerun original execution (10) and finish (11).
The shared catalogue defines stable named ports independently of displayed labels. Each outcome
has at most one outgoing edge; alternatives converge as one exclusive token. Publication requires
all declared outcome routes, an acyclic reachable graph and valid typed inputs. Earlier outputs
must be available on every incoming path. General joins, cycles and arbitrary executable expressions
are not accepted.

The graph budget is 32 nodes, 64 edges and 128 KiB canonical JSON, including at most 32 input
bindings/node and eight finite world-coordinate route points/edge. Schema one's eight-node,
seven-edge and 32 KiB limits remain unchanged. Stable edge `id` and optional `route.points` persist;
hover, selection, live connection geometry and playback do not. Explicit migration keeps node IDs,
assigns edge IDs and exposes missing new outcome routes without rewriting published history.
Canonical cloning remaps graph-owned node/edge/action identities and bindings into a disabled draft.
For schema two, `FlowGraphSerialization` sorts JSON object members and normalizes exact numeric
spellings before size/hash calculation, so PostgreSQL JSONB round trips preserve the configuration
hash. Schema-one bytes and hashes retain their historical serialization; published versions are not rewritten.

Execution facts use `FlowExecutionEventDto` schema one with exact source, normalized and raw outcome,
definition/execution/attempt identity, target, timestamps, causation and bounded evidence references.
Execution identifiers remain strings across JavaScript; numeric IDs use decimal text without number conversion. The native fact budget is 16 KiB; the
source-aware runtime wrapper is 24 KiB. These limits are separate from the graph, prepared-action
and trace budgets. `occurredAtUtc` records the source transition/terminal-ready selection boundary under the durable
subscription locks and live database clock. Native `startedAtUtc`/`completedAtUtc` remain separate: a delayed
projection can commit after enable even when native completion was earlier.
Replay descriptors reference server-owned protected original inputs/definition/target;
they contain no credentials and confer no authority. The immutable run envelope retains up to 16
contributing source-event IDs and an omitted-default `sourceEventIdsTruncated` flag for additional
references known at admission. Later delivery receipts do not rewrite this flag or snapshot.
Run-detail reads require current source authority before exposing either; historical absent references
and withheld references remain unavailable rather than becoming an invented empty list.

`GET /api/v1/flows/tenants` returns authorized tenant names and effective flow permissions.
All routes below are under `/api/v1/tenants/{tenantId}/flows`; current tenant permission is
checked before definition/version/run disclosure.

| Route | Permission and behavior |
| --- | --- |
| `GET /`, `/template`, `/{flowId}` | `flow.read`; bounded definitions and the historical alert template. |
| `GET /schema-capabilities`, `/templates` | `flow.read`; explicit supported/executable schemas and named disabled configurable templates. |
| `GET /connectors`, `/execution-connectors?sources=Job&sources=Request` | `flow.read`; current authorized destination references. Source-aware lookup uses the selected native source permissions. |
| `GET /execution-definitions?sourceKind=Job&search=inventory&afterId=42&limit=50` | `flow.read`, `flow.execute` and native job/script permission; searchable metadata with decimal-string cursor and at most 50 results. |
| `GET /execution-definitions/{sourceKind}/{definitionId}` | The same source permissions; current revision/hash/input metadata and bound target, with unavailable references explicit. |
| `GET /execution-definitions/targets?sourceKind=Script&search=synthetic&afterId={agentId}&limit=50` | The same source permissions; eligible named target metadata and GUID cursor. Discovery does not promise online presence or future execution authority. |
| `GET /execution-definitions/targets/selected?sourceKind=Script&ids={agentId}` | Bounded retained target selections; repeat `ids` for at most 128 entries. |
| `POST /validate`, `/dry-run`, `/dry-run-execution` | `flow.read`; complete validation and deterministic inert evaluation. V2 preview supplies exactly one source plus optional explicit external outcome/output fixtures. |
| `POST /`, `POST /from-template` | `flow.edit`; new inert draft. Template creation uses a separate request with template key and name, and returns disabled/unpublished revision one. |
| `PUT /{flowId}/draft`, `POST /{flowId}/clone`, `PUT /{flowId}/enabled` | `flow.edit`; canonical revision-fenced mutation. Existing definitions require `expectedRevision`. |
| `POST /{flowId}/publish` | `flow.publish`; immutable version after current execution pin, connector authority/capability/revision and graph checks. |
| `GET /{flowId}/versions`, `/{flowId}/runs`, `/{flowId}/runs/{runId}` | `flow.read`; bounded paired-tenant history. |
| `GET /versions/{versionId}`, `/runs/{runId}` | `flow.read`; direct paired lookup for deep links. Source/AI/output permission checks redact unavailable data explicitly. |

Successful mutations return `FlowDefinitionDto`; publish returns `FlowVersionDto`. Rejections
use bounded safe codes: 409 for revision conflict, 429 for capacity and 422 for unavailable/unauthorized
dependencies. An unverified receiver returns `receiver-idempotency-unverified` and preserves the draft.
Definition/target query denials return 403; unsupported source families return 422 and malformed filters 400.
Execution definition metadata retains names up to 512 characters; exceeding that bound is explicit.
Pinned native `secret_reference` inputs accept only an environment-name reference matching
`[A-Za-z_][A-Za-z0-9_]{0,127}`. The reference name is passed only into protected canonical admission,
never resolved or rendered by preview, and is excluded from durable node input snapshots.

Schema-two save/validate clients send `X-NetRatel-Flow-Schema: 2` to select the finite graph body
budget before JSON binding; ordinary schema-one limits remain intact. The separate v2 preview body
budget includes bounded source and fixtures. Declared and chunked bodies follow the same limits.
Missing fixture output is unavailable; explicit JSON null and empty values stay distinct.
Recorded node timing uses the persisted start and trusted elapsed time across durable waits;
simulated timing remains null. Native output uses the existing authorized redacted projection,
with explicit missing, empty, withheld and truncated labels and exact string execution references. Preview
performs zero script/job admissions, AI turns, notifications and incident writes.

There is no browser/API execute route. Durable native transitions commit immutable source facts and
selected subscription intents; the trusted backend ingress consumes their exact IDs and rechecks
current source authority. Combined subscriptions aggregate only the configured version/trigger,
accepted attempt and outcome, retaining contributing facts. A proven job-backed request or script
step in an explicit combined aggregate waits for its parent terminal-ready result, even when Job is
not selected as a source. Request-only failures do not need a job.
Unknown/DeliveryUnknown is distinct from definitive failure or authoritative timeout.
See [Flows](../operations/FLOWS.md) for durable admission, replay, recovery and receiver behavior.

## Release verification

The protected release and PR workflows validate the generated surface with
the normal build/test matrix, Scalar Web routing, local-first browser journey,
and source/release-image OIDC Compose smoke. For runnable deployment and
credential examples, use [first-run setup](../deployment/FIRST_RUN_SETUP.md),
[configuration](../deployment/CONFIGURATION.md), [self-hosting](../deployment/SELF_HOSTING.md), and
[CLI and MCP](../integrations/CLI_AND_MCP.md).

## Reviewed MCP expiry

Reviewed access proposals and approvals accept exact UTC instants with at most
six fractional digits, matching PostgreSQL microsecond storage. Unsupported
precision is rejected before date parsing or persistence; an accepted expiry is
never rounded. The 30-day preset adds 720 hours to the displayed UTC reference.
See [reviewed setup](../mcp-http/setup.md) for protected delivery and lifecycle.

## Job input confidentiality

The server binds each submitted run to an immutable parameter sensitivity
snapshot and definition revision. Web parameters of type `secret` are masked
as `[REDACTED]` in both `inputsJson` and derived options. Ordinary values keep
their JSON types and original keys, including empty strings, Unicode, zero,
and false. MCP `secret_reference` parameters keep their opaque target-local
references; inline secret defaults remain rejected by the MCP contract.

Run detail/list/query responses, stream snapshots, linked task history,
step output, MCP job logs and linked request diagnostics use the same server
projection. The raw input is never sent to the browser for cosmetic masking.
There is no reveal API. Job-management access does not grant access to the
protected execution payload. Secret input values echoed verbatim in run
errors, task results, command labels and logs are also scrubbed before their
ordinary projections are persisted. Scripts must still avoid printing
transformed or encoded credentials; input redaction cannot infer arbitrary
transformations performed by user code.

Run responses include `inputProtectionStatus` and
`submittedDefinitionRevision`. `submitted-definition-v1` means sensitivity
was captured from the authoritative submitted definition. Editing parameters
or deleting the source definition cannot downgrade a retained run's snapshot.
Historical rows without a complete submission snapshot use
`legacy-unknown-v1`, with no fabricated revision. The first server read or
recovery protects the exact historical execution material and masks every
input/option and free-form diagnostic conservatively. This inspection does
not execute the job. Existing accepted terminal callback hashes are rebound
to the protected projection only after the original body matches its
committed hash; an existing mismatch remains rejected. Backups made before this upgrade can still contain
historical plaintext and need the deployment's normal protected backup
retention treatment.

Exact input and execution options are stored separately under the API's
Data Protection key ring, bound to the tenant, job and run (or linked request)
identity. Only internal execution, recovery and diagnostic redaction can access this
material; ordinary responses never include it. Restore the
original durable key ring alongside the database; missing keys fail recovery
with a value-free error. The migration refuses destructive downgrade once
protected payloads exist. See [self-hosting](../deployment/SELF_HOSTING.md)
for durable storage and backup requirements.
