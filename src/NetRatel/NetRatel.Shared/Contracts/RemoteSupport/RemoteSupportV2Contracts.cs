using System.Text.Json.Serialization;

namespace NetRatel.Shared.Contracts.RemoteSupport;

/// <summary>Versioned, add-only contracts for the authoritative Remote Support V2 lifecycle.</summary>
public static class RemoteSupportV2ContractVersions
{
    public const int V1 = 1;
    public const int Current = V1;
}

public static class RemoteSupportV2TargetKinds
{
    /// <summary>The explicit V2 active-console/Winlogon target.</summary>
    public const string ConsoleLogin = "console_login";

    /// <summary>Legacy spelling accepted only for rolling V2 readers.</summary>
    public const string Console = "console";
    public const string InteractiveUser = "interactive_user";

    public static bool IsConsoleLogin(string? kind) =>
        string.Equals(kind, ConsoleLogin, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(kind, Console, StringComparison.OrdinalIgnoreCase);
}

public static class RemoteSupportV2SessionStates
{
    public const string Requested = "requested";
    public const string PreparingTarget = "preparing_target";
    public const string ReadyForOffer = "ready_for_offer";
    public const string Negotiating = "negotiating";
    public const string Connected = "connected";
    public const string Reconnecting = "reconnecting";
    public const string RenegotiationRequired = "renegotiation_required";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Expired = "expired";
}

public static class RemoteSupportV2ControlTypes
{
    public const string Close = "close";
    public const string RequestResume = "request_resume";
    public const string RequestSas = "request_sas";
    public const string RepairHelper = "repair_helper";
}

/// <summary>Runtime-advertised Remote Support V2 capabilities. These names are
/// endpoint facts, never client-version gates.</summary>
public static class RemoteSupportV2CapabilityNames
{
    public const string RemoteSupportV2 = "remote_support_v2";
    public const string InteractiveAssist = "interactive_assist";
    public const string SessionInventory = "session_inventory";
    public const string TargetPreflight = "target_preflight";
    public const string DirectWebRtcMedia = "direct_webrtc_media";
    public const string InputControl = "input_control";
    public const string ConsoleLogin = "console_login";
    public const string LockScreen = "lock_screen";
    public const string SecureAttention = "secure_attention";
    public const string HelperRepair = "helper_repair";
    public const string DesktopTransitionRecovery = "desktop_transition_recovery";
}

/// <summary>Fresh, presence-bound capabilities projected from the admitted
/// preparation stream for operator discovery.</summary>
public sealed record RemoteSupportV2CapabilitySnapshot(
    int TenantId,
    Guid AgentId,
    Guid ConnectionId,
    ulong ConnectionEpoch,
    IReadOnlyList<string> Capabilities,
    DateTimeOffset ObservedAtUtc,
    DateTimeOffset ExpiresAtUtc)
{
    public bool IsFresh(DateTimeOffset now) => now < ExpiresAtUtc;
    public bool Has(string capability) => Capabilities.Contains(capability, StringComparer.OrdinalIgnoreCase);
}

public static class RemoteSupportV2AuditEventTypes
{
    public const string SessionRequested = "session_requested";
    public const string LifecycleChanged = "lifecycle_changed";
    public const string ControlRequested = "control_requested";
    public const string SessionClosed = "session_closed";
    public const string SessionExpired = "session_expired";
    public const string TransitionStarted = "transition_started";
    public const string TargetSelectionRequired = "target_selection_required";
    public const string ReplacementPrepared = "replacement_prepared";
    public const string TransitionCompleted = "transition_completed";
}

/// <summary>Directions accepted by the transient V2 WebRTC negotiation boundary.</summary>
public static class RemoteSupportV2NegotiationDirections
{
    public const string Browser = "browser";
    public const string Agent = "agent";
}

/// <summary>
/// The only signal kinds accepted by the V2 media boundary. Their bounded
/// payloads are transient and must never be written to lifecycle/audit state.
/// </summary>
public static class RemoteSupportV2NegotiationSignalTypes
{
    public const string Offer = "offer";
    public const string Answer = "answer";
    public const string Ice = "ice";
    public const string EndOfCandidates = "end_of_candidates";
    public const string Status = "status";
}

/// <summary>Factual, bounded endpoint evidence that can start or refine an RS2-5 transition.</summary>
public static class RemoteSupportV2TransitionEvidenceKinds
{
    public const string SessionConnected = "session_connected";
    public const string SessionDisconnected = "session_disconnected";
    public const string SessionLoggedOff = "session_logged_off";
    public const string ActiveConsoleChanged = "active_console_changed";
    public const string WinlogonVisible = "winlogon_visible";
    public const string SignedInDesktopObserved = "signed_in_desktop_observed";
    public const string WorkstationLocked = "workstation_locked";
    public const string WorkstationUnlocked = "workstation_unlocked";
    public const string HelperDisconnected = "helper_disconnected";
    public const string ProviderLost = "provider_lost";
    public const string DesktopRecoveryExhausted = "desktop_recovery_exhausted";
    public const string CaptureFailedAfterFirstFrame = "capture_failed_after_first_frame";
}

/// <summary>Typed, transient endpoint fact. The actor owns all transition policy decisions.</summary>
public sealed record RemoteSupportV2TransitionEvidence(
    RemoteSupportSessionKey Session,
    Guid EvidenceId,
    long NegotiationGeneration,
    ulong TransitionSequence,
    DateTimeOffset ObservedAtUtc,
    string EvidenceKind,
    int? WindowsSessionId = null,
    string? UserSidHash = null,
    int? ActiveConsoleSessionId = null,
    string? WindowsSessionState = null,
    string? DesktopKind = null,
    string? ProviderKind = null,
    Guid? HelperRouteId = null,
    ulong? InventorySequence = null);

/// <summary>
/// Strict, transient WebRTC negotiation envelope. This deliberately sits
/// outside lifecycle snapshots, audit events, and persistence contracts.
/// </summary>
public sealed record RemoteSupportV2NegotiationEnvelope(
    RemoteSupportSessionKey Session,
    long Generation,
    string Direction,
    long Sequence,
    Guid MessageId,
    string SignalType,
    byte[] Payload,
    RemoteSupportSessionIceConfiguration? IceConfiguration = null);

/// <summary>Ephemeral ICE material issued by the API for exactly one logical session and generation.</summary>
public sealed record RemoteSupportSessionIceConfiguration(
    RemoteSupportSessionKey Session,
    long Generation,
    IReadOnlyList<RemoteSupportIceServerDto> Servers,
    DateTimeOffset ExpiresAtUtc);

/// <summary>
/// Bounded privileged control sent from the lifecycle actor to the exact agent
/// edge. It deliberately contains no key, media, SDP, or desktop content.
/// </summary>
public sealed record RemoteSupportV2PrivilegedControl(
    RemoteSupportSessionKey Session,
    Guid CommandId,
    string ControlType,
    long ExpectedLifecycleRevision,
    long NegotiationGeneration,
    RemoteSupportTargetDescriptor Target,
    Guid HelperRouteId);

/// <summary>
/// The sharded actor identity. WTS session IDs and Windows SID hashes are target
/// attributes and must never be substituted into this identity.
/// </summary>
public sealed record RemoteSupportSessionKey(int TenantId, Guid AgentId, Guid RemoteSupportSessionId);

/// <summary>Stable operator binding carried by every V2 read, signal, control, and resume contract.</summary>
public sealed record RemoteSupportOperatorBinding(string OperatorId);

/// <summary>Immutable target contract. It contains no media payload or credential.</summary>
public sealed record RemoteSupportTargetDescriptor(
    string Kind,
    int? WindowsSessionId = null,
    string? UserSidHash = null,
    ulong? InventorySequence = null);

/// <summary>Initial request. The future authority assigns the remote-support session ID after idempotency resolution.</summary>
public sealed record RemoteSupportOpenSessionCommand(
    int ContractVersion,
    int TenantId,
    Guid AgentId,
    Guid RequestId,
    RemoteSupportOperatorBinding InitiatingOperator,
    RemoteSupportTargetDescriptor Target,
    IReadOnlyList<string> RequestedCapabilities,
    DateTimeOffset RequestedAtUtc,
    DateTimeOffset? ExpiresAtUtc = null);

/// <summary>Authoritative lifecycle projection; transient SDP, ICE and media remain intentionally absent.</summary>
public sealed record RemoteSupportSessionSnapshot(
    int ContractVersion,
    RemoteSupportSessionKey Session,
    Guid OpenRequestId,
    RemoteSupportOperatorBinding InitiatingOperator,
    RemoteSupportTargetDescriptor Target,
    IReadOnlyList<string> GrantedCapabilities,
    string State,
    long LifecycleRevision,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    DateTimeOffset? ExpiresAtUtc = null,
    string? TerminalReasonCode = null);

/// <summary>Small, safe lifecycle event suitable for browser status and durable audit projection.</summary>
public sealed record RemoteSupportSessionStatus(
    int ContractVersion,
    RemoteSupportSessionKey Session,
    long LifecycleRevision,
    string State,
    string MessageCode,
    DateTimeOffset OccurredAtUtc);

/// <summary>Operator-bound control command. Future controls extend through typed contracts, never an arbitrary payload blob.</summary>
public sealed record RemoteSupportControlCommand(
    int ContractVersion,
    RemoteSupportSessionKey Session,
    RemoteSupportOperatorBinding Operator,
    Guid RequestId,
    string ControlType,
    long? ExpectedLifecycleRevision,
    DateTimeOffset RequestedAtUtc);

/// <summary>Operator-bound cursor request. Cursor values order audit/status events, never media frames.</summary>
public sealed record RemoteSupportResumeRequest(
    int ContractVersion,
    RemoteSupportSessionKey Session,
    RemoteSupportOperatorBinding Operator,
    long AfterAuditSequence,
    Guid RequestId,
    DateTimeOffset RequestedAtUtc);

/// <summary>Agent capability report used by later target-preparation and workflow phases.</summary>
public sealed record RemoteSupportCapabilityAdvertisement(
    int ContractVersion,
    int TenantId,
    Guid AgentId,
    IReadOnlyList<string> Capabilities,
    DateTimeOffset ObservedAtUtc,
    DateTimeOffset? ExpiresAtUtc = null);

/// <summary>Durable, redacted audit envelope. It intentionally has no raw control or signalling payload field.</summary>
public sealed record RemoteSupportAuditEvent(
    int ContractVersion,
    RemoteSupportSessionKey Session,
    long AuditSequence,
    Guid EventId,
    string EventType,
    string ActorKind,
    string ActorId,
    Guid? RequestId,
    string Outcome,
    string? FailureCode,
    DateTimeOffset OccurredAtUtc);

public sealed record RemoteSupportContractValidationError(string Code, string Message);

/// <summary>
/// Pure validation for V2 contract writers. It is deliberately tolerant of
/// unknown lifecycle/control strings on readers so later additive writers do
/// not break deployed readers.
/// </summary>
public static class RemoteSupportV2ContractValidator
{
    private const int MaximumOperatorIdLength = 256;
    private const int MaximumCapabilityLength = 64;
    private const int MaximumCodeLength = 128;
    public const int MaximumNegotiationPayloadBytes = 32 * 1024;

    public static bool TryValidate(
        RemoteSupportOpenSessionCommand command,
        out RemoteSupportContractValidationError? error)
    {
        if (!HasCurrentVersion(command.ContractVersion, out error) ||
            !HasValidTenantAndAgent(command.TenantId, command.AgentId, out error) ||
            command.RequestId == Guid.Empty)
        {
            error ??= new("request_id_invalid", "A non-empty request ID is required.");
            return false;
        }

        if (!HasValidOperator(command.InitiatingOperator, out error) ||
            !HasValidTarget(command.Target, out error) ||
            !HasValidCapabilities(command.RequestedCapabilities, out error))
        {
            return false;
        }

        if (command.ExpiresAtUtc is { } expiresAtUtc && expiresAtUtc <= command.RequestedAtUtc)
        {
            error = new("expiry_invalid", "Expiry must be after the request timestamp.");
            return false;
        }

        error = null;
        return true;
    }

    public static bool TryValidate(
        RemoteSupportControlCommand command,
        out RemoteSupportContractValidationError? error) =>
        HasCurrentVersion(command.ContractVersion, out error) &&
        HasValidSession(command.Session, out error) &&
        HasValidOperator(command.Operator, out error) &&
        HasNonEmptyGuid(command.RequestId, "request_id_invalid", "A non-empty request ID is required.", out error) &&
        HasCode(command.ControlType, "control_type_invalid", out error);

    public static bool TryValidate(
        RemoteSupportResumeRequest request,
        out RemoteSupportContractValidationError? error) =>
        HasCurrentVersion(request.ContractVersion, out error) &&
        HasValidSession(request.Session, out error) &&
        HasValidOperator(request.Operator, out error) &&
        HasNonEmptyGuid(request.RequestId, "request_id_invalid", "A non-empty request ID is required.", out error) &&
        HasNonNegative(request.AfterAuditSequence, "audit_cursor_invalid", "The audit cursor cannot be negative.", out error);

    public static bool TryValidate(
        RemoteSupportCapabilityAdvertisement advertisement,
        out RemoteSupportContractValidationError? error)
    {
        if (!HasCurrentVersion(advertisement.ContractVersion, out error) ||
            !HasValidTenantAndAgent(advertisement.TenantId, advertisement.AgentId, out error) ||
            !HasValidCapabilities(advertisement.Capabilities, out error))
        {
            return false;
        }

        if (advertisement.ExpiresAtUtc is { } expiresAtUtc && expiresAtUtc <= advertisement.ObservedAtUtc)
        {
            error = new("capability_expiry_invalid", "Capability expiry must be after observation.");
            return false;
        }

        error = null;
        return true;
    }

    public static bool TryValidate(
        RemoteSupportV2NegotiationEnvelope envelope,
        out RemoteSupportContractValidationError? error)
    {
        if (!HasValidSession(envelope.Session, out error) || envelope.Generation <= 0 || envelope.Sequence <= 0 ||
            envelope.MessageId == Guid.Empty || envelope.Payload is null || envelope.Payload.Length > MaximumNegotiationPayloadBytes)
        {
            error ??= new("negotiation_envelope_invalid", "Negotiation requires a bounded session, generation, sequence, message ID, and payload.");
            return false;
        }

        if (envelope.Direction is not RemoteSupportV2NegotiationDirections.Browser and not RemoteSupportV2NegotiationDirections.Agent)
        {
            error = new("negotiation_direction_invalid", "Negotiation direction must be browser or agent.");
            return false;
        }

        if (envelope.SignalType is not RemoteSupportV2NegotiationSignalTypes.Offer and
            not RemoteSupportV2NegotiationSignalTypes.Answer and
            not RemoteSupportV2NegotiationSignalTypes.Ice and
            not RemoteSupportV2NegotiationSignalTypes.EndOfCandidates and
            not RemoteSupportV2NegotiationSignalTypes.Status)
        {
            error = new("negotiation_signal_type_invalid", "Negotiation signal type is unsupported.");
            return false;
        }

        if (envelope.SignalType == RemoteSupportV2NegotiationSignalTypes.Offer &&
            envelope.Direction != RemoteSupportV2NegotiationDirections.Browser)
        {
            error = new("negotiation_offer_direction_invalid", "Only the browser may issue an offer.");
            return false;
        }

        if (envelope.SignalType == RemoteSupportV2NegotiationSignalTypes.Answer &&
            envelope.Direction != RemoteSupportV2NegotiationDirections.Agent)
        {
            error = new("negotiation_answer_direction_invalid", "Only the agent may issue an answer.");
            return false;
        }

        error = null;
        return true;
    }

    public static bool TryValidate(
        RemoteSupportV2TransitionEvidence evidence,
        out RemoteSupportContractValidationError? error)
    {
        if (!HasValidSession(evidence.Session, out error) || evidence.EvidenceId == Guid.Empty ||
            evidence.NegotiationGeneration <= 0 || evidence.TransitionSequence == 0 ||
            evidence.ObservedAtUtc == default || !HasCode(evidence.EvidenceKind, "transition_evidence_kind_invalid", out error))
        {
            error ??= new("transition_evidence_invalid", "Transition evidence requires a bounded session, identity, generation, sequence, timestamp, and kind.");
            return false;
        }

        if (evidence.WindowsSessionId is <= 0 || evidence.ActiveConsoleSessionId is <= 0 ||
            (evidence.UserSidHash is not null && !HasCode(evidence.UserSidHash, "transition_evidence_identity_invalid", out error)) ||
            (evidence.WindowsSessionState is not null && !HasCode(evidence.WindowsSessionState, "transition_evidence_state_invalid", out error)) ||
            (evidence.DesktopKind is not null && !HasCode(evidence.DesktopKind, "transition_evidence_desktop_invalid", out error)) ||
            (evidence.ProviderKind is not null && !HasCode(evidence.ProviderKind, "transition_evidence_provider_invalid", out error)))
        {
            error ??= new("transition_evidence_invalid", "Transition evidence contains an invalid bounded optional value.");
            return false;
        }

        error = null;
        return true;
    }

    public static string OpenIdempotencyKey(RemoteSupportOpenSessionCommand command) =>
        $"{command.TenantId}:{command.AgentId:N}:{command.RequestId:N}";

    private static bool HasCurrentVersion(int version, out RemoteSupportContractValidationError? error)
    {
        error = version == RemoteSupportV2ContractVersions.Current
            ? null
            : new("contract_version_unsupported", $"Remote Support contract version '{version}' is unsupported.");
        return error is null;
    }

    private static bool HasValidTenantAndAgent(int tenantId, Guid agentId, out RemoteSupportContractValidationError? error)
    {
        error = tenantId <= 0
            ? new("tenant_invalid", "A positive tenant ID is required.")
            : agentId == Guid.Empty
                ? new("agent_invalid", "A non-empty agent ID is required.")
                : null;
        return error is null;
    }

    private static bool HasValidSession(RemoteSupportSessionKey session, out RemoteSupportContractValidationError? error)
    {
        if (!HasValidTenantAndAgent(session.TenantId, session.AgentId, out error))
        {
            return false;
        }

        error = session.RemoteSupportSessionId == Guid.Empty
            ? new("session_invalid", "A non-empty remote-support session ID is required.")
            : null;
        return error is null;
    }

    private static bool HasValidOperator(RemoteSupportOperatorBinding binding, out RemoteSupportContractValidationError? error)
    {
        error = string.IsNullOrWhiteSpace(binding?.OperatorId) || binding.OperatorId.Trim().Length > MaximumOperatorIdLength
            ? new("operator_invalid", "A bounded initiating operator identity is required.")
            : null;
        return error is null;
    }

    private static bool HasValidTarget(RemoteSupportTargetDescriptor target, out RemoteSupportContractValidationError? error)
    {
        if (target is null)
        {
            error = new("target_invalid", "A target is required.");
            return false;
        }

        var kind = target.Kind?.Trim();
        if (RemoteSupportV2TargetKinds.IsConsoleLogin(kind))
        {
            error = target.UserSidHash is null && target.WindowsSessionId is null
                ? null
                : new("console_target_invalid", "A console target cannot include an interactive-user identity or WTS session.");
            return error is null;
        }

        if (string.Equals(kind, RemoteSupportV2TargetKinds.InteractiveUser, StringComparison.OrdinalIgnoreCase) &&
            target.WindowsSessionId is > 0 && !string.IsNullOrWhiteSpace(target.UserSidHash))
        {
            error = null;
            return true;
        }

        error = new("interactive_target_invalid", "An interactive-user target requires a positive WTS session and a user identity hash.");
        return false;
    }

    private static bool HasValidCapabilities(IReadOnlyList<string> capabilities, out RemoteSupportContractValidationError? error)
    {
        if (capabilities is null || capabilities.Count == 0 ||
            capabilities.Any(capability => string.IsNullOrWhiteSpace(capability) || capability.Trim().Length > MaximumCapabilityLength) ||
            capabilities.Select(capability => capability.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != capabilities.Count)
        {
            error = new("capabilities_invalid", "At least one bounded, distinct requested capability is required.");
            return false;
        }

        error = null;
        return true;
    }

    private static bool HasCode(string? value, string errorCode, out RemoteSupportContractValidationError? error)
    {
        error = string.IsNullOrWhiteSpace(value) || value.Trim().Length > MaximumCodeLength
            ? new(errorCode, "A bounded code is required.")
            : null;
        return error is null;
    }

    private static bool HasNonEmptyGuid(Guid value, string errorCode, string message, out RemoteSupportContractValidationError? error)
    {
        error = value == Guid.Empty ? new(errorCode, message) : null;
        return error is null;
    }

    private static bool HasNonNegative(long value, string errorCode, string message, out RemoteSupportContractValidationError? error)
    {
        error = value < 0 ? new(errorCode, message) : null;
        return error is null;
    }
}

/// <summary>
/// Actor-issued, transient work instruction for the admitted agent edge. The
/// effect is deliberately separate from lifecycle state and carries only the
/// correlation needed to return through the canonical preparation stream.
/// </summary>
public sealed record RemoteSupportTransitionEffect(
    Guid EffectId,
    RemoteSupportSessionKey Session,
    Guid TransitionId,
    ulong PresenceEpoch,
    long NegotiationGeneration,
    string Kind,
    RemoteSupportOperatorBinding Operator,
    RemoteSupportTargetDescriptor? Target = null);

public static class RemoteSupportTransitionEffectKinds
{
    public const string ReacquireInventory = "reacquire_inventory";
    public const string PrepareReplacementTarget = "prepare_replacement_target";
    public const string Cancel = "cancel";
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(RemoteSupportOpenSessionCommand))]
[JsonSerializable(typeof(RemoteSupportSessionSnapshot))]
[JsonSerializable(typeof(RemoteSupportSessionStatus))]
[JsonSerializable(typeof(RemoteSupportControlCommand))]
[JsonSerializable(typeof(RemoteSupportResumeRequest))]
[JsonSerializable(typeof(RemoteSupportCapabilityAdvertisement))]
[JsonSerializable(typeof(RemoteSupportAuditEvent))]
[JsonSerializable(typeof(RemoteSupportV2CapabilitySnapshot))]
[JsonSerializable(typeof(RemoteSupportTargetInventorySnapshot))]
[JsonSerializable(typeof(RemoteSupportPrepareTargetRequest))]
[JsonSerializable(typeof(RemoteSupportPreparedTargetResult))]
[JsonSerializable(typeof(RemoteSupportV2NegotiationEnvelope))]
[JsonSerializable(typeof(RemoteSupportSessionIceConfiguration))]
[JsonSerializable(typeof(RemoteSupportV2PrivilegedControl))]
[JsonSerializable(typeof(RemoteSupportTransitionEffect))]
public partial class RemoteSupportV2JsonContext : JsonSerializerContext;
