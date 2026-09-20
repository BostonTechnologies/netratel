# SQLite single-node deployments

NetRatel supports SQLite only for one API instance on one host. Set
`Database__Provider=Sqlite`, `Database__InstanceCount=1`, and an absolute
durable `ConnectionStrings__NetRatelDb` `Data Source` path. The application
rejects a memory database, a relative path, or an instance count other than
one. Do not place the database on a shared network volume or run more than one
API, migration runner, or host against it. Use PostgreSQL for replicas,
cross-host operation, or higher write concurrency.

For a source deployment, `docker compose -f compose.sqlite.yaml up --build`
starts a dedicated single-node stack. It has no PostgreSQL service dependency:
the one-shot migration runner and API share the `sqlite-data` volume. Existing
PostgreSQL deployments continue to use `compose.yaml`; changing a ready
instance between providers is not an automatic data migration.

SQLite uses its own versioned migrations for both application and identity
state. Run the migration container before the API and keep its database path
identical to the API path. Do not use `EnsureCreated`, an in-memory provider,
or a different migration assembly for production upgrades.

## Search and operational differences

PostgreSQL retains its `pg_trgm`/GIN indexes and `ILIKE` query plans. SQLite
uses escaped, case-insensitive `LIKE` comparisons for the same bounded search
surfaces. This is functionally equivalent for product searches but does not
provide PostgreSQL trigram performance on large directories. Client-update
notifications fall back to bounded polling because SQLite has no `LISTEN` /
`NOTIFY` mechanism.

## Backup and restore

Stop the API and migration runner, then take a consistent SQLite backup; do not
copy a live `*.db` file or its WAL files. For example, on a trusted operator
host with the SQLite CLI installed, run `sqlite3 /absolute/netratel.db ".backup '/absolute/backup/netratel.db'"`. Back up that resulting database together
with the API bootstrap directory, Data Protection key ring, agent signing key,
and any persisted branding/artifact volumes. Restore all of those to a private
staging host, retain the original absolute database path or update the explicit
connection string, run the migration container, and verify local identity,
bootstrap state, and a synthetic agent enrollment before use. Never replace
only the database while retaining unrelated keys or bootstrap state from
another installation. PostgreSQL backup and restore procedures stay separate
and retain their existing migration history.
