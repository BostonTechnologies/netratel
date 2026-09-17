# Produce the V5, line-oriented evidence projection from the checked-in
# reconciliation inventory.  The inventory is deliberately historical as well
# as current: an excluded or pre-V2 row remains visible, but cannot be mistaken
# for an enabled Dev or Production operation.

def has_operator_route: (.endpoint | contains("/api/v2/mcp/operator/"));

def is_mutation:
  (.state | test("TARGET_MUTATION|STDIO_COMPATIBILITY")) or
  (.safetyDisposition | test("mutation|write|execution|deletion|credential"; "i"));

def scoped_oauth:
  if has_operator_route | not then []
  elif (.endpoint | contains("/policy")) then ["netratel.mcp.admin"]
  elif (.endpoint | test("/files")) then
    if is_mutation then ["netratel.mcp.write"] else ["netratel.mcp.files"] end
  elif (.endpoint | test("/terminal|/commands|/tasks|/job-runs|/connectivity/test|/events/.*/retry")) then
    if is_mutation then ["netratel.mcp.execute"] else ["netratel.mcp.observe"] end
  elif (.endpoint | test("/onboarding")) then ["netratel.mcp.onboarding"]
  elif (.endpoint | test("/clients/.*/(disable|enable|delete)")) then ["netratel.mcp.admin"]
  elif (.endpoint | test("/clients|/logs|/telemetry|/events|/notifications")) then
    if is_mutation then ["netratel.mcp.write"] else ["netratel.mcp.observe"] end
  elif is_mutation then ["netratel.mcp.write"]
  else ["netratel.mcp.read"]
  end;

def disposition:
  if .state | startswith("EXCLUDED") then "excluded"
  elif .state == "NOT_MCP_SCOPE" then "not-mcp"
  elif .state == "STDIO_COMPATIBILITY" then "stdio-only"
  elif .state == "BLOCKED_BY_UPSTREAM_CONTRACT" or .state == "TARGET_AFTER_PREREQUISITE" then "blocked"
  else "catalogued"
  end;

def http_surface:
  if .state == "STDIO_COMPATIBILITY" then "not exposed over HTTP"
  elif .state | startswith("EXCLUDED") then "intentionally excluded from HTTP"
  elif .state == "NOT_MCP_SCOPE" then "not an MCP HTTP surface"
  else "catalogued HTTP adapter; exact route is apiRoute"
  end;

def stdio_surface:
  if .state == "STDIO_COMPATIBILITY" then "supported stdio compatibility operation"
  elif .state | startswith("EXCLUDED") then "not an allowed stdio fallback"
  elif .state == "NOT_MCP_SCOPE" then "not an MCP stdio surface"
  else "catalogued or historical stdio disposition; see availability"
  end;

def agent_client_surface:
  if has_operator_route then "typed route-bound V2 AgentClient facade; selected dev/prod target only"
  elif .state | startswith("EXCLUDED") then "not applicable; excluded routes have no AgentClient fallback"
  elif .state == "NOT_MCP_SCOPE" then "not applicable; not MCP scope"
  else "not a V2 AgentClient route"
  end;

def confirmation:
  if is_mutation and has_operator_route then "server preview plus explicit confirmation; plan credentials are operation-bound"
  elif is_mutation then "transport-local confirmation or historical compatibility contract; not a V2 claim"
  else "no confirmation required for read/exclusion disposition"
  end;

def idempotency:
  if is_mutation and has_operator_route then "server-issued idempotency key required on confirmed mutation"
  elif is_mutation then "not a V2 idempotency claim"
  else "not applicable"
  end;

def dev_availability:
  if .state == "NOT_MCP_SCOPE" then "not applicable"
  elif .state | startswith("EXCLUDED") then "excluded"
  elif .state == "STDIO_COMPATIBILITY" then "stdio compatibility only"
  elif has_operator_route then "available only when the Dev operator surface is explicitly enabled and delegation/policy admit it"
  else "reconciliation state only; verify against the selected Dev catalog"
  end;

def prod_availability:
  if .state == "NOT_MCP_SCOPE" then "not applicable"
  elif .state | startswith("EXCLUDED") then "excluded"
  elif .state == "STDIO_COMPATIBILITY" then "not available"
  elif has_operator_route then "catalogued source contract; no Production activation or live claim"
  else "not a Production operator-surface claim"
  end;

def live_evidence:
  if .state | startswith("EXCLUDED") then "not eligible for live certification"
  elif .state == "NOT_MCP_SCOPE" then "not an MCP operation"
  elif .state == "TARGET_AFTER_PREREQUISITE" or .state == "BLOCKED_BY_UPSTREAM_CONTRACT" then "blocked pending the named upstream prerequisite"
  else "historical evidence only; deterministic evidence is not current live certification"
  end;

def negative_evidence:
  if .state | startswith("EXCLUDED") then "exclusion is the expected negative-path result"
  elif .state == "TARGET_AFTER_PREREQUISITE" or .state == "BLOCKED_BY_UPSTREAM_CONTRACT" then "prerequisite denial must be observed before activation"
  else "authorization, target, confirmation, and schema rejection remain required certification evidence"
  end;

.operations[] |
{
  recordVersion: 5,
  tool,
  operation,
  http: http_surface,
  stdio: stdio_surface,
  cli: "source CLI operation reconciled by capability-parity.json",
  agentClient: agent_client_surface,
  apiRoute: .endpoint,
  requiredOAuthScopes: scoped_oauth,
  policy: (if has_operator_route then "McpOperationAccessCatalog plus signed delegation and target/ControlPlane policy as applicable" else "not a V2 policy-admitted route" end),
  confirmation: confirmation,
  idempotency: idempotency,
  availability: { development: dev_availability, production: prod_availability },
  schemas: { requestAndResult: .requestResultContract, responseEnvelope: "NetRatelToolResponse or API contract documented by the exact route" },
  evidence: { deterministic: .testCoverage, live: live_evidence, negative: negative_evidence },
  documentation: ["docs/mcp-http/capability-parity.md", "docs/mcp-http/tool-contract.md"],
  issue: .dependencies,
  disposition: disposition,
  sourceState: .state,
  sensitivity: .sensitivity
}
