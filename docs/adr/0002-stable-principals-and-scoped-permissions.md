# ADR 0002: stable principals and scoped permissions

Status: accepted for implementation in P02 and P03.

Every interactive identity resolves to a durable application principal ID.
An external principal is linked only by verified issuer and subject; a native
principal is linked to its local Identity account. Email is profile/login data,
not an account-link or authorization key. Existing verified OIDC links remain
valid through adoption and upgrade.

Effective authority is evaluated as a tuple of principal, permission, tenant,
resource, and (when present) credential and operation policy. A selected
tenant may narrow an existing grant but cannot manufacture one. The API is the
authority for every protected operation; Web access projections control
navigation only.

P03 will replace administrator-group-only operational decisions with a single
effective-access service used by local users, OIDC users, integration
credentials, and delegated MCP execution. It will persist explicit role/scope
assignments and fail closed for unmapped protected endpoints. Built-in roles
are reconciled idempotently; custom roles are limited by the acting user's
delegation ceiling. Existing OIDC group mappings are preserved through an
explicit compatibility adapter, not recreated on every sign-in.

Role, membership, disablement and credential changes invalidate or revalidate
server-side authority on the next operation and have defined stream behavior.
They do not rely indefinitely on stale browser claims.
