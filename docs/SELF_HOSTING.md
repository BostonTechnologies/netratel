# Self-host NetRatel

## 1. Choose PostgreSQL and prepare the host

Use the image bundle with its bundled PostgreSQL service for a normal first
install. Use an external PostgreSQL service when you already operate one and
can supply a dedicated database and connection string. Both paths need Docker
Engine with Compose v2, OpenSSL, a Linux host that supports the published
linux/amd64 images, persistent volumes, and a browser. Local accounts require
no OIDC or SMTP configuration.

Choose a public HTTPS origin before exposing Web beyond loopback. Keep API,
PostgreSQL, and the agent gateway on a private network. The optional HTTP MCP
server has its own public resource URL and separate instructions.

## 2. Extract the published image bundle

On the [NetRatel releases page](https://github.com/BostonTechnologies/netratel/releases),
select the intended prerelease and download its matching Compose archive and
`SHA256SUMS`; do not assume GitHub's stable `/releases/latest` route selects a
prerelease. Verify the archive, extract it, and work in the extracted directory:

```sh
sha256sum -c --ignore-missing SHA256SUMS
mkdir netratel-instance
tar -xzf netratel-compose-*.tar.gz -C netratel-instance
cd netratel-instance
cp .env.images.example .env
```

Set a long random `POSTGRES_PASSWORD` in `.env`. A promoted bundle contains
approved immutable image digests; review the three `NETRATEL_*_IMAGE` values
and replace placeholders only with the verified digests from that release.
Leave OIDC entries empty for local accounts.
If old or example OIDC variables remain in your environment, add
`NETRATEL_AUTHENTICATION_MODE=Local` to `.env`. To deliberately offer OIDC or
both sign-in paths, set this value to `Oidc` or `Hybrid` and provide the
complete OIDC settings described in [configuration](CONFIGURATION.md).

Create the initial ES256 signing key in the instance directory and set its
`.env` path:

```sh
umask 077
openssl ecparam -name prime256v1 -genkey -noout -out .netratel-agent-es256-private.pem
sudo chown 1654:1654 .netratel-agent-es256-private.pem
sudo chmod 600 .netratel-agent-es256-private.pem
```

Keep `NETRATEL_AGENT_AUTH_PRIVATE_KEY=./.netratel-agent-es256-private.pem`.
The application containers run as UID/GID 1654; a managed secret mount must
be readable by that identity without making the private key world-readable.

For external PostgreSQL, also set
`NETRATEL_EXTERNAL_DATABASE_CONNECTION_STRING` in `.env` to a reachable,
dedicated PostgreSQL database, then use **both** Compose files in every
command below:

```sh
docker compose --env-file .env -f compose.images.yaml -f compose.external-postgres.yaml config --quiet
docker compose --env-file .env -f compose.images.yaml -f compose.external-postgres.yaml up -d
```

The external overlay disables the bundled database. Ensure the external
server and credentials are ready before running migrations.

## 3. Start bundled PostgreSQL

For the normal bundled path, run:

```sh
docker compose --env-file .env -f compose.images.yaml config --quiet
docker compose --env-file .env -f compose.images.yaml pull
docker compose --env-file .env -f compose.images.yaml up -d
docker compose --env-file .env -f compose.images.yaml ps -a
```

Expected result: `postgres` is healthy, `migrations` and `volume-init` exited
with code 0, and `api` and `web` are running. The one-shot `volume-init`
service prepares only the persistent API and shared-key volumes for the
non-root UID 1654 application containers. If migration failed, inspect
`docker compose --env-file .env -f compose.images.yaml logs migrations`
before retrying. The API intentionally exits once after a successful setup;
its `restart: unless-stopped` policy starts the operational host.

## 4. Open Web and create the first administrator

Open **http://127.0.0.1:8080** on the Docker host. The setup wizard asks for
a one-time code. Retrieve it from the API container:

```sh
docker compose --env-file .env -f compose.images.yaml exec -T api \
  cat /var/netratel/bootstrap/setup-proof
```

In the API container console, run only
`cat /var/netratel/bootstrap/setup-proof`. Paste the code into Web, choose the
initial tenant, administrator name, email, and passphrase, then sign in with
that email and passphrase. There is no default administrator password.
See [first-run setup](FIRST_RUN_SETUP.md) for code status, expiry, and recovery.
For external PostgreSQL, include `-f compose.external-postgres.yaml` in the
host-side command too.

## 5. Public HTTPS and managed containers

The bundle includes `compose.public-https.yaml` and
`nginx.public-https.conf`. Put a valid certificate and private key in
`./tls/fullchain.pem` and `./tls/privkey.pem`, then set these `.env` values to
your deployment (use a free, non-conflicting Docker subnet):

```dotenv
NETRATEL_PUBLIC_HOST=netratel.example.com
NETRATEL_PUBLIC_ORIGIN=https://netratel.example.com
NETRATEL_HTTPS_CERTIFICATE=./tls/fullchain.pem
NETRATEL_HTTPS_PRIVATE_KEY=./tls/privkey.pem
NETRATEL_INGRESS_SUBNET=172.29.20.0/24
NETRATEL_INGRESS_PROXY_IP=172.29.20.10
NETRATEL_ALLOW_INSECURE_LOCALHOST=false
```

Replace the example host and subnet. The overlay sends Web the real public
origin, allowed host, and exact trusted proxy address; API receives the exact
bootstrap origin. It sets secure local cookies and a shared persistent Data
Protection application identity. The base files already share the key volume,
use `http://api:9222` internally, and start API after migration completion.
Start with the overlay on **every** Compose command:

```sh
docker compose --env-file .env -f compose.images.yaml -f compose.public-https.yaml config --quiet
docker compose --env-file .env -f compose.images.yaml -f compose.public-https.yaml up -d
```

Open `NETRATEL_PUBLIC_ORIGIN`. For external PostgreSQL, include
`-f compose.external-postgres.yaml` before `-f compose.public-https.yaml`.
Managed deployments can use equivalent settings with their own HTTPS proxy.
Use their API **container console** for `cat /var/netratel/bootstrap/setup-proof`;
do not run Docker commands inside the container. Mount bootstrap state,
Data Protection keys, application storage, and the signing key durably with
UID/GID 1654 access. Keep the configured public HTTP MCP URL distinct from
the internal API URL; see [local HTTP MCP mode](mcp-http/local-credential-mode.md).

## 6. Restart, upgrade, and troubleshoot

For an ordinary restart, run:

```sh
docker compose --env-file .env -f compose.images.yaml restart
```

Keep PostgreSQL, API state, the shared key ring,
storage, and the signing key together. To upgrade, replace only verified image
digests from the new approved bundle, run the migration service, then restart
API/Web. Do not delete volumes or claim setup again.

If the setup code is missing, the API returns 403 during setup, login appears
on a fresh Local install, migrations fail, or API stops after setup, follow the
specific checks in [first-run troubleshooting](FIRST_RUN_SETUP.md#troubleshooting).
Use `dotnet NetRatel.API.dll --setup-status` in the API container to distinguish
Ready, expired, and Recovery state. A partial database/key/bootstrap reset
requires restoring one matching backup set; it must not create a new owner.

## Alternatives and references

- [Optional OIDC and Hybrid configuration](CONFIGURATION.md)
- [CLI and MCP](CLI_AND_MCP.md)
- [Backup, recovery, and first-run commands](FIRST_RUN_SETUP.md)
- [Source build for development](DEVELOPMENT.md)

### Source build for development

From a source checkout, copy `.env.example` to `.env`, set
`POSTGRES_PASSWORD` and `NETRATEL_AGENT_AUTH_PRIVATE_KEY`, then run
`docker compose up --build -d`. Check `docker compose ps -a`, open
`http://127.0.0.1:8080`, and use
`docker compose exec -T api cat /var/netratel/bootstrap/setup-proof` to finish
setup. This builds the source and is a separate path from the published bundle.

### PostgreSQL-only transition

First-party SQLite deployment and migration support ended after rc.5.
Preserve an rc.5 SQLite installation and its data before changing anything.
Use an explicit PostgreSQL transition plan; NetRatel does not silently convert
the file or create a replacement empty installation. Historical release
records remain available for audit.
