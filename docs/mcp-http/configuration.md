# HTTP MCP configuration reference

Configure ingress, outbound API identity and delegation separately. The host validates its selected `dev` or `prod` HTTPS API target before serving. Configuration validation is not authenticated MCP acceptance.

The shared tooling [precedence and persistence contract](../integrations/configuration.md) is canonical: defaults < explicitly selected profile/file < dedicated environment < explicit non-secret command options. Canonical keys win over compatibility aliases within the same provider/precedence level; a higher-level dedicated environment alias overrides a lower-level JSON canonical key. Auth modes and secret sources must be coherent. MCP never imports ambient CLI credentials.

## Compose inputs and container variables

`.env` and `docker compose --env-file` supply **Compose interpolation**. They do not automatically inject every variable into every container. `env_file` injects variables into one container; it does not turn a compact interpolation name into a .NET option. Actual .NET keys use `__` for section separators. Some compact names below work because the [release MCP overlay](../../release/compose.mcp-http.yaml) explicitly maps them; use its actual mapping or the canonical keys in custom deployments. [The interpolation template](../../release/.env.mcp-http.example), [actual outbound template](../../release/.env.mcp-http.outbound.example) and [actual API template](../../release/.env.mcp-http.api.example) have distinct roles. The [source Compose recipe](../../compose.yaml) provides equivalent service wiring.

| Compact Compose input | Actual service setting |
| --- | --- |
| `NETRATEL_PUBLIC_ORIGIN` | Deployment-owned public Web origin and derived canonical `/mcp` resource; API `Branding__SiteUrl` |
| `NETRATEL_MCP_PUBLIC_RESOURCE_URI` | MCP `NetRatel__Mcp__Http__PublicResourceUri`; API `Integration__HttpMcp__PublicResourceUri` |
| `NETRATEL_MCP_INSTANCE` | MCP selected `Instance` (implemented direct alias); Compose default `prod` |
| `NETRATEL_WEB_API_BACKEND_URL`, `NETRATEL_WEB_MCP_BACKEND_URL` | Web `ApiBaseUrl`, `WebGateway__Mcp__BackendUrl`; defaults `http://api:9222/`, `http://mcp-http:9224/` |
| `NETRATEL_MCP_DEV_API_BASE_URL`, `NETRATEL_MCP_PROD_API_BASE_URL` | MCP selected HTTPS API allowlist entries (implemented direct aliases) |
| `NETRATEL_MCP_CONFIG` | Explicit isolated outbound JSON file (implemented direct alias); the config overlay sets its mounted container path |
| `NETRATEL_MCP_OUTBOUND_ENV_FILE` | Optional actual-key MCP `env_file`; default `./mcp-http.outbound.env` |
| `NETRATEL_MCP_API_ENV_FILE` | Optional actual-key API M2M `env_file`; default `./mcp-http.api.env`; preserve existing allowed callers |
| `NETRATEL_MCP_OUTBOUND_CONFIG_FILE` | Absolute host JSON path mounted by the separate config overlay |
| `NETRATEL_MCP_SECRETS_DIRECTORY` | Owner-controlled host directory mounted read-only by the separate secrets overlay |
| `NETRATEL_MCP_HTTP_BIND_ADDRESS`, `NETRATEL_MCP_HTTP_PORT`, `NETRATEL_MCP_HTTP_CONTAINER_PORT` | Optional port overlay only; bind address, host port and container target port. Defaults `127.0.0.1`, `9224`, `9224`; container target must match the reviewed listening port |
| `NETRATEL_MCP_HTTP_IMAGE` | Approved existing HTTP MCP image digest; never an inferred version |
| `NETRATEL_SETUP_VOLUME` | Persistent named setup volume, default `netratel-http-host-setup`; supplied tooling runs as UID/GID `10001` and prepares protected runtime access automatically |
| `NETRATEL_SETUP_DIRECTORY` | Effective tooling setup location; supplied setup service injects `/setup-output`; native fallback is per-user application data `NetRatel/http-host` |
| `NETRATEL_SETUP_COMPOSE_FILES` | Comma-separated existing deployment Compose files retained by the generated apply action; include the actual image, ingress and database overlays |
| `NETRATEL_CLIENT_VOLUME` | Separate persistent personal tooling-data volume, default `netratel-http-client-data`; mounted only into serving/tooling MCP, never API |
| `NETRATEL_CLIENT_DIRECTORY` | Personal client-profile root, supplied as `/var/lib/netratel/clients`; takes precedence over the legacy setup-directory fallback for client profiles |
| `NETRATEL_MCP_LOCAL_CREDENTIAL_MODE` | MCP `NetRatel__Mcp__Http__LocalCredentialMode`; default `false` |
| `NETRATEL_MCP_AUTHENTICATION_MODE` | Web `WebGateway__Mcp__AuthenticationMode`; default `Oidc`; set `LocalCredential` alongside local host mode |
| `NETRATEL_MCP_OIDC_AUTHORITY` | MCP `NetRatel__Mcp__Http__Authority` |
| `NETRATEL_MCP_AUDIENCE` | MCP `NetRatel__Mcp__Http__Audience` |
| `NETRATEL_MCP_REQUIRED_GROUP`, `NETRATEL_MCP_REQUIRED_SCOPE` | MCP `RequiredGroups__0`, `RequiredScopes__0` in the HTTP section |
| `NETRATEL_MCP_DELEGATION_ENABLED` | API and MCP `NetRatel__Mcp__Delegation__Enabled` |
| `NETRATEL_MCP_DELEGATION_ISSUER`, `NETRATEL_MCP_DELEGATION_AUDIENCE` | Both services' `Issuer`, `Audience` in the delegation section |
| `NETRATEL_MCP_DELEGATION_SERVICE_PRINCIPAL` | Both services' delegation `ServicePrincipal` |
| `NETRATEL_MCP_DELEGATION_KEY_ID` | Both services' delegation `KeyId` |
| `NETRATEL_MCP_DELEGATION_KEY_BASE64` | Both services' delegation `SharedKeyBase64` (inline compatibility input) |
| `NETRATEL_MCP_DELEGATION_KEY_FILE` | Both services' delegation `SharedKeyBase64File` (supported direct alias; mount the same generation independently) |

The **release-bundle path** generates a final overlay carrying reviewed non-secret values and secret-file references; its `environment` settings take precedence over `env_file`. Its service `.env` files are reusable exports, so editing them alone does not change settings embedded in that overlay. Re-run reviewed setup and apply the generated action to recreate the affected containers. This path preserves unrelated Web/API settings and replaces MCP's earlier environment/env-file configuration to avoid stale identity aliases; add extra MCP runtime settings in a later explicit reviewed override.

The **Externally managed Compose path** generates a complete deployment document at the printed export path plus private saved-request/runtime files. It preserves the existing document's unrelated settings and managed sources while updating the reviewed MCP bindings. Import that complete document into the same stack's Compose editor, Save/Deploy, then resume verification. This path does not generate the release overlay or separate service `.env` exports.

The supported setup service uses the persistent named volume for owner-only original identity/delegation material and protected runtime exports. The generated deployment document mounts runtime material read-only with explicit access for API UID `1654` and MCP UID `10001`; originals remain untouched on rerun. Adopting an existing protected OIDC JSON identity or secret reference copies the selected generation to protected exports and remaps runtime references, without rewriting the managed originals. The saved `.http-host.setup.json` retains the reviewed non-secret settings for resume. Do not delete the volume or replace keys to resolve an access failure. Custom native/split-host storage remains an explicit Advanced option.

Bare `dotnet NetRatel.Mcp.Http.dll setup` configures a personal HTTP client; `setup --mode host` configures deployment hosting. The supported serving container and one-shot tooling service both persist personal profiles under `/var/lib/netratel/clients/profiles/{surface}/{profile}.json`. They do not write those profiles to `/app/.config` or place personal credentials in the host-identity namespace. Explicit client configuration/environment sources retain their existing precedence and managed status.

An MCP overlay explicitly enables Web's MCP route and supplies its private Compose DNS destination. A Docker profile alone cannot configure Web. Refer to [Web gateway options](../deployment/web-gateway.md#generated-routes-and-explicit-overrides) for `ApiBaseUrl`, `WebGateway__Mcp__Enabled`, `BackendUrl`, `AuthenticationMode`, `ActivityTimeout`, topology and explicit YARP overrides.

## HTTP ingress options

Canonical prefix: `NetRatel__Mcp__Http__`. Each table property appends to that prefix. JSON uses section `NetRatel:Mcp:Http` with the same property names.

| Property | Default / required condition |
| --- | --- |
| `Instance` | Required `dev` or `prod`; direct alias `NETRATEL_MCP_INSTANCE` |
| `OperatorSurfaceEnabled` | `prod` enables operator surface; unset Development retains its existing compatibility surface. Explicit `true` on either surface requires delegation |
| `ConfigurationPath` | Optional explicitly selected legacy/profile file when dedicated environment configuration is complete; direct alias `NETRATEL_MCP_CONFIG`. A present empty canonical `NetRatel__Mcp__Http__ConfigurationPath` disables a lower-level file selector |
| `DevApiBaseUrl`, `ProdApiBaseUrl` | Selected entry required HTTPS; both, if supplied, must differ. Direct aliases `NETRATEL_MCP_DEV_API_BASE_URL` / `NETRATEL_MCP_PROD_API_BASE_URL` |
| `PublicResourceUri` | Exact canonical absolute HTTPS URI ending in `/mcp`; required by the host, explicitly supplied or derived by Compose/setup from the trusted deployment public origin |
| `LocalCredentialMode` | `false`; set `true` explicitly for HttpMcp-purpose integration credentials |
| `Authority` | HTTPS OIDC authority required in external OIDC mode; leave unset in local mode |
| `RequireHttpsMetadata` | `true`; disabling is accepted only by a Development host for isolated disposable tests |
| `Audience` | External OIDC required; must equal canonical `PublicResourceUri` |
| `RequiredGroups`, `RequiredScopes` | Nonempty unique OIDC group/scope arrays; use `__0`, `__1`, etc. Local credentials use their current approved grants |
| `AllowedOrigins` | Empty array; configured entries must be HTTPS origins without path/query/fragment. Supply browser origins deliberately |
| `MaxRequestBodyBytes` | `1048576`; allowed range `1024..1048576` |
| `ReadScope` | `netratel.mcp.read` |
| `ObserveScope` | `netratel.mcp.observe` |
| `FilesScope` | `netratel.mcp.files` |
| `DevelopmentWriteScope` | `netratel.mcp.write` |
| `ExecuteScope` | `netratel.mcp.execute` |
| `DevelopmentOnboardingScope` | `netratel.mcp.onboarding` |
| `AdminScope` | `netratel.mcp.admin` |

Scope values are nonempty bounded OAuth scope tokens; scope admission does not replace application permission, current account/tenant membership, target policy or review/confirmation checks. External OIDC metadata advertises applicable scopes plus `openid`, `profile` and `offline_access`.

The HTTP container listens on `9224` through its ASP.NET Core/Kestrel endpoint configuration (`ASPNETCORE_URLS=http://+:9224`). Keep it private in the normal single-origin stack. An explicit custom port binding is an operator choice; it does not replace the public HTTPS resource or validation.

## Outbound API options

Canonical prefix: `NetRatel__Mcp__Outbound__`. Dedicated aliases are `NETRATEL_MCP_OUTBOUND_` plus the upper snake case property; credentials use `INTEGRATION_TOKEN` and `INTEGRATION_TOKEN_FILE` aliases. These values configure only MCP's outbound API identity.

| Property | Default / required condition |
| --- | --- |
| `ApiBaseUrl` | Required selected HTTPS API URL, equal to `DevApiBaseUrl` or `ProdApiBaseUrl` for the chosen instance |
| `AuthenticationMode` | One explicit coherent host gateway mode: `Oidc` or `ApiM2MClientSecret`; legacy single-mode files remain supported |
| `IntegrationCredential`, `IntegrationCredentialFile` | Shared resolver properties for CLI/stdio compatibility; HTTP host outbound rejects human integration credentials and requires its OIDC application or M2M service identity |
| `OidcTokenUrl`, `OidcClientId`, `OidcUsername` | Existing OIDC-mode requirements |
| `OidcAppPassword`, `OidcAppPasswordFile` | Exactly one OIDC secret source |
| `OidcScope` | Optional existing OIDC scope |
| `ApiM2MTokenUrl`, `ApiM2MClientId` | Required M2M token endpoint and deployment-owned client ID in that mode |
| `ApiM2MClientSecret`, `ApiM2MClientSecretFile` | Exactly one service secret source in M2M mode |
| `ApiM2MScope` | Existing service scope, for example `netratel.api` |
| `ProfileName` | Explicit isolated named HTTP-MCP-outbound profile, never the default CLI profile |
| `Instance` | Profile's reviewed instance selection |
| `DefaultTenantId` | Optional default tenant; grants still authorize each operation |
| `CredentialPurpose`, `ApprovedOrigin`, `ApprovedTarget` | Profile's reviewed purpose/origin/target binding; target changes require a reviewed rebind or new approval |
| `ApprovedTokenEndpoint` | New OIDC/M2M profile token-endpoint binding; editing the issuer endpoint requires explicit reauthorization/rebind |

All secrets have explicit file properties. The implemented resolver also supports the documented `_FILE` aliases for secret fields. At the winning precedence level, inline and file alternatives cannot both be selected. A canonical property overrides its documented legacy alias within the same level. Higher-level secret-file references replace lower-level inline secrets coherently; alternatives at the same level conflict. Missing/empty/invalid explicitly selected files fail without ambient fallback.

Environment-only example with protected references:

```dotenv
NETRATEL_MCP_INSTANCE=prod
NETRATEL_MCP_PROD_API_BASE_URL=https://netratel.example.invalid
NetRatel__Mcp__Outbound__ApiBaseUrl=https://netratel.example.invalid
NetRatel__Mcp__Outbound__AuthenticationMode=ApiM2MClientSecret
NetRatel__Mcp__Outbound__ApiM2MTokenUrl=https://netratel.example.invalid/connect/token
NetRatel__Mcp__Outbound__ApiM2MClientId=netratel-mcp-http
NetRatel__Mcp__Outbound__ApiM2MClientSecretFile=/run/secrets/netratel-mcp-service
NetRatel__Mcp__Outbound__ApiM2MScope=netratel.api
```

Supply the matching allowed service client on API using its existing `M2MClients` administrator/deployment configuration; use the supported `SecretFile` reference when available. The local credential/delegation exchange still enforces the human caller's purpose/resource/current grants. A service credential alone cannot impersonate a human or bypass operator policy.

Legacy isolated files keep their flat `apiBaseUrl`, `apiM2M*` or `oidc*` schema. CLI/stdio additionally support API-purpose `integrationCredential`; HTTP host outbound rejects that human credential mode. Mount read-only and select with `NETRATEL_MCP_CONFIG=/etc/netratel/mcp.json`. The new dedicated environment overrides are allowed, but must resolve coherently and still match the selected HTTPS target. No mandatory JSON secret file is needed when environment/file references supply the complete outbound configuration.

The selected outbound destination must serve the protected API `GET /health/live` and `GET /health/ready` contracts. The shipped Web gateway forwards both exact paths to API without removing API authorization. Same-origin profiles use the reviewed Web HTTPS origin; separate API-origin profiles use their explicitly selected HTTPS API origin. Both probes stay on that destination; no alternate origin or administrative token is inferred.

Web-to-backend private HTTP URLs (`http://api:9222/`, `http://mcp-http:9224/`) are unrelated to this outbound trust boundary. Use the public Web HTTPS origin with the narrowly required API routes, or a consistently selected privately trusted HTTPS API endpoint. Do not disable validation or configure `http://api:9222/` as the MCP target.

### Exact outbound aliases

Each property above binds to `NetRatel__Mcp__Outbound__PROPERTY`; these are its dedicated compatibility aliases:

| Property | Alias |
| --- | --- |
| `ApiBaseUrl` | `NETRATEL_MCP_OUTBOUND_API_BASE_URL` |
| `AuthenticationMode` | `NETRATEL_MCP_OUTBOUND_AUTHENTICATION_MODE` |
| `IntegrationCredential` | `NETRATEL_MCP_OUTBOUND_INTEGRATION_TOKEN` |
| `IntegrationCredentialFile` | `NETRATEL_MCP_OUTBOUND_INTEGRATION_TOKEN_FILE` |
| `OidcTokenUrl` | `NETRATEL_MCP_OUTBOUND_OIDC_TOKEN_URL` |
| `OidcClientId` | `NETRATEL_MCP_OUTBOUND_OIDC_CLIENT_ID` |
| `OidcUsername` | `NETRATEL_MCP_OUTBOUND_OIDC_USERNAME` |
| `OidcAppPassword` | `NETRATEL_MCP_OUTBOUND_OIDC_APP_PASSWORD` |
| `OidcAppPasswordFile` | `NETRATEL_MCP_OUTBOUND_OIDC_APP_PASSWORD_FILE` |
| `OidcScope` | `NETRATEL_MCP_OUTBOUND_OIDC_SCOPE` |
| `ApiM2MTokenUrl` | `NETRATEL_MCP_OUTBOUND_API_M2M_TOKEN_URL` |
| `ApiM2MClientId` | `NETRATEL_MCP_OUTBOUND_API_M2M_CLIENT_ID` |
| `ApiM2MClientSecret` | `NETRATEL_MCP_OUTBOUND_API_M2M_CLIENT_SECRET` |
| `ApiM2MClientSecretFile` | `NETRATEL_MCP_OUTBOUND_API_M2M_CLIENT_SECRET_FILE` |
| `ApiM2MScope` | `NETRATEL_MCP_OUTBOUND_API_M2M_SCOPE` |
| `ProfileName` | `NETRATEL_MCP_OUTBOUND_PROFILE_NAME` |
| `Instance` | `NETRATEL_MCP_OUTBOUND_INSTANCE` |
| `DefaultTenantId` | `NETRATEL_MCP_OUTBOUND_DEFAULT_TENANT_ID` |
| `CredentialPurpose` | `NETRATEL_MCP_OUTBOUND_CREDENTIAL_PURPOSE` |
| `ApprovedOrigin` | `NETRATEL_MCP_OUTBOUND_APPROVED_ORIGIN` |
| `ApprovedTarget` | `NETRATEL_MCP_OUTBOUND_APPROVED_TARGET` |
| `ApprovedTokenEndpoint` | `NETRATEL_MCP_OUTBOUND_APPROVED_TOKEN_ENDPOINT` |

The additional canonical secret-file aliases are `NetRatel__Mcp__Outbound__IntegrationCredential_FILE`, `NetRatel__Mcp__Outbound__OidcAppPassword_FILE`, and `NetRatel__Mcp__Outbound__ApiM2MClientSecret_FILE`. The canonical `...File` property wins over its `_FILE` or compact alias within the same provider/precedence level. No other arbitrary `_FILE` name is inferred.

## API resource and delegation options

API canonical resource: `Integration__HttpMcp__PublicResourceUri`. It must match the MCP host's HTTPS `/mcp` resource exactly after supported URI normalization. Browser-approved HTTP MCP client credentials bind to this resource; CLI/stdio API-purpose credentials are distinct.

Canonical delegation prefix on **both API and MCP**: `NetRatel__Mcp__Delegation__`.

| Property | Default / required condition |
| --- | --- |
| `Enabled` | `false`; required `true` for local credential mode and any host publishing the V2 operator surface |
| `Issuer`, `Audience` | Matching explicit bounded identifiers |
| `ServicePrincipal` | `netratel-mcp-http`; match the allowed deployment identity |
| `KeyId` | Explicit generation identifier, matching on both services |
| `SharedKeyBase64` | Inline compatibility source; random base64 material decoding to at least 32 bytes |
| `SharedKeyBase64File` | Protected mounted base64 key file; preferred persistent secret reference |
| `LifetimeSeconds` | `90`; allowed range `30..300` |

`SharedKeyBase64_FILE` and `NETRATEL_MCP_DELEGATION_KEY_FILE` are supported file aliases. Within one provider/precedence level, canonical `SharedKeyBase64File` wins over those aliases. A higher-level environment file alias can replace a lower-level JSON canonical file or inline key; competing inline/file sources at the same level are rejected. Keep the same generation independently mounted on API and MCP. This key is separate from API/Web Data Protection, native AgentAuth signing material, M2M service secrets and human integration credentials.

## Persistence, reload and diagnostics

Configuration files are bounded to 64 KiB; secret files to 16 KiB. The reader trims final CR/LF, rejects symlinked paths/parents and secret storage writable by unrelated users, and accepts appropriate read-only container secret mounts. Profile creation uses owner-only files, separate protected secret references, a save lock, atomic rename and prior-content conflict checks. Replacing a profile requires explicit confirmation.

Named profiles live under `~/.config/netratel/profiles/{surface}/{safe-name}.json`; CLI, stdio, HTTP outbound and HTTP client surfaces are isolated. Select the current surface's `ProfileName`/`PROFILE_NAME`; see [shared configuration](../integrations/configuration.md).

Server setup adopts a coherent existing generation from its effective canonical environment/configuration sources, preserving the selected instance, protected outbound identity and delegation values. Supplied settings alone do not establish completed provisioning or verified readiness. Explicit key-file/key-ID/issuer/audience inputs remain available for managed native imports. Use the [server-first setup guide](setup.md) for externally managed source snapshots or the release-bundle apply path, and [migration](hosting-reference.md#migrate-an-existing-stack) before replacing an existing stack.

Runtime options and secret references are startup inputs. Apply reviewed exports and recreate affected containers after environment changes; restart processes after mounted option/secret changes. Setup cannot change another container's environment. Only the Web gateway's explicitly reloadable YARP file configuration supports its documented transactional reload; do not infer host/delegation hot rotation.

Local redacted validation reports selected target, auth mode, source and path without credential material. Then separately check DNS/TLS, routing, authentication/delegation, MCP initialize and a permitted read-only tool. See [troubleshooting](troubleshooting.md) and [setup/rotation](setup.md).
