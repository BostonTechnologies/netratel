using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using NetRatel.Web.Services;
using NetRatel.Web.Services.FileSystem;
using System.Buffers;
using System.Diagnostics;
using System.Text.Json;

namespace NetRatel.Web.Controllers;

[Authorize(Roles = "Operator")]
[Route("file-browser/transfers")]
public sealed class FileBrowserTransfersController(
    IUploadsApiClient uploads,
    IFileBrowserDownloadTransferRegistry downloads,
    ILogger<FileBrowserTransfersController> logger) : Controller
{
    [HttpGet("download")]
    public async Task<IActionResult> Download(int tenantId, Guid agentId, string path, Guid transferId, long? expectedBytes, bool inlinePreview = false, CancellationToken ct = default)
    {
        if (!IsValidRequest(tenantId, agentId, path) || transferId == Guid.Empty || !TryGetOperatorId(User, out var operatorId) ||
            (inlinePreview && !FileBrowserContentClassifier.TryGetPreviewImageContentType(FileName(path), out _)))
        {
            return BadRequest();
        }

        FileBrowserDownloadTransferLease lease;
        try
        {
            lease = downloads.Begin(operatorId, transferId, FileName(path), expectedBytes);
        }
        catch (InvalidOperationException exception)
        {
            return Conflict(new { code = "download_limit", detail = exception.Message });
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease.CancellationToken);
        var transferToken = linked.Token;
        var stopwatch = Stopwatch.StartNew();
        using var response = await uploads.Http.GetAsync(ApiPath(tenantId, agentId, path), HttpCompletionOption.ResponseHeadersRead, transferToken);
        if (!response.IsSuccessStatusCode)
        {
            FileBrowserTransferTelemetry.Record("download", "failed", 0, stopwatch.Elapsed);
            downloads.Fail(transferId, $"http_{(int)response.StatusCode}", 0);
            return StatusCode((int)response.StatusCode);
        }

        downloads.Streaming(transferId);
        Response.ContentType = inlinePreview && FileBrowserContentClassifier.TryGetPreviewImageContentType(FileName(path), out var imageContentType)
            ? imageContentType
            : response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
        Response.Headers.ContentDisposition = $"{(inlinePreview ? "inline" : "attachment")}; filename*=UTF-8''{Uri.EscapeDataString(FileName(path))}";
        long bytes = 0;
        var buffer = ArrayPool<byte>.Shared.Rent(80 * 1024);
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(transferToken);
            while (true)
            {
                var read = await stream.ReadAsync(buffer, transferToken);
                if (read == 0)
                {
                    break;
                }

                await Response.Body.WriteAsync(buffer.AsMemory(0, read), transferToken);
                bytes += read;
                downloads.Progress(transferId, bytes);
            }

            FileBrowserTransferTelemetry.Record("download", "completed", bytes, stopwatch.Elapsed);
            downloads.Complete(transferId, bytes);
            logger.LogInformation("Completed browser-native remote file download for tenant {TenantId}, agent {AgentId}; bytes {Bytes}; durationMs {DurationMs}", tenantId, agentId, bytes, stopwatch.Elapsed.TotalMilliseconds);
            return new EmptyResult();
        }
        catch (OperationCanceledException) when (transferToken.IsCancellationRequested)
        {
            FileBrowserTransferTelemetry.Record("download", "cancelled", bytes, stopwatch.Elapsed);
            downloads.Cancelled(transferId, bytes);
            logger.LogInformation("Cancelled browser-native remote file download for tenant {TenantId}, agent {AgentId}; bytes {Bytes}; durationMs {DurationMs}", tenantId, agentId, bytes, stopwatch.Elapsed.TotalMilliseconds);
            throw;
        }
        catch
        {
            FileBrowserTransferTelemetry.Record("download", "failed", bytes, stopwatch.Elapsed);
            downloads.Fail(transferId, "stream_failed", bytes);
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    [HttpGet("download/{transferId:guid}/status")]
    public IActionResult DownloadStatus(Guid transferId)
    {
        return TryGetOperatorId(User, out var operatorId) && downloads.TryGet(operatorId, transferId, out var status)
            ? Ok(status)
            : NotFound();
    }

    [HttpPost("download/{transferId:guid}/cancel")]
    public IActionResult CancelDownload(Guid transferId)
    {
        return TryGetOperatorId(User, out var operatorId) && downloads.TryCancel(operatorId, transferId)
            ? Accepted()
            : NotFound();
    }

    [HttpPut("upload")]
    [DisableRequestSizeLimit]
    public async Task<IActionResult> Upload(int tenantId, Guid agentId, string path, CancellationToken ct)
    {
        if (!IsValidRequest(tenantId, agentId, path))
        {
            return BadRequest();
        }

        var stopwatch = Stopwatch.StartNew();
        var bytes = Request.ContentLength ?? 0;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, ApiPath(tenantId, agentId, path))
            {
                Content = new StreamContent(Request.Body)
            };
            using var response = await uploads.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            var retryable = await IsRetryableUploadFailureAsync(response, ct).ConfigureAwait(false);
            var outcome = response.IsSuccessStatusCode ? "completed" : "failed";
            FileBrowserTransferTelemetry.Record("upload", outcome, bytes, stopwatch.Elapsed);
            logger.LogInformation("Completed browser-native remote file upload for tenant {TenantId}, agent {AgentId}; outcome {Outcome}; bytes {Bytes}; durationMs {DurationMs}; statusCode {StatusCode}", tenantId, agentId, outcome, bytes, stopwatch.Elapsed.TotalMilliseconds, (int)response.StatusCode);
            if (retryable)
            {
                Response.Headers["X-NetRatel-File-Transfer-Retryable"] = "true";
            }
            return response.IsSuccessStatusCode ? Ok() : StatusCode((int)response.StatusCode);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            FileBrowserTransferTelemetry.Record("upload", "cancelled", bytes, stopwatch.Elapsed);
            logger.LogInformation("Cancelled browser-native remote file upload for tenant {TenantId}, agent {AgentId}; bytes {Bytes}; durationMs {DurationMs}", tenantId, agentId, bytes, stopwatch.Elapsed.TotalMilliseconds);
            throw;
        }
        catch (OperationCanceledException)
        {
            FileBrowserTransferTelemetry.Record("upload", "failed", bytes, stopwatch.Elapsed);
            logger.LogWarning("Remote file gateway session ended during browser-native upload for tenant {TenantId}, agent {AgentId}; bytes {Bytes}; durationMs {DurationMs}", tenantId, agentId, bytes, stopwatch.Elapsed.TotalMilliseconds);
            Response.Headers["X-NetRatel-File-Transfer-Retryable"] = "true";
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
        catch
        {
            FileBrowserTransferTelemetry.Record("upload", "failed", bytes, stopwatch.Elapsed);
            throw;
        }
    }

    private static string ApiPath(int tenantId, Guid agentId, string path) =>
        $"/api/v2/agents/{tenantId}/{agentId:D}/filesystem/file?path={Uri.EscapeDataString(path)}";

    private static async Task<bool> IsRetryableUploadFailureAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.StatusCode != System.Net.HttpStatusCode.Conflict)
        {
            return false;
        }

        try
        {
            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            var code = root.TryGetProperty("code", out var directCode) ? directCode.GetString() :
                root.TryGetProperty("extensions", out var extensions) && extensions.TryGetProperty("code", out var extensionCode)
                    ? extensionCode.GetString()
                    : null;
            return string.Equals(code, "session_unavailable", StringComparison.OrdinalIgnoreCase);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsValidRequest(int tenantId, Guid agentId, string? path) =>
        tenantId > 0 && agentId != Guid.Empty && !string.IsNullOrWhiteSpace(path) && path.Length <= 4_096 && !path.Contains('\0');

    private static bool TryGetOperatorId(ClaimsPrincipal user, out string operatorId)
    {
        operatorId = user.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? user.FindFirstValue("sub")
            ?? user.FindFirstValue("preferred_username")
            ?? user.Identity?.Name
            ?? string.Empty;
        return !string.IsNullOrWhiteSpace(operatorId);
    }

    private static string FileName(string path)
    {
        var index = Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\'));
        return index < path.Length - 1 ? path[(index + 1)..] : "download";
    }
}
