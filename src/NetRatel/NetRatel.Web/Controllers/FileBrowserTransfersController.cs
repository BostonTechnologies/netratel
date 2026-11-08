using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using NetRatel.Web.Services;
using NetRatel.Web.Services.FileSystem;
using System.Buffers;
using System.Diagnostics;
using Microsoft.AspNetCore.Antiforgery;
using NetRatel.Shared.Contracts.FileSystem;

namespace NetRatel.Web.Controllers;

[Authorize]
[Route("file-browser/transfers")]
public sealed class FileBrowserTransfersController(
    IUploadsApiClient uploads,
    IFileBrowserDownloadTransferRegistry downloads,
    ILogger<FileBrowserTransfersController> logger) : Controller
{
    [HttpGet("antiforgery")]
    public IActionResult Antiforgery([FromServices] IAntiforgery antiforgery) =>
        Ok(new { token = antiforgery.GetAndStoreTokens(HttpContext).RequestToken });

    [HttpPut("upload-entry")]
    [ValidateAntiForgeryToken]
    [DisableRequestSizeLimit]
    public async Task<IActionResult> UploadEntry(int tenantId, Guid agentId, string path, bool replace,
        string? expectedVersion, CancellationToken ct)
    {
        const long limit = 16L * 1024 * 1024 * 1024;
        if (!IsValidRequest(tenantId, agentId, path) || !RemoteFilePath.TryNormalizeDirectory(path, out path) ||
            (replace && string.IsNullOrEmpty(expectedVersion))) return BadRequest();
        if (Request.ContentLength > limit) return StatusCode(StatusCodes.Status413PayloadTooLarge);
        using var request = new HttpRequestMessage(HttpMethod.Put,
            $"/api/v2/agents/{tenantId}/{agentId:D}/filesystem/content?path={Uri.EscapeDataString(path)}&replace={replace.ToString().ToLowerInvariant()}&expectedVersion={Uri.EscapeDataString(expectedVersion ?? "")}")
        { Content = new StreamContent(new BoundedUploadStream(Request.Body, limit)) };
        using var response = await uploads.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        return response.IsSuccessStatusCode ? Ok() : StatusCode((int)response.StatusCode);
    }

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
            var outcome = response.IsSuccessStatusCode ? "completed" : "failed";
            FileBrowserTransferTelemetry.Record("upload", outcome, bytes, stopwatch.Elapsed);
            logger.LogInformation("Completed browser-native remote file upload for tenant {TenantId}, agent {AgentId}; outcome {Outcome}; bytes {Bytes}; durationMs {DurationMs}; statusCode {StatusCode}", tenantId, agentId, outcome, bytes, stopwatch.Elapsed.TotalMilliseconds, (int)response.StatusCode);
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
            // The gateway may already have executed the write. A browser
            // must report this interrupted outcome without replaying the PUT.
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

    private sealed class BoundedUploadStream(Stream source, long limit) : Stream
    {
        private long _read;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var count = await source.ReadAsync(buffer, cancellationToken);
            _read += count;
            if (_read > limit) throw new InvalidDataException("Upload exceeds the 16 GiB transfer limit.");
            return count;
        }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = source.Read(buffer, offset, count);
            _read += read;
            if (_read > limit) throw new InvalidDataException("Upload exceeds the 16 GiB transfer limit.");
            return read;
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
