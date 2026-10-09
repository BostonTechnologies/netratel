# Service identities

NetRatel ↔ RatelDesk setup uses [pairing codes](integrations/rateldesk-pairing.md).
The setup credential is separate from tenant business principals. Final Save
creates narrowly scoped business credentials for the selected real tenant and
capabilities. Current connection authority is checked at token issuance and
business use, including cached tokens. Deletion immediately revokes local access.

Existing API/MCP, local/OIDC, system and native-agent credentials retain their
separate authorization boundaries. Pairing does not use a human administrator
token for remote business calls. General signing keys and installation identity
are preserved during the one-time cutover.
