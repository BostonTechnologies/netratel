# HTTP MCP verification and troubleshooting

Check each boundary separately. A valid configuration, responding route, authenticated initialize and authorized business read are different outcomes. Record completed checks and unavailable or denied checks separately; a 401/403 is not successful onboarding.

## Bounded verification

1. **Configuration:** run the explicit `config validate` command for the chosen surface. It returns redacted target/auth/source information and exits nonzero for invalid or conflicting values. Render Compose with `config --quiet`; do not print secret-bearing configuration.
2. **Reachability:** confirm DNS, certificates and shared Docker network reachability. Web's private API/MCP HTTP destinations are separate from the host's selected trusted HTTPS outbound API URL.
3. **API routing:** fetch `/api/docs/` and `/api/openapi/v1.json` at the public origin. Expect the Web Scalar page and API JSON, respectively.
4. **MCP routing:** submit an unauthenticated MCP request. Inspect HTTP status, content type and challenge; verify the response is from MCP rather than Blazor HTML.
5. **Discovery:** external OIDC only: fetch exact `/.well-known/oauth-protected-resource/mcp` and verify resource/authority. Local mode must not advertise OAuth metadata.
6. **Authentication:** use an approved client to initialize with its proper HttpMcp-purpose credential or explicit OIDC token; check negotiated protocol/server information and errors.
7. **Authorized read:** call `netratel_capabilities` with `operation: "get"` to inspect the catalog, then one permitted business read such as `netratel_clients` `get` for an approved tenant/AgentId. Use the returned closed schema, actual IDs and policy; success of the catalog call alone does not prove delegated tenant access.

An unauthenticated route probe needs no secret:

```sh
curl --silent --show-error --include \
  --header 'Accept: application/json, text/event-stream' \
  --header 'Content-Type: application/json' \
  --data '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"operator-route-check","version":"1"}}}' \
  https://netratel.example.invalid/mcp
```

A 401/403 can establish that an auth boundary responded, but it does not prove credentials, delegation or allowed tools work. For authenticated probes, use an MCP client configured with a protected secret reference; do not paste bearers into committed examples or diagnostics. Keep mutating tasks out of diagnostics. Observe incremental streaming through the actual outer ingress when that behavior matters.

## Common failures

| Observation | Check |
| --- | --- |
| `/mcp` returns Web HTML | MCP overlay selected **and** Web route enabled; exact `/mcp` route; correct destination; no fallback path rewrite |
| 502/504 or connection refused | Configured service DNS/network/port and backend readiness; do not switch targets automatically |
| Works directly, fails through ingress | Forwarded Authorization/protocol headers, trusted proxy/host, disabled buffering/caching, timeout budgets and exact metadata route |
| Local mode 401 | HttpMcp purpose, exact canonical resource, expiry/revocation/owner state, selected HTTPS API target and delegation authenticate route |
| Initialize works, business call denied | Current tenant permission, operation scope/target policy, matched delegation and independent gateway identity |
| OIDC metadata missing | OIDC mode selected; exact `/.well-known/oauth-protected-resource/mcp` routed to MCP; local mode deliberately advertises none |
| OIDC 401/403 | Actual authority/issuer, audience/resource, signing key/lifetime, required group/scope and provider registration |
| Redirect response from API/token endpoint | Automatic redirects are disabled. Verify the intended HTTPS origin/issuer, review the new endpoint and explicitly rebind/reauthorize; credentials are not automatically replayed |
| Outbound target mismatch | `ApiBaseUrl` equals the selected dev/prod allowlist HTTPS URL; no stale profile credential target; no HTTP container URL |
| Secret-file startup error | Mounted path exists, readable by serving UID, no symlinked path/parents, size/permissions valid, one secret source |
| Settings disappear after recreation | Persistent profile/output/key mounts and explicit file references; no setup output saved only in the container layer |
| `.env` value ignored | Compose interpolation mapping versus actual injected `env_file` key; use canonical .NET `__` key or implemented dedicated alias |
| `restart` does not apply environment | Recreate the affected services with the same approved Compose files/env input |
| stdio emits banners or ANSI | Run ordinary stdio protocol mode with pipes; explicit setup alone uses a TTY; diagnostics belong on stderr |

The [routing guide](../deployment/web-gateway.md), [complete configuration reference](configuration.md), [shared profile rules](../integrations/configuration.md) and [host setup](setup.md) explain the exact settings. Logs should identify the failed stage using redacted metadata, never device capabilities, user codes, bearer values or complete secret-bearing configuration.

Focused unit/component/configuration tests and optional HTTP image smoke exist in the repository. Full external ingress, browser, terminal/container and recreation acceptance remains opt-in or owner-run under [AGENTS.md](../../AGENTS.md). Preserve the actual status of checks that could not execute; historical evidence is not a current pass.
