using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using NetRatel.API.Models;
using NetRatel.Application.Artifacts;
using NetRatel.Infrastructure.Persistence;

namespace NetRatel.API.Services;

public sealed record ClientInstallLinkCreateRequest(
    int TenantId, string RuntimeId, string? ArtifactVersion, int ValidForMinutes,
    int MaxUses, bool InstallAsService, bool SilentInstall, string IdempotencyKey);

public sealed record ClientInstallLinkResult(
    Guid Id, int TenantId, string RuntimeId, string ArtifactVersion, string ArtifactSha256,
    DateTimeOffset ExpiresAtUtc, int MaxUses, int RemainingUses,
    bool InstallAsService, bool SilentInstall, string PublicUrl, string InstallCommand,
    string Script, bool Replay);

public sealed record ClientInstallLinkMetadata(
    Guid Id, int TenantId, string RuntimeId, string ArtifactVersion,
    DateTimeOffset CreatedAtUtc, DateTimeOffset ExpiresAtUtc, int MaxUses, int Uses,
    bool IsActive, DateTimeOffset? RevokedAtUtc, string CreatedBy);

public sealed class ClientInstallLinkService(
    OrchestratorDbContext db,
    ITenantLookupService tenants,
    IClientArtifactsService artifacts,
    IScriptTemplateService templates,
    IDataProtectionProvider protection,
    IConfiguration configuration,
    TimeProvider clock)
{
    private readonly IDataProtector _tokenProtector = protection.CreateProtector("NetRatel.ClientInstallGrant.Token.v1");
    private readonly IDataProtector _scriptProtector = protection.CreateProtector("NetRatel.ClientInstallGrant.Script.v1");

    public async Task<ClientInstallLinkResult> CreateAsync(
        ClientInstallLinkCreateRequest request, string createdBy, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(createdBy))
            throw new RequestValidationException("createdBy", "An authenticated operator identity is required.");
        if (!Guid.TryParse(request.IdempotencyKey, out var requestId) || requestId == Guid.Empty)
            throw new RequestValidationException("idempotencyKey", "A new request identifier is required.");
        var validated = ClientScriptValidation.Validate(new ClientScriptRequest
        {
            TenantId = request.TenantId, RuntimeId = request.RuntimeId,
            ValidForMinutes = request.ValidForMinutes, MaxUses = request.MaxUses,
            ArtifactVersion = request.ArtifactVersion,
            InstallAsService = request.InstallAsService, SilentInstall = request.SilentInstall
        }, OperatingSystem.IsWindows());
        var runtimeId = validated.normalizedRid;
        if (runtimeId is not ("win-x64" or "win-arm64" or "linux-x64" or "osx-x64" or "osx-arm64"))
            throw new RequestValidationException("runtimeId", "The selected runtime is unsupported.");
        var requestKey = Hash($"{createdBy}|{requestId:N}");
        var fingerprint = Hash(JsonSerializer.Serialize(new
        {
            request.TenantId, RuntimeId = runtimeId, request.ArtifactVersion,
            request.ValidForMinutes, request.MaxUses,
            request.InstallAsService, request.SilentInstall
        }));
        var existing = await db.ClientInstallGrants.AsNoTracking().Include(x => x.EnrollmentCode)
            .SingleOrDefaultAsync(x => x.RequestKey == requestKey, ct);
        if (existing is not null) return ToResult(existing, fingerprint, replay: true);

        var webBase = ValidatedPublicBase(configuration["PublicUrls:WebBaseUrl"], "PublicUrls:WebBaseUrl");
        var apiBase = ValidatedPublicBase(configuration["PublicUrls:ApiBaseUrl"], "PublicUrls:ApiBaseUrl");
        var keysDirectory = configuration["DataProtection:KeysDirectory"];
        if (!string.IsNullOrWhiteSpace(keysDirectory) && !Path.IsPathRooted(keysDirectory))
            keysDirectory = Path.Combine(AppContext.BaseDirectory, keysDirectory);
        if (string.IsNullOrWhiteSpace(keysDirectory) || !Directory.Exists(keysDirectory))
            throw new InvalidOperationException("Configure a persistent DataProtection:KeysDirectory before creating public install links.");
        if (!await tenants.TenantExistsAsync(request.TenantId, ct))
            throw new RequestValidationException("tenantId", "The selected tenant does not exist.");
        var artifact = request.ArtifactVersion is null or ""
            ? await artifacts.GetLatestAsync(runtimeId, ct)
            : await artifacts.GetMetadataAsync(runtimeId, request.ArtifactVersion, ct);
        if (artifact is null)
            throw new FileNotFoundException("The selected client artifact is unavailable.");
        var download = await artifacts.DownloadRawAsync(runtimeId, artifact.Version, ct);
        await using (download.Content)
        {
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(download.Content, ct)).ToLowerInvariant();
            if (actual != artifact.Sha256)
                throw new InvalidDataException("The selected client archive differs from its stored SHA256.");
        }
        await ClientArtifactManifestValidator.ValidateAsync(artifacts, artifact, ct);

        var now = clock.GetUtcNow();
        var code = "ENR-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var token = Base64Url(RandomNumberGenerator.GetBytes(32));
        var extension = templates.GetFileExtension(runtimeId);
        var expires = now.AddMinutes(validated.validForMinutes);
        var script = templates.Build(new DeploymentScriptTemplateRequest(
            request.TenantId, runtimeId, code, apiBase, expires,
            request.InstallAsService, request.SilentInstall,
            artifact.Version, artifact.Sha256));
        var codeId = Guid.NewGuid();
        var grant = new ClientInstallGrant
        {
            Id = Guid.NewGuid(), TokenHash = Hash(token), ProtectedToken = _tokenProtector.Protect(token),
            ProtectedScript = _scriptProtector.Protect(script), RequestKey = requestKey,
            RequestFingerprint = fingerprint, EnrollmentCodeId = codeId,
            TenantId = request.TenantId, RuntimeId = runtimeId,
            ArtifactVersion = artifact.Version, ArtifactSha256 = artifact.Sha256,
            InstallAsService = request.InstallAsService, SilentInstall = request.SilentInstall,
            PublicWebBaseUrl = webBase, PublicApiBaseUrl = apiBase,
            CreatedBy = createdBy, CreatedAtUtc = now, ExpiresAtUtc = expires,
            MaxUses = validated.maxUses
        };
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        db.EnrollmentCodes.Add(new EnrollmentCode
        {
            Id = codeId, TenantId = request.TenantId,
            Code = $"PROTECTED-{codeId:N}", CodeHash = EnrollmentCodeLookup.Hash(code),
            CreatedAtUtc = now, CreatedBy = createdBy, ValidFromUtc = now,
            ValidToUtc = expires, MaxUses = validated.maxUses, Uses = 0,
            Notes = $"client-install-grant:{grant.Id:N}"
        });
        db.ClientInstallGrants.Add(grant);
        try
        {
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(ct);
            db.ChangeTracker.Clear();
            existing = await db.ClientInstallGrants.AsNoTracking().Include(x => x.EnrollmentCode)
                .SingleOrDefaultAsync(x => x.RequestKey == requestKey, ct);
            if (existing is null) throw;
            return ToResult(existing, fingerprint, replay: true);
        }
        return BuildResult(grant, token, script, extension, replay: false);
    }

    public async Task<(string Script, string Extension)?> GetPublicScriptAsync(string token, string extension, CancellationToken ct)
    {
        if (token.Length != 43 || !token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') ||
            extension is not ("sh" or "ps1")) return null;
        var grant = await db.ClientInstallGrants.AsNoTracking().Include(x => x.EnrollmentCode)
            .SingleOrDefaultAsync(x => x.TokenHash == Hash(token), ct);
        if (grant is null || !IsActive(grant) || templates.GetFileExtension(grant.RuntimeId) != extension ||
            !await tenants.TenantExistsAsync(grant.TenantId, ct)) return null;
        return (_scriptProtector.Unprotect(grant.ProtectedScript), extension);
    }

    public async Task<IReadOnlyList<ClientInstallLinkMetadata>> ListAsync(int? tenantId, CancellationToken ct)
    {
        var query = db.ClientInstallGrants.AsNoTracking().Include(x => x.EnrollmentCode).AsQueryable();
        if (tenantId.HasValue) query = query.Where(x => x.TenantId == tenantId.Value);
        var grants = await query.OrderByDescending(x => x.CreatedAtUtc).Take(100).ToListAsync(ct);
        return grants.Select(ToMetadata).ToArray();
    }

    public async Task<ClientInstallLinkMetadata?> GetAsync(Guid id, CancellationToken ct)
    {
        var grant = await db.ClientInstallGrants.AsNoTracking().Include(x => x.EnrollmentCode)
            .SingleOrDefaultAsync(x => x.Id == id, ct);
        return grant is null ? null : ToMetadata(grant);
    }

    public async Task<bool> RevokeAsync(Guid id, string revokedBy, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var grant = await db.ClientInstallGrants.Include(x => x.EnrollmentCode)
            .SingleOrDefaultAsync(x => x.Id == id, ct);
        if (grant is null) return false;
        if (grant.RevokedAtUtc.HasValue) return true;
        if (db.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL")
        {
            await db.EnrollmentCodes.FromSqlInterpolated(
                    $"SELECT * FROM \"EnrollmentCodes\" WHERE \"Id\" = {grant.EnrollmentCodeId} FOR UPDATE")
                .ToListAsync(ct);
        }
        var now = clock.GetUtcNow();
        grant.RevokedAtUtc = now;
        grant.RevokedBy = revokedBy;
        grant.EnrollmentCode.RevokedAtUtc = now;
        grant.EnrollmentCode.RevokedBy = revokedBy;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return true;
    }

    private ClientInstallLinkResult ToResult(ClientInstallGrant grant, string fingerprint, bool replay)
    {
        if (grant.RequestFingerprint != fingerprint)
            throw new InvalidOperationException("The request identifier was already used with different inputs.");
        return BuildResult(grant, _tokenProtector.Unprotect(grant.ProtectedToken),
            _scriptProtector.Unprotect(grant.ProtectedScript),
            templates.GetFileExtension(grant.RuntimeId), replay);
    }

    private ClientInstallLinkResult BuildResult(ClientInstallGrant grant, string token, string script,
        string extension, bool replay)
    {
        var url = $"{grant.PublicWebBaseUrl}/clients/install/{token}.{extension}";
        var command = extension == "sh"
            ? $"bash -o pipefail -c \"curl -fsSL '{url}' | bash\""
            : $"Invoke-WebRequest -UseBasicParsing -ErrorAction Stop '{url}' | Invoke-Expression";
        return new ClientInstallLinkResult(grant.Id, grant.TenantId, grant.RuntimeId,
            grant.ArtifactVersion, grant.ArtifactSha256, grant.ExpiresAtUtc,
            grant.MaxUses, Math.Max(0, grant.MaxUses - (grant.EnrollmentCode?.Uses ?? 0)),
            grant.InstallAsService, grant.SilentInstall, url, command, script, replay);
    }

    private ClientInstallLinkMetadata ToMetadata(ClientInstallGrant grant) =>
        new(grant.Id, grant.TenantId, grant.RuntimeId, grant.ArtifactVersion,
            grant.CreatedAtUtc, grant.ExpiresAtUtc, grant.MaxUses, grant.EnrollmentCode.Uses,
            IsActive(grant), grant.RevokedAtUtc, grant.CreatedBy);

    private bool IsActive(ClientInstallGrant grant)
    {
        var now = clock.GetUtcNow();
        var code = grant.EnrollmentCode;
        return grant.RevokedAtUtc is null && grant.ExpiresAtUtc > now &&
            code.RevokedAtUtc is null && code.ValidFromUtc <= now && code.ValidToUtc > now &&
            (!code.MaxUses.HasValue || code.Uses < code.MaxUses.Value);
    }

    private static string ValidatedPublicBase(string? value, string key)
    {
        if (string.IsNullOrWhiteSpace(value) || !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || uri.Port != 443 || uri.UserInfo.Length != 0 ||
            uri.HostNameType != UriHostNameType.Dns || !uri.Host.Contains('.') ||
            uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase) ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new InvalidOperationException($"Configure {key} as a public HTTPS origin before creating install links.");
        return uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes)
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
