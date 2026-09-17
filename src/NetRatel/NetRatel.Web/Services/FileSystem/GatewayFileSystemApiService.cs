using System.Net.Http.Json;
using NetRatel.Shared.Contracts.FileSystem;

namespace NetRatel.Web.Services.FileSystem;

/// <summary>
/// Web adapter for the Agent-ID keyed gateway file API. It intentionally does
/// not replace <see cref="FileSystemApiService"/> or alter primary client cards.
/// </summary>
public sealed class GatewayFileSystemApiService(IHttpClientFactory factory)
{
    private readonly HttpClient _control = factory.CreateClient("OrchestratorApi");
    private readonly HttpClient _streaming = factory.CreateClient("OrchestratorApiStreaming");

    public async Task<GatewayFileSystemListResponse> ListAsync(int tenantId, Guid agentId, string path, CancellationToken cancellationToken = default)
    {
        using var response = await _control.GetAsync(
            $"/api/v2/agents/{tenantId}/{agentId:D}/filesystem?path={Uri.EscapeDataString(path)}",
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<GatewayFileSystemListResponse>(cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The gateway file API returned an empty response.");
    }

    public Task<HttpResponseMessage> DownloadAsync(int tenantId, Guid agentId, string path, CancellationToken cancellationToken = default) =>
        _streaming.GetAsync($"/api/v2/agents/{tenantId}/{agentId:D}/filesystem/file?path={Uri.EscapeDataString(path)}", HttpCompletionOption.ResponseHeadersRead, cancellationToken);

    public async Task<GatewayFileSystemWriteResponse> UploadAsync(int tenantId, Guid agentId, string path, Stream content, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put,
            $"/api/v2/agents/{tenantId}/{agentId:D}/filesystem/file?path={Uri.EscapeDataString(path)}")
        {
            Content = new StreamContent(content)
        };
        using var response = await _streaming.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<GatewayFileSystemWriteResponse>(cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The gateway file API returned an empty response.");
    }
}
