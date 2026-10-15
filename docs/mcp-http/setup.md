# Deploy and set up HTTP MCP

Start with a working Web/API instance and its verified image bundle. HTTP MCP is optional; ordinary Compose remains Web/API-only until its overlay is selected. Use one trusted public HTTPS origin and the existing native gateway exposure. No separate MCP DNS hostname or backend IP entry is needed.

Client connection and host administration are separate setup modes. Browser client approval creates a scoped API-purpose or HttpMcp-purpose personal credential. Host setup prepares deployment-owned ingress, outbound service identity, API delegation and persistent Web/API/MCP configuration; it does not borrow a human's HttpMcp bearer.

## Start with the instance Web URL

Run setup from the verified release bundle directory. The standalone tooling service uses the HTTP MCP image already pinned in `.env`; it does not start the serving stack or need its unrelated interpolation values. For the bundled public HTTPS stack, retain its actual existing Compose files in the generated apply action:

```sh
export NETRATEL_SETUP_COMPOSE_FILES='compose.images.yaml,compose.public-https.yaml,compose.mcp-http.yaml'
docker compose --env-file .env -f compose.setup.yaml run --rm --no-deps mcp-http
```

Use the files selected by the actual deployment: include an existing external-database or other overlay, or omit the bundled HTTPS overlay when a separately managed ingress already supplies HTTPS. The comma-separated selection is deployment context; the wizard still starts with one Web URL.

The service defaults to `setup --target http-host`. The equivalent explicit command is:

```sh
docker compose --env-file .env -f compose.setup.yaml run --rm --no-deps \
  mcp-http setup --mode host
```

The first page has one input, **Instance web URL**, with keyboard focus ready for paste. Enter either `https://netratel.example.invalid` or `https://netratel.example.invalid/mcp`, with or without a trailing slash, then press Enter. Setup retains the explicit HTTPS host/port, derives the API origin and canonical `/mcp` resource, and discovers the instance. Unknown paths, embedded credentials, query strings and fragments require correction rather than being silently discarded.

Follow Next to discover, authorize, review, save/apply and verify. Use the displayed browser link and code to approve from the instance's signed-in administrator session; a browser on another device works for a browserless container. Client approval and host approval are separate: host setup prepares or adopts a dedicated outbound service identity and its API delegation. A personal HttpMcp bearer is never stored as that identity.

Setup selects the supported authentication mode from discovery and preserves an existing managed/OIDC configuration. If external-provider details are still needed, enter them when prompted after discovery. **Advanced** contains alternate storage, explicit API/backend targets, profiles, ports, split-host/resource overrides and existing protected identity inputs. Normal setup does not ask for those details on its first page.

The standalone service creates and reuses the persistent Docker volume `netratel-http-host-setup` at `/setup-output`. It does not ask for a directory or require operator UID/GID flags. Keep this volume when recreating tooling and serving containers. Native tooling uses its configured location first, then the per-user application-data directory `NetRatel/http-host`; the final review shows the resolved path. An explicit output directory remains available through Advanced or `--output-directory`.

The native CLI and stdio distributions support the same explicit host mode:

```sh
netratel setup --mode host
dotnet /path/to/NetRatel.Mcp.dll setup --mode host
dotnet /path/to/NetRatel.Mcp.Http.dll setup --mode host
```

Normal server or stdio protocol startup never opens a wizard.

## Apply the reviewed configuration, then resume verification

Saving configuration is a local step. When Web/API/MCP need recreation, setup displays **deployment apply pending**, with one complete apply command for the deployment host. Review and run the command from the deployment directory, then return to **Verify**. Do not report Connected from a saved file or a successful browser approval.

The standard generated configuration includes protected runtime exports of the same generation for API UID `1654` and MCP UID `10001`, their read-only mounts and a restricted supplemental group. Setup originals remain owner-only; runtime exports grant only the required readers access. There is no manual secret copying, group creation, YAML bind-source repair or world-readable secret workaround in the supported path. The tooling container has no Docker socket and cannot recreate another container itself.

Use the exact generated command. It exports only the generated non-secret Compose overlay, retains the selected image/ingress/database files and recreates Web, API and MCP. Do not print a complete secret-bearing `docker compose config`; use `config --quiet` if inspecting configuration. Compose `2.24.4` or newer supports the generated `!override` maps/lists, which prevent stale MCP environment aliases or outbound credentials from surviving an earlier overlay.

Rerun the same setup command after recreation or an interrupted terminal session. It reuses the saved origin, identity and delegation generation; it does not rotate them or provision duplicates. Review an intentional replacement explicitly. Existing read-only/environment-managed settings remain managed by their source rather than being overwritten.

Verification uses the selected effective configuration and actual transport: compatible discovery and HTTPS reachability, canonical public MCP routing, applicable authentication, MCP initialize/catalog and a permitted read-only API-backed operation through outbound/delegation. A 200 HTML fallback, 401/403, open port, file write or unexecuted check cannot complete verification. A rejected/expired approval, disabled MCP, wrong route, incompatible instance or unconfigured host leaves a specific next action and a bounded Retry/Verify step. Temporary personal verification credentials remain separate from persistent host identity.

In local-credential mode, Verify requests a second, personal HttpMcp browser approval after deployment apply. That temporary credential remains in memory and is cancelled/revoked when setup exits; host administrator approval alone cannot supply it. OIDC verification uses its selected provider and requests any required application-account details separately.

The final summary distinguishes configuration saved, deployment apply pending, authenticated and end-to-end verified, and shows the persistent location. Back up the setup volume/generation along with the existing deployment configuration. A container restart retains old environment values; changes requiring recreation must use the displayed apply action.

## Advanced and headless hosting

Existing automation remains explicit. For a reviewed generation-only local-mode setup:

```sh
netratel setup --mode host --non-interactive \
  --public-origin https://netratel.example.invalid \
  --api-base-url https://netratel.example.invalid \
  --output-directory /absolute/path/to/reviewed-output
```

Generation-only success does not establish live readiness. Use the interactive verification/resume path against the applied deployment before declaring it connected. Preserve the configured public origin, selected HTTPS API allowlist and native gateway exposure. Web's private Compose defaults `http://api:9222/` and `http://mcp-http:9224/` describe its internal hops; they do not replace MCP's trusted outbound HTTPS API target.

Headless host options include `--auth-mode local|oidc`, `--required-groups CSV`, `--required-scopes CSV`, `--oidc-authority HTTPS`, `--instance-name dev|prod`, `--resource HTTPS_MCP_URL`, `--listen-port PORT`, `--allowed-origins CSV`, `--existing-outbound-config PATH`, `--existing-m2m-callers CSV`, `--api-m2m-audience AUDIENCE`, `--delegation-principal NAME`, and existing-generation inputs `--delegation-key-file PATH`, `--delegation-key-id ID`, `--delegation-issuer ID`, `--delegation-audience ID`. Supplied options seed the interactive wizard too. Use `--replace` only for a reviewed replacement.

Interactive discovery preserves the API's existing M2M audience and allowed callers, appending only the MCP service when needed. Setup does not change the API's global M2M issuer or audience, or replace other clients. For headless generation against a custom API service audience, supply its current value with `--api-m2m-audience` (default `netratel.api`) and preserve its existing caller IDs with `--existing-m2m-callers`; the selected audience configures only the new service registration and outbound scope.

An explicit resource such as `/prefix/mcp` requires a matching reviewed ingress/YARP mapping; the common deployment remains `/mcp`. Existing OIDC outbound identity needs a delegation principal matching the issued token subject, and M2M delegation needs its actual client ID. Existing protected JSON/key sources must remain accessible for reviewed setup and reuse. Setup preserves those managed originals, prepares protected runtime exports and remaps only their runtime references; it rejects a target rebind and does not rotate the adopted generation. Native tooling is available when custom input mounts differ between the host and a tooling container.

The generated `web.env`, `api.env` and `mcp-http.env` are configuration exports. The apply overlay embeds their reviewed non-secret values and secret references; editing an export alone does not replace an environment value embedded in that overlay. Rerun setup with reviewed inputs to regenerate and apply. Keep browser Data Protection, native AgentAuth signing keys, personal credentials and host service/delegation generations in their separate namespaces and backups.

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

Run stdio protocol serving without a TTY, preserving the same mounted profile/secret paths. Native distributions use their actual `NetRatel.Mcp` apphost or `dotnet NetRatel.Mcp.dll`; the HTTP image uses `stdio` through its dispatcher. For raw Docker pipes, disable the image's HTTP health probe with `docker run -i --rm --no-healthcheck ... IMAGE stdio`, supplying the explicitly mounted profile and secret references. `cli config validate`, `cli doctor`, and corresponding explicit stdio tooling commands emit redacted diagnostic results. Stdio doctor performs bounded initialize/catalog/authentication-status validation when the local stdio executable is installed, using only the selected effective configuration; unavailable or unexecuted protocol checks remain pending with exit `2`. Ordinary stdio initialization emits JSON-RPC only on stdout. See [CLI/stdio guidance](../integrations/CLI_AND_MCP.md) for native commands, browser approval and exit behavior.

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
