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
