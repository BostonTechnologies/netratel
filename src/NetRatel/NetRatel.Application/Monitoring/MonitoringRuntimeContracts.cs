using System.Collections.Immutable;
using NetRatel.Application.Presence;
using NetRatel.Application.Services;
using NetRatel.Application.Telemetry;
using NetRatel.Shared.Contracts.Monitoring;

namespace NetRatel.Application.Monitoring;

/// <summary>Server-owned current telemetry registration, independently fenced from agent wire fields.</summary>
public sealed record MonitoringEvidenceFence(ClientKey Client, Guid ConnectionId, long ConnectionEpoch, Guid EvidenceStreamId, long RegistrationOrdinal = 0);

public sealed record MonitoringTelemetryInput(MonitoringEvidenceFence Fence, TelemetrySnapshot Snapshot);
public sealed record MonitoringServicesInput(MonitoringEvidenceFence Fence, ClientServicesState Services);

public enum MonitoringInputDisposition { Accepted, Duplicate, StaleEvidence, CapacityExceeded, PersistenceUnavailable }
public sealed record MonitoringInputResult(MonitoringInputDisposition Disposition, int EvaluatedSeries, ulong LastAcceptedSequence);

public sealed record MonitoringConfigurationSnapshot(int TenantId, ulong Revision,
    ImmutableArray<MonitoringRuleDto> Rules, ImmutableArray<MonitoringGroupDto> Groups,
    ImmutableArray<MonitoringBypassDto> Bypasses, DateTimeOffset GeneratedAtUtc)
{
    public static MonitoringConfigurationSnapshot Empty(int tenantId, DateTimeOffset now) => new(tenantId, 0, [], [], [], now);
}

public sealed record MonitoringRuleSaveRequest(MonitoringRuleDto Rule, ulong ExpectedConfigurationRevision,
    Guid OperatorId, string Reason, MonitoringConditionResetPolicy? ResetPolicy = null);
public sealed record MonitoringGroupSaveRequest(MonitoringGroupDto Group, ulong ExpectedConfigurationRevision, Guid OperatorId, string Reason);
public sealed record MonitoringBypassSaveRequest(MonitoringBypassDto Bypass, ulong ExpectedConfigurationRevision);
public sealed record MonitoringConfigurationDeleteRequest(int TenantId, Guid EntityId, ulong ExpectedConfigurationRevision, Guid OperatorId, string Reason);

public enum MonitoringConfigurationWriteDisposition { Stored, Conflict, NotFound }
public sealed record MonitoringConfigurationWriteResult(MonitoringConfigurationWriteDisposition Disposition, MonitoringConfigurationSnapshot Configuration);

/// <summary>Every mutation fences the tenant configuration revision and atomically persists an operator audit.</summary>
public interface IMonitoringConfigurationStore
{
    Task<MonitoringConfigurationSnapshot> GetAsync(int tenantId, CancellationToken cancellationToken);
    Task<MonitoringConfigurationWriteResult> SaveRuleAsync(MonitoringRuleSaveRequest request, CancellationToken cancellationToken);
    Task<MonitoringConfigurationWriteResult> SaveGroupAsync(MonitoringGroupSaveRequest request, CancellationToken cancellationToken);
    Task<MonitoringConfigurationWriteResult> SaveBypassAsync(MonitoringBypassSaveRequest request, CancellationToken cancellationToken);
    Task<MonitoringConfigurationWriteResult> DeleteRuleAsync(MonitoringConfigurationDeleteRequest request, CancellationToken cancellationToken);
    Task<MonitoringConfigurationWriteResult> DeleteGroupAsync(MonitoringConfigurationDeleteRequest request, CancellationToken cancellationToken);
    Task<MonitoringConfigurationWriteResult> DeleteBypassAsync(MonitoringConfigurationDeleteRequest request, CancellationToken cancellationToken);
}

public sealed record MonitoringCommitRequest(MonitoringEvaluationResult Evaluation, ulong ExpectedConfigurationRevision,
    MonitoringEvidenceFence? ExpectedEvidenceFence = null);
public enum MonitoringStoreWriteDisposition { Stored, Conflict, StaleEvidence }
public sealed record MonitoringStoreWriteResult(MonitoringStoreWriteDisposition Disposition, MonitoringSeriesState? State);


public enum MonitoringOutboxStatus { Pending, Leased, Completed, Skipped, Failed, DeliveryUnknown, Cancelled }
public sealed record MonitoringOutboxLease(Guid OutboxId, MonitoringOutboxIntent Intent, Guid WorkerId, Guid LeaseId,
    long LeaseFence, DateTimeOffset LeaseExpiresAtUtc, int Attempt, Guid? FlowRunId = null);
public sealed record MonitoringUnsettledFlowReceipt(Guid OutboxId, MonitoringOutboxIntent Intent, Guid? FlowRunId);
public sealed record MonitoringOutboxClaimRequest(int TenantId, Guid WorkerId, int MaximumCount, TimeSpan LeaseDuration);
public sealed record MonitoringOutboxCompletion(MonitoringOutboxLease Lease, MonitoringOutboxStatus Status,
    MonitoringFlowOutcomeDto? Outcome = null, string? Code = null);

/// <summary>Scoped persistence commits state+occurrence+event+outbox+audit atomically before any fanout.</summary>
public interface IMonitoringStore
{
    Task<MonitoringSeriesState?> LoadSeriesAsync(MonitoringSeriesKey series, CancellationToken cancellationToken);
    Task<ImmutableArray<MonitoringSeriesState>> LoadClientAsync(ClientKey client, CancellationToken cancellationToken);
    Task<MonitoringSeriesPageDto> ReadTenantSeriesAsync(int tenantId, int maximumCount, string? cursor, CancellationToken cancellationToken);
    Task<MonitoringEventPageDto> ReadTenantEventsAsync(int tenantId, int maximumCount, string? cursor, CancellationToken cancellationToken);
    Task<MonitoringSummaryDto> ReadTenantSummaryAsync(int tenantId, CancellationToken cancellationToken);
    Task<MonitoringStoreWriteResult> CommitAsync(MonitoringCommitRequest request, CancellationToken cancellationToken);
    Task<MonitoringEvidenceFence?> ReserveEvidenceRegistrationAsync(ClientKey client, Guid connectionId, long connectionEpoch,
        Guid registrationId, CancellationToken cancellationToken) => Task.FromResult<MonitoringEvidenceFence?>(null);
    Task<bool> BeginEvidenceStreamAsync(MonitoringEvidenceFence fence, CancellationToken cancellationToken);
    Task<bool> EndEvidenceStreamAsync(MonitoringEvidenceFence fence, CancellationToken cancellationToken);
    Task<ImmutableArray<MonitoringOutboxLease>> ClaimOutboxAsync(MonitoringOutboxClaimRequest request, CancellationToken cancellationToken);
    Task<bool> CompleteOutboxAsync(MonitoringOutboxCompletion completion, CancellationToken cancellationToken);
    Task<bool> MarkOutboxHandedOffAsync(MonitoringOutboxLease lease, Guid realRunId, CancellationToken cancellationToken);
    Task<ImmutableArray<MonitoringUnsettledFlowReceipt>> ListUnsettledFlowRunsAsync(int tenantId, int maximumCount, CancellationToken cancellationToken);
    Task<bool> ReconcileOutboxOutcomeAsync(MonitoringUnsettledFlowReceipt receipt, MonitoringFlowOutcomeDto outcome, CancellationToken cancellationToken);
}

public sealed record MonitoringOperatorCommand(MonitoringSeriesKey Series, Guid OccurrenceId, Guid OperatorId,
    string Reason, ulong? ExpectedStateRevision = null);

/// <summary>Sequential per-client runtime; accepted inputs are awaited before the gateway acknowledges them.</summary>
public interface IMonitoringRuntime
{
    Task<MonitoringEvidenceFence?> ReserveEvidenceRegistrationAsync(ClientKey client, Guid connectionId, long connectionEpoch,
        Guid registrationId, CancellationToken cancellationToken) => Task.FromResult<MonitoringEvidenceFence?>(null);
    Task<MonitoringInputResult> BeginEvidenceStreamAsync(MonitoringEvidenceFence fence, CancellationToken cancellationToken);
    Task<MonitoringInputResult> EndEvidenceStreamAsync(MonitoringEvidenceFence fence, CancellationToken cancellationToken);
    Task<MonitoringInputResult> RecordTelemetryAsync(MonitoringTelemetryInput input, CancellationToken cancellationToken);
    Task<MonitoringInputResult> RecordServicesAsync(MonitoringServicesInput input, CancellationToken cancellationToken);
    Task<ImmutableArray<MonitoringSeriesState>> GetClientAsync(ClientKey client, CancellationToken cancellationToken);
    Task<MonitoringSeriesPageDto> ReadTenantAsync(int tenantId, int maximumCount, string? cursor, CancellationToken cancellationToken);
    Task<MonitoringEventPageDto> ReadTenantEventsAsync(int tenantId, int maximumCount, string? cursor, CancellationToken cancellationToken);
    Task<MonitoringSummaryDto> ReadTenantSummaryAsync(int tenantId, CancellationToken cancellationToken);
    Task<MonitoringStoreWriteResult> AcknowledgeAsync(MonitoringOperatorCommand command, CancellationToken cancellationToken);
    Task<MonitoringStoreWriteResult> ClearAsync(MonitoringOperatorCommand command, CancellationToken cancellationToken);
    Task RefreshAsync(ClientKey client, CancellationToken cancellationToken);
}

/// <summary>Maps tenant/client directory and current accepted registration without granting cross-tenant authority.</summary>
public interface IMonitoringAgentEligibility
{
    Task<ImmutableArray<Guid>> GetEligibleAgentsAsync(int tenantId, CancellationToken cancellationToken);
    async Task<bool> IsEligibleAsync(ClientKey client, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!client.IsValid) return false;
        return (await GetEligibleAgentsAsync(client.TenantId, cancellationToken).ConfigureAwait(false)).Contains(client.AgentId);
    }
}

public interface IMonitoringClientDirectory : IMonitoringAgentEligibility
{
    Task<MonitoringEvidenceFence?> GetCurrentEvidenceAsync(ClientKey client, CancellationToken cancellationToken);
    Task<bool> IsPresentedEvidenceAsync(MonitoringEvidenceFence fence, CancellationToken cancellationToken) =>
        Task.FromResult(false);
}

/// <summary>Only real immutable published flows may be selected. The phase-one fallback always returns false.</summary>
public interface IMonitoringPublishedFlowProvider
{
    Task<bool> IsPublishedAsync(int tenantId, Guid flowVersionId, CancellationToken cancellationToken);
    Task<ImmutableArray<MonitoringPublishedFlowDto>> ListPublishedAsync(int tenantId, int maximumCount, CancellationToken cancellationToken);
}

public sealed class UnavailableMonitoringPublishedFlowProvider : IMonitoringPublishedFlowProvider
{
    public Task<ImmutableArray<MonitoringPublishedFlowDto>> ListPublishedAsync(int tenantId, int maximumCount, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (tenantId <= 0 || maximumCount is < 1 or > MonitoringLimits.MaximumRowsPerRead)
            throw new ArgumentException("invalid_published_flow_query");
        return Task.FromResult(ImmutableArray<MonitoringPublishedFlowDto>.Empty);
    }

    public Task<bool> IsPublishedAsync(int tenantId, Guid flowVersionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(false);
    }
}
