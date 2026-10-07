// Candidate physical orchestration: registered through PhysicalDiskIncidentTests.
// Runtime proof is emitted only after real product commands and owned cleanup succeed.
using System.Net;
using Xunit;

namespace NetRatel.API.IntegrationTests.ServiceLinks;

internal static class PhysicalDiskIncidentAcceptance
{
    internal static void ReportProgress(Action<string>? progress, string stage)
    {
        // Diagnostics must not replace the original physical operation or cleanup failure.
        try { progress?.Invoke(stage); } catch { }
    }

    public static async Task RunAsync(IPhysicalIncidentFixture runtime, CancellationToken outer, Action<string>? progress = null)
    {
        void Mark(string stage) => ReportProgress(progress, stage);
        // Product boot/source adoption/image verification happens before this
        // call. The genuine Client enrolls before the ordinary pairing ceremony
        // selects its newly created job target. The native
        // lifetime is bounded to the existing 300-second physical-process limit.
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(outer);
        budget.CancelAfter(TimeSpan.FromSeconds(300));
        var ct = budget.Token;
        await using var ownership = runtime; // Stops containers before unmounting.
        Mark("source-identity:start");
        await runtime.AssertExactCurrentSourcesAndPublishedCompanionAsync(ct);
        Mark("source-identity:complete");
        Mark("empty-authority:start");
        await runtime.AssertNoFixtureSeededExecutionAuthorityAsync(ct);
        Mark("empty-authority:complete");
        Mark("full-client:start");
        await runtime.StartFullProductionClientAsync(ct);
        Mark("full-client:complete");
        Mark("admission:start");
        var admitted = await runtime.WaitRealFirstHeartbeatAndTelemetryAdmissionAsync(ct);
        Mark("admission:complete");
        Assert.NotEqual(Guid.Empty, admitted.AgentId);
        Assert.NotEqual(Guid.Empty, admitted.ConnectionId);
        Assert.NotEqual(Guid.Empty, admitted.EvidenceStreamId);
        Assert.True(admitted.ConnectionEpoch > 0 && admitted.CommittedOwner && admitted.ProductionSystemClock);
        Assert.Equal("full-production-NetRatel.Client", admitted.Producer);
        // Reuse the actual recorded-form-task HTTP ceremony with this genuinely
        // enrolled AgentId; preserve native ACK/result/provider-callback proof.
        Mark("recorded-task:start");
        await runtime.RunProtectedRecordedFormTaskAsync(admitted.AgentId, ct);
        Mark("recorded-task:complete");
        Mark("second-replica:start");
        await runtime.StartSecondRealApiWorkerReplicaAsync(ct);
        Mark("second-replica:complete");

        Mark("baseline:start");
        var baseline = await UniqueCollectionsAsync(runtime, admitted.AgentId, after: default,
            condition: _ => true, requiredSpan: TimeSpan.FromSeconds(10), ct);
        Mark("baseline:complete");
        Assert.All(baseline, sample => Assert.Equal("/netratel-physical-disk", sample.Scope));
        Assert.All(baseline, sample => Assert.True(sample.FreeBytes >= 2L * 1024 * 1024 * 1024));
        Assert.True(baseline.Max(x => x.FreeBytes) - baseline.Min(x => x.FreeBytes) < 16L * 1024 * 1024);
        const long physicalBytes = 256L * 1024 * 1024;
        var minimum = baseline.Min(x => x.FreeBytes);
        Assert.True(physicalBytes <= minimum / 10 && minimum - physicalBytes >= 2L * 1024 * 1024 * 1024);
        var thresholds = new PhysicalThresholds(minimum - physicalBytes / 2, minimum - physicalBytes / 4,
            HoldSeconds: 10, RecoveryHoldSeconds: 10, FreshnessSeconds: 65);
        Mark("flow-configuration:start");
        var configured = await runtime.ConfigureOwnerPublishedFlowAndSelectedDiskRuleAsync(admitted.AgentId,
            baseline[0].Scope, thresholds, ct);
        Mark("flow-configuration:complete");
        Mark("dry-run:start");
        await runtime.ValidateAndDryRunWithoutEffectsAsync(configured, ct);
        Mark("dry-run:complete");
        Mark("initial-durable-read:start");
        var before = await runtime.ReadScopedDurableProofAsync(configured, ct);
        Mark("initial-durable-read:complete");
        RequireCount(before, 0);

        Mark("response-loss:start");
        await using var loss = await runtime.ArmOneActualReceiver201AfterCommitLossAsync(configured, ct);
        Mark("response-loss:complete");
        Mark("first-allocation:start");
        var allocated = await runtime.AllocateOwnedVolumeAsync(physicalBytes, ct);
        Mark("first-allocation:complete");
        Mark("first-breach:start");
        var firstBreach = await UniqueCollectionsAsync(runtime, admitted.AgentId, allocated.CompletedAtUtc,
            sample => sample.FreeBytes < thresholds.BreachBytes, TimeSpan.FromSeconds(10), ct);
        Mark("first-breach:complete");
        Mark("committed-receipt:start");
        var committed = await loss.WaitIndependentCommittedReceiptAsync(ct);
        Mark("committed-receipt:complete");
        Assert.Equal(201, committed.ActualUpstreamStatus);
        Assert.True(committed.TransactionCommitted && committed.ResponseAbortedAfterIndependentRead);
        Assert.Equal(1, committed.IncidentRows);
        Assert.Equal(1, committed.ReceiptRows);
        Assert.Equal(1, committed.ConfirmationEnqueues);
        Mark("ambiguous-action:start");
        var ambiguous = await runtime.WaitDurableMayHaveCommittedActionAsync(configured, ct);
        Mark("ambiguous-action:complete");
        RequireCount(ambiguous, 1);
        Assert.True(ambiguous.MayHaveCommitted && !ambiguous.ActionSucceeded);
        Assert.Equal(committed.NamespaceId, ambiguous.Actions[0].SourceNamespaceId);
        Assert.Equal(committed.Key, ambiguous.Actions[0].ReceiverKey);
        Assert.Equal(committed.Fingerprint, ambiguous.Actions[0].ReceiverFingerprint);
        // Loss holds only the identical action's real retry/lookup requests with
        // the original RequestAborted token. It never fabricates a receiver reply.
        Mark("worker-restart:start");
        await runtime.RestartRealApiWorkerReplicasWithPersistedStateAsync(ct);
        Mark("worker-restart:complete");
        Mark("two-replicas:start");
        await runtime.AssertTwoActualWorkerReplicasAsync(ct);
        Mark("two-replicas:complete");
        Mark("rotation:start");
        var rotation = await runtime.RotateThroughActualOwnerServiceLinkAsync(ct);
        Mark("rotation:complete");
        Assert.True(rotation.AfterCredentialRevision > rotation.BeforeCredentialRevision);
        Assert.Equal(rotation.BeforeSemanticRevision, rotation.AfterSemanticRevision);
        Assert.Equal(rotation.BeforeSourceNamespaceId, rotation.AfterSourceNamespaceId);
        Assert.Equal(ambiguous.Actions[0].SourceNamespaceId, rotation.AfterSourceNamespaceId);
        Mark("same-key-recovery:start");
        await loss.ObserveRealSameKeyRecoveryAfterRestartAsync(ct);
        Mark("same-key-recovery:complete");
        loss.ReleaseIdenticalRetryGate();
        Mark("verified-action:start");
        var completed = await runtime.WaitRealVerifiedReceiptAndActionSuccessAsync(configured, ct);
        Mark("verified-action:complete");
        RequireCount(completed, 1);
        Assert.True(completed.ActionSucceeded);
        Assert.Equal(ambiguous.Actions, completed.Actions);
        Assert.Equal(ambiguous.Occurrences, completed.Occurrences);
        Assert.Equal(ambiguous.Incidents, completed.Incidents);
        Assert.Equal(ambiguous.Receipts, completed.Receipts);
        Assert.Equal(ambiguous.ConfirmationEffects, completed.ConfirmationEffects);
        Mark("continued-breach:start");
        var stillBad = await UniqueCollectionsAsync(runtime, admitted.AgentId, allocated.CompletedAtUtc,
            sample => sample.FreeBytes < thresholds.BreachBytes, TimeSpan.FromSeconds(10), ct);
        Mark("continued-breach:complete");
        Mark("sustained-durable-read:start");
        var sustained = await runtime.ReadScopedDurableProofAsync(configured, ct);
        Mark("sustained-durable-read:complete");
        RequireCount(sustained, 1);
        Assert.Equal(completed.Actions, sustained.Actions);

        Mark("allocation-recovery:start");
        var recovered = await runtime.DeleteOnlyOwnedAllocationAsync(ct);
        Mark("allocation-recovery:complete");
        Mark("recovery-collections:start");
        var recoverySamples = await UniqueCollectionsAsync(runtime, admitted.AgentId, recovered.CompletedAtUtc,
            sample => sample.FreeBytes > thresholds.RecoveryBytes, TimeSpan.FromSeconds(10), ct);
        Mark("recovery-collections:complete");
        Mark("occurrence-resolution:start");
        await runtime.WaitActualOccurrenceResolvedOnceAsync(configured, completed.Occurrences[0], ct);
        Mark("occurrence-resolution:complete");
        Mark("recovery-durable-read:start");
        var recoveredProof = await runtime.ReadScopedDurableProofAsync(configured, ct);
        Mark("recovery-durable-read:complete");
        RequireCount(recoveredProof, 1);
        Assert.Equal(completed.Actions, recoveredProof.Actions);
        Mark("second-allocation:start");
        var secondAllocation = await runtime.AllocateOwnedVolumeAsync(physicalBytes, ct);
        Mark("second-allocation:complete");
        Mark("second-breach:start");
        var secondBreach = await UniqueCollectionsAsync(runtime, admitted.AgentId, secondAllocation.CompletedAtUtc,
            sample => sample.FreeBytes < thresholds.BreachBytes, TimeSpan.FromSeconds(10), ct);
        Mark("second-breach:complete");
        Mark("second-incident:start");
        var second = await runtime.WaitSecondActualOccurrenceAndVerifiedIncidentAsync(configured, ct);
        Mark("second-incident:complete");
        RequireCount(second, 2);
        Assert.Contains(completed.Actions[0], second.Actions);
        Assert.Contains(completed.Occurrences[0], second.Occurrences);
        Assert.All(second.Actions, action => Assert.Equal(rotation.AfterSourceNamespaceId, action.SourceNamespaceId));
        Assert.Equal(2, second.Actions.Select(x => x.ReceiverKey).Distinct().Count());
        Assert.Equal(2, second.Actions.Select(x => x.OccurrenceId).Distinct().Count());

        // Cache the actually CURRENT successor after rotation, and prove it is
        // authorized before unlink. A retired predecessor already returning401
        // would not establish that unlink caused the protected denial.
        Mark("cached-authority:start");
        await using var cachedAuthorization = await runtime.CaptureActuallyIssuedBusinessAuthorizationPrivatelyAsync(ct);
        Mark("cached-authority:complete");
        Assert.True(cachedAuthorization.ExpiresAtUtc > DateTimeOffset.UtcNow.AddMinutes(2));
        Mark("cached-authority-control:start");
        Assert.Equal(HttpStatusCode.OK, await runtime.ProtectedReceiverLookupWithCachedAuthorizationAsync(
            cachedAuthorization, completed.Actions[0], ct));
        Mark("cached-authority-control:complete");
        Mark("peer-stop:start");
        await runtime.StopOnlyFixtureRatelDeskPeerAsync(ct);
        Mark("peer-stop:complete");
        Mark("owner-unlink:start");
        await runtime.UnlinkThroughActualOwnerAsync(reason: "isolated physical acceptance complete", ct);
        Mark("owner-unlink:complete");
        Mark("local-unlink:start");
        await runtime.AssertImmediateLocalSenderAndInboundAuthorityStoppedAsync(ct);
        Mark("local-unlink:complete");
        Mark("settled-unlink:start");
        await runtime.RestoreFixtureRatelDeskAndSettleUnlinkAsync(ct);
        Mark("settled-unlink:complete");
        Assert.True(cachedAuthorization.ExpiresAtUtc > DateTimeOffset.UtcNow.AddSeconds(20));
        Mark("cached-authority-denial:start");
        var deniedStatus = await runtime.ProtectedReceiverLookupWithCachedAuthorizationAsync(cachedAuthorization,
            completed.Actions[0], ct);
        Mark("cached-authority-denial:complete");
        var denialObservedAtUtc = DateTimeOffset.UtcNow;
        Assert.Contains(deniedStatus, new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden });
        Assert.True(cachedAuthorization.ExpiresAtUtc > denialObservedAtUtc);
        Mark("old-authority-stopped:start");
        await runtime.AssertNoOldAuthorizationCanIssueOrSendAsync(cachedAuthorization, ct);
        Mark("old-authority-stopped:complete");
        var latestDenialObservedAtUtc = cachedAuthorization.LatestDenialObservedAtUtc
            ?? throw new InvalidOperationException("The actual final cached-token denial observation is missing.");
        Assert.True(latestDenialObservedAtUtc >= denialObservedAtUtc);
        Assert.True(cachedAuthorization.ExpiresAtUtc > latestDenialObservedAtUtc);
        var unlinkProof = new PhysicalUnlinkProof(200, (int)deniedStatus,
            cachedAuthorization.ExpiresAtUtc, latestDenialObservedAtUtc,
            cachedAuthorization.PositivelyControlledInboundReplicas, cachedAuthorization.ImmediateDeniedInboundReplicas,
            cachedAuthorization.SettledDeniedInboundReplicas, SamePrivateAuthorizationHandleUsed: true);
        Mark("terminal-durable-read:start");
        var terminal = await runtime.ReadScopedDurableProofAsync(configured, ct);
        Mark("terminal-durable-read:complete");
        RequireCount(terminal, 2);
        Assert.Equal(second.Actions, terminal.Actions);
        // Cleanup is part of acceptance. No PASS document may claim a future
        // teardown succeeded. These owned ports must dispose idempotently;
        // await-using still covers failures before this successful path.
        Mark("cleanup-cached-authority:start");
        await cachedAuthorization.DisposeAsync();
        Mark("cleanup-cached-authority:complete");
        Mark("cleanup-loss:start");
        await loss.DisposeAsync();
        Mark("cleanup-loss:complete");
        Mark("cleanup-runtime:start");
        await runtime.DisposeAsync();
        Mark("cleanup-runtime:complete");
        // Export only this typed schema. Native inputs, tokens, raw bodies,
        // arbitrary logs and browser traces never become proof artifacts.
        Mark("proof-export:start");
        await runtime.WritePrivacySafePhysicalProofAsync(admitted, configured, baseline, firstBreach, stillBad,
            recoverySamples, secondBreach, allocated, recovered, rotation, committed, terminal, unlinkProof, ct);
        Mark("proof-export:complete");
    }

    private static async Task<PhysicalDiskSample[]> UniqueCollectionsAsync(IPhysicalIncidentFixture runtime,
        Guid agent, DateTimeOffset after, Func<PhysicalDiskSample, bool> condition, TimeSpan requiredSpan,
        CancellationToken ct)
    {
        var samples = new List<PhysicalDiskSample>();
        var seen = new Dictionary<Guid, PhysicalDiskSample>();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            await runtime.AssertFullProductionClientWithinFixedLifetimeAsync(ct);
            var value = await runtime.ReadActualAcceptedSelectedDiskAsync(agent, ct);
            Assert.True(value.TransportAccepted && value.DurableCommittedOwner && value.CollectionComplete);
            Assert.NotEqual(Guid.Empty, value.CollectionId);
            Assert.True(value.CollectedAtUtc <= value.FrameObservedAtUtc && value.FrameObservedAtUtc <= value.ReceivedAtUtc);
            Assert.True(value.ReceivedAtUtc - value.CollectedAtUtc <= TimeSpan.FromSeconds(15));
            Assert.True(DateTimeOffset.UtcNow - value.CollectedAtUtc <= TimeSpan.FromSeconds(15));
            Assert.True(value.TotalBytes > 0 && value.FreeBytes >= 0 && value.FreeBytes <= value.TotalBytes);
            if (seen.TryGetValue(value.CollectionId, out var original))
            {
                // Cached copies may advance transport cursors, never counters,
                // collection identity/time or the physical hold window.
                Assert.Equal(original.CollectedAtUtc, value.CollectedAtUtc);
                Assert.Equal(original.FreeBytes, value.FreeBytes);
                Assert.Equal(original.TotalBytes, value.TotalBytes);
            }
            else
            {
                seen.Add(value.CollectionId, value);
                if (value.CollectedAtUtc > after && condition(value))
                {
                    if (samples.Count > 0)
                    {
                        Assert.True(value.CollectedAtUtc > samples[^1].CollectedAtUtc);
                        Assert.Equal(samples[0].Scope, value.Scope);
                    }
                    samples.Add(value);
                    if (samples.Count >= 3 && value.CollectedAtUtc - samples[0].CollectedAtUtc >= requiredSpan)
                        return samples.ToArray();
                }
                else samples.Clear(); // A real opposite sample interrupts the hold.
            }
            Assert.True(seen.Count <= 128, "The bounded physical sample receipt limit was exceeded.");
            await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
        }
    }

    private static void RequireCount(PhysicalDurableProof value, int count)
    {
        Assert.Equal(count, value.Occurrences.Length);
        Assert.Equal(count, value.RaisedEvents.Length);
        Assert.Equal(count, value.DispatchIntentKeys.Length);
        Assert.Equal(count, value.FlowRunIds.Length);
        Assert.Equal(count, value.Actions.Length);
        Assert.Equal(count, value.Receipts.Length);
        Assert.Equal(count, value.Incidents.Length);
        Assert.Equal(count, value.ConfirmationEffects.Length);
        Assert.Equal(count, value.Actions.Select(x => x.ReceiverKey).Distinct().Count());
    }
}

// These are allowlisted proof DTOs, not alternative production authority stores.
internal sealed record PhysicalAdmission(Guid AgentId, Guid ConnectionId, ulong ConnectionEpoch,
    Guid EvidenceStreamId, bool CommittedOwner, bool ProductionSystemClock, string Producer);
internal sealed record PhysicalThresholds(long BreachBytes, long RecoveryBytes, int HoldSeconds,
    int RecoveryHoldSeconds, int FreshnessSeconds);
internal sealed record PhysicalRule(Guid RuleId, Guid FlowVersionId, string ConfigurationHash,
    string ResourceKey, long ConfigurationRevision, Guid ConnectorId, long SemanticRevision);
internal sealed record PhysicalDiskSample(string Scope, Guid CollectionId, DateTimeOffset CollectedAtUtc,
    DateTimeOffset FrameObservedAtUtc, DateTimeOffset ReceivedAtUtc, ulong TransportSequence,
    long TotalBytes, long FreeBytes, bool CollectionComplete, bool TransportAccepted, bool DurableCommittedOwner);
internal sealed record PhysicalAllocation(DateTimeOffset CompletedAtUtc, long Bytes, long ActualBlocksBytes,
    long SelectedFreeBefore, long SelectedFreeAfter, long HostFreeAfter);
internal sealed record PhysicalAction(Guid OccurrenceId, Guid FlowRunId, Guid FlowVersionId, string ActionKey,
    string ReceiverKey, string PayloadSha256, string ReceiverFingerprint, Guid SourceNamespaceId,
    Guid SourceInstanceId, long SemanticRevision);
internal sealed record PhysicalDurableProof(Guid[] Occurrences, Guid[] RaisedEvents, string[] DispatchIntentKeys,
    Guid[] FlowRunIds, PhysicalAction[] Actions, Guid[] Receipts, string[] Incidents,
    Guid[] ConfirmationEffects, bool MayHaveCommitted, bool ActionSucceeded);
internal sealed record PhysicalCommittedLoss(int ActualUpstreamStatus, bool TransactionCommitted,
    bool ResponseAbortedAfterIndependentRead, Guid NamespaceId, string Key, string Fingerprint,
    int IncidentRows, int ReceiptRows, int ConfirmationEnqueues);
internal sealed record PhysicalRotation(long BeforeCredentialRevision, long AfterCredentialRevision,
    long BeforeSemanticRevision, long AfterSemanticRevision, Guid BeforeSourceNamespaceId, Guid AfterSourceNamespaceId);
internal sealed record PhysicalUnlinkProof(int PositiveControlStatusBeforeUnlink, int ProtectedDenialStatus,
    DateTimeOffset CachedAuthorizationCommonExpiresAtUtc, DateTimeOffset LatestDenialObservedAtUtc,
    int PositivelyControlledInboundReplicas, int ImmediateDeniedInboundReplicas, int SettledDeniedInboundReplicas,
    bool SamePrivateAuthorizationHandleUsed);
internal interface IPrivateCachedPhysicalAuthorization : IAsyncDisposable
{
    DateTimeOffset ExpiresAtUtc { get; } // Minimum of both parsed expiries; never a token accessor.
    DateTimeOffset? LatestDenialObservedAtUtc { get; }
    int PositivelyControlledInboundReplicas { get; }
    int ImmediateDeniedInboundReplicas { get; }
    int SettledDeniedInboundReplicas { get; }
}
internal interface IPhysicalCommittedResponseLoss : IAsyncDisposable
{
    Task<PhysicalCommittedLoss> WaitIndependentCommittedReceiptAsync(CancellationToken ct);
    Task ObserveRealSameKeyRecoveryAfterRestartAsync(CancellationToken ct);
    void ReleaseIdenticalRetryGate();
}

// PhysicalIncidentFixture implements these ports with actual production processes. In particular
// no constructor/port may populate Agents, owner/evidence, FlowRunNode, incidents
// or receipts in SQL. Only genuine product commands create business authority.
internal interface IPhysicalIncidentFixture : IAsyncDisposable
{
    Task AssertExactCurrentSourcesAndPublishedCompanionAsync(CancellationToken ct);
    Task AssertNoFixtureSeededExecutionAuthorityAsync(CancellationToken ct);
    Task StartFullProductionClientAsync(CancellationToken ct);
    Task<PhysicalAdmission> WaitRealFirstHeartbeatAndTelemetryAdmissionAsync(CancellationToken ct);
    Task RunProtectedRecordedFormTaskAsync(Guid agent, CancellationToken ct);
    Task StartSecondRealApiWorkerReplicaAsync(CancellationToken ct);
    Task AssertFullProductionClientWithinFixedLifetimeAsync(CancellationToken ct);
    Task<PhysicalDiskSample> ReadActualAcceptedSelectedDiskAsync(Guid agent, CancellationToken ct);
    Task<PhysicalRule> ConfigureOwnerPublishedFlowAndSelectedDiskRuleAsync(Guid agent, string scope, PhysicalThresholds thresholds, CancellationToken ct);
    Task ValidateAndDryRunWithoutEffectsAsync(PhysicalRule rule, CancellationToken ct);
    Task<PhysicalDurableProof> ReadScopedDurableProofAsync(PhysicalRule rule, CancellationToken ct);
    Task<IPrivateCachedPhysicalAuthorization> CaptureActuallyIssuedBusinessAuthorizationPrivatelyAsync(CancellationToken ct);
    Task<IPhysicalCommittedResponseLoss> ArmOneActualReceiver201AfterCommitLossAsync(PhysicalRule rule, CancellationToken ct);
    Task<PhysicalAllocation> AllocateOwnedVolumeAsync(long count, CancellationToken ct);
    Task<PhysicalAllocation> DeleteOnlyOwnedAllocationAsync(CancellationToken ct);
    Task<PhysicalDurableProof> WaitDurableMayHaveCommittedActionAsync(PhysicalRule rule, CancellationToken ct);
    Task RestartRealApiWorkerReplicasWithPersistedStateAsync(CancellationToken ct);
    Task AssertTwoActualWorkerReplicasAsync(CancellationToken ct);
    Task<PhysicalRotation> RotateThroughActualOwnerServiceLinkAsync(CancellationToken ct);
    Task<PhysicalDurableProof> WaitRealVerifiedReceiptAndActionSuccessAsync(PhysicalRule rule, CancellationToken ct);
    Task WaitActualOccurrenceResolvedOnceAsync(PhysicalRule rule, Guid occurrence, CancellationToken ct);
    Task<PhysicalDurableProof> WaitSecondActualOccurrenceAndVerifiedIncidentAsync(PhysicalRule rule, CancellationToken ct);
    Task StopOnlyFixtureRatelDeskPeerAsync(CancellationToken ct);
    Task UnlinkThroughActualOwnerAsync(string reason, CancellationToken ct);
    Task AssertImmediateLocalSenderAndInboundAuthorityStoppedAsync(CancellationToken ct);
    Task RestoreFixtureRatelDeskAndSettleUnlinkAsync(CancellationToken ct);
    Task<HttpStatusCode> ProtectedReceiverLookupWithCachedAuthorizationAsync(IPrivateCachedPhysicalAuthorization cached, PhysicalAction action, CancellationToken ct);
    Task AssertNoOldAuthorizationCanIssueOrSendAsync(IPrivateCachedPhysicalAuthorization cached, CancellationToken ct);
    // Safe AFTER DisposeAsync: use retained allowlisted observations and the
    // actual successful cleanup receipt only; never reopen a disposed provider.
    Task WritePrivacySafePhysicalProofAsync(PhysicalAdmission admitted, PhysicalRule rule, PhysicalDiskSample[] baseline,
        PhysicalDiskSample[] firstBreach, PhysicalDiskSample[] continuedBad, PhysicalDiskSample[] recovery,
        PhysicalDiskSample[] secondBreach, PhysicalAllocation allocated, PhysicalAllocation recovered, PhysicalRotation rotation,
        PhysicalCommittedLoss loss, PhysicalDurableProof terminal, PhysicalUnlinkProof unlink, CancellationToken ct);
}
