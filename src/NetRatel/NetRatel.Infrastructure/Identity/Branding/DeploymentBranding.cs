using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace NetRatel.Infrastructure.Identity.Branding;

/// <summary>Deployment-owned presentation settings. Empty properties mean that the deployment does not lock that field.</summary>
public sealed class DeploymentBrandingOptions
{
    public const string SectionName = "Branding";

    [StringLength(96)] public string? ApplicationName { get; init; }
    [StringLength(96)] public string? OrganizationName { get; init; }
    [StringLength(160)] public string? Tagline { get; init; }
    [StringLength(2048)] public string? LogoLightUrl { get; init; }
    [StringLength(2048)] public string? LogoDarkUrl { get; init; }
    [StringLength(2048)] public string? CompactLogoUrl { get; init; }
    [StringLength(2048)] public string? FaviconUrl { get; init; }
    [StringLength(2048)] public string? SupportUrl { get; init; }
    [StringLength(2048)] public string? SiteUrl { get; init; }
}

public sealed class DeploymentBrandingOptionsValidator : IValidateOptions<DeploymentBrandingOptions>
{
    public ValidateOptionsResult Validate(string? name, DeploymentBrandingOptions options)
    {
        var failures = new List<string>();
        foreach (var (field, value, maxLength) in new[]
                 {
                     (nameof(options.ApplicationName), options.ApplicationName, 96),
                     (nameof(options.OrganizationName), options.OrganizationName, 96),
                     (nameof(options.Tagline), options.Tagline, 160),
                     (nameof(options.LogoLightUrl), options.LogoLightUrl, 2048),
                     (nameof(options.LogoDarkUrl), options.LogoDarkUrl, 2048),
                     (nameof(options.CompactLogoUrl), options.CompactLogoUrl, 2048),
                     (nameof(options.FaviconUrl), options.FaviconUrl, 2048),
                     (nameof(options.SupportUrl), options.SupportUrl, 2048),
                     (nameof(options.SiteUrl), options.SiteUrl, 2048)
                 })
        {
            if (value is null)
                continue;
            if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength || value.Any(char.IsControl))
                failures.Add($"Branding:{field} must be non-empty, no longer than {maxLength} characters, and contain no control characters.");
        }

        foreach (var (field, value) in new[]
                 {
                     (nameof(options.LogoLightUrl), options.LogoLightUrl),
                     (nameof(options.LogoDarkUrl), options.LogoDarkUrl),
                     (nameof(options.CompactLogoUrl), options.CompactLogoUrl),
                     (nameof(options.FaviconUrl), options.FaviconUrl),
                     (nameof(options.SupportUrl), options.SupportUrl),
                     (nameof(options.SiteUrl), options.SiteUrl)
                 })
        {
            if (value is not null && !BrandingUrl.IsSafeConfiguredUrl(value))
                failures.Add($"Branding:{field} must be an absolute HTTPS URL or a root-relative path.");
        }

        return failures.Count is 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}

public static class BrandingUrl
{
    public static bool IsSafeConfiguredUrl(string value)
    {
        var trimmed = value.Trim();
        return (trimmed.StartsWith("/", StringComparison.Ordinal) && !trimmed.StartsWith("//", StringComparison.Ordinal)) ||
               (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps);
    }
}

public sealed class DeploymentBrandingOverride
{
    public const string SingletonId = "default";

    public string Id { get; set; } = SingletonId;
    public string? ApplicationName { get; set; }
    public string? OrganizationName { get; set; }
    public string? Tagline { get; set; }
    public string? LogoLightAssetId { get; set; }
    public string? LogoDarkAssetId { get; set; }
    public string? CompactLogoAssetId { get; set; }
    public string? FaviconAssetId { get; set; }
    public string? SupportUrl { get; set; }
    public string? SiteUrl { get; set; }
    public long Version { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public string? UpdatedByPrincipalId { get; set; }
}

public sealed class DeploymentBrandingAsset
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ContentType { get; set; } = string.Empty;
    public byte[] Content { get; set; } = [];
    public int Width { get; set; }
    public int Height { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

public enum BrandingValueSource { Default, Administrator, Deployment }

public sealed record BrandingField(string Value, BrandingValueSource Source, bool IsLocked);
public sealed record EffectiveDeploymentBranding(
    BrandingField ApplicationName,
    BrandingField OrganizationName,
    BrandingField Tagline,
    BrandingField LogoLightUrl,
    BrandingField LogoDarkUrl,
    BrandingField CompactLogoUrl,
    BrandingField FaviconUrl,
    BrandingField SupportUrl,
    BrandingField SiteUrl,
    long Version);

public sealed record BrandingFieldUpdate(string Field, string? Value, bool Reset);
public sealed record UpdateDeploymentBrandingRequest(IReadOnlyList<BrandingFieldUpdate>? Fields);
public sealed record BrandingAssetUpload(string Slot, string ContentType, byte[] Content);
public sealed record BrandingAssetUploadResult(string Slot, string AssetId, string Url, int Width, int Height);

public interface IDeploymentBrandingService
{
    Task<EffectiveDeploymentBranding> GetEffectiveAsync(CancellationToken cancellationToken = default);
    Task<EffectiveDeploymentBranding> UpdateAsync(UpdateDeploymentBrandingRequest request, string? actorPrincipalId, CancellationToken cancellationToken = default);
    Task<BrandingAssetUploadResult> UploadAssetAsync(BrandingAssetUpload upload, string? actorPrincipalId, CancellationToken cancellationToken = default);
    Task<DeploymentBrandingAsset?> FindAssetAsync(string assetId, CancellationToken cancellationToken = default);
}
