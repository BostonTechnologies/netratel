# Troubleshooting

## The Compose configuration does not render

Run `docker compose ... config --quiet` before starting a stack. The tracked
examples intentionally require deployment-specific values for the PostgreSQL
password, OIDC settings, image digests, and agent-signing key. Supply them
through the deployment secret mechanism, not an edited tracked file.

## Browser login fails or a protected API call is rejected

Check that the OIDC authority, audience, client identifier, redirect/logout
paths, group/role mapping, and API scope match the provider configuration. The
application intentionally has no anonymous Production mode or default admin
password. Browser sessions, API machine credentials, and agent credentials
are separate trust paths.

## stdio MCP exits at startup

Set `NETRATEL_MCP_CONFIG` to an existing absolute configuration-file path. The
server intentionally ignores ambient CLI and AgentClient credential variables.
Validate the API and token endpoint values in that private file, then inspect
standard error for diagnostics; standard output is reserved for protocol
frames.

## A Native Client cannot connect

Confirm it was enrolled with a valid short-lived code for the intended
deployment, that its persisted identity was not copied from another machine,
and that the API/gateway endpoints are reachable with the deployment's TLS
configuration. Do not reset an identity or disable downgrade protection as a
first response; preserve logs and recovery material for the authorized
operator.
