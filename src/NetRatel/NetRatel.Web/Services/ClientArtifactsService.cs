using Microsoft.Extensions.Logging;
using BlazorDownloadFile;
using NetRatel.Web.Components.Dialogs;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using MudBlazor;
using Microsoft.JSInterop;
using NetRatel.Shared;

namespace NetRatel.Web.Services;

public interface IClientArtifactsService
{
    Task<GitHubClientReleasePageModel> GetGitHubReleasesAsync(string channel, int page, bool refresh, CancellationToken ct = default);
    Task<List<ClientReleaseImportModel>> GetReleaseImportsAsync(CancellationToken ct = default) =>
        Task.FromResult(new List<ClientReleaseImportModel>());
    Task<ClientReleaseImportModel> QueueReleaseImportAsync(long releaseId, CancellationToken ct = default) =>
        throw new NotSupportedException();
    Task CancelReleaseImportAsync(Guid id, CancellationToken ct = default) =>
        throw new NotSupportedException();
    Task RetryReleaseImportAsync(Guid id, CancellationToken ct = default) =>
        throw new NotSupportedException();
    Task PublishReleaseImportAsync(Guid id, bool confirmPrerelease, CancellationToken ct = default) =>
        throw new NotSupportedException();
    Task<List<ClientArtifactSummaryModel>> ListAsync(string? rid, CancellationToken ct = default);
    Task<ClientArtifactPageModel> GetArtifactsAsync(
        int page,
        int pageSize,
        string? rid = null,
        string? search = null,
        CancellationToken ct = default);
    Task<List<ClientUpdateReleaseModel>> ListUpdateReleasesAsync(string? rid, CancellationToken ct = default);
    Task<ClientUpdateReleasePageModel> GetUpdateReleasePageAsync(
        int page,
        int pageSize,
        string? search = null,
        string? runtimeId = null,
        string? version = null,
        string? channel = null,
        bool? enabled = null,
        CancellationToken ct = default);
    Task<List<ClientUpdateAttemptModel>> ListUpdateAttemptsAsync(CancellationToken ct = default);
    Task<ClientUpdateHistoryPageModel> GetUpdateHistoryAsync(
        int page,
        int pageSize,
        string? search = null,
        string? status = null,
        int? releaseId = null,
        string? runtimeId = null,
        int? tenantId = null,
        string? version = null,
        CancellationToken ct = default);
    Task<List<AgentClientUpdateStateModel>> ListUpdateStatesAsync(CancellationToken ct = default);
    Task<AgentClientUpdateStatePageModel> GetSuspendedAgentsAsync(
        int page,
        int pageSize,
        string? search = null,
        int? tenantId = null,
        CancellationToken ct = default);
    Task DisableReleaseAsync(Guid releaseId, CancellationToken ct = default);
    Task ResumeAgentAsync(int tenantId, Guid agentId, CancellationToken ct = default);
    Task DownloadAsync(string rid, string version, CancellationToken ct = default);
    Task DownloadClientPackageAsync(ClientPackageDownloadRequest request, CancellationToken ct = default);
    Task DownloadDeploymentScriptAsync(ClientScriptGenerateRequest request, CancellationToken ct = default);
    Task DeleteAsync(string rid, string version, CancellationToken ct = default);
    void OpenUploadDialog(string initialRid, Func<Task> onUploaded);
    Task UploadAsync(string rid, string version, string? notes, IBrowserFile file, CancellationToken ct = default);
}

public class ClientArtifactsService : IClientArtifactsService
{
    private const long MaxUploadBytes = 1024L * 1024L * 1024L; // 1 GB ceiling

    private readonly IHttpClientFactory _clientFactory;
    private readonly IDialogService _dialogService;
    private readonly ISnackbar _snackbar;
    private readonly IBlazorDownloadFileService _downloader;
    private readonly ILogger<ClientArtifactsService> _logger;
    private readonly UploadsApiClient _uploads;
    private readonly IJSRuntime _jsRuntime;

    public ClientArtifactsService(
        IHttpClientFactory clientFactory,
        IDialogService dialogService,
        ISnackbar snackbar,
        IBlazorDownloadFileService downloader,
        ILogger<ClientArtifactsService> logger,
        UploadsApiClient uploads,
        IJSRuntime jsRuntime)
    {
        _clientFactory = clientFactory;
        _dialogService = dialogService;
        _snackbar = snackbar;
        _downloader = downloader;
        _logger = logger;
        _uploads = uploads;
        _jsRuntime = jsRuntime;
    }

    public async Task<GitHubClientReleasePageModel> GetGitHubReleasesAsync(string channel, int page, bool refresh, CancellationToken ct = default)
    {
        var uri = $"/api/v1/client-artifacts/github-releases?channel={Uri.EscapeDataString(channel)}&page={page}&refresh={refresh.ToString().ToLowerInvariant()}";
        return await _uploads.Http.GetFromJsonAsync<GitHubClientReleasePageModel>(uri, ct)
            ?? throw new HttpRequestException("The GitHub releases response was empty.");
    }

    public async Task<List<ClientReleaseImportModel>> GetReleaseImportsAsync(CancellationToken ct = default) =>
        await _uploads.Http.GetFromJsonAsync<List<ClientReleaseImportModel>>("/api/v1/client-artifacts/imports", ct) ?? [];

    public async Task<ClientReleaseImportModel> QueueReleaseImportAsync(long releaseId, CancellationToken ct = default)
    {
        using var response = await _uploads.Http.PostAsync($"/api/v1/client-artifacts/github-releases/{releaseId}/imports", null, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ClientReleaseImportModel>(cancellationToken: ct)
            ?? throw new HttpRequestException("The import response was empty.");
    }

    public async Task CancelReleaseImportAsync(Guid id, CancellationToken ct = default)
    {
        using var response = await _uploads.Http.PostAsync($"/api/v1/client-artifacts/imports/{id}/cancel", null, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task RetryReleaseImportAsync(Guid id, CancellationToken ct = default)
    {
        using var response = await _uploads.Http.PostAsync($"/api/v1/client-artifacts/imports/{id}/retry", null, ct);
        response.EnsureSuccessStatusCode();
    }

    public async Task PublishReleaseImportAsync(Guid id, bool confirmPrerelease, CancellationToken ct = default)
    {
        using var response = await _uploads.Http.PostAsJsonAsync(
            $"/api/v1/client-artifacts/imports/{id}/publish",
            new { ConfirmPrerelease = confirmPrerelease }, ct);
        response.EnsureSuccessStatusCode();
    }


    public async Task<List<ClientArtifactSummaryModel>> ListAsync(string? rid, CancellationToken ct = default)
    {
        var client = _uploads.Http;
        var query = new List<string> { "take=200" };
        if (!string.IsNullOrWhiteSpace(rid))
        {
            query.Add($"rid={Uri.EscapeDataString(rid)}");
        }

        var uri = "/api/v1/client-artifacts" + (query.Count > 0 ? $"?{string.Join('&', query)}" : string.Empty);
        try
        {
            var dto = await client.GetFromJsonAsync<ClientArtifactListResult>(uri, ct) ?? new ClientArtifactListResult();
            return dto.Items.ToList();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Failed to list client artifacts");
            _snackbar.Add("Could not load client artifacts.", Severity.Error);
            return new List<ClientArtifactSummaryModel>();
        }
    }

    public async Task<ClientArtifactPageModel> GetArtifactsAsync(
        int page,
        int pageSize,
        string? rid = null,
        string? search = null,
        CancellationToken ct = default)
    {
        var query = new List<string>
        {
            $"skip={page * pageSize}",
            $"take={pageSize}"
        };
        if (!string.IsNullOrWhiteSpace(rid)) query.Add($"rid={Uri.EscapeDataString(rid)}");
        if (!string.IsNullOrWhiteSpace(search)) query.Add($"search={Uri.EscapeDataString(search)}");

        try
        {
            var result = await _uploads.Http.GetFromJsonAsync<ClientArtifactPageModel>(
                $"/api/v1/client-artifacts?{string.Join('&', query)}", ct);
            return result ?? new ClientArtifactPageModel { Page = page, PageSize = pageSize };
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Failed to load paged client artifacts");
            _snackbar.Add("Could not load client artifacts.", Severity.Error);
            return new ClientArtifactPageModel { Page = page, PageSize = pageSize };
        }
    }

    public async Task<List<ClientUpdateReleaseModel>> ListUpdateReleasesAsync(string? rid, CancellationToken ct = default)
    {
        var client = _uploads.Http;
        var uri = "/api/v1/client-updates/releases";
        if (!string.IsNullOrWhiteSpace(rid))
        {
            uri += $"?runtimeId={Uri.EscapeDataString(rid)}";
        }

        try
        {
            return await client.GetFromJsonAsync<List<ClientUpdateReleaseModel>>(uri, ct) ?? new List<ClientUpdateReleaseModel>();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Failed to list client update releases");
            _snackbar.Add("Could not load client update releases.", Severity.Error);
            return new List<ClientUpdateReleaseModel>();
        }
    }

    public async Task<ClientUpdateReleasePageModel> GetUpdateReleasePageAsync(
        int page,
        int pageSize,
        string? search = null,
        string? runtimeId = null,
        string? version = null,
        string? channel = null,
        bool? enabled = null,
        CancellationToken ct = default)
    {
        var query = new List<string> { $"page={page}", $"pageSize={pageSize}" };
        if (!string.IsNullOrWhiteSpace(search)) query.Add($"search={Uri.EscapeDataString(search)}");
        if (!string.IsNullOrWhiteSpace(runtimeId)) query.Add($"runtimeId={Uri.EscapeDataString(runtimeId)}");
        if (!string.IsNullOrWhiteSpace(version)) query.Add($"version={Uri.EscapeDataString(version)}");
        if (!string.IsNullOrWhiteSpace(channel)) query.Add($"channel={Uri.EscapeDataString(channel)}");
        if (enabled.HasValue) query.Add($"enabled={enabled.Value.ToString().ToLowerInvariant()}");

        try
        {
            return await _uploads.Http.GetFromJsonAsync<ClientUpdateReleasePageModel>(
                       $"/api/v1/client-updates/management/releases?{string.Join('&', query)}", ct)
                   ?? new ClientUpdateReleasePageModel { Page = page, PageSize = pageSize };
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Failed to load paged client update releases");
            _snackbar.Add("Could not load update releases.", Severity.Error);
            return new ClientUpdateReleasePageModel { Page = page, PageSize = pageSize };
        }
    }

    public async Task<List<ClientUpdateAttemptModel>> ListUpdateAttemptsAsync(CancellationToken ct = default)
    {
        try
        {
            return await _uploads.Http.GetFromJsonAsync<List<ClientUpdateAttemptModel>>(
                "/api/v1/client-updates/attempts", ct) ?? [];
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Failed to list client update attempts");
            return [];
        }
    }

    public async Task<ClientUpdateHistoryPageModel> GetUpdateHistoryAsync(
        int page,
        int pageSize,
        string? search = null,
        string? status = null,
        int? releaseId = null,
        string? runtimeId = null,
        int? tenantId = null,
        string? version = null,
        CancellationToken ct = default)
    {
        var query = new List<string>
        {
            $"page={page}",
            $"pageSize={pageSize}"
        };
        if (!string.IsNullOrWhiteSpace(search)) query.Add($"search={Uri.EscapeDataString(search)}");
        if (!string.IsNullOrWhiteSpace(status)) query.Add($"status={Uri.EscapeDataString(status)}");
        if (releaseId.HasValue) query.Add($"releaseId={releaseId.Value}");
        if (!string.IsNullOrWhiteSpace(runtimeId)) query.Add($"runtimeId={Uri.EscapeDataString(runtimeId)}");
        if (tenantId.HasValue) query.Add($"tenantId={tenantId.Value}");
        if (!string.IsNullOrWhiteSpace(version)) query.Add($"version={Uri.EscapeDataString(version)}");

        try
        {
            return await _uploads.Http.GetFromJsonAsync<ClientUpdateHistoryPageModel>(
                       $"/api/v1/client-updates/history?{string.Join('&', query)}", ct)
                   ?? new ClientUpdateHistoryPageModel();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Failed to load paged client update history");
            _snackbar.Add("Could not load update history.", Severity.Error);
            return new ClientUpdateHistoryPageModel { Page = page, PageSize = pageSize };
        }
    }

    public async Task<List<AgentClientUpdateStateModel>> ListUpdateStatesAsync(CancellationToken ct = default)
    {
        try
        {
            return await _uploads.Http.GetFromJsonAsync<List<AgentClientUpdateStateModel>>(
                "/api/v1/client-updates/agent-states", ct) ?? [];
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Failed to list agent update states");
            return [];
        }
    }

    public async Task<AgentClientUpdateStatePageModel> GetSuspendedAgentsAsync(
        int page,
        int pageSize,
        string? search = null,
        int? tenantId = null,
        CancellationToken ct = default)
    {
        var query = new List<string> { $"page={page}", $"pageSize={pageSize}" };
        if (!string.IsNullOrWhiteSpace(search)) query.Add($"search={Uri.EscapeDataString(search)}");
        if (tenantId.HasValue) query.Add($"tenantId={tenantId.Value}");

        try
        {
            return await _uploads.Http.GetFromJsonAsync<AgentClientUpdateStatePageModel>(
                       $"/api/v1/client-updates/management/suspended?{string.Join('&', query)}", ct)
                   ?? new AgentClientUpdateStatePageModel { Page = page, PageSize = pageSize };
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Failed to load paged suspended update agents");
            _snackbar.Add("Could not load suspended update agents.", Severity.Error);
            return new AgentClientUpdateStatePageModel { Page = page, PageSize = pageSize };
        }
    }

    public async Task DisableReleaseAsync(Guid releaseId, CancellationToken ct = default)
    {
        using var response = await _uploads.Http.PostAsync(
            $"/api/v1/client-updates/releases/{releaseId:D}/disable", null, ct);
        response.EnsureSuccessStatusCode();
        _snackbar.Add("Update release disabled.", Severity.Success);
    }

    public async Task ResumeAgentAsync(int tenantId, Guid agentId, CancellationToken ct = default)
    {
        using var response = await _uploads.Http.PostAsync(
            $"/api/v1/client-updates/tenants/{tenantId}/agents/{agentId:D}/resume", null, ct);
        response.EnsureSuccessStatusCode();
        _snackbar.Add("Agent auto-update resumed for future releases.", Severity.Success);
    }

    public async Task DownloadAsync(string rid, string version, CancellationToken ct = default)
    {
        try
        {
            var url = $"/clients/mgmt/downloads/artifact?rid={Uri.EscapeDataString(rid)}&version={Uri.EscapeDataString(version)}";
            await StartBrowserDownloadAsync(url, ct);
            _snackbar.Add($"Download started for {rid} {version}.", Severity.Success);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Error downloading artifact {Rid} {Version}", rid, version);
            _snackbar.Add("Could not start artifact download.", Severity.Error);
        }
    }

    public async Task DownloadClientPackageAsync(ClientPackageDownloadRequest request, CancellationToken ct = default)
    {
        var url = BuildPackageDownloadUrl(request);
        await StartBrowserDownloadAsync(url, ct);
        _snackbar.Add($"Download started for tenant {request.TenantId}.", Severity.Success);
    }

    public async Task DownloadDeploymentScriptAsync(ClientScriptGenerateRequest request, CancellationToken ct = default)
    {
        var client = _uploads.Http;
        using var response = await client.PostAsJsonAsync("/api/v1/client/script", request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var problem = await TryReadProblemAsync(response, ct);
            _snackbar.Add(problem ?? $"Script generation failed with status {(int)response.StatusCode}.", Severity.Error);
            return;
        }

        var fileName = response.Content.Headers.ContentDisposition?.FileNameStar
                       ?? response.Content.Headers.ContentDisposition?.FileName
                       ?? $"netratel-install-{request.TenantId}.ps1";
        var contentType = response.Content.Headers.ContentType?.MediaType ?? "text/plain";
        var data = await response.Content.ReadAsByteArrayAsync(ct);
        await _downloader.DownloadFile(fileName, data, contentType);
        await TryShowEnrollmentCodeAsync(response, ct);
        _snackbar.Add($"Deployment script generated for tenant {request.TenantId}.", Severity.Success);
    }

    private async Task StartBrowserDownloadAsync(string url, CancellationToken ct)
    {
        try
        {
            await _jsRuntime.InvokeVoidAsync("netratelDownloads.start", ct, url);
        }
        catch (JSException ex)
        {
            _logger.LogWarning(ex, "netratelDownloads.start was unavailable; falling back to window.open for {Url}", url);
            await _jsRuntime.InvokeVoidAsync("open", ct, url, "_blank");
        }
    }

    private static string BuildPackageDownloadUrl(ClientPackageDownloadRequest request)
    {
        var query = new Dictionary<string, string?>
        {
            ["tenantId"] = request.TenantId.ToString(),
            ["environment"] = request.Environment.ToString(),
            ["runtimeId"] = request.RuntimeId,
            ["version"] = request.Version,
            ["injectEnrollment"] = request.InjectEnrollment.ToString().ToLowerInvariant(),
            ["validForMinutes"] = request.ValidForMinutes?.ToString(),
            ["maxUses"] = request.MaxUses?.ToString()
        };

        var parts = query
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
            .Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value!)}");

        return $"/clients/mgmt/downloads/package?{string.Join('&', parts)}";
    }

    public async Task DeleteAsync(string rid, string version, CancellationToken ct = default)
    {
        var confirm = await _dialogService.ShowMessageBoxAsync(
            "Delete Artifact",
            $"Delete artifact {rid} / {version}?",
            yesText: "Delete",
            cancelText: "Cancel",
            options: new DialogOptions { CloseOnEscapeKey = true });

        if (confirm != true)
        {
            return;
        }

        var client = _uploads.Http;
        var url = $"/api/v1/client-artifacts/{Uri.EscapeDataString(rid)}/{Uri.EscapeDataString(version)}";

        try
        {
            using var response = await client.DeleteAsync(url, ct);
            if (response.IsSuccessStatusCode)
            {
                _snackbar.Add($"Deleted {rid} / {version}.", Severity.Success);
                return;
            }

            var problem = await TryReadProblemAsync(response, ct);
            _snackbar.Add(problem ?? $"Delete failed with status {(int)response.StatusCode}.", Severity.Error);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Failed to delete artifact {Rid} {Version}", rid, version);
            _snackbar.Add("Network error while deleting artifact.", Severity.Error);
        }
    }

    public void OpenUploadDialog(string initialRid, Func<Task> onUploaded)
    {
        var parameters = new DialogParameters
        {
            [nameof(ClientArtifactUploadDialog.InitialRid)] = string.IsNullOrWhiteSpace(initialRid) ? "win-x64" : initialRid,
            [nameof(ClientArtifactUploadDialog.OnUploaded)] = EventCallback.Factory.Create(this, onUploaded)
        };

        var options = new DialogOptions
        {
            CloseButton = true,
            CloseOnEscapeKey = true,
            MaxWidth = MaxWidth.Medium,
            FullWidth = true
        };

        _dialogService.ShowAsync<ClientArtifactUploadDialog>("Upload Artifact", parameters, options);
    }

    public async Task UploadAsync(string rid, string version, string? notes, IBrowserFile file, CancellationToken ct = default)
    {
        var client = _uploads.Http;
        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(rid), "rid");
        content.Add(new StringContent(version), "version");
        if (!string.IsNullOrWhiteSpace(notes))
        {
            content.Add(new StringContent(notes), "notes");
        }

        var stream = file.OpenReadStream(MaxUploadBytes);
        var streamContent = new StreamContent(stream);
        streamContent.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data")
        {
            Name = "file",
            FileName = file.Name
        };
        streamContent.Headers.ContentType = new MediaTypeHeaderValue(file.ContentType ?? "application/octet-stream");
        content.Add(streamContent, "file", file.Name);

        using var response = await client.PostAsync("/api/v1/client-artifacts/upload", content);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            var conflict = await TryReadProblemAsync(response, ct) ?? "Artifact already exists.";
            throw new InvalidOperationException(conflict);
        }

        if (!response.IsSuccessStatusCode)
        {
            var error = await TryReadProblemAsync(response, ct) ?? $"Upload failed with status {(int)response.StatusCode}.";
            throw new InvalidOperationException(error);
        }

        _snackbar.Add($"Uploaded {rid} / {version}.", Severity.Success);
    }

    private async Task<string?> TryReadProblemAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var problem = await response.Content.ReadFromJsonAsync<ProblemDetailsModel>(cancellationToken: ct);
            if (problem is null)
            {
                return null;
            }

            return string.IsNullOrWhiteSpace(problem.Detail) ? problem.Title : problem.Detail;
        }
        catch
        {
            return null;
        }
    }

    private async Task TryShowEnrollmentCodeAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.Headers.TryGetValues("X-Enrollment-Code", out var codeValues))
        {
            return;
        }

        var code = codeValues.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(code))
        {
            return;
        }

        response.Headers.TryGetValues("X-Enrollment-Expires", out var expiryValues);
        var expiryRaw = expiryValues?.FirstOrDefault();
        var hasExpiry = DateTimeOffset.TryParse(expiryRaw, out var expiryUtc);

        try
        {
            await _jsRuntime.InvokeVoidAsync("navigator.clipboard.writeText", ct, code);
            _snackbar.Add(
                hasExpiry
                    ? $"Enrollment code issued and copied: {code} (expires {expiryUtc.ToLocalTime():g})"
                    : $"Enrollment code issued and copied: {code}",
                Severity.Success);
        }
        catch
        {
            _snackbar.Add(
                hasExpiry
                    ? $"Enrollment code issued: {code} (expires {expiryUtc.ToLocalTime():g})"
                    : $"Enrollment code issued: {code}",
                Severity.Info);
        }
    }

    private sealed class ClientArtifactListResult
    {
        public int Total { get; set; }
        public List<ClientArtifactSummaryModel> Items { get; set; } = new();
    }

    private sealed class ProblemDetailsModel
    {
        public string? Title { get; set; }
        public string? Detail { get; set; }
        public int? Status { get; set; }
    }
}

public sealed class ClientArtifactSummaryModel
{
    public string Rid { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long Size { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public DateTimeOffset UploadedAt { get; set; }
    public string? Notes { get; set; }
}

public sealed class ClientArtifactPageModel
{
    public List<ClientArtifactSummaryModel> Items { get; set; } = [];
    public int Total { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; } = 10;
}

public sealed class ClientUpdateReleaseModel
{
    public int Id { get; set; }
    public int? TenantId { get; set; }
    public ClientEnvironment? Environment { get; set; }
    public string RuntimeId { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string ArtifactUrl { get; set; } = string.Empty;
    public string Sha256 { get; set; } = string.Empty;
    public bool Enabled { get; set; }
    public DateTimeOffset PublishedAt { get; set; }
    public string PublishedBy { get; set; } = string.Empty;
    public Guid ReleaseId { get; set; }
    public long Revision { get; set; }
    public long SizeBytes { get; set; }
    public string Channel { get; set; } = string.Empty;
}

public sealed class ClientUpdateReleasePageModel
{
    public List<ClientUpdateReleaseModel> Items { get; set; } = [];
    public int Total { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; } = 10;
}

public sealed class ClientUpdateAttemptModel
{
    public Guid AttemptId { get; set; }
    public int TenantId { get; set; }
    public string? TenantName { get; set; }
    public Guid AgentId { get; set; }
    public string? AgentName { get; set; }
    public string RuntimeId { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? FailureCode { get; set; }
    public string? Message { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class ClientUpdateHistoryPageModel
{
    public List<ClientUpdateHistoryItemModel> Items { get; set; } = [];
    public int Total { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; } = 10;
}

public sealed class ClientUpdateHistoryItemModel
{
    public Guid AttemptId { get; set; }
    public int ReleaseId { get; set; }
    public string RuntimeId { get; set; } = string.Empty;
    public string TargetVersion { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? FailureCode { get; set; }
    public string? Message { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public int TenantId { get; set; }
    public string? TenantName { get; set; }
    public Guid AgentId { get; set; }
    public string ClientDisplayName { get; set; } = string.Empty;
    public string? ClientHostName { get; set; }
}

public sealed class AgentClientUpdateStateModel
{
    public Guid AgentId { get; set; }
    public int TenantId { get; set; }
    public string? TenantName { get; set; }
    public string? AgentName { get; set; }
    public string? ClientDisplayName { get; set; }
    public string? ClientHostName { get; set; }
    public DateTimeOffset? SuspendedAtUtc { get; set; }
    public string? SuspensionReason { get; set; }
    public Guid? SuppressedReleaseId { get; set; }
    public long PolicyRevision { get; set; }
}

public sealed class AgentClientUpdateStatePageModel
{
    public List<AgentClientUpdateStateModel> Items { get; set; } = [];
    public int Total { get; set; }
    public int Page { get; set; }
    public int PageSize { get; set; } = 10;
}

public sealed class ClientPackageDownloadRequest
{
    public int TenantId { get; set; }
    public int Environment { get; set; }
    public string RuntimeId { get; set; } = "win-x64";
    public string Version { get; set; } = "latest";
    public bool InjectEnrollment { get; set; }
    public int? ValidForMinutes { get; set; }
    public int? MaxUses { get; set; }
    public Guid? EnrollmentCodeId { get; set; }
}

public sealed class ClientScriptGenerateRequest
{
    public int TenantId { get; set; }
    public string RuntimeId { get; set; } = "win-x64";
    public string? ArtifactVersion { get; set; }
    public int ValidForMinutes { get; set; } = 60;
    public int? MaxUses { get; set; } = 1;
    public bool InstallAsService { get; set; } = true;
    public bool SilentInstall { get; set; } = true;
}

public sealed class GitHubClientReleasePageModel
{
    public List<GitHubClientReleaseModel> Items { get; set; } = [];
    public int Page { get; set; }
    public bool HasMore { get; set; }
    public bool ScanLimitReached { get; set; }
    public DateTimeOffset RefreshedAtUtc { get; set; }
    public string? Warning { get; set; }
}

public sealed class GitHubClientReleaseModel
{
    public long Id { get; set; }
    public string Tag { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public DateTimeOffset PublishedAtUtc { get; set; }
    public bool IsPrerelease { get; set; }
    public string DetailsUrl { get; set; } = string.Empty;
    public List<GitHubClientAssetModel> ClientAssets { get; set; } = [];
    public long TotalClientBytes { get; set; }
    public string PublicationState { get; set; } = string.Empty;
}

public sealed class GitHubClientAssetModel
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string RuntimeId { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string? Sha256Digest { get; set; }
}

public sealed class ClientReleaseImportModel
{
    public Guid Id { get; set; }
    public long GitHubReleaseId { get; set; }
    public string Tag { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public int State { get; set; }
    public long? TotalBytes { get; set; }
    public long DownloadedBytes { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public DateTimeOffset? PublishedAtUtc { get; set; }
    public List<ClientReleaseImportAssetModel> Assets { get; set; } = [];
}

public sealed class ClientReleaseImportAssetModel
{
    public string RuntimeId { get; set; } = string.Empty;
    public string SourceName { get; set; } = string.Empty;
    public int State { get; set; }
    public long SourceSizeBytes { get; set; }
    public long DownloadedBytes { get; set; }
    public string? SourceSha256 { get; set; }
    public string? LocalSha256 { get; set; }
    public string? Error { get; set; }
}
