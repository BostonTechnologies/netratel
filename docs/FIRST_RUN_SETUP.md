# First-run setup

NetRatel has two supported fresh-install paths: the single-node SQLite profile
for a small durable installation, and the normal PostgreSQL Compose profile.
Both use the same persistent bootstrap descriptor, setup proof, local identity
transaction, and local sign-in flow. The setup browser cannot create database
containers or rewrite deployment configuration.

## Before opening the browser

Choose the provider and prepare its persistent storage first. SQLite is only
for one NetRatel instance with a local durable path; PostgreSQL remains the
supported provider for multi-instance or scaled deployments. See
[SQLite](SQLITE.md) for its limits and backup requirements.

For a loopback-only SQLite source evaluation, provide a native-agent signing
key and start the dedicated profile:

```sh
openssl ecparam -name prime256v1 -genkey -noout -out .netratel-agent-es256-private.pem
chmod 600 .netratel-agent-es256-private.pem
NETRATEL_AGENT_AUTH_PRIVATE_KEY=./.netratel-agent-es256-private.pem \
  docker compose -f compose.sqlite.yaml up --build
```

For bundled PostgreSQL, copy `.env.example`, set `POSTGRES_PASSWORD`, create
the same agent key outside source control, and run `docker compose up --build`.
For an external PostgreSQL service, supply its connection string and required
provider settings through the deployment configuration before starting the
migration runner and API. Migrations must complete before entering a password.

The supplied source profiles bind Web to loopback and explicitly opt into
insecure local cookies for that loopback evaluation only. Public deployments
must terminate HTTPS, configure trusted proxy/public-host settings, set
`Bootstrap:AllowedOrigins` to their real browser origin, and omit
`Authentication:Local:AllowInsecureLocalhost`.

## Browser wizard

1. Open Web at its configured loopback or public HTTPS address. Fresh `/`,
   `/login`, and protected application routes lead to `/setup` until the
   instance is ready.
2. Retrieve the one-time proof only on the trusted API host, for example:

   ```sh
   docker compose -f compose.sqlite.yaml exec -T api \
     cat /var/netratel/bootstrap/setup-proof
   ```

   Do not put this value in a URL, shell history, screenshot, browser storage,
   source file, or support bundle.
3. Enter the proof, confirm the deployment-selected storage shown by the
   wizard, then provide the initial tenant, administrator name, email and
   passphrase. The wizard validates the configured provider; it does not
   change environment variables or provision a database server.
4. NetRatel atomically creates the local principal, first instance
   administrator, tenant and ready marker, then deliberately restarts from the
   restricted bootstrap host into the normal runtime. Sign in with the local
   account after it returns. An enrolled authenticator prompts for MFA as a
   separate second step.

The proof is consumed after a claim, and browser setup uses a short-lived,
HTTP-only same-origin setup session. Passwords and the proof remain transient
in the form request; they are not retained in the bootstrap descriptor. Once
ready, `/setup` shows a completion message only and no anonymous caller can
replay setup.

Display branding, timezone and advanced OIDC/integration configuration are
deployment or administration concerns. Do not use the first-run form to claim
that a Docker service, reverse proxy, provider connection string, or secret
manager value has changed.

## Unattended initialization

Use unattended setup only from deployment-controlled automation with a
protected password file/secret mount. It accepts the same setup proof,
validation and transaction as the browser flow, and never places a password on
the command line.

```sh
umask 077
printf '%s\n' 'replace-with-a-secret-passphrase' > /run/secrets/netratel-admin-password

Bootstrap__Unattended__PasswordFile=/run/secrets/netratel-admin-password \
Bootstrap__Unattended__DisplayName='Initial administrator' \
Bootstrap__Unattended__Email='admin@example.test' \
Bootstrap__Unattended__TenantName='Initial tenant' \
  dotnet NetRatel.API.dll --initialize-unattended
```

Run that command in the same deployment context that owns the bootstrap state,
proof file, provider configuration and key material. It is rejected after
setup has been claimed or completed, and must not be retried as a recovery
mechanism.

## Recovery and existing OIDC installations

If an initialized instance loses its selected store, bootstrap descriptor, or
matching key material, it enters recovery rather than showing a new-owner
wizard. Restore the matching provider data, bootstrap directory, Data
Protection keys, native-agent signing material and other deployment state from
the same backup set. Do not delete bootstrap state to create a replacement
administrator.

Existing configured OIDC installations retain their OIDC mode and established
identities. Do not replace their provider configuration or use the guided
wizard to merge accounts by email. Administrator-assisted local-account
recovery requires deployment access and invalidates prior local sessions. To
reset an existing instance administrator when no normal administrator session
is available, mount a protected password file and invoke the explicit recovery
command in the API deployment context:

```sh
Bootstrap__Unattended__PasswordFile=/run/secrets/netratel-recovery-password \
Bootstrap__Unattended__RecoveryEmail='admin@example.test' \
  dotnet NetRatel.API.dll --recover-local-admin
```

It rejects an unknown or non-instance-administrator account, does not create a
replacement owner, resets local MFA for that recovered account, and rotates
the security stamp plus authorization revision. Do not expose this command
through a browser route, support script, or password-bearing process argument.
