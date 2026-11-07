using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NetRatel.Shared.Contracts.Flows;

namespace NetRatel.Web.Services.Flows;

public interface IFlowApiService
{
    Task<FlowPageDto> QueryAsync(int tenantId, FlowQueryDto query, CancellationToken token = default);
    Task<FlowSummaryDto> GetMetadataAsync(int tenantId, Guid flowId, CancellationToken token = default);
    Task<FlowDeleteResultDto> DeleteAsync(int tenantId, Guid flowId, FlowDeleteRequest request, CancellationToken token = default);
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
    Task<FlowDryRunResultDto> DryRunExecutionAsync(int tenantId, FlowExecutionDryRunRequest request, CancellationToken token = default);
    Task<IReadOnlyList<FlowConnectorReferenceDto>> GetConnectorsAsync(int tenantId, CancellationToken token = default);
    Task<IReadOnlyList<FlowVersionDto>> GetVersionsAsync(int tenantId, Guid flowId, CancellationToken token = default);
    Task<IReadOnlyList<FlowRunSummaryDto>> GetRunsAsync(int tenantId, Guid flowId, CancellationToken token = default);
    Task<FlowRunDetailDto> GetRunAsync(int tenantId, Guid flowId, Guid runId, CancellationToken token = default);
    Task<FlowVersionDto> GetVersionAsync(int tenantId, Guid versionId, CancellationToken token = default);
    Task<FlowRunDetailDto> GetRunByIdAsync(int tenantId, Guid runId, CancellationToken token = default);
    Task<FlowSchemaCapabilitiesDto> GetSchemaCapabilitiesAsync(int tenantId, CancellationToken token = default) =>
        Task.FromResult(new FlowSchemaCapabilitiesDto([FlowLimits.SchemaVersion], [FlowLimits.SchemaVersion]));
    Task<IReadOnlyList<FlowTemplateDescriptorDto>> GetTemplatesAsync(int tenantId, CancellationToken token = default) =>
        Task.FromException<IReadOnlyList<FlowTemplateDescriptorDto>>(new NotSupportedException("Flow templates are unavailable."));
    Task<FlowDefinitionDto> CreateFromTemplateAsync(int tenantId, FlowCreateFromTemplateRequest request, CancellationToken token = default) =>
        Task.FromException<FlowDefinitionDto>(new NotSupportedException("Flow templates are unavailable."));
    Task<IReadOnlyList<FlowExecutionTargetSelectionDto>> GetSelectedExecutionTargetsAsync(int tenantId, FlowExecutionSourceKind sourceKind,
        IReadOnlyList<Guid> ids, CancellationToken token = default) =>
        Task.FromException<IReadOnlyList<FlowExecutionTargetSelectionDto>>(new NotSupportedException("Selected endpoint metadata is unavailable."));
    Task<FlowExecutionDefinitionsPageDto> SearchExecutionDefinitionsAsync(int tenantId, FlowExecutionSourceKind sourceKind,
        string? search = null, string? afterId = null, int limit = 50, CancellationToken token = default) =>
        Task.FromException<FlowExecutionDefinitionsPageDto>(new NotSupportedException("Execution definition search is unavailable."));
    Task<FlowExecutionTargetsPageDto> SearchExecutionTargetsAsync(int tenantId, FlowExecutionSourceKind sourceKind,
        string? search = null, string? afterId = null, int limit = 50, CancellationToken token = default) =>
        Task.FromException<FlowExecutionTargetsPageDto>(new NotSupportedException("Execution target search is unavailable."));
    Task<FlowExecutionActionSettingsDto> GetExecutionDefinitionAsync(int tenantId, FlowExecutionSourceKind sourceKind,
        string definitionId, CancellationToken token = default) =>
        Task.FromException<FlowExecutionActionSettingsDto>(new NotSupportedException("Execution definition details are unavailable."));
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
        "execution-definition-source-unsupported" => "This lifecycle source has no runnable library definition. Choose a job or library script.",
        "execution-definition-unavailable" => "The selected execution definition is unavailable in this tenant.",
        "execution-definition-name-limit" => "This definition name exceeds the supported 512-character limit.",
        "execution-trigger-source-unavailable" => "Request events currently support job-backed definitions. Select Script execution events for library scripts, or remove the unavailable filter. Your draft is preserved.",
        "execution-definition-query-invalid" or "execution-definition-id-invalid" or "execution-target-query-invalid" => "The definition search or selection is invalid. Reload the catalogue.",
        "flow-active-work" => "This flow has unfinished work or a live delivery obligation. Open History and wait for it to settle before deleting. Nothing was disabled or deleted.",
        "flow-revision-conflict" => "This flow changed. Refresh and review its current revision before deleting.",
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
    public Task<FlowPageDto> QueryAsync(int tenantId, FlowQueryDto query, CancellationToken token = default)
    {
        if (!query.IsValid) throw new ArgumentException("flow-query-invalid");
        var path = $"{Root(tenantId)}/summary?search={Uri.EscapeDataString(query.Search?.Trim() ?? "")}&enabled={query.Enabled}&publication={query.Publication}&sort={query.Sort}&descending={query.Descending}&pageIndex={query.PageIndex}&pageSize={query.PageSize}";
        return SendAsync<FlowPageDto>(HttpMethod.Get, path, null, token);
    }
    public Task<FlowSummaryDto> GetMetadataAsync(int tenantId, Guid flowId, CancellationToken token = default) =>
        SendAsync<FlowSummaryDto>(HttpMethod.Get, Flow(tenantId, flowId) + "/metadata", null, token);
    public Task<FlowDeleteResultDto> DeleteAsync(int tenantId, Guid flowId, FlowDeleteRequest request, CancellationToken token = default) =>
        SendAsync<FlowDeleteResultDto>(HttpMethod.Delete, Flow(tenantId, flowId), request, token);
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
    public Task<FlowDryRunResultDto> DryRunExecutionAsync(int tenantId, FlowExecutionDryRunRequest request, CancellationToken token = default) => SendAsync<FlowDryRunResultDto>(HttpMethod.Post, Root(tenantId) + "/dry-run-execution", request, token);
    public Task<IReadOnlyList<FlowConnectorReferenceDto>> GetConnectorsAsync(int tenantId, CancellationToken token = default) => SendAsync<IReadOnlyList<FlowConnectorReferenceDto>>(HttpMethod.Get, Root(tenantId) + "/connectors", null, token);
    public Task<IReadOnlyList<FlowVersionDto>> GetVersionsAsync(int tenantId, Guid flowId, CancellationToken token = default) => SendAsync<IReadOnlyList<FlowVersionDto>>(HttpMethod.Get, Flow(tenantId, flowId) + "/versions", null, token);
    public Task<IReadOnlyList<FlowRunSummaryDto>> GetRunsAsync(int tenantId, Guid flowId, CancellationToken token = default) => SendAsync<IReadOnlyList<FlowRunSummaryDto>>(HttpMethod.Get, Flow(tenantId, flowId) + "/runs", null, token);
    public Task<FlowRunDetailDto> GetRunAsync(int tenantId, Guid flowId, Guid runId, CancellationToken token = default) => SendAsync<FlowRunDetailDto>(HttpMethod.Get, Flow(tenantId, flowId) + $"/runs/{runId:D}", null, token);
    public Task<FlowVersionDto> GetVersionAsync(int tenantId, Guid versionId, CancellationToken token = default) => SendAsync<FlowVersionDto>(HttpMethod.Get, Root(tenantId) + $"/versions/{versionId:D}", null, token);
    public Task<FlowRunDetailDto> GetRunByIdAsync(int tenantId, Guid runId, CancellationToken token = default) => SendAsync<FlowRunDetailDto>(HttpMethod.Get, Root(tenantId) + $"/runs/{runId:D}", null, token);

    public Task<FlowSchemaCapabilitiesDto> GetSchemaCapabilitiesAsync(int tenantId, CancellationToken token = default) =>
        SendAsync<FlowSchemaCapabilitiesDto>(HttpMethod.Get, Root(tenantId) + "/schema-capabilities", null, token);
    public Task<IReadOnlyList<FlowTemplateDescriptorDto>> GetTemplatesAsync(int tenantId, CancellationToken token = default) =>
        SendAsync<IReadOnlyList<FlowTemplateDescriptorDto>>(HttpMethod.Get, Root(tenantId) + "/templates", null, token);
    public Task<FlowDefinitionDto> CreateFromTemplateAsync(int tenantId, FlowCreateFromTemplateRequest request, CancellationToken token = default) =>
        SendAsync<FlowDefinitionDto>(HttpMethod.Post, Root(tenantId) + "/from-template", request, token);
    public Task<IReadOnlyList<FlowExecutionTargetSelectionDto>> GetSelectedExecutionTargetsAsync(int tenantId, FlowExecutionSourceKind sourceKind,
        IReadOnlyList<Guid> ids, CancellationToken token = default)
    {
        var path = $"{Root(tenantId)}/execution-definitions/targets/selected?sourceKind={Uri.EscapeDataString(sourceKind.ToString())}";
        foreach (var id in ids) path += "&ids=" + id.ToString("D");
        return SendAsync<IReadOnlyList<FlowExecutionTargetSelectionDto>>(HttpMethod.Get, path, null, token);
    }
    public Task<FlowExecutionDefinitionsPageDto> SearchExecutionDefinitionsAsync(int tenantId, FlowExecutionSourceKind sourceKind,
        string? search = null, string? afterId = null, int limit = 50, CancellationToken token = default)
    {
        var path = $"{Root(tenantId)}/execution-definitions?sourceKind={Uri.EscapeDataString(sourceKind.ToString())}&limit={limit}";
        if (!string.IsNullOrWhiteSpace(search)) path += "&search=" + Uri.EscapeDataString(search.Trim());
        if (!string.IsNullOrEmpty(afterId)) path += "&afterId=" + Uri.EscapeDataString(afterId);
        return SendAsync<FlowExecutionDefinitionsPageDto>(HttpMethod.Get, path, null, token);
    }
    public Task<FlowExecutionTargetsPageDto> SearchExecutionTargetsAsync(int tenantId, FlowExecutionSourceKind sourceKind,
        string? search = null, string? afterId = null, int limit = 50, CancellationToken token = default)
    {
        var path = $"{Root(tenantId)}/execution-definitions/targets?sourceKind={Uri.EscapeDataString(sourceKind.ToString())}&limit={limit}";
        if (!string.IsNullOrWhiteSpace(search)) path += "&search=" + Uri.EscapeDataString(search.Trim());
        if (!string.IsNullOrEmpty(afterId)) path += "&afterId=" + Uri.EscapeDataString(afterId);
        return SendAsync<FlowExecutionTargetsPageDto>(HttpMethod.Get, path, null, token);
    }
    public async Task<FlowExecutionActionSettingsDto> GetExecutionDefinitionAsync(int tenantId, FlowExecutionSourceKind sourceKind,
        string definitionId, CancellationToken token = default)
    {
        try
        {
            return await SendAsync<FlowExecutionActionSettingsDto>(HttpMethod.Get,
                $"{Root(tenantId)}/execution-definitions/{Uri.EscapeDataString(sourceKind.ToString())}/{Uri.EscapeDataString(definitionId)}", null, token);
        }
        catch (FlowApiException error) when (error.StatusCode == HttpStatusCode.NotFound && error.Code is null)
        { throw new FlowApiException(HttpStatusCode.NotFound, "execution-definition-unavailable"); }
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var request = new HttpRequestMessage(method, path);
        if (body is FlowSaveDraftRequest { Graph.SchemaVersion: FlowGraphV2Limits.SchemaVersion } or FlowGraphDto { SchemaVersion: FlowGraphV2Limits.SchemaVersion })
            request.Headers.Add("X-NetRatel-Flow-Schema", FlowGraphV2Limits.SchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
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
