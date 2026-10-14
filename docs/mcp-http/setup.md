# Deploy and set up HTTP MCP

Start with a working Web/API instance and its verified image bundle. HTTP MCP is optional; ordinary Compose remains Web/API-only until its overlay is selected. Use one trusted public HTTPS origin and the existing native gateway exposure. No separate MCP DNS hostname or backend IP entry is needed.

Client connection and host administration are separate setup modes. Browser client approval creates a scoped API-purpose or HttpMcp-purpose personal credential. Host setup prepares deployment-owned ingress, outbound service identity, API delegation and persistent Web/API/MCP configuration; it does not borrow a human's HttpMcp bearer.

## Prepare a coordinated deployment bundle

The explicit host wizard runs before an HTTP server is configured. Native CLI tooling supports:

```sh
netratel setup --mode host
```

The stdio distribution also accepts explicit `setup --mode host` before normal protocol startup. For example, from its extracted framework-dependent archive:

```sh
dotnet /path/to/NetRatel.Mcp.dll setup --mode host
```

Choose an absolute persistent output directory, the trusted public HTTPS origin, the selected HTTPS API target and ingress mode. Private Compose backends default to `http://api:9222/` and `http://mcp-http:9224/`; advanced settings retain explicit reviewed destinations. External OIDC additionally requires its HTTPS authority, groups and scopes. Review the redacted changes and explicitly confirm replacing any existing configuration.

For a first-run container, use the standalone [setup Compose file](../../release/compose.setup.yaml), without the serving base or unrelated required interpolation. Set the approved existing HTTP MCP image digest in `.env`; keep that image's tooling dependencies, SBOM and notices together.

```sh
mkdir -m 700 mcp-setup
export NETRATEL_SETUP_OUTPUT_DIRECTORY="$PWD/mcp-setup"
export NETRATEL_SETUP_UID="$(id -u)"
export NETRATEL_SETUP_GID="$(id -g)"
docker compose --env-file .env -f compose.setup.yaml run --rm --no-deps \
  mcp-http setup --target http-host
```

The selected UID/GID must own the writable output directory. The image serving UID is `10001`; API uses `1654`. Setup can run as the operator's UID without root, a shell editor, package installation or a Docker socket. In the wizard, choose `/setup-output` as the container output directory. The standalone Compose file supplies its absolute host path through `NETRATEL_SETUP_HOST_OUTPUT_DIRECTORY`, so generated bind mounts reference the host directory, not an ephemeral container path. Interactive setup uses a TTY; normal stdio serving uses pipes.

The equivalent noninteractive command is explicit about reviewed inputs:

```sh
docker compose --env-file .env -f compose.setup.yaml run --rm --no-deps -T \
  mcp-http setup --target http-host --non-interactive \
  --output-directory /setup-output \
  --public-origin https://netratel.example.invalid \
  --api-base-url https://netratel.example.invalid
```

Headless host options also include `--auth-mode local|oidc`, `--required-groups CSV`, `--required-scopes CSV`, `--oidc-authority HTTPS`, `--instance-name dev|prod`, `--resource HTTPS_MCP_URL`, `--listen-port PORT`, `--allowed-origins CSV`, `--existing-outbound-config PATH`, `--existing-m2m-callers CSV`, `--delegation-principal NAME`, and existing-generation inputs `--delegation-key-file PATH`, `--delegation-key-id ID`, `--delegation-issuer ID`, `--delegation-audience ID`. Use the existing outbound option for a supported private JSON identity and mount its secret references deliberately; setup rejects a target rebind. An OIDC deployment must explicitly select OIDC and supply its required group/scope values. An explicit resource path such as `/prefix/mcp` requires a reviewed ingress/YARP mapping to the host's actual `/mcp` endpoint; generated routing defaults remain `/mcp`. An existing OIDC outbound identity also requires an explicit delegation service principal matching the issued service token subject; the login username alone does not establish that identity. An M2M delegation principal must match its actual client ID. Use `--replace` only after reviewing an intended replacement.

The output contains `web.env`, `api.env`, `mcp-http.env`, `compose.http-host.yaml` and `.gitignore`. Fresh setup also creates `secrets/delegation.key` and, for a newly provisioned M2M identity, `secrets/outbound-m2m.secret`. Supplying an existing delegation key preserves its original protected source and creates no replacement key in the output directory. Secret values live only in owner-only files; environment files contain references. Fresh setup generates independent random material once. When supplying `--delegation-key-file`, also supply its existing key ID, issuer and audience explicitly; setup validates and snapshots that source and metadata instead of inferring or rotating them. The generated API and MCP mounts reference the chosen existing file at the stable container destination `/run/netratel-mcp-secrets/delegation.key`. Repeating setup with the same inputs reuses the existing generation and files; conflicting files require explicit review. Existing outbound configuration remains isolated and is mounted without copying its credentials into Web or a human client profile. A new identity uses `netratel-mcp-http`; an existing different service principal requires its matching existing outbound JSON. When running setup inside a container, existing JSON, delegation key and referenced secret files must be deliberately mounted at the same absolute paths used on the host for the generated binds; setup cannot infer input host paths. Native tooling is an alternative when those input mount paths differ.

The generated API configuration provisions the actual `M2M` / `M2MClients` service registration, selected allowed callers and secret reference. Review the current deployment's allowed caller list before applying; preserve unrelated service identities. Human client pairing never provisions deployment credentials. API/MCP recreation applies that registration; the setup process cannot change another container's environment.

## Apply and persist

Use Docker Compose `2.24.4` or later. The generated final overlay embeds the reviewed non-secret Web/API/MCP settings and protected secret-file references. Web/API values compose additively, preserving unrelated database, browser auth and gateway settings. MCP uses a complete reviewed `environment: !override` map and `env_file: !override` list so earlier outbound credentials and aliases cannot survive unnoticed. Add additional MCP runtime settings through a later explicit reviewed override.

Compose `environment` values take precedence over `env_file`. The generated `web.env`, `api.env` and `mcp-http.env` remain reusable configuration exports; changing those exports alone does not change values already embedded in `compose.http-host.yaml`. Re-run setup with the reviewed inputs or update the reviewed final overlay, then recreate the affected containers. The final YAML contains secret references, never actual credential/signing-key values.

Export the generated owner-only secret material through the deployment's protected secret store with read access for API UID `1654` and MCP UID `10001`. Keep the generation consistent, mounted read-only and unwritable by unrelated users. Retain the owner-only setup originals for idempotent reuse. **Before `config` or `up`, update the generated overlay's bind-source paths**: API mounts must point to API-readable protected runtime exports, and MCP mounts to MCP-readable exports of the **same generation and bytes**. Keep their container destinations `/run/netratel-mcp-secrets/delegation.key` and `/run/netratel-mcp-secrets/outbound-m2m.secret` and `read_only: true` unchanged. For example, provide separate `0600` exports owned by UID `1654` and UID `10001`, respectively, or a deliberately restricted shared-group export. The delegation file is identical on both services; the newly provisioned outbound service secret is likewise identical at its API and MCP references. Apply the source-path change through the deployment's reviewed configuration mechanism. The generated bindings initially reference the setup-owned originals, so exporting copies alone does not make the stack use them. Do not make secrets world-readable to resolve a UID mismatch.

Apply the setup overlay after the MCP overlay, together with the deployment's existing HTTPS and database overlays. Example for a bundled PostgreSQL/public HTTPS stack:

```sh
docker compose --env-file .env \
  -f compose.images.yaml -f compose.public-https.yaml \
  -f compose.mcp-http.yaml -f mcp-setup/compose.http-host.yaml config --quiet
docker compose --env-file .env \
  -f compose.images.yaml -f compose.public-https.yaml \
  -f compose.mcp-http.yaml -f mcp-setup/compose.http-host.yaml up -d --force-recreate
```

Keep the reviewed immutable image digests and existing native gateway exposure. The generated overlay prepares settings only; it does not install certificates, alter ingress or claim live checks passed. Configure the selected public/trusted HTTPS API target consistently. Private Web-to-API/MCP HTTP does not satisfy MCP outbound HTTPS validation.

An already running tooling container can perform the same explicit setup if an operator has deliberately mounted a writable persistent output path:

```sh
docker compose --env-file .env -f compose.images.yaml -f compose.mcp-http.yaml \
  exec mcp-http setup --target http-host
```

Choose that mounted output path in the wizard. The normal serving deployment does not provide a writable setup mount automatically; add it explicitly or use the standalone one-shot flow. Inject `NETRATEL_SETUP_HOST_OUTPUT_DIRECTORY` with the actual host path when the generated bundle will be applied on the host. A read-only secret mount cannot be edited by setup.

Back up protected deployment configuration and secret generations. Keep them separate from browser Data Protection, native AgentAuth signing keys, human credentials and application data backups. Changing environment requires recreation, not a simple restart. Mounted runtime option/secret changes require process restart; Web's supported YARP file reload is a separate [routing contract](../deployment/web-gateway.md).

## Manual Docker/environment configuration

Use the [configuration reference](configuration.md) and the included `.env.mcp-http.example`, `.env.mcp-http.outbound.example` and `.env.mcp-http.api.example`. The outbound/API examples contain actual injected keys; the main template contains Compose interpolation inputs. It is a Compose interpolation template, not an arbitrary container `env_file`. For local mode, explicitly set **both** `NETRATEL_MCP_LOCAL_CREDENTIAL_MODE=true` and `NETRATEL_MCP_AUTHENTICATION_MODE=LocalCredential`; external OIDC uses `false` and `Oidc` with the required authority/group/scope values.

For the manual overlay path, supply a dedicated `mcp-http.outbound.env` with actual `NetRatel__Mcp__Outbound__...` keys and protected service-secret references, or select [the legacy config overlay](../../release/compose.mcp-http.config.yaml) and its absolute `NETRATEL_MCP_OUTBOUND_CONFIG_FILE`. The main MCP overlay does not inject blank outbound credentials over a working JSON identity. Use [the secrets overlay](../../release/compose.mcp-http.secrets.yaml) for an explicitly prepared `NETRATEL_MCP_SECRETS_DIRECTORY`; configure matching API service registration and key generation yourself when using this manual path.

MCP publishes no port in the normal single-origin deployment. [The optional port overlay](../../release/compose.mcp-http.ports.yaml) maps `${NETRATEL_MCP_HTTP_BIND_ADDRESS:-127.0.0.1}:${NETRATEL_MCP_HTTP_PORT:-9224}:${NETRATEL_MCP_HTTP_CONTAINER_PORT:-9224}` for a reviewed custom topology. Set `NETRATEL_MCP_HTTP_CONTAINER_PORT` to the selected `--listen-port` value when it differs from `9224`; the host port remains independently configurable. Change bind address deliberately. A split-host deployment sets explicit private Web destinations and the independently trusted HTTPS outbound API target. Container `localhost` never points at another service.

## Client tooling in the HTTP image

The existing HTTP MCP image includes CLI and stdio setup/runtime assets at build time. Its dispatcher supports default/`http` serving, `cli ...`, `stdio ...`, and `setup --target http-host|cli|stdio|http-client`. For a client profile saved to the persistent setup mount:

```sh
docker compose --env-file .env -f compose.setup.yaml run --rm --no-deps \
  mcp-http setup --target cli --config /setup-output/cli.json
```

Use `--target stdio` for an isolated stdio profile or `--target http-client` for a canonical-resource HttpMcp profile. An HTTP client secret belongs to its own namespace, never the host's outbound profile. The wizard's returned launch/client snippet uses a secret reference rather than printing a bearer. Third-party clients need their own supported secret-injection configuration.

Run stdio protocol serving without a TTY, preserving the same mounted profile/secret paths. Native distributions use their actual `NetRatel.Mcp` apphost or `dotnet NetRatel.Mcp.dll`; the HTTP image uses `stdio` through its dispatcher. For raw Docker pipes, disable the image's HTTP health probe with `docker run -i --rm --no-healthcheck ... IMAGE stdio`, supplying the explicitly mounted profile and secret references. `cli config validate`, `cli doctor`, and corresponding explicit stdio tooling commands emit redacted diagnostic results; ordinary stdio initialization emits JSON-RPC only on stdout. See [CLI/stdio guidance](../integrations/CLI_AND_MCP.md) for native commands, browser approval and exit behavior.

## Rotation

Rerunning setup does not rotate keys. Rotate intentionally using protected new material, a new key ID where applicable, and coordinated API/MCP updates. There is one configured delegation generation; an incompatible partial rollout must fail rather than fall back to old or different credentials.

For delegation rotation, drain/stop MCP, update the protected generation and matching issuer/audience/service principal/key ID references on API and MCP, restart/recreate API with the reviewed configuration, then start/recreate MCP. Validate configuration, initialize and a permitted delegated read before resuming callers. Plan this interruption; there is no advertised overlapping-key hot rotation.

Rotate an outbound service secret through its actual API registration and MCP secret reference in the same coordinated maintenance window. Do not revoke an unrelated client's personal credential. For a personal client credential, create/approve its replacement, save and validate the intended consumer, then revoke the old credential through the account page.

## Migrate an existing stack

Preserve the current explicit destinations, isolated outbound JSON, selected HTTPS API allowlist, service identity, protected delegation generation and persistent key mounts. Inventory actual container keys versus compact `.env` inputs first. To import an existing host generation, choose the protected outbound JSON and key file in the wizard's advanced fields, or use these explicit native inputs:

```sh
netratel setup --mode host --non-interactive \
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

When enabling the new route path, configure the trusted public origin and matching API/MCP canonical resource, choose ingress auth explicitly, and include the MCP overlay so it enables Web as well as the service. Preserve custom `ReverseProxy` routes/destinations; generated defaults and explicit overrides follow the [routing guide](../deployment/web-gateway.md). Apply/recreate the affected containers and verify each stage in [troubleshooting](troubleshooting.md).

Successful local validation or an unauthenticated 401 does not complete migration. Owner acceptance should exercise browser URL/code approval, CLI/stdio read-only execution, MCP initialize/read, denial/revocation, persistent settings after recreation, explicit custom destinations and applicable external OIDC discovery. Record unavailable or unauthorized checks accurately.
