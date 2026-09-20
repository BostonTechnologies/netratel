using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using NetRatel.Infrastructure.Identity.Branding;

namespace NetRatel.API.Endpoints.Auth;

/// <summary>Public effective presentation and instance-administrator branding management.</summary>
public static class DeploymentBrandingEndpoints
{
    public static IEndpointRouteBuilder MapDeploymentBrandingEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v2/branding", async (IDeploymentBrandingService branding, CancellationToken ct) =>
            Results.Ok(await branding.GetEffectiveAsync(ct).ConfigureAwait(false)))
            .WithTags("Setup and instance configuration")
            .AllowAnonymous();

        app.MapGet("/api/v2/branding/assets/{assetId}", async (string assetId, IDeploymentBrandingService branding, CancellationToken ct) =>
        {
            if (assetId.Length is not 32 || !assetId.All(Uri.IsHexDigit))
                return Results.NotFound();
            var asset = await branding.FindAssetAsync(assetId, ct).ConfigureAwait(false);
            return asset is null
                ? Results.NotFound()
                : Results.File(asset.Content, asset.ContentType, enableRangeProcessing: false, lastModified: asset.CreatedAtUtc, entityTag: new Microsoft.Net.Http.Headers.EntityTagHeaderValue($"\"{asset.Sha256}\""));
        }).WithTags("Setup and instance configuration").AllowAnonymous();

        var group = app.MapGroup("/api/v2/branding")
            .WithTags("Setup and instance configuration")
            .RequireAuthorization("InstanceAdministrator");

        group.MapPut("", async (
            [FromBody] UpdateDeploymentBrandingRequest request,
            ClaimsPrincipal principal,
            IDeploymentBrandingService branding,
            CancellationToken ct) =>
        {
            try
            {
                return Results.Ok(await branding.UpdateAsync(request, principal.FindFirst("netratel_principal_id")?.Value, ct).ConfigureAwait(false));
            }
            catch (BrandingLockedException exception)
            {
                return Results.Conflict(new { error = "branding_field_deployment_managed", detail = exception.Message });
            }
            catch (BrandingValidationException exception)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["branding"] = [exception.Message] });
            }
        });

        group.MapPost("/assets", async (
            [FromForm] string slot,
            [FromForm] IFormFile file,
            ClaimsPrincipal principal,
            IDeploymentBrandingService branding,
            CancellationToken ct) =>
        {
            if (file.Length is <= 0 or > 256 * 1024)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["file"] = ["Branding assets must be between 1 and 262144 bytes."] });

            await using var stream = file.OpenReadStream();
            using var content = new MemoryStream((int)file.Length);
            await stream.CopyToAsync(content, ct).ConfigureAwait(false);
            var contentType = file.ContentType.Equals("image/vnd.microsoft.icon", StringComparison.OrdinalIgnoreCase)
                ? "image/x-icon"
                : file.ContentType;
            try
            {
                return Results.Ok(await branding.UploadAssetAsync(
                    new BrandingAssetUpload(slot, contentType, content.ToArray()),
                    principal.FindFirst("netratel_principal_id")?.Value,
                    ct).ConfigureAwait(false));
            }
            catch (BrandingLockedException exception)
            {
                return Results.Conflict(new { error = "branding_field_deployment_managed", detail = exception.Message });
            }
            catch (BrandingValidationException exception)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["asset"] = [exception.Message] });
            }
        });

        return app;
    }
}
