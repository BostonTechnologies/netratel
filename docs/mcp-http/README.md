# HTTP MCP operator guide

HTTP MCP lets Netclaw and other MCP clients use the NetRatel API. First connect the MCP server to the platform once, then give each client a scoped credential for that endpoint. Server/client modes describe the two sides of the connection, not home/company use or tenant count. The standard deployment exposes `/mcp` at the trusted Web HTTPS origin while its listener remains private. Ordinary Web/API and native Client operation does not require MCP.

- [Set up the server, then connect clients](setup.md): start here for the host/client command table, runnable Docker-host and container-terminal commands, browser approval, deploy, verify and client setup.
- [Give Netclaw customer connections access](../integrations/netclaw-http-mcp.md): supported HTTP transport, protected credential storage and customer isolation checks after server setup.
- [Host setup and recovery reference](hosting-reference.md): deployment ownership, alternate launch contexts, storage, headless generation and rotation.
- [Configuration reference](configuration.md): every host, outbound, delegation and Compose setting, defaults, secret-file mappings and precedence.
- [Authentication modes](authentication.md): local integration credentials, browser client approval and explicit external OIDC.
- [Bounded operator policies in Web](authentication.md#bounded-operator-policies-in-the-web-ui): inspect policy/target bounds, review and confirm lifecycle changes, and retain audit readback.
- [Computer use](computer-use.md): exact Windows application targets, separate desktop view/control permissions, confirmed actions and image observations through the enrolled agent.
- [Local credential mode](local-credential-mode.md): the API-bound credential/delegation flow.
- [Troubleshooting and verification](troubleshooting.md): configuration, TLS, routing, authentication, initialize, published tool argument contracts and one permitted read-only call.
- [Web/YARP routing](../deployment/web-gateway.md): generated defaults and manual JSON/environment overrides.
- [CLI and stdio MCP](../integrations/CLI_AND_MCP.md): separate tooling entry points and isolated identities.
- [Release installation](../../release/INSTALL.md): verified image bundle and first-run Web/API setup.

[The production activation manifest](production-activation-manifest.yml) is an existing owner acceptance record template for OIDC deployments. It is not runtime configuration or evidence that production checks passed. Local mode has no OAuth metadata or OIDC authority; use the applicable mode's procedures.

## Script folder placement

The production `netratel_scripts/create` and `update` schemas accept optional
`folderPath`. Omit it to retain the default `/mcp-operator/<tenantId>/` on create
or preserve placement on update. `/` selects the logical library root; nested
destinations such as `/Linux/disk/` use the same normalization and collision
rules as Script Library. Placement never changes ownership or tool authority.

To move an existing owned script, read its definition and parameter schema,
retain its reviewed content/hash and metadata, then call `update` with the
destination, `expectedVersion` and `expectedSourceRevision` from the current
read. Preview first and confirm the unchanged request using the returned plan
token and idempotency key. Changed destinations, stale revisions and collisions
are rejected. Read back `folderPath` and the new versions; script ID, source
bytes/hash and existing references are preserved. An omitted destination remains
backward compatible. Development's admitted canonical source variant retains
its separate source-revision/content-hash contract; discover the active schema
before choosing a request shape.

Monitoring rule authoring, explicit nullable fields, CPU/disk examples and typed
validation recovery are documented in the canonical [Monitoring guide](../operations/MONITORING.md#author-rules-through-http-or-stdio-mcp). The same schemas and guidance are published by HTTP and stdio catalogs.

## HA Groups and job targets

HTTP and stdio publish the same closed schemas for
`netratel_execution_groups`, `netratel_job_targets`, `netratel_jobs` and
`netratel_job_runs`. Use the [CLI/MCP operation table](../integrations/CLI_AND_MCP.md#execution-groups-and-multi-node-jobs)
and [Jobs guide](../operations/JOBS.md) for target modes, typed runtime inputs,
filter grammar, immutable plans and node-specific output.

Group management does not grant execution authority. Dynamic discovery and its
counts respect current target visibility. Explicit-node admission requires
the whole set; HA pools contain eligible authorized workers and select one
healthy worker per fresh invocation. Successive HA runs balance across the
pool, and offline members are skipped. Compatibility tool/route names retain
`execution_groups` / `execution-groups`. Preview is read-only. Confirm
the unchanged operation with its returned review credentials. Review binds the
complete target-plan digest, definition/inputs, settings and effective per-agent
policy versions. A changed group cannot widen an old preview. Target/fan-out
limits and configured remaining concurrency quota apply atomically to the
whole request; transports do not simulate a multi-node run by looping over
singleton launches.

An aggregate result identifies one parent, counts and distinct node/native-run
references. Fetch logs with node and step-run identity, retaining authorization
and redaction. An old singular operation that cannot represent the requested
set returns an explicit validation/unsupported result.
