using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NetRatel.Application.Operations;
using NetRatel.Shared.Connectivity;
using NetRatel.Infrastructure.Identity;

namespace NetRatel.Web.Services.OperatorPolicy;

public enum PolicyChangeAction { Create, Replace, Disable, Revoke, Delete }
public sealed record PolicyCredentialOption(string Id, string Name, IntegrationCredentialPurpose Purpose);
public sealed record PolicyAccountAuthority(string Subject, McpOperatorEnvironment Environment);
public sealed record PolicyMutationPreview(string PlanToken, string IdempotencyKey, DateTimeOffset ExpiresAtUtc,
    McpOperatorConfirmationClass ConfirmationClass, int TenantId, Guid? AgentId, string CorrelationId)
{
    public override string ToString() => $"Policy preview {CorrelationId}, expires {ExpiresAtUtc:O} (credentials redacted)";
}
public sealed record PolicyMutationResult(McpOperatorPolicy Policy, bool Replay, string CorrelationId);
public sealed record PreparedPolicyChange(PolicyChangeAction Action, McpOperatorPolicyDraft? Draft,
    Guid? PolicyId, long? ExpectedVersion, McpOperatorTargetSelector Target);

public interface IMcpOperatorPolicyApiService
{
    Task<McpPolicyConsolidationPlan> PreviewConsolidationAsync(string credentialId, CancellationToken ct = default) => throw new NotSupportedException();
    Task<McpPolicyConsolidationPlan> ConfirmConsolidationAsync(string credentialId, string planToken, CancellationToken ct = default) => throw new NotSupportedException();
    Task<McpPolicyDirectory> GetDirectoryAsync(string? search, int? tenantId, CancellationToken ct = default) =>
        Task.FromResult(new McpPolicyDirectory([], [], [], [], false));
    Task<PolicyAccountAuthority> GetAuthorityAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PolicyCredentialOption>> GetOwnedCredentialsAsync(CancellationToken cancellationToken = default);
    Task<McpOperatorPolicyPage> ListAsync(McpOperatorEnvironment environment, int? tenantId, CancellationToken cancellationToken = default);
    Task<McpOperatorPolicy> GetAsync(Guid policyId, CancellationToken cancellationToken = default);
    Task<McpOperatorTargetProfile?> GetTargetAsync(int tenantId, Guid agentId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<McpOperatorPolicyChangeAudit>> GetAuditsAsync(Guid policyId, CancellationToken cancellationToken = default);
    Task<CredentialMcpAdmission> CheckAdmissionAsync(string credentialId, ReviewedMcpAccessTarget target, CancellationToken cancellationToken = default);
    Task<PolicyMutationPreview> PreviewAsync(PreparedPolicyChange change, CancellationToken cancellationToken = default);
    Task<PolicyMutationResult> ConfirmAsync(PreparedPolicyChange change, PolicyMutationPreview preview, CancellationToken cancellationToken = default);
}

/// <summary>Signed-in account transport. Confirmation credentials stay in component memory and are never rendered.</summary>
public sealed class McpOperatorPolicyApiService(IHttpClientFactory clients) : IMcpOperatorPolicyApiService
{
    private const string BasePath = "/api/v2/mcp-operator/policies";
    private HttpClient Client => clients.CreateClient("OrchestratorApi");
    public async Task<McpPolicyConsolidationPlan> PreviewConsolidationAsync(string credentialId, CancellationToken ct = default)
    {
        using var response = await Client.PostAsJsonAsync($"/api/v2/account/integration-credentials/{Uri.EscapeDataString(credentialId)}/policy-consolidation/preview", new { }, ct);
        return await ReadAsync<McpPolicyConsolidationPlan>(response, ct);
    }
    public async Task<McpPolicyConsolidationPlan> ConfirmConsolidationAsync(string credentialId, string planToken, CancellationToken ct = default)
    {
        using var response = await Client.PostAsJsonAsync($"/api/v2/account/integration-credentials/{Uri.EscapeDataString(credentialId)}/policy-consolidation/confirm", new { planToken, confirm = true }, ct);
        return await ReadAsync<McpPolicyConsolidationPlan>(response, ct);
    }
    public Task<McpPolicyDirectory> GetDirectoryAsync(string? search, int? tenantId, CancellationToken ct = default) =>
        GetAsync<McpPolicyDirectory>($"/api/v2/mcp-operator/directory?search={Uri.EscapeDataString(search ?? "")}" + (tenantId is { } id ? $"&tenantId={id}" : ""), ct);

    public Task<PolicyAccountAuthority> GetAuthorityAsync(CancellationToken cancellationToken = default) =>
        GetAsync<PolicyAccountAuthority>($"{BasePath}/manage/self", cancellationToken);

    public async Task<IReadOnlyList<PolicyCredentialOption>> GetOwnedCredentialsAsync(CancellationToken cancellationToken = default) =>
        (await GetAsync<List<PolicyCredentialOption>>("/api/v2/account/integration-credentials/", cancellationToken).ConfigureAwait(false))
            .Where(credential => credential.Purpose == IntegrationCredentialPurpose.HttpMcp).ToArray();

    public Task<McpOperatorPolicyPage> ListAsync(McpOperatorEnvironment environment, int? tenantId, CancellationToken cancellationToken = default) =>
        GetAsync<McpOperatorPolicyPage>($"{BasePath}?includeHistory=true&environment={environment}" + (tenantId is { } id ? $"&tenantId={id}" : ""), cancellationToken);

    public Task<McpOperatorPolicy> GetAsync(Guid policyId, CancellationToken cancellationToken = default) =>
        GetAsync<McpOperatorPolicy>($"{BasePath}/{policyId:D}", cancellationToken);

    public async Task<McpOperatorTargetProfile?> GetTargetAsync(int tenantId, Guid agentId, CancellationToken cancellationToken = default)
    {
        using var response = await Client.GetAsync($"/api/v2/mcp-operator/targets/{tenantId}/{agentId:D}", cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        return await ReadAsync<McpOperatorTargetProfile>(response, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<McpOperatorPolicyChangeAudit>> GetAuditsAsync(Guid policyId, CancellationToken cancellationToken = default) =>
        await GetAsync<List<McpOperatorPolicyChangeAudit>>($"/api/v2/mcp-operator/audits?policyId={policyId:D}", cancellationToken).ConfigureAwait(false);

    public async Task<CredentialMcpAdmission> CheckAdmissionAsync(string credentialId, ReviewedMcpAccessTarget target, CancellationToken cancellationToken = default)
    {
        using var response = await Client.PostAsJsonAsync($"/api/v2/account/integration-credentials/{Uri.EscapeDataString(credentialId)}/admission",
            new { targets = new[] { target } }, cancellationToken).ConfigureAwait(false);
        return await ReadAsync<CredentialMcpAdmission>(response, cancellationToken).ConfigureAwait(false);
    }

    public Task<PolicyMutationPreview> PreviewAsync(PreparedPolicyChange change, CancellationToken cancellationToken = default) =>
        SendAsync<PolicyMutationPreview>(change, null, cancellationToken);

    public Task<PolicyMutationResult> ConfirmAsync(PreparedPolicyChange change, PolicyMutationPreview preview, CancellationToken cancellationToken = default) =>
        SendAsync<PolicyMutationResult>(change, preview, cancellationToken);

    private async Task<T> SendAsync<T>(PreparedPolicyChange change, PolicyMutationPreview? preview, CancellationToken cancellationToken)
    {
        var action = change.Action switch
        {
            PolicyChangeAction.Create => "create", PolicyChangeAction.Replace => "replace",
            PolicyChangeAction.Disable => "disable", PolicyChangeAction.Revoke => "revoke",
            PolicyChangeAction.Delete => "delete",
            _ => throw new ArgumentOutOfRangeException(nameof(change))
        };
        var body = new Dictionary<string, object?>();
        if (change.Action is PolicyChangeAction.Create or PolicyChangeAction.Replace) body["policy"] = change.Draft;
        if (change.Action != PolicyChangeAction.Create)
        {
            body["policyId"] = change.PolicyId;
            body["expectedVersion"] = change.ExpectedVersion;
        }
        if (change.Action is PolicyChangeAction.Disable or PolicyChangeAction.Revoke or PolicyChangeAction.Delete) body["target"] = change.Target;
        if (preview is not null)
        {
            body["planToken"] = preview.PlanToken;
            body["idempotencyKey"] = preview.IdempotencyKey;
        }
        using var response = await Client.PostAsJsonAsync($"{BasePath}/manage/{action}/{(preview is null ? "preview" : "confirm")}", body, cancellationToken).ConfigureAwait(false);
        return await ReadAsync<T>(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task<T> GetAsync<T>(string path, CancellationToken cancellationToken)
    {
        using var response = await Client.GetAsync(path, cancellationToken).ConfigureAwait(false);
        return await ReadAsync<T>(response, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            var code = "operator_policy_request_failed";
            if (response.Content.Headers.ContentType?.MediaType is "application/problem+json" or "application/json")
            {
                try
                {
                    var error = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken).ConfigureAwait(false);
                    if (error.TryGetProperty("failure", out var failure) && failure.TryGetProperty("code", out var nested)) code = SafeCode(nested);
                    else if (error.TryGetProperty("code", out var direct)) code = SafeCode(direct);
                }
                catch (JsonException) { code = "operator_policy_request_failed"; }
            }
            throw new PolicyApiException(response.StatusCode, code);
        }
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken).ConfigureAwait(false)
            ?? throw new HttpRequestException("The policy service returned no result.");
    }

    private static string SafeCode(JsonElement element) =>
        element.ValueKind == JsonValueKind.String && element.GetString() is { Length: > 0 and <= 80 } code &&
        code.All(character => char.IsAsciiLetterOrDigit(character) || character == '_') ? code : "operator_policy_request_failed";
}

public sealed class PolicyApiException(HttpStatusCode status, string code)
    : HttpRequestException($"Policy request was not accepted ({code}).", null, status)
{
    public string Code { get; } = code;
}
