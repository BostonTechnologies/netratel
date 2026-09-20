# ADR 0001: durable local-first bootstrap runtime

Status: accepted for implementation in P01.

NetRatel will have four durable, API-owned states: `Unconfigured`,
`Configuring`, `Ready`, and `RecoveryRequired`. State is held in a protected
descriptor and transition journal on persistent API-owned storage; it is never
inferred from an empty user table, a missing file, migration history, or a
failed database connection.

Before `Ready`, the API exposes only liveness, setup status, safe built-in
branding, and setup operations authenticated with a deployment-controlled,
single-use bootstrap proof. Business APIs, agent admission, schedulers,
outbox work, Akka authority and optional integrations are not started merely
to fail later. `RecoveryRequired` is the outcome when a selected store cannot
be used after initialization, not a reason to create a new SQLite database or
reopen ownership setup.

Setup provisions and migrates the selected store before accepting an initial
administrator password. It commits the stable principal, tenant, initialization
marker, and non-secret setup data atomically in the selected provider, then
activates the descriptor using the same operation identifier. A restart that
observes the committed marker completes activation without duplicating an owner.

An established PostgreSQL/OIDC deployment is adopted only by an explicit,
idempotent compatibility check for meaningful existing application state and
verified identity mappings. A migrated-but-empty database is never adoption
evidence. Runtime composition is selected once at startup, not per request.

Configuration precedence is deployment-owned configuration, durable
administrator/setup values where allowed, then product defaults. Deployment
examples are not deployment-owned values.
