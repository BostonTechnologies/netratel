using NetRatel.Application.Flows;
using NetRatel.Application.RatelDesk;
using NetRatel.Shared.SystemPairing;
using NetRatel.Shared.Contracts.RatelDesk;

namespace NetRatel.Infrastructure.RatelDesk;

public sealed class RatelDeskFlowReceiverAdapter(IRatelDeskConnectorStore connectors,
    IRatelDeskConnectorBindingStore bindings, IRatelDeskConnectorAuthorization authorization,
    IRatelDeskOutboundBindingResolver credentials, IRatelDeskReceiverTransport receiver,
    ReceiverPreparationBuilder builder, IFlowReceiverEvidenceStore evidenceStore, TimeProvider clock)
    : IFlowReceiverDispatcher
{
    public async Task<FlowReceiverPreparationResult> PrepareReceiverAsync(FlowIncidentActionDraft draft,
        DateTimeOffset originalCreatedAtUtc, CancellationToken ct)
    {
        try
        {
        if (!FlowContractValidation.ValidEnvelope(draft.Event) || draft.TenantId != draft.Event.TenantId ||
            !await authorization.CanExecuteAsync(draft.Event.Authority.PrincipalId,
                draft.Event.Authority.IntegrationCredentialId, draft.TenantId, ct))
            return new(FlowIncidentPreparationStatus.Denied, Code: "connector-current-authority-denied");
        var connector = await CurrentConnector(draft.TenantId, draft.ConnectorId, draft.ConnectorRevision, ct);
        var authentication = await bindings.GetAuthenticationAsync(draft.TenantId, draft.ConnectorId, ct);
        var peer = await credentials.CaptureAsync(connector, authentication, draft.SourceInstanceId, ct);
        var readBearer = await credentials.GetBearerAsync(connector, peer, "rateldesk.incident-receipts.read", ct);
        var capability = await receiver.CapabilitiesAsync(peer, readBearer, ct);
        var targetBearer = await credentials.GetBearerAsync(connector, peer, "rateldesk.incident-targets.read", ct);
        await receiver.ValidateTargetsAsync(peer, capability, targetBearer, ct);
        // Reuse the historical stable description mapping exactly, rather than regenerate
        // old work from current labels/Flow versions. See captured RatelDeskFlowConnector.
        var description = BuildOriginalDescription(draft);
        var priority = draft.Fields.Priority ?? Map(connector.Configuration.Priorities, draft.Event.Data.Severity);
        var body = RatelDeskPayload.Create(connector.Configuration, draft.Fields.Title, description, priority);
        var action = new FlowIncidentActionRequest(draft.TenantId, draft.RunId, draft.ActionNodeId,
            draft.ConnectorId, draft.ConnectorRevision, draft.SourceInstanceId, draft.IdempotencyKey, draft.Event,
            new(body.Title, body.Description, body.Priority),
            new(body.OrganizationId, body.CustomerId, body.AssignedToId, body.CategoryIds), true, "");
        action = action with { SemanticFingerprint = FlowContractValidation.Fingerprint(action) };
        if (!FlowContractValidation.ValidPrepared(action, draft))
            return new(FlowIncidentPreparationStatus.Invalid, Code: "invalid-incident-payload");
        var evidence = builder.Build(draft, action, peer, capability, originalCreatedAtUtc, null);
        return new(FlowIncidentPreparationStatus.Ready, action, evidence, "receiver-idempotency-verified");
        }
        catch (UnauthorizedAccessException)
        { return new(FlowIncidentPreparationStatus.Denied, Code: "connector-current-authority-denied"); }
        catch (RatelDeskReceiverReadException error)
        {
            if (error.HttpStatus is 401 or 403)
                return new(FlowIncidentPreparationStatus.Denied, Code: "receiver-current-authority-denied");
            if (error.HttpStatus is 400 or 409 or 422)
                return new(FlowIncidentPreparationStatus.Invalid, Code: "receiver-current-target-rejected");
            return new(FlowIncidentPreparationStatus.Unavailable, Code: "receiver-preparation-unavailable",
                RetryAfter: error.RetryAfter ?? TimeSpan.FromSeconds(5));
        }
        catch (PairingException error)
        {
            return error.StatusCode is 401 or 403 or 409 or 422
                ? new(FlowIncidentPreparationStatus.Denied, Code: "receiver-current-profile-denied")
                : new(FlowIncidentPreparationStatus.Unavailable, Code: "receiver-current-profile-unavailable", RetryAfter: TimeSpan.FromSeconds(5));
        }
        catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException)
        { return new(FlowIncidentPreparationStatus.Unavailable, Code: "receiver-preparation-interrupted", RetryAfter: TimeSpan.FromSeconds(5)); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.Text.Json.JsonException)
        { return new(FlowIncidentPreparationStatus.Invalid, Code: "receiver-preparation-invalid"); }
    }

    public async Task<RatelDeskReceiverObservation> DispatchReceiverAsync(FlowRunLease lease, Guid nodeId,
        RatelDeskDispatchEvidence evidence, Func<CancellationToken, Task<FlowDispatchDecision>> recheckFlowGuard,
        CancellationToken ct)
    {
        var prepared = evidence.Prepared;
        if (ReceiverPreparationBuilder.EvidenceHash(prepared) != prepared.EvidenceFingerprint)
            return new(RatelDeskReceiverObservationKind.Unavailable, "persisted-receiver-evidence-invalid");
        var current = await CurrentConnector(prepared.Peer.LocalTenantId, prepared.ConnectorId,
            prepared.ConnectorRevision, ct);
        if (!await authorization.CanExecuteAsync(lease.Event.Authority.PrincipalId,
                lease.Event.Authority.IntegrationCredentialId, lease.Event.TenantId, ct))
            return new(RatelDeskReceiverObservationKind.AuthenticationRejected, "connector-current-authority-denied");
        var readBearer = await credentials.GetBearerAsync(current, prepared.Peer, "rateldesk.incident-receipts.read", ct);
        if (evidence.MayHaveCommitted)
        {
            var lookup = await receiver.LookupAsync(prepared, readBearer, ct);
            // A read can outlive a tenant/owner grant or a semantic binding. Re-authorize
            // that captured peer after the await before accepting its committed receipt.
            current = await CurrentConnector(prepared.Peer.LocalTenantId, prepared.ConnectorId, prepared.ConnectorRevision, ct);
            if (!await authorization.CanExecuteAsync(lease.Event.Authority.PrincipalId,
                    lease.Event.Authority.IntegrationCredentialId, lease.Event.TenantId, ct))
                return new(RatelDeskReceiverObservationKind.AuthenticationRejected, "connector-current-authority-denied");
            _ = await credentials.GetBearerAsync(current, prepared.Peer, "rateldesk.incident-receipts.read", ct);
            if (lookup.Kind != RatelDeskReceiverObservationKind.Missing) return lookup;
        }
        if (clock.GetUtcNow() >= prepared.AutomaticReplayUntilUtc ||
            evidence.Attempts >= NetRatel.Shared.Contracts.Flows.FlowLimits.MaximumActionAttempts)
            return new(RatelDeskReceiverObservationKind.Gone, "automatic-replay-horizon-exhausted");
        var capability = await receiver.CapabilitiesAsync(prepared.Peer, readBearer, ct);
        // Original evidence is not rewritten. Fresh observations must support its exact endpoints.
        if (capability.Endpoints != prepared.Capability.Endpoints ||
            capability.SourceNamespaceId != prepared.Peer.SourceNamespaceId ||
            capability.SourceInstanceId != prepared.Peer.SourceInstanceId ||
            capability.ReceiverInstanceId != prepared.Peer.ReceiverInstanceId)
            return new(RatelDeskReceiverObservationKind.Unavailable, "receiver-capability-drift");
        // Existing committed receipt was already checked above; this creation-time
        // validation cannot prevent authorized receipt reconciliation after category changes.
        var targetBearer = await credentials.GetBearerAsync(current, prepared.Peer, "rateldesk.incident-targets.read", ct);
        await receiver.ValidateTargetsAsync(prepared.Peer, capability, targetBearer, ct);
        var gate = await recheckFlowGuard(ct);
        if (!gate.Allowed) return new(RatelDeskReceiverObservationKind.AuthenticationRejected, gate.Code);
        // Async target probes may have raced disable/rotation/mapping update.
        current = await CurrentConnector(prepared.Peer.LocalTenantId, prepared.ConnectorId, prepared.ConnectorRevision, ct);
        var createBearer = await credentials.GetBearerAsync(current, prepared.Peer, "rateldesk.incidents.create", ct);
        gate = await recheckFlowGuard(ct);
        if (!gate.Allowed) return new(RatelDeskReceiverObservationKind.AuthenticationRejected, gate.Code);
        if (!await evidenceStore.MarkPostAttemptAsync(lease, nodeId, ct))
            return new(RatelDeskReceiverObservationKind.Unavailable, "action-lease-lost");
        // Persisted MayHaveCommitted and the exact POST-attempt count precede HTTP. A last
        // awaited local gate can still stop the send; it cannot clear durable uncertainty.
        gate = await recheckFlowGuard(ct);
        if (!gate.Allowed) return new(RatelDeskReceiverObservationKind.AuthenticationRejected, gate.Code);
        current = await CurrentConnector(prepared.Peer.LocalTenantId, prepared.ConnectorId, prepared.ConnectorRevision, ct);
        if (!await authorization.CanExecuteAsync(lease.Event.Authority.PrincipalId,
                lease.Event.Authority.IntegrationCredentialId, lease.Event.TenantId, ct))
            return new(RatelDeskReceiverObservationKind.AuthenticationRejected, "connector-current-authority-denied");
        createBearer = await credentials.GetBearerAsync(current, prepared.Peer, "rateldesk.incidents.create", ct);
        gate = await recheckFlowGuard(ct);
        if (!gate.Allowed) return new(RatelDeskReceiverObservationKind.AuthenticationRejected, gate.Code);
        ct.ThrowIfCancellationRequested();
        return await receiver.CreateAsync(prepared, createBearer, ct);
    }

    private async Task<RatelDeskConnectorState> CurrentConnector(int tenant, Guid id, long revision, CancellationToken ct)
    {
        var connector = await connectors.GetAsync(tenant, id, ct);
        if (connector is null || connector.Revision != revision || !connector.Configuration.Enabled ||
            !await authorization.CanExecuteAsync(connector.OwnerPrincipalId, null, tenant, ct))
            throw new UnauthorizedAccessException("connector-revision-or-owner-unavailable");
        return connector;
    }
    private static int Map(RatelDeskPriorityMapping mapping, string severity) => severity.ToLowerInvariant() switch
    { "critical" => mapping.Critical, "error" or "high" => mapping.Error, "warning" or "medium" => mapping.Warning, _ => mapping.Information };
    private static string BuildOriginalDescription(FlowIncidentActionDraft draft) => draft.Fields.Description + "\n\nNetRatel monitoring occurrence\n" +
        $"Client: {draft.Event.Data.ClientName} ({draft.Event.Data.AgentId:D})\nResource: {draft.Event.Data.Resource}\n" +
        $"Rule: {draft.Event.Data.RuleName} ({draft.Event.Data.RuleId:D})\nSeverity: {draft.Event.Data.Severity}\n" +
        $"Metric: {draft.Event.Data.Metric}\nObserved value: {draft.Event.Data.NumericValue?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? draft.Event.Data.ServiceState ?? "Unknown"}\n" +
        $"Observed at: {draft.Event.Data.ObservedAtUtc:O}\nOccurred at: {draft.Event.OccurredAtUtc:O}\n" +
        $"Source: {draft.SourceInstanceId:D}/{draft.Event.OccurrenceId:D}/{draft.Event.EventId:D}";
}
