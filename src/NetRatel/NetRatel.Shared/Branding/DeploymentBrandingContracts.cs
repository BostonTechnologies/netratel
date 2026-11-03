// Kept in the original namespace for source and assembly-forwarding compatibility.
namespace NetRatel.Infrastructure.Identity.Branding;

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
    long Version,
    BrandingField? GatewayUrl = null);

public sealed record BrandingFieldUpdate(string Field, string? Value, bool Reset);
public sealed record UpdateDeploymentBrandingRequest(IReadOnlyList<BrandingFieldUpdate>? Fields);
public sealed record BrandingAssetUploadResult(string Slot, string AssetId, string Url, int Width, int Height);
