using System.Text;
using System.Text.Json;
using NetRatel.Application.Flows;
using NetRatel.Application.RatelDesk;
using NetRatel.Shared.Contracts.Flows;
using NetRatel.Shared.Contracts.RatelDesk;

namespace NetRatel.Infrastructure.RatelDesk;

public static class ReceiverPreparedBinding
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static bool Valid(RatelDeskReceiverPreparationV2 evidence, FlowIncidentActionRequest action,
        FlowIncidentActionDraft draft, DateTimeOffset runCreatedAtUtc, IRatelDeskReceiverFingerprint receiverHash)
    {
        if (evidence is null || evidence.Peer is null || evidence.Capability is null ||
            evidence.Peer.CategoryIds is null || evidence.ExactCreateBodyJson is null ||
            runCreatedAtUtc == default || runCreatedAtUtc.Offset != TimeSpan.Zero ||
            !FlowContractValidation.ValidPrepared(action, draft) || !action.SupportsSafeReplay ||
            evidence.SchemaVersion != 2 || evidence.ConnectorId != action.ConnectorId ||
            evidence.ConnectorRevision != action.ConnectorRevision || evidence.Peer.ConnectorId != action.ConnectorId ||
            evidence.Peer.LocalTenantId != action.TenantId || evidence.Peer.SourceInstanceId != action.SourceInstanceId ||
            evidence.Peer.SourceNamespaceId == Guid.Empty || evidence.Peer.OrganizationId != action.Target.OrganizationId ||
            evidence.Peer.CustomerId != action.Target.CustomerId || evidence.Peer.AssignedToId != action.Target.AssignedToId ||
            !action.Target.CategoryIds.Select(x => x.ToString("D")).Order(StringComparer.Ordinal)
                .SequenceEqual(evidence.Peer.CategoryIds, StringComparer.Ordinal) ||
            !ReceiverWireValidation.ValidCapability(evidence.Capability, evidence.Peer) ||
            evidence.OriginalActionCreatedAtUtc != runCreatedAtUtc ||
            evidence.AutomaticReplayUntilUtc != runCreatedAtUtc.AddSeconds(Math.Min(
                (long)FlowLimits.MaximumRetryAge.TotalSeconds, evidence.Capability.MaximumAutomaticReplaySeconds)) ||
            evidence.EvidenceFingerprint != ReceiverPreparationBuilder.EvidenceHash(evidence)) return false;
        var body = new RatelDeskCreateIncidentDto(action.Fields.Title, action.Fields.Description,
            action.Fields.Priority!.Value, action.Target.CustomerId, action.Target.OrganizationId,
            action.Target.AssignedToId, action.Target.CategoryIds);
        if (evidence.ExactCreateBodyJson != JsonSerializer.Serialize(body, Json) ||
            Encoding.UTF8.GetByteCount(evidence.ExactCreateBodyJson) > RatelDeskConnectorLimits.MaximumRequestBytes ||
            evidence.ReceiverFingerprint != receiverHash.Compute(body)) return false;
        string expectedKey;
        try { expectedKey = RatelDeskReceiverKey.Prepare(draft, null); }
        catch (ArgumentException) { return false; }
        // A new sidecar cannot invent a different wire key by labeling it preserved.
        return evidence.ReceiverIdempotencyKey == expectedKey &&
            (evidence.KeyAlgorithm == RatelDeskReceiverKey.Algorithm ||
             evidence.KeyAlgorithm == "preserved" && expectedKey == draft.IdempotencyKey);
    }
}
