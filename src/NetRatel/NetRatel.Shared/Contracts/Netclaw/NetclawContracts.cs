namespace NetRatel.Shared.Contracts.Netclaw;

public enum InvestigationState { Idle, Processing, AwaitingApproval, DeliveryUnknown, Archived, Queued }

public sealed record NetclawTenantDto(int TenantId, string TenantName, bool CanInvestigate, bool CanConfigure);
public sealed record NetclawConnectionDto(Guid Id, int TenantId, long Revision, bool Enabled, string? DaemonAddress,
    bool HasDeviceToken, DateTimeOffset? LastTestedAtUtc, bool? LastTestSucceeded, string? LastTestCode);
public sealed record NetclawPairRequest(string Address, string PairingCode, long ExpectedRevision = 0);
public sealed record NetclawEnabledRequest(long ExpectedRevision, bool Enabled);
public sealed record NetclawTestResult(bool Success, string Code, NetclawConnectionDto Connection);
public sealed record InvestigationCreateRequest(Guid RequestId, string Title, IReadOnlyList<Guid> AgentIds, Guid? OccurrenceId = null);
public sealed record InvestigationMessageRequest(Guid MessageId, string Text);
public sealed record InvestigationApprovalRequest(string SelectedKey);
public sealed record InvestigationArchiveRequest(Guid ExpectedMessageId, bool AcknowledgePossibleDelivery = false);
public sealed record InvestigationClientDto(Guid AgentId, string HostName, string? FriendlyName, IReadOnlyList<string> Tags,
    DateTimeOffset? LastSeenUtc);
public sealed record InvestigationSummaryDto(Guid Id, int TenantId, string Title, InvestigationState State,
    IReadOnlyList<Guid> AgentIds, Guid? OccurrenceId, Guid? FlowRunId, DateTimeOffset LastActivityUtc, string CreatedBy);
public sealed record InvestigationEventDto(long Sequence, string Type, string Text, string? Actor, string? CallId,
    IReadOnlyList<InvestigationOptionDto>? Options, DateTimeOffset CreatedAtUtc, Guid? MessageId = null);
public sealed record InvestigationOptionDto(string Key, string Label);
public sealed record InvestigationSnapshotDto(InvestigationSummaryDto Investigation, long LastSequence, Guid? ActiveMessageId,
    IReadOnlyList<InvestigationEventDto> Events, IReadOnlyList<InvestigationClientDto> Clients,
    IReadOnlyList<InvestigationApprovalDto> PendingApprovals, string? Draft = null);
public sealed record InvestigationApprovalDto(string CallId, IReadOnlyList<InvestigationOptionDto> Options);
