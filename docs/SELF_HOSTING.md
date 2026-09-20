# Self-hosting NetRatel

NetRatel requires PostgreSQL for durable application data. Run Web, API, and
PostgreSQL on a private network, expose Web through HTTPS, and retain backups of
the PostgreSQL database, Data Protection key ring, agent-signing material, and
client artifact store together.

## Local source evaluation

Install Docker Compose and copy `.env.example` to `.env`. A fresh P01 source
evaluation needs a PostgreSQL password; OIDC values may remain unset while the
restricted setup-status shell is being evaluated. Existing OIDC deployments
continue to require their configured OIDC settings and a native-agent signing
key. Create that ES256 key outside source control when exercising operational
native-agent connectivity:

```sh
openssl ecparam -name prime256v1 -genkey -noout -out .netratel-agent-es256-private.pem
sudo chown 2000:2000 .netratel-agent-es256-private.pem
sudo chmod 600 .netratel-agent-es256-private.pem
```

Set `NETRATEL_AGENT_AUTH_PRIVATE_KEY` in `.env` to that key file before an
operational run, then run `docker compose up --build`. With OIDC unset, the
Web application at `http://localhost:8080` shows only setup status and the API
does not start business endpoints. The private API volume contains the
one-time bootstrap proof at `/var/netratel/bootstrap/setup-proof`; retrieve it
only from a trusted operator console. This source-build stack uses only
PostgreSQL and the locally built Web/API images; it does not contact any
vendor-operated service by default. The source image runs as UID/GID `2000`;
deployments using a secret manager should mount the key read-only with
equivalent ownership and mode.

The `migrations` service is a one-shot explicit migration runner; API starts
only after it succeeds. The stack intentionally does not provide an
administrator password or anonymous business mode. The P01 shell does not
complete first-user setup; later setup phases add native identity and guided
initialization. Configure a real OIDC client before using the retained OIDC
operational path.

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

At minimum, provide a PostgreSQL connection string, a persistent Data
Protection directory, unique system-token and agent-signing material, and OIDC
client settings for interactive browser login.

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

`0.1.0-rc.1` remains a historical prerelease. `0.1.0-rc.2` images and release
bundles are not published yet. Use a reviewed source build for evaluation only.
A production rollout requires release-artifact verification and owner approval.
