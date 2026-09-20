# Architecture

NetRatel is a self-hosted control plane. PostgreSQL holds durable application
state, while Akka.NET coordinates real-time client and gateway work. The API
owns authorization, persistence, client enrollment, and the authenticated
gateway. The Web application provides browser sessions and calls the API; it
does not replace the API authorization boundary.

The native Client is enrolled to one NetRatel deployment and uses its own
agent credentials. Browser cookies, external OIDC tokens, API machine
credentials, and agent credentials are separate trust paths. Do not reuse a
credential across those roles.

The CLI and stdio MCP server call the protected API with their own configured
credentials. HTTP MCP is an optional separately deployed service with an
independent OAuth/resource boundary. Optional MCP and AI integrations are not
required for Web, API, or native Client operation.

```text
Browser ──OIDC session──> Web ──protected API calls──> API ──> PostgreSQL
                                                 │
                                         authenticated gateway
                                                 │
                                             Native Client

CLI / stdio MCP ──separate least-privilege credentials──> API
HTTP MCP ──separate OAuth/resource policy───────────────> API
```

See [configuration](CONFIGURATION.md) for deployment inputs and
[self-hosting](SELF_HOSTING.md) for the supported evaluation stack. The
operation and credential boundary is rendered from the running API at
[`/api/docs/`](API_REFERENCE.md).
