namespace NetRatel.API.Bootstrap;

/// <summary>
/// Durable, non-secret record of a NetRatel installation lifecycle. Secrets remain in deployment
/// configuration or in permission-restricted bootstrap files; this descriptor contains only hashes
/// and references so copying it cannot grant setup access.
/// </summary>
public sealed record BootstrapDescriptor(
    int Version,
    Guid InstanceId,
    BootstrapState State,
    string SetupProofHash,
    DateTimeOffset SetupProofExpiresAtUtc,
    string KeyMaterialProofHash,
    string? SelectedProvider,
    string? ConnectionReference,
    Guid? OperationId,
    DateTimeOffset? OperationLeaseExpiresAtUtc,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    bool AdoptedExistingInstallation);

public sealed record BootstrapStatus(
    BootstrapState State,
    bool SetupRequired,
    bool IsReady,
    bool IsRecoveryRequired,
    string? SelectedProvider,
    Guid? OperationId);

internal sealed record BootstrapJournalEntry(
    Guid OperationId,
    string Event,
    BootstrapState State,
    DateTimeOffset RecordedAtUtc);
