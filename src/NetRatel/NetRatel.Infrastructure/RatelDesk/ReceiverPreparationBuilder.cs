using System.Security.Cryptography;
using System.Text.Json;
using NetRatel.Application.Flows;
using NetRatel.Application.RatelDesk;
using NetRatel.Shared.Contracts.RatelDesk;

namespace NetRatel.Infrastructure.RatelDesk;

/// <summary>The frozen receiver sanitizer and typed incident projection.</summary>
public interface IRatelDeskReceiverFingerprint
{
    string Compute(RatelDeskCreateIncidentDto body);
}

public sealed class ReceiverPreparationBuilder(IRatelDeskReceiverFingerprint fingerprint)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public RatelDeskReceiverPreparationV2 Build(FlowIncidentActionDraft draft,
        FlowIncidentActionRequest action, RatelDeskSemanticPeer peer,
        RatelDeskVerifiedCapability capability, DateTimeOffset originalCreatedAtUtc,
        string? previouslyPersistedWireKey)
    {
        if (!FlowContractValidation.ValidPrepared(action, draft) || action.SourceInstanceId != peer.SourceInstanceId ||
            action.ConnectorId != peer.ConnectorId || action.TenantId != peer.LocalTenantId ||
            action.Target.OrganizationId != peer.OrganizationId || action.Target.CustomerId != peer.CustomerId ||
            action.Target.AssignedToId != peer.AssignedToId ||
            !action.Target.CategoryIds.Select(x => x.ToString("D")).Order(StringComparer.Ordinal)
                .SequenceEqual(peer.CategoryIds, StringComparer.Ordinal) ||
            !ReceiverWireValidation.ValidCapability(capability, peer) || originalCreatedAtUtc == default ||
            originalCreatedAtUtc.Offset != TimeSpan.Zero)
            throw new InvalidOperationException("prepared-receiver-binding-invalid");
        var body = new RatelDeskCreateIncidentDto(action.Fields.Title, action.Fields.Description,
            action.Fields.Priority!.Value, action.Target.CustomerId, action.Target.OrganizationId,
            action.Target.AssignedToId, action.Target.CategoryIds);
        var presentation = action.PresentationPayload;
        if (presentation is not null && (!AutomationTrace.AutomationTraceValidation.Create(presentation) ||
            !ReceiverWireValidation.ValidPresentationCapability(capability.AutomationPresentation, peer)))
            throw new InvalidOperationException("incident-presentation-unverified");
        var exactBody = presentation is null ? JsonSerializer.Serialize(body, Json) : JsonSerializer.Serialize(AutomationTrace.AutomationTraceValidation.Normalize(presentation), Json);
        if (System.Text.Encoding.UTF8.GetByteCount(exactBody) > (presentation is null ? RatelDeskConnectorLimits.MaximumRequestBytes : 131_072))
            throw new InvalidOperationException("incident-request-too-large");
        var key = RatelDeskReceiverKey.Prepare(draft, previouslyPersistedWireKey);
        var replaySeconds = Math.Min((long)NetRatel.Shared.Contracts.Flows.FlowLimits.MaximumRetryAge.TotalSeconds,
            capability.MaximumAutomaticReplaySeconds);
        var evidence = new RatelDeskReceiverPreparationV2(2, action.ConnectorId, action.ConnectorRevision,
            peer, capability, key, previouslyPersistedWireKey is not null || key == draft.IdempotencyKey ? "preserved" : RatelDeskReceiverKey.Algorithm,
            exactBody, presentation is null ? fingerprint.Compute(body) : AutomationTrace.AutomationTraceValidation.Fingerprint(presentation), originalCreatedAtUtc,
            originalCreatedAtUtc.AddSeconds(replaySeconds), "")
        { PresentationContract = presentation?.ContractVersion };
        evidence = evidence with { EvidenceFingerprint = EvidenceHash(evidence) };
        if (!ReceiverPreparedBinding.Valid(evidence, action, draft, originalCreatedAtUtc, fingerprint))
            throw new InvalidOperationException("prepared-receiver-binding-invalid");
        return evidence;
    }
    public static string EvidenceHash(RatelDeskReceiverPreparationV2 evidence) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
            evidence with { EvidenceFingerprint = "" }, Json)));
}
