using System.Security.Cryptography;
using System.Text.Json;
using NetRatel.Shared.Contracts.Flows;

namespace NetRatel.Application.Flows;

public static class FlowContractValidation
{
    public static bool ValidEnvelope(FlowEventEnvelope? input) => input is not null && input.TenantId > 0 && input.EventId != Guid.Empty &&
        input.OccurrenceId != Guid.Empty && input.FlowVersionId != Guid.Empty && input.OccurredAtUtc != default && FlowPureEvaluation.ValidEventData(input.Data) &&
        ValidAuthority(input.Authority) && JsonSerializer.SerializeToUtf8Bytes(input).Length <= FlowLimits.MaximumEventBytes;
    public static bool ValidAuthority(FlowExecutionAuthorityDto? authority) => authority is not null &&
        FlowGraphValidator.IsBoundedText(authority.PrincipalId, 256) && (authority.IntegrationCredentialId is null || FlowGraphValidator.IsBoundedText(authority.IntegrationCredentialId, 256));
    public static bool ValidPrepared(FlowIncidentActionRequest? action, FlowIncidentActionDraft draft) => action is not null &&
        action.TenantId == draft.TenantId && action.RunId == draft.RunId && action.ActionNodeId == draft.ActionNodeId && action.ConnectorId == draft.ConnectorId &&
        action.ConnectorRevision == draft.ConnectorRevision && action.ConnectorRevision > 0 && action.SourceInstanceId == draft.SourceInstanceId &&
        action.IdempotencyKey == draft.IdempotencyKey && JsonSerializer.Serialize(action.Event) == JsonSerializer.Serialize(draft.Event) &&
        action.Fields is not null && FlowGraphValidator.IsBoundedText(action.Fields.Title, FlowLimits.MaximumTitleLength) &&
        FlowGraphValidator.IsBoundedText(action.Fields.Description, FlowLimits.MaximumDescriptionLength, multiline: true) && action.Fields.Priority is >= 0 and <= 3 &&
        (draft.Fields.Priority is null || action.Fields.Priority == draft.Fields.Priority) && action.Target is not null &&
        FlowGraphValidator.IsBoundedText(action.Target.OrganizationId, 256) && FlowGraphValidator.IsBoundedText(action.Target.CustomerId, 256) &&
        (action.Target.AssignedToId is null || FlowGraphValidator.IsBoundedText(action.Target.AssignedToId, 256)) && action.Target.CategoryIds is not null &&
        action.Target.CategoryIds.Count <= 16 && action.Target.CategoryIds.All(id => id != Guid.Empty) && action.Target.CategoryIds.Distinct().Count() == action.Target.CategoryIds.Count &&
        action.SemanticFingerprint == Fingerprint(action) && JsonSerializer.SerializeToUtf8Bytes(action).Length <= FlowLimits.MaximumActionBytes;

    public static string Fingerprint(FlowIncidentActionRequest action) => Hash(JsonSerializer.SerializeToUtf8Bytes(action with { SemanticFingerprint = "" }));
    public static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
