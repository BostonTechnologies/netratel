# Historical SQLite installations

This page records the rc.5 single-node SQLite deployment boundary. Current
NetRatel releases support PostgreSQL only and reject an explicit SQLite
provider before creating replacement application state. The normal install
instructions are in [self-hosting](SELF_HOSTING.md).

If you operate an rc.5 SQLite instance, preserve the original installation
and its database, bootstrap state, Data Protection key ring, signing key, and
artifact storage together. Stop the old API and migration runner before
taking a consistent SQLite backup. On a trusted host with the SQLite CLI,
`sqlite3 /absolute/netratel.db ".backup '/absolute/backup/netratel.db'"`
creates a database backup; copying a live `*.db` file is unsafe. Keep the
backup private and test it on an isolated host.

There is no automatic lossless SQLite-to-PostgreSQL converter in NetRatel.
Plan and validate an explicit transition before running a PostgreSQL-only
image against a new database. Do not delete the old installation to force a
fresh setup or assume identity, signing, and Client continuity would survive.
