using System.Text.Json;
using NetRatel.Shared.Connectivity;

namespace NetRatel.Setup;

public sealed partial class SetupSession
{
    private async Task<SetupCheck> CheckCurrentAdmissionAsync(Uri target, string token, string? sessionId, JsonElement status, CancellationToken ct)
    {
        try { return await ReadCurrentAdmissionAsync(target, token, sessionId, status, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException or NetRatel.AgentClient.AgentClientValidationException)
        {
            return new("current admission", "unavailable", "Current owner-scoped admission is unavailable. Authentication completed; tenant policies and dispatch readiness remain unverified.")
            { Layer = "admission", Code = "admission_unavailable", NextAction = "Refresh current admission in credential details or inspect the caller-owned reviewed access receipt." };
        }
    }

    private async Task<SetupCheck> ReadCurrentAdmissionAsync(Uri target, string token, string? sessionId, JsonElement status, CancellationToken ct)
    {
        const string next = "Open credential details to refresh current admission. For missing Production policies, use the independently reviewed netratel_access request/receipt/resume reconciliation flow; preserve configured grants and the original exact UTC expiry.";
        if (!status.TryGetProperty("credentialId", out var id) || id.ValueKind != JsonValueKind.String || !Guid.TryParse(id.GetString(), out var credentialId))
            return new("current admission", "not_checked", "Configured application grants, current durable owner permissions, tenant policies and runtime readiness were not checked by auth/status.")
            { Layer = "admission", Code = "admission_not_checked", NextAction = next };
        var reply = await SendMcpAsync(target, token, new { jsonrpc = "2.0", id = 4, method = "tools/call",
            @params = new { name = "netratel_access", arguments = new { operation = "credential", request = new { credentialId = credentialId.ToString("N") } } } }, sessionId, ct);
        if (reply.Status is >= 200 and < 300 && reply.Result is { } envelope && IsMcpReply(envelope, 4) &&
            envelope.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.Object && (!result.TryGetProperty("isError", out var error) || error.ValueKind == JsonValueKind.False) &&
            result.TryGetProperty("structuredContent", out var structured) && structured.ValueKind == JsonValueKind.Object && structured.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.True &&
            structured.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object && data.TryGetProperty("receipt", out var receipt) && receipt.ValueKind == JsonValueKind.Object &&
            receipt.TryGetProperty("admission", out var value) && value.ValueKind == JsonValueKind.Object)
        {
            var admission = value.Deserialize<CredentialMcpAdmission>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (admission is not null && admission.EffectiveAdmission is not null && admission.RemainingPrerequisites is not null &&
                admission.EffectiveAdmission.All(x => x is not null && x.Target is not null) && admission.CredentialId == credentialId.ToString("N") && McpResourceUri.Equivalent(admission.Resource, target.AbsoluteUri))
            {
                var authorized = admission.CredentialState == "active" && admission.AuthenticationStatus == "current_credential_valid" &&
                    admission.EffectiveAdmission.Count > 0 && admission.EffectiveAdmission.All(x => x.AuthorizationAdmitted);
                var admissionStatus = authorized ? "completed" : admission.CredentialState == "active" &&
                    admission.AuthenticationStatus == "current_credential_valid" && admission.EffectiveAdmission.Count == 0 ? "not_checked" : "blocked";
                var facts = string.Join("; ", admission.EffectiveAdmission.Select(x =>
                    $"{x.Target.Tool}/{x.Target.Operation} tenant={x.Target.TenantId}: configured={x.ApplicationGrantConfigured}, owner={x.OwnerPermissionGranted}, visible={x.TenantVisible}, policy={x.PolicyAdmissionStatus}, result={x.FailureCode ?? "authorization_admitted"}, readiness={x.TargetReadiness}, capability={x.CapabilityAvailability}"));
                return new("current admission", admissionStatus,
                    $"Lifecycle={admission.CredentialState}; authentication validity={admission.AuthenticationStatus}; binding={admission.Resource} {admission.Instance} {admission.Environment}; exact expiry={admission.CredentialExpiresAtUtc.UtcDateTime:O}. {facts}. " +
                    string.Join(" ", admission.RemainingPrerequisites), reply.Status)
                { Layer = "admission", Code = authorized ? null : admissionStatus == "not_checked" ? "admission_not_checked" : "admission_prerequisite_missing", NextAction = admission.NextAction, Admission = admission };
            }
        }
        return new("current admission", "unavailable", "The owned current admission view is unavailable; authentication success does not establish configured grants, tenant admission or runtime readiness.", reply.Status)
        { Layer = "admission", Code = "admission_unavailable", NextAction = next };
    }
}
