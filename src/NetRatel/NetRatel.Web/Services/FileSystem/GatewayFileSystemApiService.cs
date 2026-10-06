using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NetRatel.Shared.Contracts;
using NetRatel.Shared.Contracts.FileSystem;

namespace NetRatel.Web.Services.FileSystem;

/// <summary>
/// Web adapter for the Agent-ID keyed gateway file API. It intentionally does
/// not replace <see cref="FileSystemApiService"/> or alter primary client cards.
/// </summary>
public sealed class GatewayFileSystemApiService(IHttpClientFactory factory, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    internal static readonly TimeSpan ReadinessRecoveryTimeout = TimeSpan.FromSeconds(30);
    private readonly HttpClient _control = factory.CreateClient("OrchestratorApi");
    private readonly HttpClient _streaming = factory.CreateClient("OrchestratorApiStreaming");

    public async Task<GatewayFileSystemListResponse> ListAsync(int tenantId, Guid agentId, string path, CancellationToken cancellationToken = default)
    {
        using var response = await _control.GetAsync(
            $"/api/v2/agents/{tenantId}/{agentId:D}/filesystem?path={Uri.EscapeDataString(path)}",
            cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            using var problem = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            if (problem.RootElement.TryGetProperty("code", out var code) && code.GetString() == "session_unavailable")
            {
                throw new GatewayFileCapabilityUnavailableException();
            }
        }
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<GatewayFileSystemListResponse>(cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The gateway file API returned an empty response.");
    }

    /// <summary>
    /// Only a folder query may be reissued after an interrupted physical
    /// stream. Every attempt uses the API's fresh request/attempt identity.
    /// Mutations and terminal input never enter this recovery path.
    /// </summary>
    public async Task<GatewayFileSystemListResponse> ListWithRecoveryAsync(
        int tenantId, Guid agentId, string path, Func<Task> onReconnecting,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await ListAsync(tenantId, agentId, path, cancellationToken).ConfigureAwait(false);
        }
        catch (GatewayFileCapabilityUnavailableException)
        {
            await onReconnecting().ConfigureAwait(false);
        }

        using var deadline = new CancellationTokenSource(ReadinessRecoveryTimeout, _clock);
        using var recovery = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            while (true)
            {
                if (await IsReadyAsync(tenantId, agentId, recovery.Token).ConfigureAwait(false))
                {
                    try
                    {
                        return await ListAsync(tenantId, agentId, path, recovery.Token).ConfigureAwait(false);
                    }
                    catch (GatewayFileCapabilityUnavailableException)
                    {
                        // A directory snapshot may race another replacement.
                    }
                }
                await Task.Delay(TimeSpan.FromSeconds(1), _clock, recovery.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw new GatewayFileCapabilityUnavailableException();
        }
    }

    private async Task<bool> IsReadyAsync(int tenantId, Guid agentId, CancellationToken cancellationToken)
    {
        using var response = await _control.GetAsync(
            $"/api/v2/client-presence/?tenantId={tenantId}&search={agentId:D}&limit=1", cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var directory = await response.Content.ReadFromJsonAsync<ClientPresenceListDto>(cancellationToken: cancellationToken).ConfigureAwait(false);
        return directory?.Items.Any(client => client.TenantId == tenantId && client.AgentId == agentId &&
            client.Online && client.IsAuthoritative &&
            client.File is { Advertised: true, SessionActive: true, FenceMatchesPresence: true, ReadinessReason: null }) == true;
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

public sealed class GatewayFileCapabilityUnavailableException()
    : InvalidOperationException("The file gateway is unavailable. Browse again after it reconnects.");
