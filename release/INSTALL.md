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

1. Download all assets to one directory and run `sha256sum -c SHA256SUMS`.
2. Extract the matching `netratel-compose-<version>.tar.gz` into a new directory and enter it.
3. Copy `.env.images.example` to `.env`. A promoted bundle supplies image digests;
   retain these. Replace the database password; leave the OIDC settings empty
   for local-account mode, or set them for a deliberately configured OIDC or
   hybrid deployment.
4. Generate the API signing key in this directory:

   ```sh
   umask 077
   openssl ecparam -name prime256v1 -genkey -noout -out .netratel-agent-es256-private.pem
   sudo chown 2000:2000 .netratel-agent-es256-private.pem
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

The migration service must exit zero before API starts. Web binds to
`127.0.0.1:8080` by default. For remote use, terminate HTTPS at your reverse proxy,
choose `NETRATEL_WEB_BIND_ADDRESS` deliberately and provide a Compose override:

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
See [First-run setup](docs/FIRST_RUN_SETUP.md) for recovery cases.

```yaml
services:
  web:
    environment:
      ForwardedHeaders__KnownProxies__0: "203.0.113.10"
      ForwardedHeaders__AllowedHosts__0: "netratel.example.com"
```

Replace both example values with your actual proxy address and public hostname,
set `NETRATEL_ALLOW_INSECURE_LOCALHOST=false`, and use HTTPS at the proxy.
do not trust arbitrary private networks. Include the override with a second
`-f proxy.override.yaml` on every Compose command.

Optional HTTP MCP is not enabled by default. Its external-OIDC mode requires
separate OIDC audience, scope, group and target API configuration in `.env`.
Use both `-f compose.images.yaml -f compose.mcp-http.yaml` with `--env-file
.env` for config, pull and startup.

An explicit local-credential HTTP MCP alternative is documented in
`docs/mcp-http/local-credential-mode.md` in this bundle. It requires a
canonical HTTPS resource URL and a paired API/MCP delegation key; it does not
use or emulate an OIDC authority. Do not enable both identity modes for one
MCP host.

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
