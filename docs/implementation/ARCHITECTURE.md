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

Optional [computer use](../mcp-http/computer-use.md) follows the same control
boundary. The API binds each bounded operation to a tenant, enrolled client,
gateway connection, delegated owner and short-lived desktop lease. The native
Client forwards it over the authenticated Remote Support helper pipe into the
exact Windows user session. `IComputerUseProvider` keeps the pinned Cua Driver
stdio MCP process and the limited native Windows fallback behind the same
versioned contracts. The HTTP MCP host exposes only the allowlisted NetRatel
operations, never the driver's tool catalog or socket. Separate view/control
permissions and policy families do not inherit terminal execution permission.
Desktop content and screenshots are transient; persistence retains operation
identity, target, request hashes and outcomes for audit. CUA remains opt-in on
both server and endpoint while [Windows canary acceptance](../client/computer-use.md#owner-run-windows-canary)
is performed separately from source and component validation.

```text
Browser ──OIDC session──> Web ──protected API calls──> API ──> PostgreSQL
                                                 │
                                         authenticated gateway
                                                 │
                                             Native Client

CLI / stdio MCP ──separate least-privilege credentials──> API
HTTP MCP ──separate OAuth/resource policy───────────────> API
```

See [configuration](../deployment/CONFIGURATION.md) for deployment inputs and
[self-hosting](../deployment/SELF_HOSTING.md) for the supported evaluation stack. The
operation and credential boundary is rendered from the running API at
[`/api/docs/`](../contracts/API_REFERENCE.md).
