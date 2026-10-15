# NetRatel image bundle

This bundle extracts at its archive root. Run commands from that directory.
Candidate/rehearsal bundles contain image placeholders and cannot be pulled.
Only an owner-approved promoted bundle includes verified immutable image digests.

Prerequisites: Docker Engine with Compose v2, OpenSSL, and a Linux host that
supports the published linux/amd64 images. Fresh local-account setup does not
need OIDC or SMTP. An optional OIDC or hybrid deployment needs a configured
HTTPS OIDC provider that allows the Web callback and logout URLs for its public
origin.
CLI and stdio MCP linux-x64 archives are framework-dependent and require the
.NET 10 runtime; the CLI NuGet tool additionally requires the .NET SDK to install.
Native Client archives are self-contained and platform-specific.

1. Download the matching Compose archive and `SHA256SUMS` to one directory.
   Run `sha256sum -c --ignore-missing SHA256SUMS` there.
2. Extract that one `netratel-compose-*.tar.gz` into a new directory and enter it.
3. Copy `.env.images.example` to `.env`. A promoted bundle supplies image digests;
   retain these. Replace the database password; leave the OIDC settings empty
   for local-account mode, or set them for a deliberately configured OIDC or
   hybrid deployment. Set `NETRATEL_PUBLIC_ORIGIN` and
   `NETRATEL_PUBLIC_GATEWAY_ORIGIN` to the actual public HTTPS origins, or leave
   both blank for a base deployment whose addresses will be set on Branding.
   For a shared ingress, explicitly set both variables to the same origin.
4. Generate the API signing key in this directory:

   ```sh
   umask 077
   openssl ecparam -name prime256v1 -genkey -noout -out .netratel-agent-es256-private.pem
   sudo chown 1654:1654 .netratel-agent-es256-private.pem
   sudo chmod 600 .netratel-agent-es256-private.pem
   ```

   Keep `NETRATEL_AGENT_AUTH_PRIVATE_KEY=./.netratel-agent-es256-private.pem`.
   Compose resolves this path relative to the Compose file, not the env-file.
5. Validate and start:

   ```sh
   docker compose --env-file .env -f compose.images.yaml config --quiet
   docker compose --env-file .env -f compose.images.yaml pull
   docker compose --env-file .env -f compose.images.yaml up -d
   docker compose --env-file .env -f compose.images.yaml ps -a
   ```

The migration and volume-init services must exit zero before API starts; the
one-shot volume helper prepares named storage for the non-root UID 1654 API
and Web processes. Web binds to
`127.0.0.1:8080` by default.

Open `http://127.0.0.1:8080` on the Docker host. The first-run wizard asks for a
one-time setup code. Retrieve it from the running API container:

```sh
docker compose --env-file .env -f compose.images.yaml exec -T api \
  cat /var/netratel/bootstrap/setup-proof
```

Enter the code, choose an administrator email and passphrase, and use those
details to sign in. There is no default administrator password. From an API
container console, use `cat /var/netratel/bootstrap/setup-proof` directly.
Run `dotnet NetRatel.API.dll --setup-status` there if the code is unavailable.
See [First-run setup](docs/deployment/FIRST_RUN_SETUP.md) for recovery cases.

For public HTTPS, use the included `compose.public-https.yaml` and
`nginx.public-https.conf`. Put a valid certificate and key at the paths in
`.env`, set the actual public host and HTTPS origin, choose a free Docker
subnet, and set `NETRATEL_ALLOW_INSECURE_LOCALHOST=false`. Include
`-f compose.public-https.yaml` after the base file on every Compose command.
The overlay configures the trusted proxy, allowed host, secure cookies, API's
exact bootstrap origin, and shared key-ring application identity. Never trust
arbitrary forwarded hosts.

Before generating client installers, configure both **Site URL** and **Gateway
URL** on `/admin/branding`, or through the API options `Branding__SiteUrl` and
`Branding__GatewayUrl`. Compose maps the two public-origin variables to these
options; deployment-owned fields are locked in the editor. Gateway URL is the
public HTTPS native HTTP/2 origin. Ingress forwards it to private h2c
`api:9223`; clients must receive the public address. New installers include
both addresses even when they share a host. Generate a fresh protected link
after changing addresses; previously issued links keep their original script.
For standalone Client Docker deployments, set
`NetRatelCLIENT__Client__ApiBaseUrl` and `NetRatelCLIENT__Gateway__Endpoint`.
See [configuration](docs/deployment/CONFIGURATION.md) for precedence and split REST hosts.

Optional HTTP MCP is not enabled by default. To connect a **personal HTTP MCP
client** to an already configured instance, use the URL-first personal wizard:

```sh
docker compose --env-file .env -f compose.setup.yaml run --rm --no-deps \
  mcp-http setup
```

Inside the supported serving container, `dotnet NetRatel.Mcp.Http.dll setup`
opens the same personal flow. Profiles and protected personal credentials use
the separate persistent `netratel-http-client-data` volume, so Save does not
target `/app/.config`. Existing explicitly selected client settings remain in
effect. See [client persistence and existing-container recovery](docs/mcp-http/setup.md#persistent-client-storage-and-an-existing-container).

To provision the **deployment host**, select host mode explicitly:

```sh
export NETRATEL_SETUP_COMPOSE_FILES='compose.images.yaml,compose.public-https.yaml,compose.mcp-http.yaml'
docker compose --env-file .env -f compose.setup.yaml run --rm --no-deps \
  mcp-http setup --mode host
```

Select only the Compose files used by the actual deployment; include the
external-database or other existing overlays where applicable. The first page
asks for the instance Web URL; the root and `/mcp` forms both work. Approve the
host in the signed-in administrator browser, review and save, then run the
complete generated apply command on the deployment host and resume Verify.
The supported setup service manages persistent storage and protected runtime
secret access automatically. Keep its `netratel-http-host-setup` volume.

Host service identity, delegation and personal client approval remain separate.
An existing OIDC deployment retains its mode; local credential mode does not
invent an OIDC authority. See [HTTP MCP setup](docs/mcp-http/setup.md) for the
guided flow and advanced/manual alternatives. Declare readiness only after
the real authenticated MCP and API-backed read checks pass.

Back up PostgreSQL, signing keys and Data Protection volumes together. Do not
delete persistent volumes during upgrades. No local administrator credentials or
identity provider are included.

## External PostgreSQL

`compose.images.yaml` starts bundled PostgreSQL. For a deployment-owned server,
retain `compose.images.yaml` and add
`-f compose.external-postgres.yaml` to every command. Set
`NETRATEL_EXTERNAL_DATABASE_CONNECTION_STRING` to the dedicated NetRatel
database connection string. The override disables the bundled database rather
than requiring privileges that only its superuser has. Ensure the external
database is reachable before starting the one-shot migrations service.

For a missing code, run `dotnet NetRatel.API.dll --setup-status` inside the API
container. Restore a matching backup set after a partial database, bootstrap,
or key reset; do not delete one volume to reopen setup. The bundled
`docs/deployment/FIRST_RUN_SETUP.md` has the detailed checks.

## Same-origin Web and optional HTTP MCP

The [Web/YARP routing guide](docs/deployment/web-gateway.md) explains generated API destinations and custom overrides. The optional MCP overlay explicitly enables Web routing as well as the MCP service. [HTTP MCP setup](docs/mcp-http/setup.md) covers persistent host/tooling setup, local or external OIDC ingress, independent outbound identity, matching delegation and migration. Use the [full configuration reference](docs/mcp-http/configuration.md) for Compose interpolation versus actual injected .NET keys and secret-file inputs. Complete initialize and a permitted read-only tool call before reporting functional acceptance.
