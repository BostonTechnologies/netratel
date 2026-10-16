# Documentation move map

The documentation reorganization on 14 October 2026 preserves operator guides, release history and implementation evidence. This table identifies the canonical destinations of retired docs-root paths. Git history retains the original files and their content; published release assets and tags are unchanged.

| Previous path | Canonical guide |
| --- | --- |
| `docs/API_REFERENCE.md` | [docs/contracts/API_REFERENCE.md](../../docs/contracts/API_REFERENCE.md) |
| `docs/ARCHITECTURE.md` | [docs/implementation/ARCHITECTURE.md](../../docs/implementation/ARCHITECTURE.md) |
| `docs/BRANDING.md` | [docs/deployment/BRANDING.md](../../docs/deployment/BRANDING.md) |
| `docs/CLIENT.md` | [docs/client/CLIENT.md](../../docs/client/CLIENT.md) |
| `docs/CLIENT_DEPENDENCY_TRIMMING.md` | [docs/implementation/CLIENT_DEPENDENCY_TRIMMING.md](../../docs/implementation/CLIENT_DEPENDENCY_TRIMMING.md) |
| `docs/CLIENT_INSTALLER_RECOVERY.md` | [docs/client/CLIENT_INSTALLER_RECOVERY.md](../../docs/client/CLIENT_INSTALLER_RECOVERY.md) |
| `docs/CLIENT_OUTAGE_RESILIENCE.md` | [docs/client/CLIENT_OUTAGE_RESILIENCE.md](../../docs/client/CLIENT_OUTAGE_RESILIENCE.md) |
| `docs/CLIENT_RENEWAL_DIAGNOSTICS.md` | [docs/client/CLIENT_RENEWAL_DIAGNOSTICS.md](../../docs/client/CLIENT_RENEWAL_DIAGNOSTICS.md) |
| `docs/CLI_AND_MCP.md` | [docs/integrations/CLI_AND_MCP.md](../../docs/integrations/CLI_AND_MCP.md) |
| `docs/CONFIGURATION.md` | [docs/deployment/CONFIGURATION.md](../../docs/deployment/CONFIGURATION.md) |
| `docs/DEVELOPMENT.md` | [docs/implementation/DEVELOPMENT.md](../../docs/implementation/DEVELOPMENT.md) |
| `docs/FIRST_RUN_SETUP.md` | [docs/deployment/FIRST_RUN_SETUP.md](../../docs/deployment/FIRST_RUN_SETUP.md) |
| `docs/FLOWS.md` | [docs/operations/FLOWS.md](../../docs/operations/FLOWS.md) |
| `docs/GATEWAY_CONNECTIVITY_HARDENING.md` | [docs/client/GATEWAY_CONNECTIVITY_HARDENING.md](../../docs/client/GATEWAY_CONNECTIVITY_HARDENING.md) |
| `docs/MONITORING.md` | [docs/operations/MONITORING.md](../../docs/operations/MONITORING.md) |
| `docs/NOTIFICATION-CONNECTORS.md` | [docs/integrations/NOTIFICATION-CONNECTORS.md](../../docs/integrations/NOTIFICATION-CONNECTORS.md) |
| `docs/RATELDESK-CONNECTOR.md` | [docs/integrations/RATELDESK-CONNECTOR.md](../../docs/integrations/RATELDESK-CONNECTOR.md) |
| `docs/RC11_AKKA_RUNTIME_REFACTOR.md` | [docs/implementation/RC11_AKKA_RUNTIME_REFACTOR.md](../../docs/implementation/RC11_AKKA_RUNTIME_REFACTOR.md) |
| `docs/RC16_CLIENT_RELIABILITY.md` | [docs/implementation/RC16_CLIENT_RELIABILITY.md](../../docs/implementation/RC16_CLIENT_RELIABILITY.md) |
| `docs/RC19_INSTALLER_GATEWAY_RELIABILITY.md` | [docs/implementation/RC19_INSTALLER_GATEWAY_RELIABILITY.md](../../docs/implementation/RC19_INSTALLER_GATEWAY_RELIABILITY.md) |
| `docs/RELEASES.md` | [docs/deployment/RELEASES.md](../../docs/deployment/RELEASES.md) |
| `docs/RELEASE_NOTES_0.1.0-rc.14.md` | [docs/release-notes/RELEASE_NOTES_0.1.0-rc.14.md](../../docs/release-notes/RELEASE_NOTES_0.1.0-rc.14.md) |
| `docs/RELEASE_NOTES_0.1.0-rc.5.md` | [docs/release-notes/RELEASE_NOTES_0.1.0-rc.5.md](../../docs/release-notes/RELEASE_NOTES_0.1.0-rc.5.md) |
| `docs/RELEASE_NOTES_0.1.0-rc.6.md` | [docs/release-notes/RELEASE_NOTES_0.1.0-rc.6.md](../../docs/release-notes/RELEASE_NOTES_0.1.0-rc.6.md) |
| `docs/SELF_HOSTING.md` | [docs/deployment/SELF_HOSTING.md](../../docs/deployment/SELF_HOSTING.md) |
| `docs/SERVICES.md` | [docs/operations/SERVICES.md](../../docs/operations/SERVICES.md) |
| `docs/SERVICE_IDENTITY.md` | [docs/deployment/SERVICE_IDENTITY.md](../../docs/deployment/SERVICE_IDENTITY.md) |
| `docs/SQLITE.md` | [docs/implementation/SQLITE.md](../../docs/implementation/SQLITE.md) |
| `docs/SUPPORTED_PLATFORMS.md` | [docs/deployment/SUPPORTED_PLATFORMS.md](../../docs/deployment/SUPPORTED_PLATFORMS.md) |
| `docs/TROUBLESHOOTING.md` | [docs/operations/TROUBLESHOOTING.md](../../docs/operations/TROUBLESHOOTING.md) |
| `docs/client-services-inventory.md` | [docs/client/client-services-inventory.md](../../docs/client/client-services-inventory.md) |
| `docs/deployment-branding.md` | [docs/deployment/deployment-branding.md](../../docs/deployment/deployment-branding.md) |
| `docs/netclaw-ai-operations.md` | [docs/integrations/netclaw-ai-operations.md](../../docs/integrations/netclaw-ai-operations.md) |
| `docs/rbac.md` | [docs/operations/rbac.md](../../docs/operations/rbac.md) |
| `docs/theme-first-paint.md` | [docs/implementation/theme-first-paint.md](../../docs/implementation/theme-first-paint.md) |

`docs/README.md` is the navigation entry point. `docs/THIRD-PARTY-NOTICES.txt` remains at the root because build and release packages distribute that legal notice; it is the only non-index exception.

HTTP MCP detailed host setup, recovery, client storage and rotation sections moved from `docs/mcp-http/setup.md` to [the hosting reference](../mcp-http/hosting-reference.md). The original setup URL remains the short operator quickstart.
