# ADR 0004: local HTTP MCP delegation

Status: accepted for implementation in P06 through P08.

NetRatel retains distinct browser, local API credential, HTTP MCP ingress,
short-lived MCP execution, OIDC/M2M, system, and native-agent trust paths.
Token prefixes or routing hints select a candidate scheme only; each scheme
performs its own cryptographic and purpose validation.

P06 introduces separate local API and HTTP MCP credentials. P08 extends the
existing HTTP MCP resource boundary rather than creating a parallel API bypass.
The gateway validates the ingress credential's purpose, audience and resource,
then obtains caller-bound delegated execution authority. The API evaluates the
intersection of current principal access, credential ceiling, tenant/resource
scope, target/environment policy, confirmation/approval and idempotency rules
before side effects.

Gateway persistence records only the authorization data needed for current
revocation and auditability. It never substitutes a shared administrator or
agent credential. Invalid credentials, insufficient access, rate limits and
dependency failures remain distinguishable without leaking account existence.
Existing external-OIDC HTTP MCP configuration and resource names remain
compatible.

Operation target contracts distinguish no target, optional tenant filters, exact tenant/agent pairs, selector-bound policy drafts and persisted references. The API resolves a policy reference's stored ownership before attenuation, rechecks it at execution, and the receiving policy authority validates selector/version binding. An absent agent is valid for a tenant or control-plane policy and never supplies a broader permission. Unfiltered control-plane access requires an explicit instance grant.

The shared local-operation authorization catalog publishes named application prerequisites. Connectivity inspection/preview uses `mcp.connectivity.read`; confirmed dispatch additionally uses `mcp.connectivity.test`. OIDC and local modes retain scope, role, current application-grant and Connectivity ControlPlane policy checks, followed by the existing server-owned destination and confirmation/idempotency authority.

Exchange and receiving middleware retain bounded allowlisted failure codes, layer, retryability, correlation and server-authored remediation. Removing a permission does not revoke a credential. Previously authenticated execution identifiers can distinguish expiry and revocation; unknown tokens keep limited disclosure. Dependency recovery never authorizes automatic retry of an uncertain mutation.

Raw ingress lifecycle disclosure additionally requires a valid signed paired-resource proof, constant-time full-hash comparison and purpose/resource match. Only then may the API and MCP challenge expose expired or revoked status. Unknown tokens and purpose/resource mismatches retain generic invalid status. Challenge bodies contain safe code/remediation/correlation without identity or credential identifiers.
