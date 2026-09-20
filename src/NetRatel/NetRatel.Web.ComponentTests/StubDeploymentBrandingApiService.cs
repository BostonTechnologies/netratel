using Microsoft.AspNetCore.Components.Forms;
using NetRatel.Infrastructure.Identity.Branding;
using NetRatel.Web.Services.Branding;

namespace NetRatel.Web.ComponentTests;

internal sealed class StubDeploymentBrandingApiService : IDeploymentBrandingApiService
{
    private static readonly EffectiveDeploymentBranding DefaultBranding = new(
        new BrandingField("NetRatel", BrandingValueSource.Default, false),
        new BrandingField("Automation Platform", BrandingValueSource.Default, false),
        new BrandingField("Automate with confidence.", BrandingValueSource.Default, false),
        new BrandingField("/images/netratel-logo-light.svg", BrandingValueSource.Default, false),
        new BrandingField("/images/netratel-logo-dark.svg", BrandingValueSource.Default, false),
        new BrandingField("/images/netratel-mark.svg", BrandingValueSource.Default, false),
        new BrandingField("/favicon.ico", BrandingValueSource.Default, false),
        new BrandingField("https://netratel.com/support", BrandingValueSource.Default, false),
        new BrandingField("https://netratel.com", BrandingValueSource.Default, false),
        1);

    public Task<EffectiveDeploymentBranding> GetPublicAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(DefaultBranding);

    public Task<EffectiveDeploymentBranding> UpdateAsync(
        IReadOnlyList<BrandingFieldUpdate> fields,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This test double only supplies public branding.");

    public Task<BrandingAssetUploadResult> UploadAsync(
        string slot,
        IBrowserFile file,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("This test double only supplies public branding.");
}
