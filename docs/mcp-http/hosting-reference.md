# HTTP MCP host setup and recovery reference

Start with the [short host quickstart](setup.md). This reference covers alternate launch contexts, persistent storage, headless generation and coordinated recovery. Host approval and client approval remain separate decisions.

## Deployment ownership and saved sources

The host launcher reads the selected container's Compose metadata and resolves the ordered saved files and interpolation inputs privately. It checks service environment files and image defaults separately from the current serving configuration. Missing or inconsistent sources stop setup before approval. Restore the saved source at its actual location, then rerun discovery; do not invent a file or replace the existing stack with a new deployment.

For externally managed Compose, use the complete private deployment document at the printed export location. Review it and apply through the existing manager's normal saved-source workflow. Preserve interpolation values, secret sources, database, ingress and volumes. Replacing the saved source or its inputs invalidates the previous proposal; repeat review rather than bypassing the source guard. For an explicitly release-managed stack, use its reviewed apply command with the original ordered files.

## Persistent client storage and an existing container

The launcher supplies separate persistent host-setup and client-data storage. Keep both when containers are recreated. Missing storage is a prerequisite failure, not a reason to change setup modes or run the serving container as root.

For tooling in the HTTP image, use the launcher with `--target http-client`, `--target cli` or `--target stdio`. A supplied release bundle also includes `compose.setup.yaml` for deployments with explicitly configured source/storage metadata. This file is not expected in an externally managed stack. See [shared configuration](../integrations/configuration.md) for profile precedence and [troubleshooting](troubleshooting.md) for recovery.

## Advanced and headless hosting

The direct container command is `dotnet /app/NetRatel.Mcp.Http.dll setup --mode host`. Use it when that serving container already has the supported persistent setup volume and deployment-manager/source metadata. A missing-mount preflight is a prerequisite failure before administrator approval; changing client/server modes does not supply storage. For a custom stack missing those prerequisites, start with the deployment-host launcher above.

CLI and stdio distributions also support explicit `netratel setup --mode host` and `dotnet NetRatel.Mcp.dll setup --mode host`. Native Unix host generation uses its configured private directory or per-user application data `NetRatel/http-host`; an existing selected directory must be owned by the current user with mode `0700`. Native Windows host generation cannot verify the required private ACL boundary and stops before creating identity material; use the supported Linux Docker path. Windows client setup remains supported.

The server verification step uses a separate caller identity. Local credential mode requests a temporary client approval for the authenticated MCP/delegated read; it has a short server-enforced lifetime, a private cleanup record and idempotent cancellation/revocation on exit; interrupted cleanup is retried on resume. Administrator provisioning approval alone cannot supply client access. OIDC uses the selected provider and any required application-account details. Keep these identities separate from the persistent host service identity and from Netclaw's customer credentials.

Existing automation remains explicit. For a reviewed generation-only local-mode setup:

```sh
netratel setup --mode host --non-interactive \
  --deployment-manager release-compose \
  --auth-mode local \
  --public-origin https://netratel.example.invalid \
  --api-base-url https://netratel.example.invalid \
  --output-directory /absolute/path/to/reviewed-output
```

Generation-only success does not establish live readiness. Use the interactive verification/resume path against the applied deployment before declaring it connected. Preserve the configured public origin, selected HTTPS API allowlist and native gateway exposure. Web's private Compose defaults `http://api:9222/` and `http://mcp-http:9224/` describe its internal hops; they do not replace MCP's trusted outbound HTTPS API target.

Headless host options include `--auth-mode local|oidc`, `--required-groups CSV`, `--required-scopes CSV`, `--oidc-authority HTTPS`, `--instance-name dev|prod`, `--resource HTTPS_MCP_URL`, `--listen-port PORT`, `--allowed-origins CSV`, `--existing-outbound-config PATH`, `--existing-m2m-callers CSV`, `--api-m2m-audience AUDIENCE`, `--delegation-principal NAME`, and existing-generation inputs `--delegation-key-file PATH`, `--delegation-key-id ID`, `--delegation-issuer ID`, `--delegation-audience ID`. Supplied options seed the interactive wizard too. Use `--replace` only for a reviewed replacement.

Interactive discovery preserves the API's existing M2M audience and allowed callers, appending only the MCP service when needed. Setup does not change the API's global M2M issuer or audience, or replace other clients. For headless generation against a custom API service audience, supply its current value with `--api-m2m-audience` (default `netratel.api`) and preserve its existing caller IDs with `--existing-m2m-callers`; the selected audience configures only the new service registration and outbound scope.

An explicit resource such as `/prefix/mcp` requires a matching reviewed ingress/YARP mapping; the common deployment remains `/mcp`. Existing OIDC outbound identity needs a delegation principal matching the issued token subject, and M2M delegation needs its actual client ID. Existing protected JSON/key sources must remain accessible for reviewed setup and reuse. Setup preserves those managed originals, prepares protected runtime exports and remaps only their runtime references; it rejects a target rebind and does not rotate the adopted generation. Native tooling is available when custom input mounts differ between the host and a tooling container.

The **release-bundle path** generates `web.env`, `api.env` and `mcp-http.env` as configuration exports. Its apply overlay embeds the reviewed non-secret values and secret references; editing an export alone does not replace a value embedded in that overlay. The **Externally managed Compose path** generates the complete deployment document at the printed export path with private saved-request/runtime files. Import its complete Compose document into the editor; it does not generate separate service env exports or a release apply overlay. Rerun setup with reviewed inputs to regenerate and apply. Keep browser Data Protection, native AgentAuth signing keys, client credentials and host service/delegation generations in their separate namespaces and backups.

## Manual Docker/environment configuration

Use the [configuration reference](configuration.md) and the included `.env.mcp-http.example`, `.env.mcp-http.outbound.example` and `.env.mcp-http.api.example`. The outbound/API examples contain actual injected keys; the main template contains Compose interpolation inputs. It is a Compose interpolation template, not an arbitrary container `env_file`. For local mode, explicitly set **both** `NETRATEL_MCP_LOCAL_CREDENTIAL_MODE=true` and `NETRATEL_MCP_AUTHENTICATION_MODE=LocalCredential`; external OIDC uses `false` and `Oidc` with the required authority/group/scope values.

For the manual overlay path, supply a dedicated `mcp-http.outbound.env` with actual `NetRatel__Mcp__Outbound__...` keys and protected service-secret references, or select [the legacy config overlay](../../release/compose.mcp-http.config.yaml) and its absolute `NETRATEL_MCP_OUTBOUND_CONFIG_FILE`. The main MCP overlay does not inject blank outbound credentials over a working JSON identity. Use [the secrets overlay](../../release/compose.mcp-http.secrets.yaml) for an explicitly prepared `NETRATEL_MCP_SECRETS_DIRECTORY`; configure matching API service registration and key generation yourself when using this manual path.

MCP publishes no port in the normal single-origin deployment. [The optional port overlay](../../release/compose.mcp-http.ports.yaml) maps `${NETRATEL_MCP_HTTP_BIND_ADDRESS:-127.0.0.1}:${NETRATEL_MCP_HTTP_PORT:-9224}:${NETRATEL_MCP_HTTP_CONTAINER_PORT:-9224}` for a reviewed custom topology. Set `NETRATEL_MCP_HTTP_CONTAINER_PORT` to the selected `--listen-port` value when it differs from `9224`; the host port remains independently configurable. Change bind address deliberately. A split-host deployment sets explicit private Web destinations and the independently trusted HTTPS outbound API target. Container `localhost` never points at another service.

## Client tooling in the HTTP image

The existing HTTP MCP image includes CLI and stdio setup/runtime assets at build time. Its dispatcher supports default/`http` serving, `cli ...`, `stdio ...`, and `setup --target http-host|cli|stdio|http-client`. For a externally managed Compose deployment, the launcher supports `--target cli` and `--target stdio` with the same actual Compose/project inputs shown above. For a supplied release-bundle deployment, client profiles use its separate persistent client-data volume:

```sh
docker compose --env-file .env -f compose.setup.yaml run --rm --no-deps \
  mcp-http setup --target cli
```

Use `--target stdio` for an isolated stdio profile or `--target http-client` for a canonical-resource HttpMcp profile. An HTTP client secret belongs to its own namespace, never the host's outbound profile. The wizard's returned launch/client snippet uses a secret reference rather than printing a bearer. Third-party clients need their own supported secret-injection configuration.

Run stdio protocol serving without a TTY, preserving the same mounted profile/secret paths. Native distributions use their actual `NetRatel.Mcp` apphost or `dotnet NetRatel.Mcp.dll`; the HTTP image uses `stdio` through its dispatcher. For raw Docker pipes, disable the image's HTTP health probe with `docker run -i --rm --no-healthcheck ... IMAGE stdio`, supplying the explicitly mounted profile and secret references. `cli config validate`, `cli doctor`, and corresponding explicit stdio tooling commands emit redacted diagnostic results. Stdio doctor performs bounded initialize/catalog/authentication-status validation when the local stdio executable is installed, using only the selected effective configuration; unavailable or unexecuted protocol checks remain pending with exit `2`. Ordinary stdio initialization emits JSON-RPC only on stdout. See [CLI/stdio guidance](../integrations/CLI_AND_MCP.md) for native commands, browser approval and exit behavior.

## Rotation

Rerunning setup does not rotate keys. Rotate intentionally using protected new material, a new key ID where applicable, and coordinated API/MCP updates. There is one configured delegation generation; an incompatible partial rollout must fail rather than fall back to old or different credentials.

For delegation rotation, drain/stop MCP, update the protected generation and matching issuer/audience/service principal/key ID references on API and MCP, restart/recreate API with the reviewed configuration, then start/recreate MCP. Validate configuration, initialize and a permitted delegated read before resuming callers. Plan this interruption; there is no advertised overlapping-key hot rotation.

Rotate an outbound service secret through its actual API registration and MCP secret reference in the same coordinated maintenance window. Do not revoke an unrelated client's personal credential. For a personal client credential, create/approve its replacement, save and validate the intended consumer, then revoke the old credential through the account page.

## Migrate an existing stack

Preserve the current explicit destinations, isolated outbound JSON, selected HTTPS API allowlist, service identity, protected delegation generation and persistent key mounts. The supported container flow adopts coherent effective canonical environment/configuration sources securely; supplied values are not evidence of completed approval or verification. Do not copy an inline delegation key into a new file by hand to progress through setup.

For an explicitly managed native import using existing protected files, choose the outbound JSON/key reference in Advanced, or use these native inputs:

```sh
netratel setup --mode host --non-interactive \
  --deployment-manager release-compose \
  --auth-mode local \
  --output-directory /absolute/path/to/reviewed-output \
  --public-origin https://netratel.example.invalid \
  --api-base-url https://netratel.example.invalid \
  --existing-outbound-config /absolute/path/to/private/mcp.json \
  --delegation-key-file /absolute/path/to/private/delegation.key \
  --delegation-key-id existing-generation \
  --delegation-issuer netratel-mcp-prod \
  --delegation-audience netratel-api-prod
```

Use the deployment's actual existing identifiers rather than these placeholders. For an existing OIDC outbound token, additionally supply `--delegation-principal` matching its issued service subject. Preserve the original sources and mounted generation. Setup validates exact API-target equality and rejects source changes between review and apply; it does not replace a supplied key. Mount an existing supported JSON using the config overlay, or review its migration to dedicated outbound environment/file references; do not remove a working secret file until the replacement is verified.

When enabling the route, configure the trusted public origin and matching API/MCP canonical resource, then select ingress authentication. The release-bundle path includes the MCP overlay to enable Web as well as the service; externally managed Compose uses the complete generated document. Preserve custom `ReverseProxy` routes/destinations; generated defaults and explicit overrides follow the [routing guide](../deployment/web-gateway.md). Apply/recreate the affected containers and verify each stage in [troubleshooting](troubleshooting.md).

Successful local validation or an unauthenticated 401 does not complete migration. Owner acceptance should exercise browser URL/code approval, CLI/stdio read-only execution, MCP initialize/read, denial/revocation, persistent settings after recreation, explicit custom destinations and applicable external OIDC discovery. Record unavailable or unauthorized checks accurately.
