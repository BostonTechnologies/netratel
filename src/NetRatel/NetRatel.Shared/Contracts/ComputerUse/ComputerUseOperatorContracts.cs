namespace NetRatel.Shared.Contracts.ComputerUse;

/// <summary>Redacted automation lease status for the existing Remote Support operator surface.</summary>
public sealed record ComputerUseOperatorSession(
    Guid SessionId,
    ComputerUseTarget Target,
    string Provider,
    string State,
    bool ControlAllowed,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset IdleExpiresAtUtc,
    DateTimeOffset ExpiresAtUtc,
    long Revision);

public sealed record ComputerUseOperatorStatus(
    bool Enabled,
    ComputerUseOperatorSession? Session,
    bool CanControl = false);
