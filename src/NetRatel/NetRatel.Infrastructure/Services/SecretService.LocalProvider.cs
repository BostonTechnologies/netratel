using System.Globalization;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Secrets;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.Secrets;
using static NetRatel.Infrastructure.Secrets.SecretProviderValidation;

namespace NetRatel.Infrastructure.Services;

public sealed partial class SecretService
{
    internal static string ValidateLocalLocator(string locator)
    {
        if (PositiveInt64(locator) > int.MaxValue)
            throw Invalid("The Local secret ID is outside the supported range.");
        return locator;
    }

    internal async Task<SecretBrowsePage> BrowseLocalAsync(int tenantId, SecretBrowseQuery query, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(query.Folder))
            throw Invalid("Local storage has a single folder.");
        var after = query.Cursor is null ? 0L : PositiveInt64(query.Cursor);
        var entries = db.Secrets.AsNoTracking().Where(s => s.TenantId == tenantId && s.Id > after);
        if (query.Search is { Length: > 0 } search)
        {
            var pattern = "%" + search.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            _ = int.TryParse(search, NumberStyles.None, CultureInfo.InvariantCulture, out var id);
            entries = entries.Where(s => s.Id == id || EF.Functions.ILike(s.Name ?? "", pattern, "\\") ||
                EF.Functions.ILike(s.Description ?? "", pattern, "\\"));
        }

        var page = await entries.OrderBy(s => s.Id).Take(query.Limit + 1).Select(s => new LocalMetadata(
            s.Id, s.Name, s.Version, s.Revision, s.UpdatedAtUtc, s.Description)).ToArrayAsync(ct);
        var next = page.Length > query.Limit ? Format(page[query.Limit - 1].Id) : null;
        return new(page.Take(query.Limit).Select(LocalEntry).ToArray(), next, false, null);
    }

    internal async Task<SecretEntryDetails> GetLocalMetadataAsync(int tenantId, string locator, string? version, CancellationToken ct)
    {
        var id = (int)PositiveInt64(ValidateLocalLocator(locator));
        var secret = await db.Secrets.AsNoTracking().Where(s => s.TenantId == tenantId && s.Id == id)
            .Select(s => new LocalMetadata(s.Id, s.Name, s.Version, s.Revision, s.UpdatedAtUtc, s.Description))
            .SingleOrDefaultAsync(ct) ?? throw Missing();
        const int versionLimit = 50;
        var archived = await db.SecretVersions.AsNoTracking().Where(s => s.SecretId == id && s.Version < secret.Version)
            .OrderByDescending(s => s.Version).Select(s => s.Version).Take(versionLimit).ToArrayAsync(ct);
        if (version is not null && PositiveInt64(version) != secret.Version && !await db.SecretVersions.AsNoTracking()
                .AnyAsync(s => s.SecretId == id && s.Version == PositiveInt64(version), ct))
            throw MissingVersion();
        var versions = new[] { Format(secret.Version) }.Concat(archived.Take(versionLimit - 1).Select(Format)).ToArray();
        return new(LocalEntry(secret), [], versions, archived.Length == versionLimit, []);
    }

    internal async Task<SecretEntryMetadata> WriteLocalAsync(int tenantId, WriteSecretEntryRequest request, CancellationToken ct)
    {
        CheckScalarWrite(request);
        if (request.Enabled is not null)
            throw Invalid("Local secret entries are always enabled.");
        var description = OptionalText(request.Description, 1024, "Description");
        if (request.Locator is null)
        {
            if (request.ExpectedVersion is not null || request.ExpectedRevision is not null || request.Value is null)
                throw Invalid("A new Local entry requires a value and has no expected version.");
            var name = RequiredText(request.Name, 256, "Secret name");
            return LocalEntry(await CreateRecordAsync(new(tenantId, null, request.Value, description), name, ct));
        }

        var record = await GetLocalRecordAsync(tenantId, request.Locator, tracking: true, ct);
        RequireExpected(LocalEntry(record), request.ExpectedRevision, request.ExpectedVersion);
        if (request.Name is not null)
            record.Name = RequiredText(request.Name, 256, "Secret name");
        if (request.Description is not null)
            record.Description = description;
        if (request.Value is not null)
        {
            try { ReplaceValue(record, request.Value); }
            catch (CryptographicException) { throw UnreadableLocal(); }
        }
        record.Revision++;
        record.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await SaveChangesAsync(ct);
        return LocalEntry(record);
    }

    internal async Task<SecretDeletionResult> DeleteLocalAsync(int tenantId, DeleteSecretEntryRequest request, CancellationToken ct)
    {
        var record = await GetLocalRecordAsync(tenantId, request.Locator, tracking: true, ct);
        RequireExpected(LocalEntry(record), request.ExpectedRevision, request.ExpectedVersion);
        db.Secrets.Remove(record);
        await SaveChangesAsync(ct);
        return new(request.Locator, request.ExpectedVersion, SecretDeletionMode.RemoveEntry, false);
    }

    internal async Task<(string Version, string Value)> ResolveLocalAsync(int tenantId, string locator, string? version, CancellationToken ct)
    {
        var secret = await GetLocalRecordAsync(tenantId, locator, tracking: false, ct);
        var resolvedVersion = version is null ? secret.Version : PositiveInt64(version);
        try
        {
            if (!secret.ValueProtected)
                throw UnreadableLocal();
            if (resolvedVersion == secret.Version)
                return (Format(resolvedVersion), ReadValue(secret));
            var archived = await db.SecretVersions.AsNoTracking().Where(s => s.SecretId == secret.Id && s.Version == resolvedVersion)
                .Select(s => s.ProtectedValue).SingleOrDefaultAsync(ct) ?? throw MissingVersion();
            return (Format(resolvedVersion), VersionProtector(secret, resolvedVersion).Unprotect(archived));
        }
        catch (CryptographicException) { throw UnreadableLocal(); }
    }

    private async Task<SecretRecord> GetLocalRecordAsync(int tenantId, string locator, bool tracking, CancellationToken ct)
    {
        var id = (int)PositiveInt64(ValidateLocalLocator(locator));
        var records = tracking ? db.Secrets.AsTracking() : db.Secrets.AsNoTracking();
        return await records.SingleOrDefaultAsync(s => s.TenantId == tenantId && s.Id == id, ct) ?? throw Missing();
    }

    private static string Format(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static SecretEntryMetadata LocalEntry(SecretRecord row) => LocalEntry(new LocalMetadata(
        row.Id, row.Name, row.Version, row.Revision, row.UpdatedAtUtc, row.Description));
    private static SecretEntryMetadata LocalEntry(LocalMetadata row) => new(Format(row.Id), row.Name ?? $"Secret {Format(row.Id)}",
        false, Format(row.Version), Format(row.Revision), row.Updated, row.Description, true);
    private static SecretProviderException UnreadableLocal() => new(SecretProviderFailure.KeyUnavailable,
        "Local secret protection keys are unavailable. Restore the matching API key ring.");

    private sealed record LocalMetadata(int Id, string? Name, long Version, long Revision, DateTimeOffset Updated, string? Description);
}
