# ADR 0003: provider selection and migration ownership

Status: accepted for implementation in P04.

NetRatel will support an explicit provider choice: SQLite for a single-node
deployment, bundled PostgreSQL, or externally managed PostgreSQL. Provider
selection is independent of authentication mode. An initialized descriptor
binds the instance to its selected provider; a connection failure never causes
fallback to another provider.

PostgreSQL migration history remains intact. SQLite has its own explicit
migration assembly/snapshot and provider-specific model/query implementations
where required. Setup, the migrations container, the API and design-time tools
must resolve the same provider and migration owner. Upgrade tests use real
migrations, never `EnsureCreated` or an in-memory substitute.

The current PostgreSQL model contains `pg_trgm`, GIN/trigram indexes, `jsonb`,
SQL defaults and PostgreSQL filters. P04 will retain their PostgreSQL semantics
and add honest SQLite equivalents for search, ordering, uniqueness,
concurrency, outbox/idempotency and durable command/job state. Unsupported
SQLite multi-instance topology is rejected; shared network-volume and
cross-host SQLite are not advertised.

Changing an existing provider is a separate documented data-migration process,
not an automatic setup option. Backup and restore cover application/identity
state, bootstrap state, Data Protection material, agent signing material and
branding assets.
