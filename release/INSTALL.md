# NetRatel image bundle

This bundle extracts at its archive root. Run commands from that directory.
Candidate/rehearsal bundles contain image placeholders and cannot be pulled.
Only an owner-approved promoted bundle includes verified immutable image digests.

Prerequisites: Docker Engine with Compose v2, OpenSSL, a configured HTTPS OIDC
provider, and a Linux host that supports the published linux/amd64 images.
The provider must allow the Web callback and logout URLs for your public origin.
CLI and stdio MCP linux-x64 archives are framework-dependent and require the
.NET 10 runtime; the CLI NuGet tool additionally requires the .NET SDK to install.
Native Client archives are self-contained and platform-specific.

1. Download all assets to one directory and run `sha256sum -c SHA256SUMS`.
2. Extract `netratel-compose-0.1.0-rc.2.tar.gz` into a new directory and enter it.
3. Copy `.env.images.example` to `.env`. A promoted bundle supplies image digests;
   retain these. Replace the database password and OIDC settings with your values.
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

```yaml
services:
  web:
    environment:
      ForwardedHeaders__KnownProxies__0: "203.0.113.10"
      ForwardedHeaders__AllowedHosts__0: "netratel.example.com"
```

Replace both example values with your actual proxy address and public hostname;
do not trust arbitrary private networks. Include the override with a second
`-f proxy.override.yaml` on every Compose command.

Optional HTTP MCP requires its separate OIDC audience, scope, group and target
API configuration in `.env`. Use both `-f compose.images.yaml -f compose.mcp-http.yaml`
with `--env-file .env` for config, pull and startup. It is not enabled by default.

Back up PostgreSQL, signing keys and Data Protection volumes together. Do not
delete persistent volumes during upgrades. No local administrator credentials or
identity provider are included.
