namespace NetRatel.Shared.Contracts.RatelDesk;

public sealed record RatelDeskConnectorTenantDto(int TenantId, string Name);

public sealed record RatelDeskPriorityMapping(int Information = 0, int Warning = 1, int Error = 2, int Critical = 3);

public sealed record RatelDeskConnectorConfiguration(
    string Name, string Origin, string OrganizationId, string CustomerId,
    string? AssignedToId, IReadOnlyList<Guid> CategoryIds, RatelDeskPriorityMapping Priorities, bool Enabled);

public sealed record RatelDeskConnectorDto(Guid Id, int TenantId, long Revision,
    RatelDeskConnectorConfiguration Configuration, bool HasCredential, long CredentialRevision,
    bool AutomaticDeliveryAvailable, string AvailabilityCode);

public sealed record SaveRatelDeskConnectorRequest(long ExpectedRevision, RatelDeskConnectorConfiguration Configuration);
public sealed record RotateRatelDeskConnectorCredentialRequest(long ExpectedCredentialRevision, string Credential);
public enum RatelDeskConnectionTestStatus { MappingValidated, AuthenticationRejected, MappingRejected, Unavailable }
public sealed record RatelDeskConnectionTestResult(RatelDeskConnectionTestStatus Status, string Code,
    bool AutomaticDeliveryAvailable = false, int? RetryAfterSeconds = null);

/// <summary>Subset of the actual RatelDesk CreateIncidentDto; priority is the numeric TicketPriority (0..3).</summary>
public sealed record RatelDeskCreateIncidentDto(string Title, string Description, int Priority,
    string CustomerId, string OrganizationId, string? AssignedToId, IReadOnlyList<Guid> CategoryIds);

public sealed record RatelDeskDryRunRequest(string Title, string Description, int Priority);
public sealed record RatelDeskDryRunResult(RatelDeskCreateIncidentDto Payload, string SemanticFingerprint,
    string FakeIncidentId = "dry-run-only", bool SendsIncident = false, bool AutomaticDeliveryAvailable = false);

public enum RatelDeskDeliveryStatus { Succeeded, AuthenticationRejected, PayloadRejected, RateLimited, Unavailable, DeliveryUnknown }
public sealed record RatelDeskIncidentReceipt(string IncidentId, string TrackingId, string? SafeLink);
public sealed record RatelDeskDeliveryResult(RatelDeskDeliveryStatus Status, string Code,
    RatelDeskIncidentReceipt? Receipt = null, int? RetryAfterSeconds = null);

public static class RatelDeskConnectorLimits
{
    public const int MaximumConnectorsPerTenant = 256;
    public const int MaximumTitleCharacters = 200;
    public const int MaximumDescriptionCharacters = 8_000;
    public const int MaximumRequestBytes = 32_768;
    public const int MaximumResponseBytes = 131_072;
    public const int MaximumLookupItems = 512;
    public const int MaximumCategories = 16;
    public const string ReceiverUnavailableCode = "receiver-idempotency-unverified";
}
