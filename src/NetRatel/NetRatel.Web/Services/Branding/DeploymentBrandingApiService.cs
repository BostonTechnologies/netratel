using System.Net.Http.Json;
using Microsoft.AspNetCore.Components.Forms;
using NetRatel.Infrastructure.Identity.Branding;

namespace NetRatel.Web.Services.Branding;

public interface IDeploymentBrandingApiService
{
    Task<EffectiveDeploymentBranding> GetPublicAsync(CancellationToken cancellationToken = default);
    Task<EffectiveDeploymentBranding> UpdateAsync(IReadOnlyList<BrandingFieldUpdate> fields, CancellationToken cancellationToken = default);
    Task<BrandingAssetUploadResult> UploadAsync(string slot, IBrowserFile file, CancellationToken cancellationToken = default);
}

public sealed class DeploymentBrandingApiService(IHttpClientFactory clients) : IDeploymentBrandingApiService
{
    public async Task<EffectiveDeploymentBranding> GetPublicAsync(CancellationToken cancellationToken = default) =>
        await clients.CreateClient("SystemApiNoAuth").GetFromJsonAsync<EffectiveDeploymentBranding>("/api/v2/branding", cancellationToken).ConfigureAwait(false)
        ?? throw new InvalidOperationException("The API did not return deployment branding.");

    public async Task<EffectiveDeploymentBranding> UpdateAsync(IReadOnlyList<BrandingFieldUpdate> fields, CancellationToken cancellationToken = default)
    {
        var response = await clients.CreateClient("OrchestratorApi")
            .PutAsJsonAsync("/api/v2/branding", new UpdateDeploymentBrandingRequest(fields), cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<EffectiveDeploymentBranding>(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The API did not return updated deployment branding.");
    }

    public async Task<BrandingAssetUploadResult> UploadAsync(string slot, IBrowserFile file, CancellationToken cancellationToken = default)
    {
        if (file.Size is 0 or > 256 * 1024)
            throw new InvalidOperationException("Branding assets must be between 1 and 262144 bytes.");
        await using var stream = file.OpenReadStream(256 * 1024, cancellationToken);
        using var content = new StreamContent(stream);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(file.ContentType);
        using var form = new MultipartFormDataContent
        {
            { new StringContent(slot), "slot" },
            { content, "file", file.Name }
        };
        var response = await clients.CreateClient("OrchestratorApi").PostAsync("/api/v2/branding/assets", form, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<BrandingAssetUploadResult>(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The API did not return uploaded branding asset details.");
    }
}
