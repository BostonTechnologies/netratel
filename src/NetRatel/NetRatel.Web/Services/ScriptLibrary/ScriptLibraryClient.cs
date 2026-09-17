using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;

using NetRatel.Shared.Contracts.Scripts;

namespace NetRatel.Web.Services.ScriptLibrary;

public class ScriptLibraryClient
{
    private readonly IHttpClientFactory _http;

    public ScriptLibraryClient(IHttpClientFactory http) => _http = http;

    private HttpClient Api => _http.CreateClient("OrchestratorApi");

    public async Task<List<ScriptDto>> ListAsync(CancellationToken ct)
    {
        using var resp = await Api.GetAsync("/api/v1/script-library", ct);
        await EnsureJsonAsync(resp);
        return (await resp.Content.ReadFromJsonAsync<List<ScriptDto>>(cancellationToken: ct)) ?? new();
    }

    public async Task<ScriptDto?> GetAsync(ulong id, CancellationToken ct)
    {
        using var resp = await Api.GetAsync($"/api/v1/script-library/{id}", ct);
        await EnsureJsonAsync(resp);
        return await resp.Content.ReadFromJsonAsync<ScriptDto>(cancellationToken: ct);
    }

    public async Task CreateAsync(CreateScriptRequest req, CancellationToken ct)
    {
        using var resp = await Api.PostAsJsonAsync("/api/v1/script-library", req, ct);
        await EnsureSuccessJsonOrNoContentAsync(resp);
    }

    public async Task<long> UpdateAsync(ulong id, UpdateScriptRequest req, CancellationToken ct)
    {
        using var resp = await Api.PutAsJsonAsync($"/api/v1/script-library/{id}", req, ct);
        await EnsureSuccessJsonOrNoContentAsync(resp);
        var result = await resp.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(cancellationToken: ct);
        return result.GetProperty("sourceRevision").GetInt64();
    }

    public async Task DeleteAsync(ulong id, long expectedSourceRevision, CancellationToken ct)
    {
        using var resp = await Api.DeleteAsync($"/api/v1/script-library/{id}?expectedSourceRevision={expectedSourceRevision}", ct);
        await EnsureSuccessJsonOrNoContentAsync(resp);
    }

    private static async Task EnsureJsonAsync(HttpResponseMessage resp)
    {
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"API error {(int)resp.StatusCode} {resp.ReasonPhrase}");

        var ct = resp.Content.Headers.ContentType?.MediaType;
        if (ct is not null && !ct.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            var peek = await resp.Content.ReadAsStringAsync();
            throw new InvalidOperationException($"Unexpected content-type '{ct}'. First chars: '{peek.AsSpan(0, Math.Min(80, peek.Length))}'");
        }
    }

    private static async Task EnsureSuccessJsonOrNoContentAsync(HttpResponseMessage resp)
    {
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync();
            throw new InvalidOperationException($"API error {(int)resp.StatusCode}: {body}");
        }
    }
}
