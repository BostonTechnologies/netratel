namespace NetRatel.Shared.Contracts.Netclaw;

public enum InvestigationState { Idle, Processing, AwaitingApproval, DeliveryUnknown, Archived, Queued }

public sealed record NetclawTenantDto(int TenantId, string TenantName, bool CanInvestigate, bool CanConfigure);
public sealed record NetclawConnectionDto(Guid Id, int TenantId, long Revision, bool Enabled, string? DaemonAddress,
    bool HasDeviceToken, DateTimeOffset? LastTestedAtUtc, bool? LastTestSucceeded, string? LastTestCode)
{
    public string? LastTestRemediation => LastTestSucceeded == false ? NetclawConnectionTestHelp.ForCode(LastTestCode) : null;
}
public enum NetclawSignalRState { Unknown, Disconnected, Connecting, Connected, Reconnecting }
public sealed record NetclawSessionObservationDto(Guid InvestigationId, NetclawSignalRState SignalRState,
    DateTimeOffset AuthenticatedAtUtc, DateTimeOffset LastActivityUtc);
public sealed record NetclawConnectionDiagnosticsDto(int TenantId, Guid ConnectionId, long Revision,
    bool ObservationAvailable, DateTimeOffset ObservedAtUtc, IReadOnlyList<NetclawSessionObservationDto> Sessions);
public sealed record NetclawPairRequest(string Address, string PairingCode, long ExpectedRevision = 0);
public sealed record NetclawEnabledRequest(long ExpectedRevision, bool Enabled);
public sealed record NetclawTestResult(bool Success, string Code, NetclawConnectionDto Connection)
{
    public string? Remediation => Success ? null : NetclawConnectionTestHelp.ForCode(Code);
}
public sealed record InvestigationCreateRequest(Guid RequestId, string Title, IReadOnlyList<Guid> AgentIds, Guid? OccurrenceId = null,
    IReadOnlyList<AiPromptReference>? SelectedPrompts = null);
public sealed record GeneralChatCreateRequest(Guid RequestId, string Title);
public sealed record InvestigationMessageRequest(Guid MessageId, string Text);
public sealed record InvestigationApprovalRequest(string SelectedKey);
public sealed record InvestigationArchiveRequest(Guid ExpectedMessageId, bool AcknowledgePossibleDelivery = false);
public sealed record InvestigationClientDto(Guid AgentId, string HostName, string? FriendlyName, IReadOnlyList<string> Tags,
    DateTimeOffset? LastSeenUtc, string? OperatingSystem = null, string? Architecture = null,
    string? PrimaryIpAddress = null, IReadOnlyList<string>? IpAddresses = null, string Status = "unknown",
    string? AgentVersion = null, DateTimeOffset? ObservedAtUtc = null, bool HostNameKnown = true,
    string? TenantName = null, string? DisplayName = null);
public sealed record InvestigationClientPageDto(IReadOnlyList<InvestigationClientDto> Items, int? NextOffset);
public sealed record InvestigationSummaryDto(Guid Id, int TenantId, string Title, InvestigationState State,
    IReadOnlyList<Guid> AgentIds, Guid? OccurrenceId, Guid? FlowRunId, DateTimeOffset LastActivityUtc, string CreatedBy)
{
    public bool IsGeneralChat => AgentIds.Count == 0;
}
public sealed record InvestigationEventDto(long Sequence, string Type, string Text, string? Actor, string? CallId,
    IReadOnlyList<InvestigationOptionDto>? Options, DateTimeOffset CreatedAtUtc, Guid? MessageId = null);
public sealed record InvestigationOptionDto(string Key, string Label);
public sealed record InvestigationSnapshotDto(InvestigationSummaryDto Investigation, long LastSequence, Guid? ActiveMessageId,
    IReadOnlyList<InvestigationEventDto> Events, IReadOnlyList<InvestigationClientDto> Clients,
    IReadOnlyList<InvestigationApprovalDto> PendingApprovals, string? Draft = null, IReadOnlyList<AiPromptSnapshot>? Prompts = null,
    InvestigationSentContextDto? SentContext = null);
public sealed record InvestigationSentContextDto(Guid MessageId, string Request, string MetadataJson, int PayloadBytes,
    DateTimeOffset PreparedAtUtc, DateTimeOffset? SendAttemptedAtUtc, IReadOnlyList<string> OmissionNotes);
public sealed record InvestigationApprovalDto(string CallId, IReadOnlyList<InvestigationOptionDto> Options);
