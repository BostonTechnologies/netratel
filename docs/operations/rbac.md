# Scoped RBAC

NetRatel evaluates access as one tuple: stable principal, permission, tenant,
resource, credential (when present), and operation policy. A role from tenant
A and a role from tenant B are never combined into a grant for either tenant.

## Principals and compatibility

Local accounts and validated OIDC identities each have a durable NetRatel
principal ID. OIDC identity linking uses issuer plus subject only; email is not
an authorization or linking key. Existing OIDC `Operator` group/role mappings
remain an explicit instance-administrator compatibility path during migration.
They are not copied into local accounts and do not create new assignments.

## Role catalog

The protected built-ins are `InstanceAdministrator`, `TenantAdministrator`,
`Operator`, `Observer`, `ScriptEditor`, `IntegrationAdministrator`, and
`Publisher`. They cover tenant/user administration, client management,
telemetry, script editing/execution, jobs, terminal/file/remote-support,
secret use/reveal, audit, integrations, artifact publication, and MCP policy
administration. The catalog inserts only missing roles at startup; it never
rewrites an existing role or custom role during an upgrade.

Custom roles use the same fixed permission vocabulary. Their creator must not
delegate permissions beyond the creator's active scoped authority. Assignments
are explicit principal-to-role-to-tenant records; a null tenant is an
instance-wide assignment. Removing the last instance administrator is rejected.

## Enforcement and change semantics

The API is the authorization authority. Browser navigation is only a
projection and is never a permission boundary. Local sessions are revalidated
on each request; role and assignment changes are therefore reflected on the
next operation. Existing long-running work is not retroactively elevated;
subsequent dispatch and target/operation-policy checks use current access.
MCP target/environment policy, confirmations, and command admission remain
additional constraints for delegated operations and cannot be bypassed by a role.

Execution groups reuse tenant authority: `tenant.admin` manages groups;
`tenant.admin` or `job.manage` reads groups and permitted target-directory
previews. Group membership is not an execution grant. Job admission checks the
complete resolved set, and current per-node authority still applies when queued
work dispatches. A denied static member rejects the whole new admission;
dynamic discovery stays within the caller's visible inventory. Offline clients
retain their identities and receive explicit dispatch outcomes.

Delegated multi-node review binds all approved IDs, definition/plan digests,
inputs, execution settings and policy versions. Each agent must have a current
grant; a broad policy on another member cannot supply it. `MaxJobTargetCount`
and `MaxFanOut` bound a request's complete set. The optional
`MaxConcurrentJobTargets` separately bounds remaining active job-target
reservations, consumed atomically for the whole plan. Existing policies without
that optional constraint retain their existing fan-out semantics. A changed
group cannot widen an old confirmation. See [Jobs](JOBS.md) for immutable plans,
failure/cancellation behavior and owner acceptance.

Unresolved/Unknown parents retain their concurrency reservation; a lost response
does not establish that physical capacity is free. Group administration tooling
uses `tenant.admin`; job operators use the read-only `job-targets groups/group`
discovery path under `job.manage`, without gaining management authority.

Interactive instance administrators manage the policies themselves through current
application authority, not a self-granted MCP parent policy. Their policy changes
still require exact preview/confirmation, current authority, version checks and
immutable change audits. Scoped policy administrators retain bounded-parent
requirements. Credentials and service tokens cannot use this account-only path.

The instance-administration API is at `/api/v2/access`; its compact browser
editor is `/admin/access`. They provide the role catalog, local-account
listing, and assignment operations. The API is protected by an
instance-administrator check and deliberately has no provider callout:
local-user administration does not depend on Authentik or another IdP.
