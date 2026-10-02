using System.Collections.Immutable;
using NetRatel.Shared.Contracts.Services;

namespace NetRatel.Shared.Contracts.Monitoring;

/// <summary>HTTP configuration view; operator identities are attributed by the server on writes.</summary>
public sealed record MonitoringConfigurationDto(int TenantId, ulong Revision,
    ImmutableArray<MonitoringRuleDto> Rules, ImmutableArray<MonitoringGroupDto> Groups,
    ImmutableArray<MonitoringBypassDto> Bypasses, DateTimeOffset GeneratedAtUtc, bool WatchPolicyUpdatePending = false);

public sealed record MonitoringPermissionsDto(int TenantId, bool CanRead, bool CanManage,
    bool CanAcknowledge, bool CanClear, bool CanBypass, bool CanTargetAll);

public sealed record MonitoringRuleWriteDto(MonitoringRuleDto Rule, ulong ExpectedConfigurationRevision,
    string Reason, MonitoringConditionResetPolicy? ResetPolicy = null);

public sealed record MonitoringGroupWriteDto(MonitoringGroupDto Group, ulong ExpectedConfigurationRevision, string Reason);

/// <summary>The route supplies the bypass ID; the server supplies the operator and start time.</summary>
public sealed record MonitoringBypassWriteDto(ulong ExpectedConfigurationRevision, string Reason,
    Guid? RuleId = null, Guid? AgentId = null, string? ResourceKey = null,
    DateTimeOffset? ExpiresAtUtc = null, Guid? GroupId = null);

public sealed record MonitoringDeleteDto(ulong ExpectedConfigurationRevision, string Reason);

/// <summary>The route and resourceKey query identify the series; no command accepts an operator from the body.</summary>
public sealed record MonitoringOperatorActionDto(Guid OccurrenceId, string Reason, ulong? ExpectedStateRevision = null);

public sealed record MonitoringTargetPreviewRequest(MonitoringTargetSelectionDto Targets, MonitoringConditionDto? Condition = null);
public enum MonitoringTargetSupport { Unknown, Supported, Unsupported }
public sealed record MonitoringTargetPreviewEntryDto(Guid AgentId, string? DisplayName,
    MonitoringTargetSupport Support, string Code, DateTimeOffset? EvidenceAtUtc = null);
public sealed record MonitoringTargetPreviewDto(ImmutableArray<Guid> AgentIds, ulong ConfigurationRevision,
    ImmutableArray<MonitoringTargetPreviewEntryDto> Details = default, int Total = 0, bool DetailsTruncated = false);

public sealed record MonitoringTenantDto(int TenantId, string Name);
public sealed record MonitoringPublishedFlowDto(Guid PublishedFlowVersionId, string Name, int Version);
public sealed record MonitoringClientDto(Guid AgentId, string? DisplayName, ClientServicePlatform? Platform,
    MonitoringTargetSupport ServicesSupport, string Code);
public sealed record MonitoringClientPageDto(ImmutableArray<MonitoringClientDto> Items, string? NextCursor, int Total);

public sealed record MonitoringSeriesPageDto(ImmutableArray<MonitoringSeriesState> Items, string? NextCursor);
public sealed record MonitoringEventPageDto(ImmutableArray<MonitoringEventIntent> Items, string? NextCursor);

/// <summary>Aggregate counts across the authorized tenant, independently of a paged series view.</summary>
public sealed record MonitoringSummaryDto(int TenantId, long SeriesCount, long ActiveOccurrences,
    long PendingSeries, long UnknownSeries, long AcknowledgedOccurrences, long SuppressedOccurrences,
    DateTimeOffset GeneratedAtUtc);
