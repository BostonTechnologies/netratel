namespace NetRatel.Shared.Client;

/// <summary>The public origins used by one installer snapshot. Null values denote facts
/// that were not recorded by older snapshots; they must not be filled from current settings.</summary>
public sealed record ClientInstallEndpointSummary(
    string PublicWebBaseUrl,
    string PublicApiBaseUrl,
    string? EffectiveGatewayBaseUrl,
    string? PublicWebSource,
    string? PublicApiSource,
    string? GatewaySource);
