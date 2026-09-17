using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NetRatel.Web.Services;

namespace NetRatel.Web.Controllers;

[Authorize(Roles = "Operator")]
[Route("clients/mgmt/downloads")]
public sealed class ClientArtifactDownloadsController(
    UploadsApiClient uploads,
    ILogger<ClientArtifactDownloadsController> logger) : Controller
{
    [HttpGet("artifact")]
    public async Task<IActionResult> Artifact(
        [FromQuery] string rid,
        [FromQuery] string version,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(rid) || string.IsNullOrWhiteSpace(version))
        {
            return BadRequest("RID and version are required.");
        }

        logger.LogInformation("Starting artifact download proxy for {Rid} {Version}", rid, version);
        var uri = $"/api/v1/client-artifacts/{Uri.EscapeDataString(rid)}/{Uri.EscapeDataString(version)}/download";
        return await StreamApiDownloadAsync(uri, request: null, fallbackFileName: $"NetRatel.Client-{rid}-{version}.zip", ct);
    }

    [HttpGet("package")]
    public async Task<IActionResult> Package(
        [FromQuery] int tenantId,
        [FromQuery] int environment,
        [FromQuery] string runtimeId,
        [FromQuery] string version,
        [FromQuery] bool injectEnrollment,
        [FromQuery] int? validForMinutes,
        [FromQuery] int? maxUses,
        CancellationToken ct)
    {
        if (tenantId <= 0 || string.IsNullOrWhiteSpace(runtimeId))
        {
            return BadRequest("Tenant and runtime are required.");
        }

        var request = new ClientPackageDownloadRequest
        {
            TenantId = tenantId,
            Environment = environment,
            RuntimeId = runtimeId,
            Version = string.IsNullOrWhiteSpace(version) ? "latest" : version,
            InjectEnrollment = injectEnrollment,
            ValidForMinutes = injectEnrollment ? validForMinutes : null,
            MaxUses = injectEnrollment ? maxUses : null
        };

        logger.LogInformation(
            "Starting client package download proxy for tenant {TenantId}, runtime {RuntimeId}, version {Version}, injectEnrollment {InjectEnrollment}",
            tenantId,
            runtimeId,
            request.Version,
            injectEnrollment);

        return await StreamApiDownloadAsync(
            "/api/v1/client/download",
            request,
            $"netratel-client-{tenantId}-{runtimeId}-{request.Version}.zip",
            ct);
    }

    private async Task<IActionResult> StreamApiDownloadAsync(
        string uri,
        object? request,
        string fallbackFileName,
        CancellationToken ct)
    {
        using var message = request is null
            ? new HttpRequestMessage(HttpMethod.Get, uri)
            : new HttpRequestMessage(HttpMethod.Post, uri) { Content = JsonContent.Create(request) };

        var response = await uploads.Http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct);
        logger.LogInformation(
            "Client artifact API download response {StatusCode} for {Uri}; contentLength={ContentLength}; contentType={ContentType}",
            (int)response.StatusCode,
            uri,
            response.Content.Headers.ContentLength,
            response.Content.Headers.ContentType?.ToString());

        if (!response.IsSuccessStatusCode)
        {
            var problem = await ReadErrorAsync(response, ct);
            logger.LogWarning("Client artifact API download failed with {StatusCode} for {Uri}: {@Problem}", (int)response.StatusCode, uri, problem);
            response.Dispose();
            return StatusCode((int)response.StatusCode, problem);
        }

        Response.RegisterForDispose(response);
        CopyEnrollmentHeaders(response);

        var contentType = response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
        var fileName = ResolveFileName(response) ?? fallbackFileName;
        var stream = await response.Content.ReadAsStreamAsync(ct);
        logger.LogInformation("Streaming client artifact download {FileName} with content type {ContentType}", fileName, contentType);
        return File(stream, contentType, fileName, enableRangeProcessing: true);
    }

    private void CopyEnrollmentHeaders(HttpResponseMessage response)
    {
        foreach (var headerName in new[] { "X-Enrollment-Code", "X-Enrollment-Expires" })
        {
            if (response.Headers.TryGetValues(headerName, out var values))
            {
                Response.Headers[headerName] = values.ToArray();
            }
        }
    }

    private static string? ResolveFileName(HttpResponseMessage response)
    {
        var disposition = response.Content.Headers.ContentDisposition;
        var fileName = disposition?.FileNameStar ?? disposition?.FileName;
        return string.IsNullOrWhiteSpace(fileName) ? null : fileName.Trim('"');
    }

    private static async Task<object> ReadErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(body))
        {
            body = response.StatusCode == HttpStatusCode.Unauthorized
                ? "Download was not authorized."
                : $"Download failed with status {(int)response.StatusCode}.";
        }

        return new
        {
            status = (int)response.StatusCode,
            title = response.ReasonPhrase,
            detail = body
        };
    }
}
