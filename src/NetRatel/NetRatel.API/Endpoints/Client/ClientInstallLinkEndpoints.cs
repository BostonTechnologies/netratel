using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using NetRatel.API.Models;
using NetRatel.API.Services;

namespace NetRatel.API.Endpoints;

public static class ClientInstallLinkEndpoints
{
    public static IEndpointRouteBuilder MapClientInstallLinkEndpoints(this IEndpointRouteBuilder app)
    {
        var management = app.MapGroup("/api/v1/client-install-links")
            .WithTags("Client Onboarding")
            .RequireAuthorization("ClientArtifactsWrite");

        management.MapPost(string.Empty, async (
            ClientInstallLinkCreateRequest request, HttpContext http,
            [FromServices] ClientInstallLinkService links, CancellationToken ct) =>
        {
            var actor = OperatorIdentity(http);
            if (actor is null) return Results.Forbid();
            try { return Results.Ok(await links.CreateAsync(request, actor, ct)); }
            catch (RequestValidationException exception) { return Results.BadRequest(new { message = exception.Message }); }
            catch (FileNotFoundException) { return Results.NotFound(new { message = "The selected client artifact is unavailable." }); }
            catch (InvalidDataException exception) { return Results.Conflict(new { message = exception.Message }); }
            catch (InvalidOperationException exception) { return Results.Conflict(new { message = exception.Message }); }
        })
        .WithName("ClientInstallLinks_Create")
        .Produces<ClientInstallLinkResult>();

        management.MapGet(string.Empty, async (
            [FromQuery] int? tenantId, [FromServices] ClientInstallLinkService links, CancellationToken ct) =>
            Results.Ok(await links.ListAsync(tenantId, ct)))
            .WithName("ClientInstallLinks_List")
            .Produces<IReadOnlyList<ClientInstallLinkMetadata>>();

        management.MapGet("{id:guid}", async (
            Guid id, [FromServices] ClientInstallLinkService links, CancellationToken ct) =>
        {
            var metadata = await links.GetAsync(id, ct);
            return metadata is null ? Results.NotFound() : Results.Ok(metadata);
        })
        .WithName("ClientInstallLinks_Get")
        .Produces<ClientInstallLinkMetadata>();

        management.MapPost("{id:guid}/revoke", async (
            Guid id, HttpContext http, [FromServices] ClientInstallLinkService links,
            CancellationToken ct) =>
        {
            var actor = OperatorIdentity(http);
            if (actor is null) return Results.Forbid();
            return await links.RevokeAsync(id, actor, ct) ? Results.NoContent() : Results.NotFound();
        })
        .WithName("ClientInstallLinks_Revoke");

        app.MapMethods("/clients/install/{token}.{extension}", ["GET", "HEAD"], async (
            string token, string extension, HttpContext http,
            [FromServices] ClientInstallLinkService links, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store, max-age=0";
            http.Response.Headers["X-Content-Type-Options"] = "nosniff";
            http.Response.Headers["Referrer-Policy"] = "no-referrer";
            http.Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
            if (http.Request.QueryString.HasValue) return Results.NotFound();
            try
            {
                var result = await links.GetPublicScriptAsync(token, extension, ct);
                if (result is null) return Results.NotFound();
                var contentType = extension == "sh" ? "text/x-shellscript; charset=utf-8" : "text/plain; charset=utf-8";
                http.Response.Headers.ContentDisposition = $"inline; filename=\"netratel-install.{extension}\"";
                if (HttpMethods.IsHead(http.Request.Method))
                {
                    http.Response.ContentType = contentType;
                    http.Response.ContentLength = Encoding.UTF8.GetByteCount(result.Value.Script);
                    return Results.StatusCode(StatusCodes.Status200OK);
                }
                return Results.Text(result.Value.Script, contentType, Encoding.UTF8);
            }
            catch (CryptographicException)
            {
                return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                    title: "Protected install data is unavailable.");
            }
        })
        .AllowAnonymous()
        .RequireRateLimiting("public-client-install")
        .WithName("ClientInstallLinks_PublicScript")
        .WithTags("Client Onboarding")
        .Produces(StatusCodes.Status200OK, contentType: "text/plain")
        .ProducesProblem(StatusCodes.Status404NotFound);

        return app;
    }

    private static string? OperatorIdentity(HttpContext http) =>
        http.User.FindFirstValue(ClaimTypes.NameIdentifier) ??
        http.User.FindFirstValue("sub") ?? http.User.Identity?.Name;
}
