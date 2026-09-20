# Local-first endpoint and permission inventory

Baseline inspected at `7ef86801c80b6cdf3c495ce7ff0a4fff41fa8ac4` for
[P00](https://github.com/BostonTechnologies/netratel/issues/30). This is the
authoritative starting inventory for P03; it is deliberately an inventory, not
evidence that the current administrator-group policies meet scoped-RBAC needs.

## Current boundary

`NetRatel.API/Program.cs` retains the legacy authenticated **Operator**
compatibility policy for unported routes. Scoped routes use one effective
access evaluator and the explicit `InstanceAdministrator`,
`TenantAdministrator`, `ClientManager`, `TelemetryReader`,
`TerminalOperator`, `RemoteSupportOperator`, and `McpOperatorPolicyAdmin`
policies. The latter preserves the legacy OIDC `netratel.mcp.admin` scope
requirement. Other explicit policies are `Operator`, `AkkaShadowAccess`,
`ClientArtifactsWrite`, `ClientArtifactsUpload`, `ClientArtifactsDownload`,
`HealthRead`, `M2MOnly`, `AgentAccess`, `AgentGatewayAccess`, and
`MachineTokenApi`. The API also has OIDC, machine-token, M2M, system and native
agent schemes. This preserves the existing trust-path separation while
allowing durable local and external principals to be evaluated with tenant
scope on the ported routes.

## Route families and target checks

| Route family | Current endpoint policy | Current target boundary | P03 permission family / acceptance owner |
| --- | --- | --- | --- |
| `/api/v2/tenants`, tenant cards and tenant-scoped lists | `InstanceAdministrator` / `TenantAdministrator` | Route `tenantId` plus service queries | Tenant administration; inventory/list/search isolation |
| `/api/v2/agents/{tenantId}/{agentId}` including telemetry, logs, presence and streams | `ClientManager`, `TelemetryReader`, `TerminalOperator`, `RemoteSupportOperator`, `FileReader`/`FileWriter` | Tenant and agent IDs; gateway services | Client inventory, telemetry/log read, streaming revalidation |
| Job definitions, parameters, steps, runs and requests | `job.manage` | Parent job/run/request tenant is resolved before read, list, update, cancellation, deletion, and SSE delivery | Legacy OIDC `Operator` remains compatible; tenant-scoped roles cannot cross the resolved resource tenant |
| Direct agent tasks, activity history and task logs | `script.execute` | Persisted task tenant is resolved before object/log read; list/history results are filtered before paging | Dispatch, command admission, idempotency, and target checks remain in force |
| Files, artifacts, downloads and uploads | `FileReader`/`FileWriter`, artifact policies, or `M2MOnly` | Tenant/agent/artifact ownership | File read/write/delete and artifact/update publication |
| Terminals and remote support | `TerminalOperator`, `RemoteSupportOperator`, or `M2MOnly` | Tenant/agent/session ownership; existing transport checks | Terminal and remote-support permissions with current target policy retained |
| MCP operator client, file, observability, command, script, job, task and request routes | mostly `M2MOnly` | Existing MCP policy/profile, confirmation, idempotency and target checks | Integration-management plus operation-specific effective access; no gateway bypass |
| MCP policy administration | `McpOperatorPolicyAdmin` | Persisted policy/profile IDs | MCP policy administration and audit; legacy OIDC scope remains required |
| `/api/v2/branding` and managed branding assets | anonymous effective presentation reads; `InstanceAdministrator` for update/upload | Singleton deployment record and opaque database asset IDs | Global presentation only; no tenant context, arbitrary path/URL fetch, or credential/configuration disclosure |
| Development MCP/onboarding and operator-target routes | `Operator` | Tenant/agent and grant IDs | Enrollment/client-management and scoped target administration |
| Agent enrollment, refresh, updates and gateway transport | `AgentAccess` / `AgentGatewayAccess` | Native agent identity and enrollment state | Native Client identity remains separate; no operator credential reuse |
| Client artifacts and update publication | `ArtifactPublisher` plus explicit artifact policies | Artifact/release ownership | Artifact/update publication; preserve native updater contract |
| Health, OpenAPI and operational endpoints | `HealthRead`, fallback, or explicit anonymous metadata where present | No business resource | Instance administration / deliberate pre-ready status only |

## P03 enforcement contract

P03 maintains a testable endpoint-registration inventory. New or ported
protected routes must have an explicit permission mapping or an approved
non-business exception. It covers direct object reads, lists, counts, search,
downloads, SSE/WebSocket paths, gateway admission, command dispatch and
scheduled or background execution. Any existing confirmation, target,
environment, approval, or idempotency restriction remains an additional
constraint.
