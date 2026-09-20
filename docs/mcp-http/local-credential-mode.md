# Local HTTP MCP credentials

Local credential mode is an explicit alternative to the normal external OIDC
HTTP MCP deployment. It never starts an OIDC discovery request and does not
publish OAuth protected-resource metadata. It is appropriate only for an
operator-managed local installation with an already configured NetRatel API
and an HTTPS MCP origin.

The user creates a `HttpMcp` integration credential through the account API or
the Integration Credentials page. Its resource must exactly equal the public
`https://.../mcp` URL. The one-time-revealed bearer is supplied manually to an
MCP client as `Authorization: Bearer nrt_ic_...`; do not put it in a URL,
browser store, shell history, image, or Compose file.

For a combined Compose installation, use the normal image bundle plus the
optional overlay:

```sh
docker compose --env-file .env -f compose.images.yaml -f compose.mcp-http.yaml config --quiet
docker compose --env-file .env -f compose.images.yaml -f compose.mcp-http.yaml up -d
```

Set `NETRATEL_MCP_LOCAL_CREDENTIAL_MODE=true`, the canonical public resource,
the selected API base URL, and a deployment-owned delegation key reference in
the environment file. Set the same `NETRATEL_MCP_DELEGATION_*` values for API
and MCP host. The key must be base64-encoded random material of at least 32
bytes, unique per environment and rotation generation. Do not set the OIDC
variables in this mode.

For a standalone MCP host paired with an existing API, configure the same
`NetRatel:Mcp:Delegation` values on both deployments, mount an isolated MCP
outbound configuration at `NETRATEL_MCP_CONFIG`, and set exactly one selected
development or production API allowlist URL. The host rejects a configuration
file whose API endpoint differs from that selected allowlist entry. Never use
a production API URL from a development host.

Every tool call exchanges the ingress bearer only with
`/api/v2/mcp/local-delegation/exchange`. The API checks purpose, canonical
resource, active owner, current grant, target scope, and policy, then returns
a short-lived signed execution assertion. Business APIs receive their existing
gateway identity and that assertion, never the ingress bearer. The API
rechecks credential revocation, expiry, account status, resource, tenant grant
and effective access before each execution; confirmation, classification,
idempotency and audit behavior therefore remains caller-bound.

To use the established external OIDC path instead, leave
`NETRATEL_MCP_LOCAL_CREDENTIAL_MODE=false` and set the OIDC authority,
audience, required group and required scope. Existing OIDC deployments retain
their OAuth protected-resource metadata and behavior unchanged.
