using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NetRatel.Shared.Contracts.Flows;

namespace NetRatel.Web.Services.Flows;

public interface IFlowApiService
{
    Task<IReadOnlyList<FlowTenantAccessDto>> GetTenantsAsync(CancellationToken token = default);
    Task<IReadOnlyList<FlowDefinitionDto>> ListAsync(int tenantId, CancellationToken token = default);
    Task<FlowDefinitionDto> GetAsync(int tenantId, Guid flowId, CancellationToken token = default);
    Task<FlowDefinitionDto> CreateAsync(int tenantId, FlowCreateRequest request, CancellationToken token = default);
    Task<FlowDefinitionDto> SaveAsync(int tenantId, Guid flowId, FlowSaveDraftRequest request, CancellationToken token = default);
    Task<FlowDefinitionDto> CloneAsync(int tenantId, Guid flowId, FlowCloneRequest request, CancellationToken token = default);
    Task<FlowDefinitionDto> SetEnabledAsync(int tenantId, Guid flowId, FlowEnabledRequest request, CancellationToken token = default);
    Task<FlowVersionDto> PublishAsync(int tenantId, Guid flowId, FlowRevisionRequest request, CancellationToken token = default);
    Task<FlowValidationResultDto> ValidateAsync(int tenantId, FlowGraphDto graph, CancellationToken token = default);
    Task<FlowDryRunResultDto> DryRunAsync(int tenantId, FlowDryRunRequest request, CancellationToken token = default);
    Task<IReadOnlyList<FlowConnectorReferenceDto>> GetConnectorsAsync(int tenantId, CancellationToken token = default);
    Task<IReadOnlyList<FlowVersionDto>> GetVersionsAsync(int tenantId, Guid flowId, CancellationToken token = default);
    Task<IReadOnlyList<FlowRunSummaryDto>> GetRunsAsync(int tenantId, Guid flowId, CancellationToken token = default);
    Task<FlowRunDetailDto> GetRunAsync(int tenantId, Guid flowId, Guid runId, CancellationToken token = default);
    Task<FlowVersionDto> GetVersionAsync(int tenantId, Guid versionId, CancellationToken token = default);
    Task<FlowRunDetailDto> GetRunByIdAsync(int tenantId, Guid runId, CancellationToken token = default);
}

public sealed class FlowApiException(HttpStatusCode statusCode, string? code = null) : HttpRequestException(MessageFor(statusCode, code), null, statusCode)
{
    public string? Code { get; } = code;
    private static string MessageFor(HttpStatusCode status, string? code) => code switch
    {
        "receiver-idempotency-unverified" => "Publishing is unavailable until the RatelDesk receiver supports verified incident deduplication. Your draft is preserved; review Flows connectors.",
        "connector-disabled-or-owner-denied" or "connector-authority" or "connector-current-authority-denied" => "The connector is disabled or its owner no longer has permission. Choose an authorized connector in this tenant. Your draft is preserved.",
        "connector-revision-unavailable" => "The connector changed. Select its current reference and save the draft again.",
        "connector-unavailable" => "The selected connector is unavailable. Review Flows connectors before publishing; your draft is preserved.",
        "mapping-output-limit" => "The sample expands beyond the incident field limits. Shorten the title or description template.",
        "canonical-principal-required" => "This session cannot publish. Sign in again with an authorized operator account; your draft is preserved.",
        _ => StatusMessage(status)
    };
    private static string StatusMessage(HttpStatusCode code) => code switch
    {
        HttpStatusCode.Conflict => "This flow changed in another session. Your edits are preserved; reload the latest draft before saving again.",
        HttpStatusCode.Forbidden => "You no longer have permission for this flow or tenant.",
        HttpStatusCode.NotFound => "This flow is unavailable in the selected tenant.",
        HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity => "The flow could not be accepted. Validate the draft and check its fields and connector reference.",
        HttpStatusCode.TooManyRequests => "The flow limit or request limit was reached. Try again later.",
        _ => "The flow request failed. Your unsaved edits are preserved."
    };
}

/// <summary>Only explicit commands write drafts or publish. No browser execution endpoint is exposed.</summary>
public sealed class FlowApiService(IHttpClientFactory factory) : IFlowApiService
{
    public const string ClientName = "FlowsApi";
    private readonly HttpClient _http = factory.CreateClient(ClientName);
    private static string Root(int tenantId) => $"/api/v1/tenants/{tenantId}/flows";
    private static string Flow(int tenantId, Guid id) => $"{Root(tenantId)}/{id:D}";
    public Task<IReadOnlyList<FlowTenantAccessDto>> GetTenantsAsync(CancellationToken token = default) => SendAsync<IReadOnlyList<FlowTenantAccessDto>>(HttpMethod.Get, "/api/v1/flows/tenants", null, token);
    public Task<IReadOnlyList<FlowDefinitionDto>> ListAsync(int tenantId, CancellationToken token = default) => SendAsync<IReadOnlyList<FlowDefinitionDto>>(HttpMethod.Get, Root(tenantId), null, token);
    public Task<FlowDefinitionDto> GetAsync(int tenantId, Guid flowId, CancellationToken token = default) => SendAsync<FlowDefinitionDto>(HttpMethod.Get, Flow(tenantId, flowId), null, token);
    public Task<FlowDefinitionDto> CreateAsync(int tenantId, FlowCreateRequest request, CancellationToken token = default) => SendAsync<FlowDefinitionDto>(HttpMethod.Post, Root(tenantId), request, token);
    public Task<FlowDefinitionDto> SaveAsync(int tenantId, Guid flowId, FlowSaveDraftRequest request, CancellationToken token = default) => SendAsync<FlowDefinitionDto>(HttpMethod.Put, Flow(tenantId, flowId) + "/draft", request, token);
    public Task<FlowDefinitionDto> CloneAsync(int tenantId, Guid flowId, FlowCloneRequest request, CancellationToken token = default) => SendAsync<FlowDefinitionDto>(HttpMethod.Post, Flow(tenantId, flowId) + "/clone", request, token);
    public Task<FlowDefinitionDto> SetEnabledAsync(int tenantId, Guid flowId, FlowEnabledRequest request, CancellationToken token = default) => SendAsync<FlowDefinitionDto>(HttpMethod.Put, Flow(tenantId, flowId) + "/enabled", request, token);
    public Task<FlowVersionDto> PublishAsync(int tenantId, Guid flowId, FlowRevisionRequest request, CancellationToken token = default) => SendAsync<FlowVersionDto>(HttpMethod.Post, Flow(tenantId, flowId) + "/publish", request, token);
    public Task<FlowValidationResultDto> ValidateAsync(int tenantId, FlowGraphDto graph, CancellationToken token = default) => SendAsync<FlowValidationResultDto>(HttpMethod.Post, Root(tenantId) + "/validate", graph, token);
    public Task<FlowDryRunResultDto> DryRunAsync(int tenantId, FlowDryRunRequest request, CancellationToken token = default) => SendAsync<FlowDryRunResultDto>(HttpMethod.Post, Root(tenantId) + "/dry-run", request, token);
    public Task<IReadOnlyList<FlowConnectorReferenceDto>> GetConnectorsAsync(int tenantId, CancellationToken token = default) => SendAsync<IReadOnlyList<FlowConnectorReferenceDto>>(HttpMethod.Get, Root(tenantId) + "/connectors", null, token);
    public Task<IReadOnlyList<FlowVersionDto>> GetVersionsAsync(int tenantId, Guid flowId, CancellationToken token = default) => SendAsync<IReadOnlyList<FlowVersionDto>>(HttpMethod.Get, Flow(tenantId, flowId) + "/versions", null, token);
    public Task<IReadOnlyList<FlowRunSummaryDto>> GetRunsAsync(int tenantId, Guid flowId, CancellationToken token = default) => SendAsync<IReadOnlyList<FlowRunSummaryDto>>(HttpMethod.Get, Flow(tenantId, flowId) + "/runs", null, token);
    public Task<FlowRunDetailDto> GetRunAsync(int tenantId, Guid flowId, Guid runId, CancellationToken token = default) => SendAsync<FlowRunDetailDto>(HttpMethod.Get, Flow(tenantId, flowId) + $"/runs/{runId:D}", null, token);
    public Task<FlowVersionDto> GetVersionAsync(int tenantId, Guid versionId, CancellationToken token = default) => SendAsync<FlowVersionDto>(HttpMethod.Get, Root(tenantId) + $"/versions/{versionId:D}", null, token);
    public Task<FlowRunDetailDto> GetRunByIdAsync(int tenantId, Guid runId, CancellationToken token = default) => SendAsync<FlowRunDetailDto>(HttpMethod.Get, Root(tenantId) + $"/runs/{runId:D}", null, token);

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body, body.GetType());
        try
        {
            using var response = await _http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                string? code = null;
                try
                {
                    using var error = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(timeout.Token), cancellationToken: timeout.Token).ConfigureAwait(false);
                    if (error.RootElement.ValueKind == JsonValueKind.Object && error.RootElement.TryGetProperty("code", out var property) && property.ValueKind == JsonValueKind.String && property.GetString() is { Length: <= 128 } candidate)
                        code = candidate;
                }
                catch (JsonException) { }
                throw new FlowApiException(response.StatusCode, code);
            }
            return await response.Content.ReadFromJsonAsync<T>(cancellationToken: timeout.Token).ConfigureAwait(false)
                ?? throw new HttpRequestException("The flow response was empty.");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new HttpRequestException("The flow request timed out. Your unsaved edits are preserved."); }
        catch (JsonException) { throw new HttpRequestException("The flow response was invalid. Reload before continuing."); }
    }
}
