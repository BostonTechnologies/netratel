# NetRatel agent trust model

Status: current PostgreSQL/Akka gateway architecture. The former
SpacetimeDB/reducer trust model is retired and must not be used as a deployment
or implementation reference.

## Enrollment and authenticated gateway access

1. An agent sends an enrollment code to `NetRatel.API`.
2. The API creates or binds the canonical PostgreSQL agent record and issues a
   refresh credential.
3. The agent exchanges that credential for a short-lived ES256 access token.
4. The authenticated gateway validates the token and binds the connection to
   the canonical tenant/agent identity before presence, command, telemetry,
   file, terminal, remote-support, or update work is accepted.

The API private key is configured through `AgentAuth:PrivateKeyPath` in the
deployment environment. It must remain outside source control. Gateway
authorization and PostgreSQL ownership enforce tenant isolation; a client
supplied legacy identity is never authoritative.

## Security boundaries

| Component | Responsibility |
| --- | --- |
| NetRatel.API | Enrolls agents, signs access tokens, and authorizes API work. |
| Authenticated gateway | Fences each active connection to the validated tenant and canonical agent. |
| PostgreSQL | Stores durable agents, workflows, remote-support inventory, and update state. |
| NetRatel.Client | Protects its refresh credential and uses the authenticated API origin only. |

## Future Akka work

Future agent-facing functionality must use the authenticated gateway and
PostgreSQL durable authority. For the removed remote-support protocol and
rebuild constraints, see
[`remote-support-legacy-spec-and-retirement.md`](docs/akka-migration/remote-support-legacy-spec-and-retirement.md).
