# Set up the HTTP MCP server, then connect clients

To give Netclaw access to your NetRatel systems, complete these two connections in order:

```text
Netclaw customer connections → NetRatel HTTP MCP server → NetRatel backend/API
       client credentials           server service identity
```

1. **Connect the MCP server to the platform once for this deployment.** Server setup (`--mode host`) configures its dedicated backend service identity, delegation and coordinated API/Web/MCP settings. Apply the reviewed deployment changes, then complete verification.
2. **Connect each client to that working MCP endpoint.** For Netclaw, create and install a separate HTTP MCP credential for each customer connection. For NetRatel's own HTTP-client tooling, bare `setup` saves a client profile on the machine running it.

The mode describes which connection you are setting up. It does **not** describe home versus company use or the number of tenants. One credential can have several tenant grants; customer isolation requires separately restricted grants. A single-tenant installation still needs the server connection. Ordinary Web/API and native NetRatel Client operation does not require HTTP MCP.

For `customer1-netclaw`, `customer2-netclaw` and `customer3-netclaw` on the same backend, configure the server once and use the same canonical `/mcp` URL with three separate customer credentials. Follow [the Netclaw connection guide](../integrations/netclaw-http-mcp.md) after server verification. Distinct backends can require distinct HTTP MCP deployments.

| Command or selector | Configures | Where to run it |
| --- | --- | --- |
| `dotnet /app/NetRatel.Mcp.Http.dll setup --mode host` | MCP server → backend/platform | MCP container terminal with supported setup storage |
| Image command `setup --target http-host` | Same server setup as `--mode host` | Docker image dispatcher |
| `dotnet /app/NetRatel.Mcp.Http.dll setup` | A NetRatel HTTP client → MCP | Client machine or MCP container terminal with client storage |
| Image command `setup --target http-client` | Same HTTP-client setup | Docker image dispatcher |
| `setup --target cli` or `setup --target stdio` | Separate API/CLI or stdio client | Its tooling installation/container |

The supplied standalone `compose.setup.yaml` service has an explicit **host** default command. Adding a final `setup` replaces that default and selects the **client** flow. Use the explicit commands below to avoid relying on a default.

## Before you start

- **Versions and deployment access:** use API, Web, HTTP MCP image and setup files from the same verified release. Upgrade the custom stack's image references through its normal manager before setup. Have permission to use Docker on the Linux deployment host and to save/deploy the actual Komodo stack or Compose files. Keep the existing database, ingress and persistent volumes.
- **NetRatel account:** the Web instance must already be installed and reachable, with an administrator account for server approval. Client credentials require current authority for the selected tenant permissions. First-run Web/API installation is in [release installation](../../release/INSTALL.md).
- **Addresses and routing:** know the HTTPS **Web URL** and, if different, the HTTPS **API URL**. The first wizard page asks only for the Web root or `/mcp` URL. Setup reuses a separate API host from existing protected settings. If it is not already configured, choose **Advanced** on the URL page and enter **Separate API HTTPS origin** before Next. `/mcp` must route to the MCP service; an HTML page at that path is not a working MCP endpoint. Containers must reach the selected HTTPS API destination with valid TLS.
- **Authentication:** retain the deployment's actual Local credential or external OIDC mode. The wizard uses trustworthy effective configuration/discovery where available. If it cannot establish the mode, make the one explicit choice it requests. An OIDC deployment also needs its provider/client registration; local mode needs no OIDC authority.
- **Persistent private storage:** server identity/setup and client profiles need separate writable persistent locations. The custom-stack launcher below supplies these to one-shot tooling even when the serving container has neither mount. The reviewed generated Compose document retains them for recreation. Protected runtime exports give only the required API/MCP identities access; no root server, Docker socket or manual delegation-key copying is required.
- **Choose your deployment path:** a Komodo/custom stack normally has its own `compose.yaml` and optional `.env`. The launcher accepts one nonempty Compose document up to 64 KiB and does not need `compose.setup.yaml`. The release-bundle path uses the files extracted together from the matching Compose download. A file mentioned in a command must exist on the machine where you run it.

Commands below say **deployment host**, **MCP container terminal** or **client machine**. The deployment host runs Docker. A container terminal cannot recreate its sibling services. The client machine runs Netclaw or other MCP client software and has its own credential storage.

## 1. Connect the server: Komodo or a custom single Compose file

Use this path when Komodo's Compose editor owns the stack. The launcher ships in the verified release Compose bundle as `setup-http-mcp.sh`; extract that bundle to a separate directory so it does not replace your current deployment files.

**Deployment host:** locate the actual project and configuration file:

```sh
docker compose ls
docker ps --format 'table {{.Names}}\t{{.Image}}'
```

For example, if the project is `my-netratel` and its actual file is `/etc/komodo/stacks/my-netratel/compose.yaml`, run the following from the extracted matching-release bundle directory. Replace those two example values with the values you found:

```sh
./setup-http-mcp.sh \
  --compose-file /etc/komodo/stacks/my-netratel/compose.yaml \
  --project my-netratel \
  --manager komodo
```

The launcher uses `.env` beside that Compose file when present; supply `--env-file /actual/path/.env` if your interpolation file is elsewhere. Existing per-service `env_file` entries remain in effect. An empty Komodo Environment text box does not imply that service credentials are missing.

The launcher validates the actual file before starting, inherits the MCP service's existing settings and protected sources, and reuses its existing named setup/client volumes or supplies persistent ones. It copies an exact private snapshot of the Compose document into setup storage as the non-root tooling identity. A root-owned `0600` source file stays private with its original permissions; you do not need to make it readable by the serving container. Source/hash checks reject changes during review. The launcher does not look for a nonexistent setup overlay or change the running deployment by itself.

Existing Web-to-API/MCP destinations, including custom ports and paths supplied through service env files, remain in effect. The launcher reads only those two routing values from one running Web container. If a deployment-owned source hides a destination and no current value can be established, setup asks for that backend destination explicitly. Multiple serving Web replicas require selecting a single instance for this setup path.

In the wizard:

1. Paste the **Instance Web URL**, such as `https://netratel.example.invalid/` or `https://netratel.example.invalid/mcp`, then press Enter. Both forms work; credentials, queries, fragments and unrelated paths require correction.
2. Check the backend API destination displayed with administrator approval. Setup reuses existing `dev`/`prod`, protected outbound configuration and canonical delegation settings. If the API destination needs correction, choose **Back → Advanced → Separate API HTTPS origin** before approving. If authentication is unknown, a separate page asks you to select the current mode; its filled circle confirms the selection. Supplied settings alone are not a completed setup.
3. Open the administrator approval link in a signed-in browser, recognize the code and approve this server generation. You can use another device for an SSH/container terminal.
4. Review and **Save**. At **deployment apply pending**, choose **Export and exit**. The launcher exports a private complete `compose.komodo.yaml` and prints its actual host path.
5. Open that file privately. Replace the **entire Compose document** in the same Komodo stack's editor, then **Save and Deploy**. It preserves the existing ingress, database, ports, service env files and unrelated settings while adding the reviewed MCP configuration. Retain existing Komodo interpolation values. Do not paste a fragment or apply an extra CLI overlay that the next Komodo deployment will discard.
6. Run the exact command printed by the launcher, ending in `--verify`. It resumes the saved generation, without creating a second server identity or repeating administrator provisioning approval. Complete any client authentication requested for the real read-only verification.

The verification command has this shape, using the same actual paths/project as before:

```sh
./setup-http-mcp.sh \
  --compose-file /etc/komodo/stacks/my-netratel/compose.yaml \
  --project my-netratel \
  --manager komodo --verify
```

**Finished means end-to-end verified:** authenticated MCP initialize, full tool catalog and a permitted API-backed read through the real server identity/delegation path. **Configuration saved** and **deployment apply pending** are intermediate states. Keep the setup volume and private exported document with the deployment backup. After recreation, rerun verification against the same saved generation.

## 1. Alternate: connect the server using the release Compose bundle

Choose this path when the extracted release files own the deployment, rather than a custom editor. **Deployment host:** enter that existing release deployment directory; confirm that it contains `.env`, `compose.setup.yaml` and the image/ingress/database Compose files you use. These are supplied in `netratel-compose-<version>.tar.gz`; the wizard does not create a missing release bundle.

For the supplied public HTTPS deployment:

```sh
export NETRATEL_SETUP_COMPOSE_FILES='compose.images.yaml,compose.public-https.yaml,compose.mcp-http.yaml'
docker compose --env-file .env -f compose.setup.yaml run --rm --no-deps \
  mcp-http setup --target http-host
```

Select only the files used by your actual deployment. Include an existing external-database/other overlay; omit the bundled HTTPS overlay when another ingress supplies HTTPS. Enter the Web URL, complete administrator approval, review and Save. Run the **complete reviewed apply command** on the deployment host from that same directory, then resume Verify with the same setup command. The standalone tooling service uses persistent storage and the selected image from `.env`; no server starts inside it.

The generated apply action exports the non-secret Compose overlay, retains the selected image/ingress/database files and recreates Web, API and MCP. Compose `2.24.4` or newer is required for its `!override` maps/lists. Use `config --quiet` for validation; do not print secret-bearing rendered configuration. Restarting a container retains its old environment; apply requires recreation.

Host setup creates/reuses `netratel-http-host-setup` at `/setup-output`. Its originals are private; protected runtime exports allow API UID `1654` and MCP UID `10001` to read the same generation through a restricted group and read-only mounts. Keep that volume. Saving alone is not Connected.

## 2. Connect clients after the server is verified

For Netclaw, go to [Give Netclaw customer connections HTTP MCP access](../integrations/netclaw-http-mcp.md). It covers the `http` transport, encrypted credential storage, the required administrator-approved operation policy, three separate customer entries and allowed/denied tenant checks. Server setup and credential grants do not automatically create customer operation policies. A profile saved in the MCP container is not automatically installed into Netclaw.

For NetRatel's own client tooling, continue below. The installed native **NetRatel Client** agent, CLI, stdio MCP and third-party HTTP MCP clients are separate consumers; use the intended surface's command and credential purpose.

## Connect a personal HTTP MCP client

**MCP container terminal:** once the server is verified and its client-data mount is present, run:

```sh
dotnet /app/NetRatel.Mcp.Http.dll setup
```

**Client machine, native HTTP tooling:** replace `/app/` with its actual assembly directory. **Deployment host, supplied release bundle:** from that deployment's verified bundle directory, run:

```sh
docker compose --env-file .env -f compose.setup.yaml run --rm --no-deps \
  mcp-http setup
```

Include the final `setup` command to select the personal-client flow: the standalone service's unoverridden preset explicitly selects host provisioning. The tooling image digest comes from the existing `.env`; no server starts during setup.

The first page has one input, **Instance web URL**, focused and ready for paste. Enter `https://netratel.example.invalid` or `https://netratel.example.invalid/mcp`, with or without a trailing slash, then press Enter. Setup retains the HTTPS host/port and derives the API origin and canonical MCP resource once. Embedded credentials, unknown paths, query strings and fragments require correction.

Continue through discovery, personal browser approval, review, Save and actual read-only verification. Open/copy the displayed trusted URL and approve its code, tenants, permissions and expiry in the signed-in account. A browser on another device works for a container/SSH session. Existing managed/OIDC client settings are reused; any required external-provider fields appear progressively. Profile and transport changes are secondary under Advanced. A file write or browser approval alone is not Connected: HTTP setup checks authenticated MCP initialize/catalog and a permitted API-backed authentication-status read.

## Persistent client storage and an existing container

The supplied serving and tooling Compose services mount a separate personal-data volume, `netratel-http-client-data`, at `/var/lib/netratel/clients`. Its default HTTP-client profile is `/var/lib/netratel/clients/profiles/httpmcpclient/default.json`; protected personal secret files stay beside that profile. The volume is writable by MCP UID `10001`, survives recreation, and is never mounted into API. Host service/delegation originals remain in the separate host-setup volume.

Native tooling uses its selected profile/file first, then its per-user configuration directory. An explicit `--config`, profile selector or dedicated client environment remains authoritative; setup shows the effective location/source, retains existing settings and does not silently rewrite a read-only managed file or borrow the host's outbound JSON. A new container client default uses the persistent personal-data location, rather than `/app/.config`.

If an older/custom container has no persistent client mount, use the path that matches its deployment. **Komodo/custom single Compose, deployment host:** run the launcher against the actual file/project, adding `--target http-client`:

```sh
./setup-http-mcp.sh \
  --compose-file /etc/komodo/stacks/my-netratel/compose.yaml \
  --project my-netratel \
  --manager komodo --target http-client
```

It mounts separate persistent client storage for that tooling run. It does not configure the MCP server or install the saved profile in Netclaw. To add the mount to the serving container too, use the server setup/import/deploy path above.

**Release-bundle deployment host:** from the actual directory containing `.env` and `compose.setup.yaml`, use:

```sh
docker compose --env-file .env -f compose.setup.yaml run --rm --no-deps \
  mcp-http setup --target http-client
```

The supplied tooling service creates/reuses the protected client volume. If `compose.setup.yaml` is absent, this command is for a different deployment layout; changing `.yml` to `.yaml` cannot create it. Use the custom-stack launcher or extract the matching release bundle as appropriate.

For a prior failed Save under `/app/.config`, rerun client setup against the same Web URL and approve the client connection. Existing explicitly selected configuration/secret mounts remain governed by their source. Recreate the serving service using its saved updated Compose definition to add the client mount there. Keep both persistent volumes; do not make secrets public or run the server as root to resolve an access error.

**MCP container terminal:** after reopening or recreating the supported serving container, validate the saved client default explicitly:

```sh
dotnet /app/NetRatel.Mcp.Http.dll doctor --transport http --profile default
```

The `--transport http` selector matters for this diagnostic: bare HTTP `doctor` retains its existing outbound-host diagnostic contract.

## Advanced and headless hosting

The direct container command is `dotnet /app/NetRatel.Mcp.Http.dll setup --mode host`. Use it when that serving container already has the supported persistent setup volume and deployment-manager/source metadata. A missing-mount preflight is a prerequisite failure before administrator approval; changing client/server modes does not supply storage. For a custom stack missing those prerequisites, start with the deployment-host launcher above.

CLI and stdio distributions also support explicit `netratel setup --mode host` and `dotnet NetRatel.Mcp.dll setup --mode host`. Native Unix host generation uses its configured private directory or per-user application data `NetRatel/http-host`; an existing selected directory must be owned by the current user with mode `0700`. Native Windows host generation cannot verify the required private ACL boundary and stops before creating identity material; use the supported Linux Docker path. Windows client setup remains supported.

The server verification step uses a separate caller identity. Local credential mode requests a temporary client approval for the authenticated MCP/delegated read; it remains in memory and is cancelled/revoked when setup exits. Administrator provisioning approval alone cannot supply client access. OIDC uses the selected provider and any required application-account details. Keep these identities separate from the persistent host service identity and from Netclaw's customer credentials.

Existing automation remains explicit. For a reviewed generation-only local-mode setup:

```sh
netratel setup --mode host --non-interactive \
  --auth-mode local \
  --public-origin https://netratel.example.invalid \
  --api-base-url https://netratel.example.invalid \
  --output-directory /absolute/path/to/reviewed-output
```

Generation-only success does not establish live readiness. Use the interactive verification/resume path against the applied deployment before declaring it connected. Preserve the configured public origin, selected HTTPS API allowlist and native gateway exposure. Web's private Compose defaults `http://api:9222/` and `http://mcp-http:9224/` describe its internal hops; they do not replace MCP's trusted outbound HTTPS API target.

Headless host options include `--auth-mode local|oidc`, `--required-groups CSV`, `--required-scopes CSV`, `--oidc-authority HTTPS`, `--instance-name dev|prod`, `--resource HTTPS_MCP_URL`, `--listen-port PORT`, `--allowed-origins CSV`, `--existing-outbound-config PATH`, `--existing-m2m-callers CSV`, `--api-m2m-audience AUDIENCE`, `--delegation-principal NAME`, and existing-generation inputs `--delegation-key-file PATH`, `--delegation-key-id ID`, `--delegation-issuer ID`, `--delegation-audience ID`. Supplied options seed the interactive wizard too. Use `--replace` only for a reviewed replacement.

Interactive discovery preserves the API's existing M2M audience and allowed callers, appending only the MCP service when needed. Setup does not change the API's global M2M issuer or audience, or replace other clients. For headless generation against a custom API service audience, supply its current value with `--api-m2m-audience` (default `netratel.api`) and preserve its existing caller IDs with `--existing-m2m-callers`; the selected audience configures only the new service registration and outbound scope.

An explicit resource such as `/prefix/mcp` requires a matching reviewed ingress/YARP mapping; the common deployment remains `/mcp`. Existing OIDC outbound identity needs a delegation principal matching the issued token subject, and M2M delegation needs its actual client ID. Existing protected JSON/key sources must remain accessible for reviewed setup and reuse. Setup preserves those managed originals, prepares protected runtime exports and remaps only their runtime references; it rejects a target rebind and does not rotate the adopted generation. Native tooling is available when custom input mounts differ between the host and a tooling container.

The **release-bundle path** generates `web.env`, `api.env` and `mcp-http.env` as configuration exports. Its apply overlay embeds the reviewed non-secret values and secret references; editing an export alone does not replace a value embedded in that overlay. The **Komodo path** generates the complete `compose.komodo.yaml` with private saved-request/runtime files. Import its complete Compose document into the editor; it does not generate separate service env exports or a release apply overlay. Rerun setup with reviewed inputs to regenerate and apply. Keep browser Data Protection, native AgentAuth signing keys, client credentials and host service/delegation generations in their separate namespaces and backups.

## Manual Docker/environment configuration

Use the [configuration reference](configuration.md) and the included `.env.mcp-http.example`, `.env.mcp-http.outbound.example` and `.env.mcp-http.api.example`. The outbound/API examples contain actual injected keys; the main template contains Compose interpolation inputs. It is a Compose interpolation template, not an arbitrary container `env_file`. For local mode, explicitly set **both** `NETRATEL_MCP_LOCAL_CREDENTIAL_MODE=true` and `NETRATEL_MCP_AUTHENTICATION_MODE=LocalCredential`; external OIDC uses `false` and `Oidc` with the required authority/group/scope values.

For the manual overlay path, supply a dedicated `mcp-http.outbound.env` with actual `NetRatel__Mcp__Outbound__...` keys and protected service-secret references, or select [the legacy config overlay](../../release/compose.mcp-http.config.yaml) and its absolute `NETRATEL_MCP_OUTBOUND_CONFIG_FILE`. The main MCP overlay does not inject blank outbound credentials over a working JSON identity. Use [the secrets overlay](../../release/compose.mcp-http.secrets.yaml) for an explicitly prepared `NETRATEL_MCP_SECRETS_DIRECTORY`; configure matching API service registration and key generation yourself when using this manual path.

MCP publishes no port in the normal single-origin deployment. [The optional port overlay](../../release/compose.mcp-http.ports.yaml) maps `${NETRATEL_MCP_HTTP_BIND_ADDRESS:-127.0.0.1}:${NETRATEL_MCP_HTTP_PORT:-9224}:${NETRATEL_MCP_HTTP_CONTAINER_PORT:-9224}` for a reviewed custom topology. Set `NETRATEL_MCP_HTTP_CONTAINER_PORT` to the selected `--listen-port` value when it differs from `9224`; the host port remains independently configurable. Change bind address deliberately. A split-host deployment sets explicit private Web destinations and the independently trusted HTTPS outbound API target. Container `localhost` never points at another service.

## Client tooling in the HTTP image

The existing HTTP MCP image includes CLI and stdio setup/runtime assets at build time. Its dispatcher supports default/`http` serving, `cli ...`, `stdio ...`, and `setup --target http-host|cli|stdio|http-client`. For a custom single Compose deployment, the launcher supports `--target cli` and `--target stdio` with the same actual Compose/project inputs shown above. For a supplied release-bundle deployment, client profiles use its separate persistent client-data volume:

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

When enabling the route, configure the trusted public origin and matching API/MCP canonical resource, then select ingress authentication. The release-bundle path includes the MCP overlay to enable Web as well as the service; Komodo imports the complete generated document. Preserve custom `ReverseProxy` routes/destinations; generated defaults and explicit overrides follow the [routing guide](../deployment/web-gateway.md). Apply/recreate the affected containers and verify each stage in [troubleshooting](troubleshooting.md).

Successful local validation or an unauthenticated 401 does not complete migration. Owner acceptance should exercise browser URL/code approval, CLI/stdio read-only execution, MCP initialize/read, denial/revocation, persistent settings after recreation, explicit custom destinations and applicable external OIDC discovery. Record unavailable or unauthorized checks accurately.
