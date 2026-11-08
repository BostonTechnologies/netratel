using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NetRatel.Web.Services;
using NetRatel.Web.Services.FileSystem;

namespace NetRatel.Web.Controllers;

[Authorize]
[Route("file-browser/content")]
public sealed class FileBrowserContentController(FileBrowserContentRegistry leases, IUploadsApiClient uploads) : Controller
{
    [AcceptVerbs("GET", "HEAD")]
    [Route("{id:guid}")]
    public async Task<IActionResult> Read(Guid id, CancellationToken cancellationToken)
    {
        if (!leases.TryGet(id, FileBrowserContentRegistry.OperatorId(User), out var lease)) return NotFound();
        using var readLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lease.Lifetime);
        cancellationToken = readLifetime.Token;
        var file = lease.File;
        using var request = new HttpRequestMessage(new HttpMethod(Request.Method),
            $"/api/v2/agents/{file.TenantId}/{file.AgentId:D}/filesystem/content?path={Uri.EscapeDataString(file.Path)}&expectedVersion={Uri.EscapeDataString(file.ObservedVersion)}");
        foreach (var header in new[] { "Range", "If-Range" })
            if (Request.Headers.TryGetValue(header, out var value)) request.Headers.TryAddWithoutValidation(header, value.ToArray());
        using var response = await uploads.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        Response.StatusCode = (int)response.StatusCode;
        Response.Headers.CacheControl = "no-store";
        Response.Headers.XContentTypeOptions = "nosniff";
        foreach (var header in new[] { "Accept-Ranges", "ETag", "Last-Modified", "Content-Range" })
        {
            if (response.Headers.TryGetValues(header, out var values) || response.Content.Headers.TryGetValues(header, out values))
                Response.Headers[header] = values.ToArray();
        }
        Response.ContentType = response.IsSuccessStatusCode ? lease.ContentType : "application/problem+json";
        Response.ContentLength = response.Content.Headers.ContentLength;
        if (!HttpMethods.IsHead(Request.Method))
        {
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await source.CopyToAsync(Response.Body, 64 * 1024, cancellationToken);
        }
        return new EmptyResult();
    }
}
