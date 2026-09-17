using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;
using NetRatel.Shared;
using NetRatel.Shared.Contracts;
using NetRatel.Shared.Contracts.Requests;

namespace NetRatel.Web.Services.Clients;

public class ClientApiService : IClientApiService
{
    private readonly IHttpClientFactory _clientFactory;
    private readonly ITokenProvider _tokenProvider;
    private readonly ILogger<ClientApiService> _logger;
    private static readonly JsonSerializerOptions _json =
        new() { PropertyNameCaseInsensitive = true };

    public ClientApiService(
        IHttpClientFactory clientFactory,
        ITokenProvider tokenProvider,
        ILogger<ClientApiService> logger)
    {
        _clientFactory = clientFactory;
        _tokenProvider = tokenProvider;
        _logger = logger;
    }

    private HttpClient CreateClient() => _clientFactory.CreateClient("OrchestratorApi");

    public async Task<IReadOnlyList<ClientDto>> GetClientsAsync(CancellationToken cancellationToken = default)
    {
        var client = CreateClient();
        var result = await client.GetFromJsonAsync<List<ClientDto>>("/api/v1/clients", cancellationToken);
        return result ?? new List<ClientDto>();
    }

    public async Task<PagedResult<ClientDto>> GetClientsAsync(
        int tenantId,
        ClientEnvironment environment,
        string? search = null,
        int page = 1,
        int pageSize = 25,
        CancellationToken cancellationToken = default)
    {
        var normalizedPage = Math.Max(page, 1);
        var normalizedSize = Math.Max(pageSize, 1);

        var query = new Dictionary<string, string?>
        {
            ["tenantId"] = tenantId.ToString(),
            ["environment"] = ((int)environment).ToString(),
            ["search"] = string.IsNullOrWhiteSpace(search) ? null : search,
            ["page"] = normalizedPage.ToString(),
            ["pageSize"] = normalizedSize.ToString()
        };

        var url = QueryHelpers.AddQueryString(
            "/api/v1/clients/search",
            query.Where(kv => kv.Value is not null).ToDictionary(kv => kv.Key, kv => kv.Value));

        _logger.LogDebug("GET {Url}", url);

        try
        {
            var client = CreateClient();
            var result = await client.GetFromJsonAsync<PagedResult<ClientDto>>(url, _json, cancellationToken)
                         ?? new PagedResult<ClientDto>(Array.Empty<ClientDto>(), normalizedPage, normalizedSize, 0);
            return result;
        }
        catch (OperationCanceledException)
        {
            return new PagedResult<ClientDto>(Array.Empty<ClientDto>(), normalizedPage, normalizedSize, 0);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get clients for tenant {TenantId} env {Environment}", tenantId, environment);
            return new PagedResult<ClientDto>(Array.Empty<ClientDto>(), normalizedPage, normalizedSize, 0);
        }
    }

    public async Task<ClientDto?> GetClientAsync(string identity, CancellationToken cancellationToken = default)
    {
        var client = CreateClient();
        var id = Uri.EscapeDataString(identity);
        try
        {
            return await client.GetFromJsonAsync<ClientDto>($"/api/v1/clients/{id}", _json, cancellationToken);
        }
        catch
        {
            return null;
        }
    }

    public async Task UpdateClientAsync(string identity, ClientUpdateRequest request, CancellationToken cancellationToken = default)
    {
        var client = CreateClient();
        var id = Uri.EscapeDataString(identity);
        var response = await client.PutAsJsonAsync($"/api/v1/clients/{id}", request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public Task SetRundeckLogStreamingAsync(string identity, bool enable, CancellationToken cancellationToken = default)
        => UpdateClientAsync(identity, new ClientUpdateRequest(null, null, null, null, enable, null), cancellationToken);

    public Task SetClientEnabledAsync(string identity, bool enabled, CancellationToken cancellationToken = default)
        => UpdateClientAsync(identity, new ClientUpdateRequest(null, null, null, null, null, enabled), cancellationToken);

    public async Task DeleteClientAsync(string identity, CancellationToken cancellationToken = default)
    {
        var client = CreateClient();
        var requestUri = $"/api/v1/clients/{Uri.EscapeDataString(identity)}";

        async Task<HttpResponseMessage> SendAsync(CancellationToken ct)
        {
            using var request = new HttpRequestMessage(HttpMethod.Delete, requestUri);
            var token = await _tokenProvider.GetBearerAsync(ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(token))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }

        HttpResponseMessage? response = null;
        try
        {
            response = await SendAsync(cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                response.Dispose();
                response = await SendAsync(cancellationToken).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.Unauthorized)
                    throw new HttpRequestException("Unauthorized (401): invalid or missing access token for DELETE /api/v1/clients/{identity}");
            }

            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"DELETE failed: {(int)response.StatusCode} {response.ReasonPhrase}");
        }
        finally
        {
            response?.Dispose();
        }
    }

    public async Task SetLogStreamingAsync(string identity, bool enable, CancellationToken cancellationToken = default)
    {
        var client = CreateClient();
        var id = Uri.EscapeDataString(identity);
        var response = await client.PutAsJsonAsync($"/api/v1/clients/{id}/logs", new ClientLogStreamRequest(enable), cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<IReadOnlyList<ClientLogEntryDto>> GetClientLogSnapshotAsync(
        string identity,
        int take = 500,
        CancellationToken cancellationToken = default)
    {
        var client = CreateClient();
        var id = Uri.EscapeDataString(identity);
        var url = QueryHelpers.AddQueryString(
            $"/api/v1/clients/{id}/logs",
            new Dictionary<string, string?>
            {
                ["snapshot"] = "true",
                ["take"] = Math.Clamp(take, 1, 1000).ToString()
            });
        var result = await client.GetFromJsonAsync<List<ClientLogEntryDto>>(url, _json, cancellationToken);
        return result ?? new List<ClientLogEntryDto>();
    }

    public async Task InitiatePingAsync(string identity, CancellationToken cancellationToken = default)
    {
        var client = CreateClient();
        var id = Uri.EscapeDataString(identity);
        var response = await client.PostAsync($"/api/v1/clients/{id}/ping", content: null, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    // Convenience: set environment with a single call (flagged enum)
    public Task SetEnvironmentAsync(string identity, ClientEnvironment environment, CancellationToken cancellationToken = default)
        => UpdateClientAsync(identity, new ClientUpdateRequest(null, null, environment, null, null, null), cancellationToken);

}
