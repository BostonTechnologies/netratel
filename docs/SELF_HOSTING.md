# Self-hosting NetRatel

NetRatel supports PostgreSQL for durable multi-instance application data and a
single-node SQLite profile for smaller local deployments. Run Web and API on a
private network, expose Web through HTTPS, and retain provider data, the Data
Protection key ring, agent-signing material, and client artifact store together.

After Web is running, `/api/docs/` exposes the generated same-origin API
reference. It describes each operation's actual credential boundary but does
not replace the deployment's OIDC, local-session, M2M, or agent configuration.

## Local source evaluation

Install Docker Compose and copy `.env.example` to `.env`. A fresh bundled
PostgreSQL source evaluation needs a PostgreSQL password; OIDC values may
remain unset because fresh installations use guided local-account setup.
Existing OIDC deployments retain their configured OIDC settings and a
native-agent signing key. Create that ES256 key outside source control when
exercising operational native-agent connectivity:

```sh
openssl ecparam -name prime256v1 -genkey -noout -out .netratel-agent-es256-private.pem
sudo chown 2000:2000 .netratel-agent-es256-private.pem
sudo chmod 600 .netratel-agent-es256-private.pem
```

Set `NETRATEL_AGENT_AUTH_PRIVATE_KEY` in `.env` to that key file before an
operational run, then run `docker compose up --build`. With OIDC unset, the
Web application at `http://localhost:8080` guides a trusted operator through
proof-gated local setup and then local sign-in. The private API volume contains
the one-time bootstrap proof at `/var/netratel/bootstrap/setup-proof`; retrieve
it only from a trusted operator console. This source-build stack uses only
PostgreSQL and locally built Web/API images; it does not contact a
vendor-operated service by default. The source image runs as UID/GID `2000`;
deployments using a secret manager should mount the key read-only with
equivalent ownership and mode.

The `migrations` service is a one-shot explicit migration runner; API starts
only after it succeeds. The stack intentionally does not provide a default
administrator password or anonymous business mode. Follow the
[first-run guide](FIRST_RUN_SETUP.md) to retrieve the private proof and create
the first local administrator and tenant. Configure an OIDC client only for an
optional OIDC or hybrid deployment; established OIDC deployments retain their
existing operational path.

The Compose Web port is loopback-bound by default. Set
`NETRATEL_WEB_BIND_ADDRESS` deliberately when a reverse proxy must reach it;
then configure that proxy's address/range and public host in the Web
`ForwardedHeaders` settings described in [configuration](CONFIGURATION.md).

## Disposable generic OIDC evaluation

For a local evaluation of the complete browser sign-in flow, the repository
includes a test-only Compose overlay backed by a publicly available generic
OIDC server. It is not a production identity provider and it creates no
administrator password. The overlay explicitly disables the otherwise-required
HTTPS metadata check only for its local HTTP test server.

Use the evaluation launcher from a fresh shell. It creates an isolated sibling
workspace (not a directory in the Git checkout), persists a private complete
Compose environment for that instance, and derives a unique Compose project,
ports, and proxy subnet from its canonical workspace path. Two workspaces can
therefore be evaluated without sharing volumes or a stop target. The web and API
ports bind to loopback. The disposable OIDC port intentionally defaults to a
non-loopback bind because containers must reach it through Docker's host gateway;
run it only on a trusted evaluation host (or set `NETRATEL_OIDC_TEST_BIND_ADDRESS`
before the first `start`).

```sh
tools/dev/oidc-evaluation.sh start
```

Open the loopback URL printed by the launcher and use
`netratel-test-operator` at the test provider's login form. On Linux hosts
where Docker does not already resolve it, add the temporary local mapping
`127.0.0.1 host.docker.internal` before opening the browser. Remove the
test stack and its volumes after verifying logout with:

```sh
tools/dev/oidc-evaluation.sh stop ../netratel-oidc-evaluation
```

Pass the same alternate workspace path to both commands when the default
sibling path is unsuitable. The automated CI smoke remains
`tools/ci/smoke-oidc-compose.sh`; it creates and removes its own disposable
resources and is not the interactive evaluation path.

## Configuration

The tracked `appsettings.json` files are examples only. Configure sensitive
values through your deployment secret mechanism and persistent volumes rather
than committing them to source control.

At minimum, provide the selected provider connection, a persistent Data
Protection directory, unique system-token and agent-signing material. OIDC
client settings are required only for a deliberately configured OIDC or hybrid
browser-login mode; local-first setup does not require an external identity
provider or SMTP service.

The optional machine-token bridge is configured under
`Authentication__MachineToken`. It validates OIDC issuer, audience, signing
keys, lifetime, signing algorithm, and required groups before issuing a Web
session. It is separate from API M2M credentials and native-agent enrollment.

## Release-image bundle

After an approved public release, extract the release Compose bundle, copy
`.env.images.example` to an untracked `.env` file beside the extracted
`compose.images.yaml`, and replace each NetRatel image placeholder with the
approved immutable digest from that release. Run `docker compose --env-file .env -f compose.images.yaml config --quiet` before starting the stack. The release
bundle never builds application source; its migration image runs before API
starts. Keep the agent key beside the extracted bundle, set
`NETRATEL_AGENT_AUTH_PRIVATE_KEY=./.netratel-agent-es256-private.pem`, and use
the same owner-only `600` permissions described above.

The HTTP MCP image is an explicit opt-in overlay. Set its distinct HTTPS OIDC,
resource URI, audience, API target, group, and scope values, then validate it
with `docker compose --env-file .env -f compose.images.yaml -f
compose.mcp-http.yaml config --quiet` from the extracted directory before starting. Do not point it
at an internal-only address or reuse Web, API, or native-agent credentials.

## Prerelease posture

`0.1.0-rc.1` remains a historical prerelease. Obtain `0.1.0-rc.3` only from
its matching prerelease after its immutable tag, assets and image digests have
been verified. A production rollout remains a separate operator decision; do
not use a prerelease for unattended fleet updates.
