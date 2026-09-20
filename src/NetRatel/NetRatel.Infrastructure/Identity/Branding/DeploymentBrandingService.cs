using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace NetRatel.Infrastructure.Identity.Branding;

public sealed class DeploymentBrandingService(
    NetRatelIdentityDbContext db,
    IOptions<DeploymentBrandingOptions> options) : IDeploymentBrandingService
{
    private const int MaxAssetBytes = 256 * 1024;
    private static readonly IReadOnlySet<string> EditableFields = new HashSet<string>(StringComparer.Ordinal)
    {
        "applicationName", "organizationName", "tagline", "supportUrl", "siteUrl"
    };

    public async Task<EffectiveDeploymentBranding> GetEffectiveAsync(CancellationToken cancellationToken = default)
    {
        var stored = await db.DeploymentBrandingOverrides.AsNoTracking()
            .SingleOrDefaultAsync(value => value.Id == DeploymentBrandingOverride.SingletonId, cancellationToken)
            .ConfigureAwait(false);
        return Resolve(stored);
    }

    public async Task<EffectiveDeploymentBranding> UpdateAsync(UpdateDeploymentBrandingRequest request, string? actorPrincipalId, CancellationToken cancellationToken = default)
    {
        if (request.Fields is null || request.Fields.Count is 0)
            throw new BrandingValidationException("At least one branding field update is required.");
        if (request.Fields.Select(field => field.Field).Distinct(StringComparer.Ordinal).Count() != request.Fields.Count)
            throw new BrandingValidationException("Each branding field may be updated only once per request.");

        var stored = await db.DeploymentBrandingOverrides.SingleOrDefaultAsync(value => value.Id == DeploymentBrandingOverride.SingletonId, cancellationToken)
            .ConfigureAwait(false) ?? new DeploymentBrandingOverride();
        var created = db.Entry(stored).State == EntityState.Detached;
        foreach (var update in request.Fields)
        {
            if (!EditableFields.Contains(update.Field))
                throw new BrandingValidationException($"'{update.Field}' is not an editable branding field.");
            if (IsDeploymentManaged(update.Field))
                throw new BrandingLockedException(update.Field);
            if (update.Reset)
            {
                SetStoredValue(stored, update.Field, null);
                continue;
            }

            var value = NormalizeEditableValue(update.Field, update.Value);
            SetStoredValue(stored, update.Field, value);
        }

        stored.Version++;
        stored.UpdatedAtUtc = DateTimeOffset.UtcNow;
        stored.UpdatedByPrincipalId = actorPrincipalId;
        if (created)
            db.DeploymentBrandingOverrides.Add(stored);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Resolve(stored);
    }

    public async Task<BrandingAssetUploadResult> UploadAssetAsync(BrandingAssetUpload upload, string? actorPrincipalId, CancellationToken cancellationToken = default)
    {
        if (upload.Content.Length is 0 or > MaxAssetBytes)
            throw new BrandingValidationException($"Branding assets must be between 1 and {MaxAssetBytes} bytes.");
        if (!TryReadDimensions(upload.ContentType, upload.Content, out var width, out var height) || width is < 16 or > 2048 || height is < 16 or > 2048)
            throw new BrandingValidationException("Only valid PNG, JPEG, or ICO assets with dimensions from 16 to 2048 pixels are accepted. SVG and remote URLs are not accepted.");

        var field = SlotToAssetField(upload.Slot);
        if (IsDeploymentManaged(field))
            throw new BrandingLockedException(field);

        var hash = Convert.ToHexString(SHA256.HashData(upload.Content)).ToLowerInvariant();
        var asset = await db.DeploymentBrandingAssets.SingleOrDefaultAsync(candidate => candidate.Sha256 == hash, cancellationToken)
            .ConfigureAwait(false) ?? new DeploymentBrandingAsset
        {
            ContentType = upload.ContentType,
            Content = upload.Content,
            Width = width,
            Height = height,
            Sha256 = hash
        };
        var stored = await db.DeploymentBrandingOverrides.SingleOrDefaultAsync(value => value.Id == DeploymentBrandingOverride.SingletonId, cancellationToken)
            .ConfigureAwait(false) ?? new DeploymentBrandingOverride();
        var created = db.Entry(stored).State == EntityState.Detached;
        SetStoredValue(stored, field, asset.Id);
        stored.Version++;
        stored.UpdatedAtUtc = DateTimeOffset.UtcNow;
        stored.UpdatedByPrincipalId = actorPrincipalId;
        if (db.Entry(asset).State == EntityState.Detached)
            db.DeploymentBrandingAssets.Add(asset);
        if (created)
            db.DeploymentBrandingOverrides.Add(stored);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new BrandingAssetUploadResult(upload.Slot, asset.Id, AssetUrl(asset.Id, stored.Version), width, height);
    }

    public Task<DeploymentBrandingAsset?> FindAssetAsync(string assetId, CancellationToken cancellationToken = default) =>
        db.DeploymentBrandingAssets.AsNoTracking().SingleOrDefaultAsync(asset => asset.Id == assetId, cancellationToken);

    private EffectiveDeploymentBranding Resolve(DeploymentBrandingOverride? stored) => new(
        ResolveText(options.Value.ApplicationName, stored?.ApplicationName, "NetRatel"),
        ResolveText(options.Value.OrganizationName, stored?.OrganizationName, "NetRatel"),
        ResolveText(options.Value.Tagline, stored?.Tagline, "Automation Platform"),
        ResolveAsset(options.Value.LogoLightUrl, stored?.LogoLightAssetId, "brand/netratel-wordmark-600.webp", stored?.Version ?? 0),
        ResolveAsset(options.Value.LogoDarkUrl, stored?.LogoDarkAssetId, "brand/netratel-wordmark-600.webp", stored?.Version ?? 0),
        ResolveAsset(options.Value.CompactLogoUrl, stored?.CompactLogoAssetId, "brand/netratel-mark-64.png", stored?.Version ?? 0),
        ResolveAsset(options.Value.FaviconUrl, stored?.FaviconAssetId, "favicon.ico", stored?.Version ?? 0),
        ResolveText(options.Value.SupportUrl, stored?.SupportUrl, string.Empty),
        ResolveText(options.Value.SiteUrl, stored?.SiteUrl, string.Empty),
        stored?.Version ?? 0);

    private static BrandingField ResolveText(string? deployment, string? stored, string fallback) =>
        !string.IsNullOrWhiteSpace(deployment) ? new(deployment.Trim(), BrandingValueSource.Deployment, true) :
        !string.IsNullOrWhiteSpace(stored) ? new(stored, BrandingValueSource.Administrator, false) :
        new(fallback, BrandingValueSource.Default, false);

    private static BrandingField ResolveAsset(string? deployment, string? assetId, string fallback, long version) =>
        !string.IsNullOrWhiteSpace(deployment) ? new(deployment.Trim(), BrandingValueSource.Deployment, true) :
        !string.IsNullOrWhiteSpace(assetId) ? new(AssetUrl(assetId, version), BrandingValueSource.Administrator, false) :
        new(fallback, BrandingValueSource.Default, false);

    private bool IsDeploymentManaged(string field) => field switch
    {
        "applicationName" => !string.IsNullOrWhiteSpace(options.Value.ApplicationName),
        "organizationName" => !string.IsNullOrWhiteSpace(options.Value.OrganizationName),
        "tagline" => !string.IsNullOrWhiteSpace(options.Value.Tagline),
        "logoLightAssetId" => !string.IsNullOrWhiteSpace(options.Value.LogoLightUrl),
        "logoDarkAssetId" => !string.IsNullOrWhiteSpace(options.Value.LogoDarkUrl),
        "compactLogoAssetId" => !string.IsNullOrWhiteSpace(options.Value.CompactLogoUrl),
        "faviconAssetId" => !string.IsNullOrWhiteSpace(options.Value.FaviconUrl),
        "supportUrl" => !string.IsNullOrWhiteSpace(options.Value.SupportUrl),
        "siteUrl" => !string.IsNullOrWhiteSpace(options.Value.SiteUrl),
        _ => throw new BrandingValidationException($"Unknown branding field '{field}'.")
    };

    private static string NormalizeEditableValue(string field, string? raw)
    {
        var value = raw?.Trim();
        var max = field is "tagline" ? 160 : field is "applicationName" or "organizationName" ? 96 : 2048;
        if (string.IsNullOrWhiteSpace(value) || value.Length > max || value.Any(char.IsControl))
            throw new BrandingValidationException($"'{field}' must be non-empty, no longer than {max} characters, and contain no control characters.");
        if (field is "supportUrl" or "siteUrl" && !BrandingUrl.IsSafeConfiguredUrl(value))
            throw new BrandingValidationException($"'{field}' must be an absolute HTTPS URL or a root-relative path.");
        return value;
    }

    private static void SetStoredValue(DeploymentBrandingOverride stored, string field, string? value)
    {
        switch (field)
        {
            case "applicationName": stored.ApplicationName = value; break;
            case "organizationName": stored.OrganizationName = value; break;
            case "tagline": stored.Tagline = value; break;
            case "supportUrl": stored.SupportUrl = value; break;
            case "siteUrl": stored.SiteUrl = value; break;
            case "logoLightAssetId": stored.LogoLightAssetId = value; break;
            case "logoDarkAssetId": stored.LogoDarkAssetId = value; break;
            case "compactLogoAssetId": stored.CompactLogoAssetId = value; break;
            case "faviconAssetId": stored.FaviconAssetId = value; break;
            default: throw new BrandingValidationException($"Unknown branding field '{field}'.");
        }
    }

    private static string SlotToAssetField(string slot) => slot.Trim().ToLowerInvariant() switch
    {
        "logo-light" => "logoLightAssetId",
        "logo-dark" => "logoDarkAssetId",
        "compact-logo" => "compactLogoAssetId",
        "favicon" => "faviconAssetId",
        _ => throw new BrandingValidationException("Asset slot must be logo-light, logo-dark, compact-logo, or favicon.")
    };

    private static string AssetUrl(string assetId, long version) => $"/api/v2/branding/assets/{assetId}?v={version}";

    private static bool TryReadDimensions(string contentType, byte[] content, out int width, out int height)
    {
        width = height = 0;
        if (contentType.Equals("image/png", StringComparison.OrdinalIgnoreCase) && content.Length >= 24 && content.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
        {
            width = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(content.AsSpan(16, 4));
            height = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(content.AsSpan(20, 4));
            return true;
        }
        if (contentType.Equals("image/x-icon", StringComparison.OrdinalIgnoreCase) && content.Length >= 22 && content[0] == 0 && content[1] == 0 && content[2] == 1 && content[3] == 0)
        {
            width = content[6] == 0 ? 256 : content[6];
            height = content[7] == 0 ? 256 : content[7];
            return true;
        }
        if (!contentType.Equals("image/jpeg", StringComparison.OrdinalIgnoreCase) || content.Length < 10 || content[0] != 0xff || content[1] != 0xd8)
            return false;
        for (var index = 2; index + 9 < content.Length;)
        {
            if (content[index++] != 0xff) continue;
            while (index < content.Length && content[index] == 0xff) index++;
            if (index >= content.Length) break;
            var marker = content[index++];
            if (marker is 0xd8 or 0xd9 || index + 1 >= content.Length) continue;
            var length = (content[index] << 8) | content[index + 1];
            if (length < 2 || index + length > content.Length) break;
            if (marker is >= 0xc0 and <= 0xc3 or >= 0xc5 and <= 0xc7 or >= 0xc9 and <= 0xcb or >= 0xcd and <= 0xcf)
            {
                height = (content[index + 3] << 8) | content[index + 4];
                width = (content[index + 5] << 8) | content[index + 6];
                return true;
            }
            index += length;
        }
        return false;
    }
}

public sealed class BrandingValidationException(string message) : InvalidOperationException(message);
public sealed class BrandingLockedException(string field) : InvalidOperationException($"'{field}' is managed by deployment configuration and cannot be changed here.");
