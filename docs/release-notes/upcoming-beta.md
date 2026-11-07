# Upcoming beta — logging and Monitoring

This is unreleased source work. Version selection, tagging, release builds and
publication are deferred while the next beta's scope remains open. Product
version authority remains `Directory.Build.props`; follow [release engineering](../deployment/RELEASES.md)
when the complete release is ready.

## Diagnostic logging and container retention

Application diagnostics default to Warning, retaining Error and Critical/Fatal.
The validated `NetRatel:Logging` options control hosted services, restricted API
startup, stdio MCP and native Client diagnostics. Use
`NetRatel__Logging__MinimumLevel=Information` or a targeted
`NetRatel__Logging__Overrides__<category>` override for diagnostics, then restart
the process. Requested command, task and terminal output retains its behavior.
Stdio MCP keeps protocol frames on stdout and diagnostics on stderr. Hosted
events have one console record while OTEL and application consumers still receive them.

Supported Compose recipes default to `json-file` retention of `200m` across
five files **per container**. Set `NETRATEL_DOCKER_LOG_MAX_SIZE` and
`NETRATEL_DOCKER_LOG_MAX_FILE` to override retention, then recreate affected
containers; a restart does not update Docker HostConfig. Compose maps
`NETRATEL_LOG_MINIMUM_LEVEL` to the process diagnostic threshold. Explicit
operator driver/options are preserved during HTTP host setup merges. See
[configuration](../deployment/CONFIGURATION.md#diagnostic-logging-and-docker-retention).

## Disk eligibility and cached policy preview

Linux collection uses the agent's mount namespace to distinguish meaningful
capacity from package images, pseudo filesystems and proven duplicate views.
Real read-only data disks, meaningful container roots, backup/network volumes
and independently limited ZFS datasets remain meaningful resources. Excluded
inventory has reasons, with explicit bounds and truncation; eligible disks take
priority within collection and transport limits.

Rules support bounded include/exclude mount and filesystem patterns with one
shared interpretation for persistence, evaluation and cached resource previews.
Explicit exclusions win. Policy edits follow evaluation revision/reset rules;
proven exclusions suspend an unresolved occurrence without declaring recovery.
Normal client upgrades supply the new optional metadata, with no re-enrollment.
Older or incomplete metadata is shown as unclassified or unconfirmed. See
[Monitoring](../operations/MONITORING.md).

## Readable, pinned incident context

New Monitoring incidents pin display identity, known disk facts, conditions,
times and public-link context at occurrence creation. Tiny free percentages
remain percentages: `0.01220703125` displays approximately `0.01% free`.
Unavailable byte counts are omitted. Disk incidents omit irrelevant service
details; service and grouped availability events retain relevant context.

Exact client and immutable occurrence links use the configured public Web base,
including its path prefix, and require existing authentication/tenant access.
Missing public configuration has an explicit unavailable-link result. Legacy
canonical envelopes, immutable published graphs, custom mappings and prepared
historical delivery bytes remain readable and stable. The stock-flow upgrade
path applies forward to new events. See [RatelDesk integration](../integrations/RATELDESK-CONNECTOR.md).

## Full-result filters, compact details and real paging

Monitoring applies state, severity, computer, text and labelled date filters
across the authorized server query before paging. Firing lifecycle remains
distinct from stale/Unknown evidence. The computer selector searches the tenant
directory, date bounds use the displayed timezone with an inclusive end date,
and query context survives refresh and browser navigation. Tenant badges are
labelled separately from matching results.

The Web default is 20 rows, with real 20/50/100 server page sizes and bounded
First/Previous/Next cursor navigation. Continuations use a common calculated,
tenant/query-bound opaque contract across API, persistence, CLI and MCP.
Pre-upgrade pagination explicitly requires a restart. Loading/paging errors
remain distinct from an empty result.

The leading keyboard-accessible Alert details disclosure creates a child row
only while expanded and preserves the existing detail cards. Exact occurrence
links work independently of the visible page. A measured bounded scrolling
region keeps the header and actual pager reachable on desktop and narrow
viewports, including expanded details.

Required fast CI remains separate from the focused optional browser and
disposable-container checks. Source validation and those synthetic checks do
not establish owner production acceptance or a live deployment.
