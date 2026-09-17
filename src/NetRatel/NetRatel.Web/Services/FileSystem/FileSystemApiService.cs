using System.Net.Http.Json;
using NetRatel.Shared.Contracts.FileSystem;

namespace NetRatel.Web.Services.FileSystem;

public sealed class FileSystemApiService
{
    private readonly HttpClient _http;

    public FileSystemApiService(IHttpClientFactory factory)
    {
        _http = factory.CreateClient("OrchestratorApi");
    }

    public async Task<FileSystemListResponse> ListDirectoryAsync(string clientIdentity, string path, CancellationToken ct = default)
    {
        var response = await _http.GetAsync(
            $"/api/v1/clients/{Uri.EscapeDataString(clientIdentity)}/filesystem?path={Uri.EscapeDataString(path)}",
            ct).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<FileSystemListResponse>(cancellationToken: ct).ConfigureAwait(false)
            ?? new FileSystemListResponse(string.Empty, path, "failed", "Empty response.", Array.Empty<FileSystemEntryDto>());
    }

    public async Task<FileSystemFileResponse> ReadFileAsync(string clientIdentity, string path, CancellationToken ct = default)
    {
        var response = await _http.GetAsync(
            $"/api/v1/clients/{Uri.EscapeDataString(clientIdentity)}/filesystem/file?path={Uri.EscapeDataString(path)}",
            ct).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<FileSystemFileResponse>(cancellationToken: ct).ConfigureAwait(false)
            ?? new FileSystemFileResponse(string.Empty, path, "failed", "Empty response.", string.Empty, 0, null, "application/octet-stream", "binary", false);
    }

    public async Task<FileSystemWriteResponse> WriteFileAsync(string clientIdentity, FileSystemWriteRequest request, CancellationToken ct = default)
    {
        var response = await _http.PostAsJsonAsync(
            $"/api/v1/clients/{Uri.EscapeDataString(clientIdentity)}/filesystem/file",
            request,
            ct).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<FileSystemWriteResponse>(cancellationToken: ct).ConfigureAwait(false)
            ?? new FileSystemWriteResponse(string.Empty, request.Path, "failed", "Empty response.");
    }
}
