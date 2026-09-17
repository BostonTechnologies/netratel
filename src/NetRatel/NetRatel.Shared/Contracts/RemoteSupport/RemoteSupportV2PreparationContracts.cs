namespace NetRatel.Shared.Contracts.RemoteSupport;

/// <summary>
/// Agent-authenticated WTS inventory. The agent remains the source of truth;
/// API consumers receive a freshness projection rather than a lifecycle authority.
/// </summary>
public sealed record RemoteSupportTargetInventorySnapshot(
    int ContractVersion,
    int TenantId,
    Guid AgentId,
    ulong InventorySequence,
    DateTimeOffset ObservedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    IReadOnlyList<RemoteSupportTargetInventoryEntry> Entries);

/// <summary>Redacted WTS target metadata. User SID hashes are comparison values, never raw Windows SIDs.</summary>
public sealed record RemoteSupportTargetInventoryEntry(
    int WindowsSessionId,
    string State,
    string? UserSidHash,
    bool IsConsoleSession,
    bool IsConnected,
    bool IsLocked,
    bool IsWinlogon,
    bool HelperConnected,
    bool HelperVersionMatches,
    string? HelperVersion,
    string? DisplayLabel = null);

/// <summary>
/// A short-lived, operator-bound request to prepare one immutable target.
/// It is not a lifecycle command and creates no Remote Support session.
/// </summary>
public sealed record RemoteSupportPrepareTargetCommand(
    int ContractVersion,
    int TenantId,
    Guid AgentId,
    Guid RequestId,
    RemoteSupportOperatorBinding Operator,
    RemoteSupportTargetDescriptor Target,
    Guid RouteNonce,
    DateTimeOffset RequestedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    RemoteSupportSessionKey? Session = null);

/// <summary>Operator API input for an exact target preflight.</summary>
public sealed record RemoteSupportPrepareTargetRequest(RemoteSupportTargetDescriptor Target);

/// <summary>
/// Opaque, per-helper connection identity bound to a preparation request.
/// Later phases must require this exact route rather than reselecting a helper.
/// </summary>
public sealed record RemoteSupportHelperRoute(
    Guid HelperRouteId,
    int WindowsSessionId,
    string UserSidHash,
    string HelperVersion);

/// <summary>
/// Result for one exact target preparation. It deliberately contains no SDP,
/// ICE, media, input payload, credential, or browser readiness claim.
/// </summary>
public sealed record RemoteSupportPreparedTargetResult(
    int ContractVersion,
    int TenantId,
    Guid AgentId,
    Guid RequestId,
    Guid RouteNonce,
    RemoteSupportTargetDescriptor Target,
    ulong InventorySequence,
    bool TargetValid,
    bool ProviderReady,
    string Code,
    string Message,
    RemoteSupportHelperRoute? HelperRoute,
    DateTimeOffset ObservedAtUtc,
    RemoteSupportSessionKey? Session = null);

public static class RemoteSupportV2PreparationValidator
{
    private const int MaximumTextLength = 256;

    public static bool TryValidate(
        RemoteSupportTargetInventorySnapshot snapshot,
        out RemoteSupportContractValidationError? error)
    {
        if (snapshot.ContractVersion != RemoteSupportV2ContractVersions.Current)
        {
            error = new("contract_version_unsupported", "The Remote Support inventory contract version is unsupported.");
            return false;
        }

        if (snapshot.TenantId <= 0 || snapshot.AgentId == Guid.Empty || snapshot.InventorySequence == 0)
        {
            error = new("inventory_identity_invalid", "Inventory requires a tenant, agent, and positive sequence.");
            return false;
        }

        if (snapshot.ExpiresAtUtc <= snapshot.ObservedAtUtc || snapshot.Entries is null ||
            snapshot.Entries.Any(entry => entry.WindowsSessionId < 0 ||
                !IsBounded(entry.State) ||
                (entry.UserSidHash is not null && !IsBounded(entry.UserSidHash)) ||
                (entry.HelperVersion is not null && !IsBounded(entry.HelperVersion)) ||
                (entry.DisplayLabel is not null && !IsBounded(entry.DisplayLabel))) ||
            snapshot.Entries.Select(entry => entry.WindowsSessionId).Distinct().Count() != snapshot.Entries.Count)
        {
            error = new("inventory_invalid", "Inventory entries must be bounded, non-duplicated, and have a future expiry.");
            return false;
        }

        error = null;
        return true;
    }

    public static bool TryValidate(
        RemoteSupportPrepareTargetCommand command,
        out RemoteSupportContractValidationError? error)
    {
        error = null;
        if (command.ContractVersion != RemoteSupportV2ContractVersions.Current || command.TenantId <= 0 ||
            command.AgentId == Guid.Empty || command.RequestId == Guid.Empty || command.RouteNonce == Guid.Empty)
        {
            error = new("prepare_identity_invalid", "Preparation requires the current version, tenant, agent, request, and route nonce.");
            return false;
        }

        if (command.Session is { } session &&
            (session.TenantId != command.TenantId || session.AgentId != command.AgentId || session.RemoteSupportSessionId == Guid.Empty))
        {
            error = new("prepare_session_invalid", "A preparation session must belong to the same tenant and agent.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(command.Operator?.OperatorId) || !IsBounded(command.Operator.OperatorId) ||
            !TryValidateTarget(command.Target, out error) || command.ExpiresAtUtc <= command.RequestedAtUtc)
        {
            error ??= new("prepare_expiry_invalid", "Preparation expiry must be after its request timestamp.");
            return false;
        }

        error = null;
        return true;
    }

    public static bool TryValidate(
        RemoteSupportPreparedTargetResult result,
        out RemoteSupportContractValidationError? error)
    {
        error = null;
        if (result.ContractVersion != RemoteSupportV2ContractVersions.Current || result.TenantId <= 0 ||
            result.AgentId == Guid.Empty || result.RequestId == Guid.Empty || result.RouteNonce == Guid.Empty ||
            result.InventorySequence == 0 || !TryValidateTarget(result.Target, out error) ||
            !IsBounded(result.Code) || !IsBounded(result.Message))
        {
            error ??= new("prepared_target_invalid", "Prepared target data is invalid.");
            return false;
        }

        if (result.Session is { } session &&
            (session.TenantId != result.TenantId || session.AgentId != result.AgentId || session.RemoteSupportSessionId == Guid.Empty))
        {
            error = new("prepared_session_invalid", "A prepared route session must belong to the same tenant and agent.");
            return false;
        }

        if (result.ProviderReady && !result.TargetValid)
        {
            error = new("prepared_target_invalid", "A provider cannot be ready for an invalid target.");
            return false;
        }

        if (result.ProviderReady && result.HelperRoute is null)
        {
            error = new("helper_route_required", "A provider-ready interactive target must include its exact helper route.");
            return false;
        }

        if (result.HelperRoute is { } route &&
            (route.HelperRouteId == Guid.Empty || route.WindowsSessionId <= 0 ||
             !IsBounded(route.UserSidHash) || !IsBounded(route.HelperVersion)))
        {
            error = new("helper_route_invalid", "A helper route must bind one helper connection to one exact WTS target.");
            return false;
        }

        error = null;
        return true;
    }

    private static bool TryValidateTarget(RemoteSupportTargetDescriptor? target, out RemoteSupportContractValidationError? error)
    {
        if (target is null)
        {
            error = new("target_invalid", "A target is required.");
            return false;
        }

        if (RemoteSupportV2TargetKinds.IsConsoleLogin(target.Kind) &&
            target.WindowsSessionId is null && target.UserSidHash is null)
        {
            error = null;
            return true;
        }

        if (string.Equals(target.Kind, RemoteSupportV2TargetKinds.InteractiveUser, StringComparison.OrdinalIgnoreCase) &&
            target.WindowsSessionId is > 0 && IsBounded(target.UserSidHash))
        {
            error = null;
            return true;
        }

        error = new("target_invalid", "The target must be either a console or an exact interactive Windows session.");
        return false;
    }

    private static bool IsBounded(string? value) => !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= MaximumTextLength;
}
