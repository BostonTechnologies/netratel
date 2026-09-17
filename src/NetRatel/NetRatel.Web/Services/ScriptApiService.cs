using System.Collections.Generic;
using System.Net.Http.Json;

namespace NetRatel.Web.Services;

public sealed class ScriptApiService
{
    private readonly HttpClient _http;

    public ScriptApiService(IHttpClientFactory factory)
        => _http = factory.CreateClient("OrchestratorApi");

    public Task<List<ScriptParamView>> GetParamsAsync(ulong id, CancellationToken ct = default)
        => _http.GetFromJsonAsync<List<ScriptParamView>>($"api/scripts/{id}/params", ct)!;

    public Task ParseManifestAsync(ulong id, string? raw = null, CancellationToken ct = default)
        => _http.PostAsJsonAsync($"api/scripts/{id}/parse-manifest", new { manifestRaw = raw }, ct);

    public async Task<ulong> UpsertAsync(object req, CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync("api/scripts/upsert", req, ct);
        resp.EnsureSuccessStatusCode();
        var payload = await resp.Content.ReadFromJsonAsync<Dictionary<string, ulong>>(cancellationToken: ct);
        return payload!["id"];
    }

    public sealed record ScriptParamView(ulong Id, ulong ScriptId, string Name, string Type, bool Required, string? Default, string? Description, string? OptionsJson);
}
