namespace NetRatel.Shared.Contracts;

public sealed record ClientOffboardingDetailsDto(
    int TenantId,
    Guid AgentId,
    bool Removed,
    bool Online,
    bool CanUninstall,
    string? UnavailableReason,
    ClientOffboardingOperationDto? Operation);

public sealed record ClientOffboardingOperationDto(
    Guid OperationId,
    string State,
    DateTimeOffset RequestedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset ReceiptExpiresAtUtc,
    string? FailureCode,
    bool ServiceRemoved,
    bool FilesRemoved);

public sealed record ClientUninstallRequest(Guid OperationId, string? Reason = null);
public sealed record ClientRemovalRequest(string? Reason = null);
