# NetRatel Client

The client connects to the authenticated Agent Gateway and uses Akka-backed
presence, command, file, terminal, telemetry, and remote-support transports.
It does not generate or consume SpacetimeDB bindings.

For installation, update, and rollout procedures, see the
[client documentation](../../../docs/CLIENT.md).

PowerShell commands and scripts use an installed external interpreter. Explicit
`Pwsh` and `WindowsPowerShell` selections require that engine; PowerShell `Auto`
prefers `pwsh` and falls back to Windows PowerShell on Windows. A missing
interpreter fails the task while the client keeps running. Installed settings
containing the retired `UseInProcPowerShell` key continue to load.
