using System.Net.Http.Json;
using System.IO;
using NetRatel.Shared;

namespace NetRatel.Web.Services;

public interface IWebClientDownloadService
{
    Task<Stream> DownloadAsync(int tenantId, ClientEnvironment env, string rid, bool injectEnrollment, int? validForMinutes, int? maxUses, CancellationToken ct = default);
}

public class WebClientDownloadService(HttpClient http) : IWebClientDownloadService
{
    private readonly HttpClient _http = http;

    public async Task<Stream> DownloadAsync(int tenantId, ClientEnvironment env, string rid, bool injectEnrollment, int? validForMinutes, int? maxUses, CancellationToken ct = default)
    {
        var req = new
        {
            tenantId,
            environment = (int)env,
            runtimeId = rid,
            injectEnrollment,
            validForMinutes,
            maxUses
        };
        var resp = await _http.PostAsJsonAsync("/api/v1/client/download", req, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStreamAsync(ct);
    }
}
