# ADR 0005: effective branding and configuration precedence

Status: accepted for implementation in P09 and P10.

NetRatel is the immutable upstream product and protocol identity. Branding can
change only visible deployment presentation: display names, approved assets,
colours, URLs and related copy. It never changes assemblies, packages,
database schema identities, signing audiences, cookies, Data Protection
application names, agent protocols or MCP resource identifiers.

For each field the effective value is deployment-managed configuration first,
durable administrator override second where the field is editable, and the
approved NetRatel default last. The administration UI shows deployment-managed
values but cannot overwrite them. Stored overrides are kept separately from
effective values so a reset removes only the stored override.

Theme selection remains client presentation state. P10 will execute the small
preference decision before visible content is painted and will test computed
colours before application runtime for light, dark and system choices,
including unavailable storage and custom branding.
