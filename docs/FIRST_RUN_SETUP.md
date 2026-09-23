# First-run setup

NetRatel uses PostgreSQL for application and identity data. A new Local or
Hybrid installation opens the proof-gated setup wizard; OIDC is optional.
Finish the [image-bundle quick start](../README.md#get-started) or the
[source recipe](SELF_HOSTING.md#source-build-for-development) first. The
`migrations` container must exit successfully before API and Web can start.

## Get the one-time setup code

Open the Web URL (`http://127.0.0.1:8080` in the loopback recipe). The first
screen asks for a setup code. In the **API container console**, run:

```sh
cat /var/netratel/bootstrap/setup-proof
```

From the host running the extracted image bundle:

```sh
docker compose --env-file .env -f compose.images.yaml exec -T api \
  cat /var/netratel/bootstrap/setup-proof
```

From the repository's source recipe:

```sh
docker compose exec -T api cat /var/netratel/bootstrap/setup-proof
```

A managed-container console is already inside the API container; enter only
the `cat` command. With Docker without Compose, identify the API container
with `docker ps`, replace `your-api-container-name` with its actual name, then
run this on the host:

```sh
API_CONTAINER=your-api-container-name
docker exec "$API_CONTAINER" cat /var/netratel/bootstrap/setup-proof
```

The code is not an
administrator password. Do not put it in a URL, log, support bundle, or
screenshot.

Paste the code into the wizard. Choose an initial tenant, administrator name,
email, and passphrase of at least 15 characters. After the API changes from
the restricted setup host to its operational host, sign in with that email
and passphrase. NetRatel has no default administrator password. An enrolled
authenticator later adds a separate sign-in step.

## Inspect or rotate the setup code

The following commands run **inside the API container** (or from the Docker
host with `docker compose ... exec -T api` before each `dotnet` invocation):

```sh
dotnet NetRatel.API.dll --setup-status
dotnet NetRatel.API.dll --show-setup-code
dotnet NetRatel.API.dll --rotate-setup-code
```

`--setup-status` reports lifecycle and code availability without printing the
code or starting services. `--show-setup-code` deliberately prints the still
usable code to the trusted console. `--rotate-setup-code` invalidates the
previous code only while the installation is unconfigured and its generated
proof is under NetRatel's control. It refuses claimed, ready, recovery, and
deployment-mounted proof states. Each refusal returns a nonzero exit code.

## Troubleshooting

| Symptom | Check | Action |
| --- | --- | --- |
| Setup code file missing | `dotnet NetRatel.API.dll --setup-status` and `docker compose ps -a` | Wait for API bootstrap. If status says Recovery, restore its matching state; do not create a new owner. |
| Code already used or expired | `--setup-status` shows Claimed, Completed, or Expired | Sign in if Ready. For an unclaimed generated code, use `--rotate-setup-code`; a claimed installation needs recovery, not rotation. |
| Login appears on an empty Local install | Check `Authentication__Mode`, OIDC settings, PostgreSQL data, and `--setup-status` | Set explicit Local mode and correct the deployment configuration. Restore matching state if it previously held an owner. |
| 403 behind a proxy | Compare the browser's Origin with `Bootstrap__AllowedOrigins__0`; check trusted proxy and public host settings | Set the exact HTTPS public origin in the deployment overlay and restart Web/API. Never trust arbitrary forwarded Host values. |
| Migration failed | `docker compose logs migrations` | Correct PostgreSQL reachability and credentials, then rerun the one-shot migration service before API. |
| API stopped after setup | `docker compose ps -a` and `docker compose logs api` | The setup host exits deliberately; the release recipe's `restart: unless-stopped` starts the operational host. Fix any reported startup error. |
| Partial state reset | Status says Recovery or a ready installation loses its marker/key/database | Restore PostgreSQL, bootstrap state, Data Protection keys, signing key, and storage from the same backup set. Never delete only one component. |

The setup page does not create databases, provision proxies, or rewrite
deployment settings. [Self-hosting](SELF_HOSTING.md) covers bundled, external,
and public HTTPS profiles.

## Administrator recovery and existing OIDC identities

An established OIDC installation retains issuer/subject mappings and its
explicit administrator roles. Email similarity does not merge identities.
If an existing local instance administrator cannot sign in, an authorized
operator can mount a protected password file and invoke the existing recovery
command in the API deployment context:

```sh
Bootstrap__Unattended__PasswordFile=/run/secrets/netratel-recovery-password \
Bootstrap__Unattended__RecoveryEmail='admin@example.test' \
  dotnet NetRatel.API.dll --recover-local-admin
```

This updates only the named existing instance administrator, clears its
lockout, disables MFA, invalidates its sessions, and revokes integrations
owned by that account. Re-enroll MFA and issue replacement integration
credentials after signing in. It does not create a new owner.
For deployment-owned unattended first setup, see [configuration](CONFIGURATION.md).

A **full reset** is appropriate only for a disposable installation whose
entire isolated Compose project and all its named volumes are intentionally
being discarded. Back up anything needed first; never use a partial volume
deletion as a recovery shortcut. Starting a new project with new PostgreSQL,
bootstrap, and key volumes creates a genuinely new setup code.
