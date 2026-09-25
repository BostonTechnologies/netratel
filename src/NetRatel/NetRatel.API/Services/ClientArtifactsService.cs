using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NuGet.Versioning;
using NetRatel.Application.Agents;
using NetRatel.Application.Artifacts;
using NetRatel.Application.Events;
using NetRatel.API.Models;
using NetRatel.Shared;
using NetRatel.Infrastructure.Persistence;

namespace NetRatel.API.Services;

public sealed class ClientArtifactsOptions
{
    public string StorageRoot { get; set; } = Path.Combine("artifacts", "client-store");
    public string LegacyRoot { get; set; } = Path.Combine("artifacts", "client");
    public bool EnableFallbackScan { get; set; } = true;
    public string? PublicBaseUrl { get; set; }

    // Test-only crash injection. Normal configuration cannot bind a delegate,
    // so production paths leave this unset.
    public Action<ClientArtifactCommitPhase>? TestCommitHook { get; set; }
}

public enum ClientArtifactCommitPhase
{
    AfterAtomicDirectoryMoveBeforeDatabaseCommit,
    BeforeAtomicDirectoryMove
}

public interface IClientArtifactsService
{
    Task<ClientArtifactListDto> ListAsync(string? rid, int skip, int take, CancellationToken ct);
    Task<ClientArtifactListDto> ListAsync(string? rid, int skip, int take, string? search, CancellationToken ct) =>
        ListAsync(rid, skip, take, ct);
    Task<ClientArtifactSummaryDto?> GetLatestAsync(string rid, CancellationToken ct);
    Task<ClientArtifactSummaryDto?> GetMetadataAsync(string rid, string version, CancellationToken ct);
    Task<ClientArtifactUploadResultDto> UploadAsync(IFormFile file, string rid, string version, string? notes, string? uploadedBy, CancellationToken ct);
    Task<ClientArtifactUploadResultDto> ImportVerifiedAsync(IFormFile file, string rid, string version,
        ClientArtifactImportProvenance provenance, string? importedBy, CancellationToken ct) =>
        throw new NotSupportedException();
    async Task CompleteImportVisibilityAsync(Guid operationId, ClientReleaseImportClaim claim, CancellationToken ct)
    {
        await CompleteImportVisibilityAsync(operationId, ct).ConfigureAwait(false);
    }
    Task CompleteImportVisibilityAsync(Guid operationId, CancellationToken ct) =>
        throw new NotSupportedException();
    Task<ClientArtifactDownloadResult> DownloadAsync(string rid, string versionOrLatest, bool allowFallback, CancellationToken ct);
    Task<ClientArtifactDownloadResult> DownloadRawAsync(string rid, string versionOrLatest, CancellationToken ct);
    Task<ClientArtifactDownloadResult> DownloadForClientAsync(ClientDownloadRequest request, CancellationToken ct);
    Task DeleteAsync(string rid, string version, CancellationToken ct);
    Task<ClientArtifactDownloadResult> RunFallbackScanAsync(string rid, string? version, CancellationToken ct);
}

public sealed record ClientArtifactImportProvenance(
    Guid OperationId,
    string Repository,
    string Tag,
    long AssetId,
    string AssetName,
    string SourceSha256,
    string BuildCommit,
    string AdapterContract,
    Guid? LeaseOwner = null,
    long? LeaseGeneration = null);

public sealed record ClientArtifactDownloadResult(
    Stream Content,
    string ContentType,
    string FileName,
    bool IsFallback,
    ClientArtifactSummaryDto? Metadata,
    string? EnrollmentCode = null,
    DateTimeOffset? EnrollmentExpiresUtc = null);

public sealed class ClientArtifactsService : IClientArtifactsService
{
    private static readonly HashSet<string> AllowedRids = new(StringComparer.OrdinalIgnoreCase)
    {
        "win-x64",
        "win-arm64",
        "linux-x64",
        "osx-x64",
        "osx-arm64"
    };

    private static readonly string[] AllowedExtensions = [".zip", ".7z", ".tar.gz"];

    private static readonly JsonSerializerOptions MetadataSerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly IWebHostEnvironment _environment;
    private readonly IConfiguration _configuration;
    private readonly ClientArtifactsOptions _options;
    private readonly ILogger<ClientArtifactsService> _logger;
    private readonly AgentAuthOptions _agentAuthOptions;
    private readonly ITenantLookupService _tenantLookupService;
    private readonly IEnrollmentCodeIssueService _enrollmentCodeIssueService;
    private readonly IArtifactZipInjectionService _zipInjectionService;
    private readonly IEventRecorder _events;
    private readonly ICorrelationContext _correlation;
    private readonly OrchestratorDbContext _db;
    private readonly TimeProvider _clock;

    public ClientArtifactsService(
        IWebHostEnvironment environment,
        IOptions<ClientArtifactsOptions> options,
        IConfiguration configuration,
        ILogger<ClientArtifactsService> logger,
        IOptions<AgentAuthOptions> agentAuthOptions,
        ITenantLookupService tenantLookupService,
        IEnrollmentCodeIssueService enrollmentCodeIssueService,
        IArtifactZipInjectionService zipInjectionService,
        IEventRecorder events,
        ICorrelationContext correlation,
        OrchestratorDbContext db,
        TimeProvider clock)
    {
        _environment = environment;
        _configuration = configuration;
        _logger = logger;
        _options = options.Value;
        _agentAuthOptions = agentAuthOptions.Value;
        _tenantLookupService = tenantLookupService;
        _enrollmentCodeIssueService = enrollmentCodeIssueService;
        _zipInjectionService = zipInjectionService;
        _events = events;
        _correlation = correlation;
        _db = db;
        _clock = clock;
    }

    public async Task<ClientArtifactListDto> ListAsync(string? rid, int skip, int take, CancellationToken ct)
        => await ListAsync(rid, skip, take, null, ct).ConfigureAwait(false);

    public async Task<ClientArtifactListDto> ListAsync(string? rid, int skip, int take, string? search, CancellationToken ct)
    {
        var storageRoot = EnsureStorageRoot();
        skip = Math.Max(0, skip);
        take = take <= 0 ? 50 : Math.Min(take, 200);

        var summaries = new List<ClientArtifactSummaryDto>();

        if (string.IsNullOrWhiteSpace(rid))
        {
            foreach (var ridDir in Directory.EnumerateDirectories(storageRoot))
            {
                var ridName = Path.GetFileName(ridDir);
                if (IsCanonicalRidDirectory(ridName, ridDir, storageRoot))
                {
                    await CollectRidSummariesAsync(ridName, ridDir, summaries, ct);
                }
            }
        }
        else
        {
            var normalizedRid = NormalizeRid(rid);
            var ridDir = Path.Combine(storageRoot, normalizedRid);
            if (Directory.Exists(ridDir))
            {
                await CollectRidSummariesAsync(normalizedRid, ridDir, summaries, ct);
            }
        }

        var ordered = summaries
            .OrderBy(s => s.Rid, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(s => ParseSemanticVersion(s.Version))
            .ThenByDescending(s => s.UploadedAt)
            .AsEnumerable();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var query = search.Trim();
            ordered = ordered.Where(summary =>
                summary.Rid.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                summary.Version.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                summary.FileName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                summary.Sha256.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                (summary.Notes?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        var filtered = ordered.ToList();

        var paged = filtered.Skip(skip).Take(take).ToList();
        _logger.LogInformation(
            "Listed {Count} client artifacts from {StorageRoot} for rid filter {Rid}",
            filtered.Count,
            storageRoot,
            string.IsNullOrWhiteSpace(rid) ? "<all>" : rid);

        return new ClientArtifactListDto
        {
            Total = filtered.Count,
            Items = paged
        };
    }

    public async Task<ClientArtifactSummaryDto?> GetLatestAsync(string rid, CancellationToken ct)
    {
        var normalizedRid = NormalizeRid(rid);
        var storageRoot = EnsureStorageRoot();
        var ridDir = Path.Combine(storageRoot, normalizedRid);
        if (!Directory.Exists(ridDir))
        {
            return null;
        }

        var summaries = new List<ClientArtifactSummaryDto>();
        await CollectRidSummariesAsync(normalizedRid, ridDir, summaries, ct);
        return summaries
            .OrderByDescending(s => ParseSemanticVersion(s.Version))
            .ThenByDescending(s => s.UploadedAt)
            .FirstOrDefault();
    }

    public async Task<ClientArtifactSummaryDto?> GetMetadataAsync(string rid, string version, CancellationToken ct)
    {
        var normalizedRid = NormalizeRid(rid);
        var normalizedVersion = NormalizeVersion(version);
        var metadata = await ReadMetadataAsync(normalizedRid, normalizedVersion, ct);
        return metadata?.ToSummary();
    }

    public Task<ClientArtifactUploadResultDto> UploadAsync(IFormFile file, string rid, string version, string? notes, string? uploadedBy, CancellationToken ct) =>
        StoreAsync(file, rid, version, notes, uploadedBy, null, ct);

    public Task<ClientArtifactUploadResultDto> ImportVerifiedAsync(IFormFile file, string rid, string version,
        ClientArtifactImportProvenance provenance, string? importedBy, CancellationToken ct) =>
        provenance.LeaseOwner.HasValue || provenance.LeaseGeneration.HasValue
            ? StoreImportedAsync(file, rid, version, importedBy, provenance, ct)
            : StoreAsync(file, rid, version, $"Imported from GitHub release {provenance.Tag}", importedBy, provenance, ct);

    public async Task CompleteImportVisibilityAsync(Guid operationId, ClientReleaseImportClaim claim,
        CancellationToken ct)
    {
        if (claim.OperationId != operationId)
            throw new InvalidOperationException("The import visibility claim does not match the operation.");

        await using var transaction = await _db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        await EnsureImportClaimLockedAsync(claim, ct).ConfigureAwait(false);
        var hasAssets = await _db.ClientReleaseImportAssets.AsNoTracking()
            .AnyAsync(x => x.OperationId == operationId, ct).ConfigureAwait(false);
        var incomplete = await _db.ClientReleaseImportAssets.AsNoTracking()
            .AnyAsync(x => x.OperationId == operationId &&
                x.State != ClientReleaseImportAssetState.Imported, ct).ConfigureAwait(false);
        if (!hasAssets || incomplete)
            throw new InvalidOperationException("The imported client pack is incomplete.");

        await WriteImportVisibilityMarkerAsync(operationId, ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task CompleteImportVisibilityAsync(Guid operationId, CancellationToken ct)
    {
        await WriteImportVisibilityMarkerAsync(operationId, ct).ConfigureAwait(false);
    }

    private async Task WriteImportVisibilityMarkerAsync(Guid operationId, CancellationToken ct)
    {
        var marker = ImportMarkerPath(operationId);
        if (File.Exists(marker)) return;
        var directory = Path.GetDirectoryName(marker)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{operationId:N}-{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temporary, "netratel.client.import-complete.v1\n", ct).ConfigureAwait(false);
            try { File.Move(temporary, marker); }
            catch (IOException) when (File.Exists(marker))
            {
                _logger.LogDebug("Import visibility marker for operation {OperationId} already exists.", operationId);
            }
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private async Task<ClientArtifactUploadResultDto> StoreImportedAsync(
        IFormFile file, string rid, string version, string? importedBy,
        ClientArtifactImportProvenance provenance, CancellationToken ct)
    {
        if (file is null or { Length: <= 0 })
            throw new RequestValidationException("file", "Upload must include a non-empty file.");
        if (!provenance.LeaseOwner.HasValue || !provenance.LeaseGeneration.HasValue)
            throw new InvalidOperationException("Claim-aware import provenance requires a complete lease claim.");

        var normalizedRid = NormalizeRid(rid);
        var normalizedVersion = NormalizeVersion(version);
        var extension = NormalizeExtension(file.FileName);
        if (!AllowedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            throw new RequestValidationException("file", "Artifacts must be .zip, .7z, or .tar.gz files.");

        var contentType = ResolveContentType(extension, file.ContentType);
        var storageRoot = EnsureStorageRoot();
        var fileName = BuildFileName(normalizedRid, normalizedVersion, extension);
        var finalDirectory = Path.Combine(storageRoot, normalizedRid, normalizedVersion);
        var stagingDirectory = Path.Combine(storageRoot, ".import-staging", provenance.OperationId.ToString("N"),
            $"{provenance.LeaseOwner:N}-{provenance.LeaseGeneration}", normalizedRid, normalizedVersion);
        Directory.CreateDirectory(stagingDirectory);
        var stagedArtifact = Path.Combine(stagingDirectory, fileName);
        var stagedMetadata = Path.Combine(stagingDirectory, "metadata.json");

        try
        {
            await using (var destination = new FileStream(stagedArtifact, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 64 * 1024, useAsync: true))
            {
                await file.CopyToAsync(destination, ct).ConfigureAwait(false);
                await destination.FlushAsync(ct).ConfigureAwait(false);
            }

            var sha256 = await ComputeSha256Async(stagedArtifact, ct).ConfigureAwait(false);
            var size = new FileInfo(stagedArtifact).Length;
            await ClientArtifactManifestValidator.ValidateFileAsync(
                stagedArtifact, normalizedRid, normalizedVersion, ct).ConfigureAwait(false);
            var metadata = new ClientArtifactMetadata
            {
                Rid = normalizedRid,
                Version = normalizedVersion,
                FileName = fileName,
                Size = size,
                Sha256 = sha256,
                UploadedAt = DateTimeOffset.UtcNow,
                UploadedBy = string.IsNullOrWhiteSpace(importedBy) ? null : importedBy,
                Notes = $"Imported from GitHub release {provenance.Tag}",
                ContentType = contentType,
                ImportOperationId = provenance.OperationId,
                SourceRepository = provenance.Repository,
                SourceTag = provenance.Tag,
                SourceAssetId = provenance.AssetId,
                SourceAssetName = provenance.AssetName,
                SourceSha256 = provenance.SourceSha256,
                SourceCommit = provenance.BuildCommit,
                ImportAdapterContract = provenance.AdapterContract
            };
            await using (var metadataStream = new FileStream(stagedMetadata, FileMode.CreateNew,
                FileAccess.Write, FileShare.None, 16 * 1024, useAsync: true))
            {
                await JsonSerializer.SerializeAsync(metadataStream, metadata, MetadataSerializerOptions, ct)
                    .ConfigureAwait(false);
                await metadataStream.FlushAsync(ct).ConfigureAwait(false);
            }

            // Keep network/file preparation outside the database transaction. The short
            // row lock below fences the claim while the same-filesystem directory
            // rename commits the immutable archive+metadata pair; a takeover cannot
            // pass the claim check until this filesystem decision is durable.
            await using var transaction = await _db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
            await EnsureImportClaimLockedAsync(new ClientReleaseImportClaim(
                provenance.OperationId, provenance.LeaseOwner.Value, provenance.LeaseGeneration.Value), ct)
                .ConfigureAwait(false);
            var created = await CommitImportedDirectoryAsync(stagingDirectory, finalDirectory, metadata, ct)
                .ConfigureAwait(false);
            if (created)
                _options.TestCommitHook?.Invoke(ClientArtifactCommitPhase.AfterAtomicDirectoryMoveBeforeDatabaseCommit);
            await transaction.CommitAsync(ct).ConfigureAwait(false);

            if (created)
            {
                await _events.RecordAsync(new DomainEvent
                {
                    EventType = NetRatelEventTypes.Artifact.Uploaded,
                    Source = "Api",
                    CorrelationId = _correlation.Current ?? $"netratel-artifact-{normalizedRid}-{normalizedVersion}",
                    EntityId = $"{normalizedRid}:{normalizedVersion}",
                    Severity = "Success",
                    Message = $"Client artifact imported for {normalizedRid} {normalizedVersion}.",
                    Payload = new { rid = normalizedRid, version = normalizedVersion, fileName, importedBy, size }
                }, ct).ConfigureAwait(false);
            }

            return new ClientArtifactUploadResultDto
            {
                Artifact = metadata.ToSummary(),
                Created = created
            };
        }
        finally
        {
            try
            {
                var claimDirectory = Path.GetDirectoryName(Path.GetDirectoryName(stagingDirectory)!)!;
                if (Directory.Exists(claimDirectory)) Directory.Delete(claimDirectory, recursive: true);
            }
            catch (Exception cleanupException)
            {
                _logger.LogWarning(cleanupException,
                    "Failed to remove claim-specific client import staging directory {StagingDirectory}.", stagingDirectory);
            }
        }
    }

    private async Task<bool> CommitImportedDirectoryAsync(string stagingDirectory, string finalDirectory,
        ClientArtifactMetadata expected, CancellationToken ct)
    {
        var finalArtifact = Path.Combine(finalDirectory, expected.FileName);
        var finalMetadata = Path.Combine(finalDirectory, "metadata.json");
        var artifactExists = File.Exists(finalArtifact);
        var metadataExists = File.Exists(finalMetadata);

        if (artifactExists && metadataExists)
        {
            var existing = await ReadMetadataFileAsync(finalMetadata, ct).ConfigureAwait(false);
            await VerifyImportedPairAsync(finalArtifact, existing, expected, ct).ConfigureAwait(false);
            return false;
        }

        if (Directory.Exists(finalDirectory) && Directory.EnumerateFileSystemEntries(finalDirectory).Any())
        {
            if (metadataExists)
            {
                var existing = await ReadMetadataFileAsync(finalMetadata, ct).ConfigureAwait(false);
                EnsureMatchingImportedMetadata(existing, expected);
            }
            else if (!artifactExists)
            {
                throw new ClientArtifactConflictException(expected.Rid, expected.Version);
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(finalDirectory)!);
        if (!artifactExists && !metadataExists && !Directory.Exists(finalDirectory))
        {
            Directory.Move(stagingDirectory, finalDirectory);
            return true;
        }

        Directory.CreateDirectory(finalDirectory);
        var stagedArtifact = Path.Combine(stagingDirectory, expected.FileName);
        var stagedMetadata = Path.Combine(stagingDirectory, "metadata.json");
        if (metadataExists)
        {
            var existing = await ReadMetadataFileAsync(finalMetadata, ct).ConfigureAwait(false);
            EnsureMatchingImportedMetadata(existing, expected);
        }
        if (artifactExists)
        {
            await VerifyImportedArchiveAsync(finalArtifact, expected, ct).ConfigureAwait(false);
        }
        else
        {
            File.Move(stagedArtifact, finalArtifact);
        }
        if (!metadataExists) File.Move(stagedMetadata, finalMetadata);
        return true;
    }

    private async Task VerifyImportedPairAsync(string artifactPath, ClientArtifactMetadata existing,
        ClientArtifactMetadata expected, CancellationToken ct)
    {
        EnsureMatchingImportedMetadata(existing, expected);
        await VerifyImportedArchiveAsync(artifactPath, expected, ct).ConfigureAwait(false);
    }

    private async Task VerifyImportedArchiveAsync(string artifactPath, ClientArtifactMetadata expected,
        CancellationToken ct)
    {
        if (new FileInfo(artifactPath).Length != expected.Size ||
            !string.Equals(await ComputeSha256Async(artifactPath, ct).ConfigureAwait(false), expected.Sha256,
                StringComparison.OrdinalIgnoreCase))
            throw new ClientArtifactConflictException(expected.Rid, expected.Version);
        await ClientArtifactManifestValidator.ValidateFileAsync(
            artifactPath, expected.Rid, expected.Version, ct).ConfigureAwait(false);
    }

    private static void EnsureMatchingImportedMetadata(ClientArtifactMetadata actual,
        ClientArtifactMetadata expected)
    {
        if (!string.Equals(actual.Rid, expected.Rid, StringComparison.Ordinal) ||
            !string.Equals(actual.Version, expected.Version, StringComparison.Ordinal) ||
            !string.Equals(actual.FileName, expected.FileName, StringComparison.Ordinal) ||
            actual.Size != expected.Size ||
            !string.Equals(actual.Sha256, expected.Sha256, StringComparison.OrdinalIgnoreCase) ||
            actual.ImportOperationId != expected.ImportOperationId ||
            !string.Equals(actual.SourceRepository, expected.SourceRepository, StringComparison.Ordinal) ||
            !string.Equals(actual.SourceTag, expected.SourceTag, StringComparison.Ordinal) ||
            actual.SourceAssetId != expected.SourceAssetId ||
            !string.Equals(actual.SourceAssetName, expected.SourceAssetName, StringComparison.Ordinal) ||
            !string.Equals(actual.SourceSha256, expected.SourceSha256, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(actual.SourceCommit, expected.SourceCommit, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(actual.ImportAdapterContract, expected.ImportAdapterContract, StringComparison.Ordinal))
            throw new ClientArtifactConflictException(expected.Rid, expected.Version);
    }

    private async Task EnsureImportClaimLockedAsync(ClientReleaseImportClaim claim, CancellationToken ct)
    {
        await _db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT \"Id\" FROM \"ClientReleaseImportOperations\" WHERE \"Id\" = {claim.OperationId} FOR UPDATE",
            ct).ConfigureAwait(false);
        var now = _clock.GetUtcNow();
        var current = await _db.ClientReleaseImportOperations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == claim.OperationId, ct).ConfigureAwait(false);
        if (current is null || current.LeaseOwner != claim.LeaseOwner ||
            current.LeaseGeneration != claim.LeaseGeneration ||
            current.LeaseUntilUtc <= now || current.CancellationRequested ||
            current.State < ClientReleaseImportState.Resolving ||
            current.State > ClientReleaseImportState.Importing)
            throw new InvalidOperationException("Client release import lease was lost.");
    }

    private async Task<ClientArtifactUploadResultDto> StoreAsync(IFormFile file, string rid, string version,
        string? notes, string? uploadedBy, ClientArtifactImportProvenance? provenance, CancellationToken ct)
    {
        if (file is null or { Length: <= 0 })
            throw new RequestValidationException("file", "Upload must include a non-empty file.");

        var normalizedRid = NormalizeRid(rid);
        var normalizedVersion = NormalizeVersion(version);
        var extension = NormalizeExtension(file.FileName);
        if (!AllowedExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            throw new RequestValidationException("file", "Artifacts must be .zip, .7z, or .tar.gz files.");

        var contentType = ResolveContentType(extension, file.ContentType);
        var storageRoot = EnsureStorageRoot();
        var versionDir = Path.Combine(storageRoot, normalizedRid, normalizedVersion);
        var fileName = BuildFileName(normalizedRid, normalizedVersion, extension);
        var artifactPath = Path.Combine(versionDir, fileName);
        var metadataPath = Path.Combine(versionDir, "metadata.json");
        var existingArtifact = File.Exists(artifactPath);
        var existingMetadata = File.Exists(metadataPath);
        if (existingArtifact != existingMetadata)
            throw new ClientArtifactConflictException(normalizedRid, normalizedVersion);

        var stagingDirectory = Path.Combine(storageRoot, ".upload-staging", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingDirectory);
        var stagedArtifact = Path.Combine(stagingDirectory, fileName);
        var stagedMetadata = Path.Combine(stagingDirectory, "metadata.json");

        try
        {
            await using (var destination = new FileStream(stagedArtifact, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 64 * 1024, useAsync: true))
            {
                await file.CopyToAsync(destination, ct).ConfigureAwait(false);
                await destination.FlushAsync(ct).ConfigureAwait(false);
            }

            var sha256 = await ComputeSha256Async(stagedArtifact, ct).ConfigureAwait(false);
            var size = new FileInfo(stagedArtifact).Length;
            await ClientArtifactManifestValidator.ValidateFileAsync(
                stagedArtifact, normalizedRid, normalizedVersion, ct).ConfigureAwait(false);

            if (existingArtifact)
            {
                var existing = await ReadMetadataFileAsync(metadataPath, ct).ConfigureAwait(false);
                if (!string.Equals(existing.Sha256, sha256, StringComparison.OrdinalIgnoreCase) || existing.Size != size)
                    throw new ClientArtifactConflictException(normalizedRid, normalizedVersion);
                if (provenance is not null &&
                    (existing.ImportOperationId != provenance.OperationId ||
                     !string.Equals(existing.SourceSha256, provenance.SourceSha256, StringComparison.OrdinalIgnoreCase)) ||
                    provenance is null && existing.ImportOperationId.HasValue && !IsVisible(existing))
                    throw new ClientArtifactConflictException(normalizedRid, normalizedVersion);
                return new ClientArtifactUploadResultDto { Artifact = existing.ToSummary(), Created = false };
            }

            var metadata = new ClientArtifactMetadata
            {
                Rid = normalizedRid,
                Version = normalizedVersion,
                FileName = fileName,
                Size = size,
                Sha256 = sha256,
                UploadedAt = DateTimeOffset.UtcNow,
                UploadedBy = string.IsNullOrWhiteSpace(uploadedBy) ? null : uploadedBy,
                Notes = string.IsNullOrWhiteSpace(notes) ? null : notes,
                ContentType = contentType,
                ImportOperationId = provenance?.OperationId,
                SourceRepository = provenance?.Repository,
                SourceTag = provenance?.Tag,
                SourceAssetId = provenance?.AssetId,
                SourceAssetName = provenance?.AssetName,
                SourceSha256 = provenance?.SourceSha256,
                SourceCommit = provenance?.BuildCommit,
                ImportAdapterContract = provenance?.AdapterContract
            };

            await using (var metaStream = new FileStream(stagedMetadata, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 16 * 1024, useAsync: true))
            {
                await JsonSerializer.SerializeAsync(metaStream, metadata, MetadataSerializerOptions, ct)
                    .ConfigureAwait(false);
                await metaStream.FlushAsync(ct).ConfigureAwait(false);
            }

            _options.TestCommitHook?.Invoke(ClientArtifactCommitPhase.BeforeAtomicDirectoryMove);
            Directory.CreateDirectory(Path.GetDirectoryName(versionDir)!);
            try
            {
                Directory.Move(stagingDirectory, versionDir);
            }
            catch (IOException) when (Directory.Exists(versionDir))
            {
                throw new ClientArtifactConflictException(normalizedRid, normalizedVersion);
            }
            _logger.LogInformation(
                "Stored client artifact {Rid}/{Version} at {ArtifactPath}; size={Size}; sha256={Sha256}",
                normalizedRid,
                normalizedVersion,
                artifactPath,
                size,
                sha256);

            await _events.RecordAsync(new DomainEvent
            {
                EventType = NetRatelEventTypes.Artifact.Uploaded,
                Source = "Api",
                CorrelationId = _correlation.Current ?? $"netratel-artifact-{normalizedRid}-{normalizedVersion}",
                EntityId = $"{normalizedRid}:{normalizedVersion}",
                Severity = "Success",
                Message = $"Client artifact uploaded for {normalizedRid} {normalizedVersion}.",
                Payload = new
                {
                    rid = normalizedRid,
                    version = normalizedVersion,
                    fileName,
                    uploadedBy,
                    size
                }
            }, ct);

            return new ClientArtifactUploadResultDto
            {
                Artifact = metadata.ToSummary(),
                Created = true
            };
        }
        finally
        {
            try
            {
                if (Directory.Exists(stagingDirectory)) Directory.Delete(stagingDirectory, recursive: true);
            }
            catch (Exception cleanupException)
            {
                _logger.LogWarning(cleanupException,
                    "Failed to remove temporary client artifact directory {TemporaryPath}.", stagingDirectory);
            }
        }
    }

    public async Task<ClientArtifactDownloadResult> DownloadAsync(string rid, string versionOrLatest, bool allowFallback, CancellationToken ct)
    {
        var normalizedRid = NormalizeRid(rid);
        var normalizedVersion = NormalizeVersion(versionOrLatest);

        var metadata = await TryResolveMetadataAsync(normalizedRid, normalizedVersion, ct);
        if (metadata is not null)
        {
            var artifactPath = Path.Combine(EnsureStorageRoot(), normalizedRid, metadata.Version, metadata.FileName);
            if (File.Exists(artifactPath))
            {
                var extension = Path.GetExtension(metadata.FileName);
                var isZip = string.Equals(extension, ".zip", StringComparison.OrdinalIgnoreCase);
                if (!isZip)
                {
                    var directStream = new FileStream(artifactPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
                    return new ClientArtifactDownloadResult(directStream, metadata.ContentType ?? "application/octet-stream", metadata.FileName, false, metadata.ToSummary());
                }

                var stream = new FileStream(artifactPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);

                return new ClientArtifactDownloadResult(
                    stream,
                    metadata.ContentType ?? "application/zip",
                    metadata.FileName,
                    false,
                    metadata.ToSummary());
            }

            _logger.LogWarning("Metadata found for {Rid}/{Version} but file missing at {Path}", normalizedRid, metadata.Version, artifactPath);
        }

        if (!allowFallback || !_options.EnableFallbackScan)
        {
            throw new FileNotFoundException($"No stored artifact found for RID '{normalizedRid}' and version '{versionOrLatest}'.");
        }

        _logger.LogWarning("Falling back to legacy artifact scan for {Rid}/{Version}", normalizedRid, versionOrLatest);
        return await BuildLegacyDownloadAsync(normalizedRid, versionOrLatest, null, ct, quickZip: true);
    }

    public async Task<ClientArtifactDownloadResult> DownloadRawAsync(string rid, string versionOrLatest, CancellationToken ct)
    {
        var normalizedRid = NormalizeRid(rid);
        var normalizedVersion = NormalizeVersion(versionOrLatest);

        var metadata = await TryResolveMetadataAsync(normalizedRid, normalizedVersion, ct);
        if (metadata is null)
        {
            throw new FileNotFoundException($"No stored artifact found for RID '{normalizedRid}' and version '{versionOrLatest}'.");
        }

        var artifactPath = Path.Combine(EnsureStorageRoot(), normalizedRid, metadata.Version, metadata.FileName);
        if (!File.Exists(artifactPath))
        {
            _logger.LogWarning("Metadata found for {Rid}/{Version} but file missing at {Path}", normalizedRid, metadata.Version, artifactPath);
            throw new FileNotFoundException($"Artifact file missing for RID '{normalizedRid}' and version '{metadata.Version}'.");
        }

        var stream = new FileStream(artifactPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
        _logger.LogInformation(
            "Raw client artifact download for {Rid}/{Version} from {ArtifactPath}; size={Size}",
            normalizedRid,
            metadata.Version,
            artifactPath,
            metadata.Size);
        return new ClientArtifactDownloadResult(
            stream,
            metadata.ContentType ?? "application/octet-stream",
            metadata.FileName,
            false,
            metadata.ToSummary());
    }

    public async Task<ClientArtifactDownloadResult> DownloadForClientAsync(ClientDownloadRequest request, CancellationToken ct)
    {
        var normalizedRid = NormalizeRid(request.RuntimeId);
        var normalizedVersion = NormalizeVersion(request.Version);
        await ValidateTenantExistsAsync(request.TenantId, ct);

        var metadata = await TryResolveMetadataAsync(normalizedRid, normalizedVersion, ct);
        if (metadata is not null)
        {
            var artifactPath = Path.Combine(EnsureStorageRoot(), normalizedRid, metadata.Version, metadata.FileName);
            if (File.Exists(artifactPath))
            {
                var extension = Path.GetExtension(metadata.FileName);
                var isZip = string.Equals(extension, ".zip", StringComparison.OrdinalIgnoreCase);
                if (!isZip)
                {
                    if (request.InjectEnrollment)
                    {
                        throw new RequestValidationException("runtimeId", "InjectEnrollment requires a .zip artifact.");
                    }

                    var directStream = new FileStream(artifactPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
                    return new ClientArtifactDownloadResult(directStream, metadata.ContentType ?? "application/octet-stream", metadata.FileName, false, metadata.ToSummary());
                }

                await using var fs = new FileStream(artifactPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
                var ms = await CloneToMemoryAsync(fs, ct);
                EnrollmentCodeIssueResult? issuedEnrollment = null;
                var finalStream = ms;
                if (request.InjectEnrollment)
                {
                    (finalStream, issuedEnrollment) = await InjectEnrollmentIntoZipAsync(ms, request, normalizedRid, metadata.Version, ct);
                }
                finalStream.Position = 0;

                return new ClientArtifactDownloadResult(
                    finalStream,
                    metadata.ContentType ?? "application/zip",
                    BuildClientDownloadFileName(request.TenantId, normalizedRid, metadata.Version),
                    false,
                    metadata.ToSummary(),
                    issuedEnrollment?.Code,
                    issuedEnrollment?.ValidToUtc);
            }
        }

        if (!_options.EnableFallbackScan)
        {
            throw new FileNotFoundException($"No stored artifact found for RID '{normalizedRid}' and version '{request.Version}'.");
        }

        _logger.LogWarning("Client download fallback triggered for tenant {TenantId}, rid {Rid}, version {Version}", request.TenantId, normalizedRid, request.Version);
        var fallback = await BuildLegacyDownloadAsync(normalizedRid, request.Version, request, ct, quickZip: false);
        if (!request.InjectEnrollment)
        {
            fallback.Content.Position = 0;
            return fallback with
            {
                FileName = BuildClientDownloadFileName(request.TenantId, normalizedRid, fallback.Metadata?.Version ?? request.Version)
            };
        }

        var (fallbackInjected, fallbackEnrollment) = await InjectEnrollmentIntoZipAsync(
            fallback.Content,
            request,
            normalizedRid,
            fallback.Metadata?.Version ?? request.Version,
            ct);
        fallbackInjected.Position = 0;
        return fallback with
        {
            Content = fallbackInjected,
            FileName = BuildClientDownloadFileName(request.TenantId, normalizedRid, fallback.Metadata?.Version ?? request.Version),
            EnrollmentCode = fallbackEnrollment?.Code,
            EnrollmentExpiresUtc = fallbackEnrollment?.ValidToUtc
        };
    }

    public async Task DeleteAsync(string rid, string version, CancellationToken ct)
    {
        var normalizedRid = NormalizeRid(rid);
        var normalizedVersion = NormalizeVersion(version);
        var storageRoot = EnsureStorageRoot();
        var versionDir = Path.Combine(storageRoot, normalizedRid, normalizedVersion);
        var metadataPath = Path.Combine(versionDir, "metadata.json");

        if (!File.Exists(metadataPath))
        {
            throw new FileNotFoundException($"No artifact metadata found for RID '{normalizedRid}' and version '{normalizedVersion}'.");
        }

        var metadata = await ReadMetadataFileAsync(metadataPath, ct);
        if (metadata.ImportOperationId.HasValue)
            throw new ClientArtifactConflictException(normalizedRid, normalizedVersion);
        var artifactPath = Path.Combine(versionDir, metadata.FileName);

        if (File.Exists(artifactPath))
        {
            File.Delete(artifactPath);
        }

        File.Delete(metadataPath);

        if (!Directory.EnumerateFileSystemEntries(versionDir).Any())
        {
            Directory.Delete(versionDir, recursive: false);
            var ridDir = Path.GetDirectoryName(versionDir);
            if (!string.IsNullOrWhiteSpace(ridDir) && Directory.Exists(ridDir) && !Directory.EnumerateFileSystemEntries(ridDir).Any())
            {
                Directory.Delete(ridDir, recursive: false);
            }
        }

        await _events.RecordAsync(new DomainEvent
        {
            EventType = NetRatelEventTypes.Artifact.Deleted,
            Source = "Api",
            CorrelationId = _correlation.Current ?? $"netratel-artifact-{normalizedRid}-{normalizedVersion}",
            EntityId = $"{normalizedRid}:{normalizedVersion}",
            Severity = "Warning",
            Message = $"Client artifact deleted for {normalizedRid} {normalizedVersion}.",
            Payload = new
            {
                rid = normalizedRid,
                version = normalizedVersion,
                metadata.FileName
            }
        }, ct);
    }

    public async Task<ClientArtifactDownloadResult> RunFallbackScanAsync(string rid, string? version, CancellationToken ct)
    {
        if (!_options.EnableFallbackScan)
        {
            throw new InvalidOperationException("Fallback scan is disabled by configuration.");
        }

        var normalizedRid = NormalizeRid(rid);
        var resolvedVersion = version is null ? "latest" : NormalizeVersion(version);
        return await BuildLegacyDownloadAsync(normalizedRid, resolvedVersion, null, ct, quickZip: true);
    }

    private static async Task<MemoryStream> CloneToMemoryAsync(Stream source, CancellationToken ct)
    {
        var destination = new MemoryStream();
        await source.CopyToAsync(destination, 64 * 1024, ct);
        destination.Position = 0;
        return destination;
    }

    private async Task ValidateTenantExistsAsync(int tenantId, CancellationToken ct)
    {
        if (!await _tenantLookupService.TenantExistsAsync(tenantId, ct))
        {
            throw new RequestValidationException("tenantId", $"Tenant '{tenantId}' does not exist.");
        }
    }

    private async Task<(MemoryStream Stream, EnrollmentCodeIssueResult Enrollment)> InjectEnrollmentIntoZipAsync(
        Stream sourceZip,
        ClientDownloadRequest request,
        string runtimeId,
        string version,
        CancellationToken ct)
    {
        var enrollment = await ResolveEnrollmentForInjectionAsync(request, ct);
        var payload = new ClientInjectedEnrollmentPayload(
            Schema: "netratel.enroll.v1",
            TenantId: request.TenantId,
            EnrollmentCode: enrollment.Code,
            Issuer: _agentAuthOptions.Issuer,
            CreatedAtUtc: enrollment.CreatedAtUtc.UtcDateTime,
            ValidToUtc: enrollment.ValidToUtc.UtcDateTime);
        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(payload, new JsonSerializerOptions { WriteIndented = true });

        sourceZip.Position = 0;
        var bytes = await _zipInjectionService.InjectAsync(sourceZip, payloadBytes, "netratel.enroll.json", ct);

        _logger.LogInformation(
            "Client download generated. tenantId={TenantId}, runtimeId={RuntimeId}, injected={Injected}, enrollmentCodeId={EnrollmentCodeId}, version={Version}",
            request.TenantId,
            runtimeId,
            true,
            enrollment.EnrollmentCodeId,
            version);

        return (new MemoryStream(bytes), enrollment);
    }

    private async Task<EnrollmentCodeIssueResult> ResolveEnrollmentForInjectionAsync(ClientDownloadRequest request, CancellationToken ct)
    {
        try
        {
            if (request.EnrollmentCodeId.HasValue)
            {
                return await _enrollmentCodeIssueService.GetActiveAsync(request.EnrollmentCodeId.Value, request.TenantId, ct);
            }

            if (!request.ValidForMinutes.HasValue)
            {
                throw new RequestValidationException("validForMinutes", "ValidForMinutes is required when issuing an injected enrollment code.");
            }

            var maxUses = request.MaxUses ?? 1;
            return await _enrollmentCodeIssueService.IssueAsync(
                new EnrollmentCodeIssueRequest(
                    request.TenantId,
                    request.ValidForMinutes.Value,
                    maxUses,
                    CreatedBy: null,
                    Notes: "download-injected"),
                ct);
        }
        catch (AgentAuthException ex)
        {
            throw new RequestValidationException("injectEnrollment", ex.Message);
        }
    }

    private static string BuildClientDownloadFileName(int tenantId, string rid, string version)
        => $"netratel-client-{tenantId}-{rid}-{version}.zip";

    private async Task CollectRidSummariesAsync(string rid, string ridDir, List<ClientArtifactSummaryDto> destination, CancellationToken ct)
    {
        var storageRoot = EnsureStorageRoot();
        if (!IsCanonicalRidDirectory(rid, ridDir, storageRoot)) return;

        foreach (var versionDir in Directory.EnumerateDirectories(ridDir))
        {
            ct.ThrowIfCancellationRequested();
            var version = Path.GetFileName(versionDir);
            if (string.IsNullOrWhiteSpace(version) ||
                (!IsLatest(version) && !SemanticVersion.TryParse(version, out _)))
            {
                continue;
            }

            var metadataPath = Path.Combine(versionDir, "metadata.json");
            if (!File.Exists(metadataPath))
            {
                continue;
            }

            var metadata = await ReadMetadataFileAsync(metadataPath, ct);
            if (!string.Equals(metadata.Rid, rid, StringComparison.Ordinal) ||
                !string.Equals(metadata.Version, version, StringComparison.Ordinal))
            {
                continue;
            }
            if (!IsVisible(metadata)) continue;
            destination.Add(metadata.ToSummary());
        }
    }

    private static bool IsCanonicalRidDirectory(string? rid, string ridDir, string storageRoot) =>
        !string.IsNullOrWhiteSpace(rid) &&
        AllowedRids.Contains(rid) &&
        string.Equals(rid, rid.ToLowerInvariant(), StringComparison.Ordinal) &&
        string.Equals(
            Path.GetFullPath(ridDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(Path.Combine(storageRoot, rid))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private async Task<ClientArtifactMetadata?> TryResolveMetadataAsync(string rid, string normalizedVersion, CancellationToken ct)
    {
        var storageRoot = EnsureStorageRoot();
        var ridDir = Path.Combine(storageRoot, rid);
        if (!Directory.Exists(ridDir))
        {
            return null;
        }

        if (IsLatest(normalizedVersion))
        {
            var summaries = new List<ClientArtifactSummaryDto>();
            await CollectRidSummariesAsync(rid, ridDir, summaries, ct);
            var latest = summaries
                .OrderByDescending(s => ParseSemanticVersion(s.Version))
                .ThenByDescending(s => s.UploadedAt)
                .FirstOrDefault();
            if (latest is null)
            {
                return null;
            }

            return await ReadMetadataAsync(rid, latest.Version, ct);
        }

        return await ReadMetadataAsync(rid, normalizedVersion, ct);
    }

    private async Task<ClientArtifactMetadata?> ReadMetadataAsync(string rid, string version, CancellationToken ct)
    {
        var storageRoot = EnsureStorageRoot();
        var metadataPath = Path.Combine(storageRoot, rid, version, "metadata.json");
        if (!File.Exists(metadataPath))
        {
            return null;
        }

        var metadata = await ReadMetadataFileAsync(metadataPath, ct);
        return IsVisible(metadata) ? metadata : null;
    }

    private bool IsVisible(ClientArtifactMetadata metadata) =>
        !metadata.ImportOperationId.HasValue || File.Exists(ImportMarkerPath(metadata.ImportOperationId.Value));

    private string ImportMarkerPath(Guid operationId) =>
        Path.Combine(EnsureStorageRoot(), ".imports", $"{operationId:N}.complete");

    private async Task<ClientArtifactMetadata> ReadMetadataFileAsync(string metadataPath, CancellationToken ct)
    {
        await using var stream = new FileStream(metadataPath, FileMode.Open, FileAccess.Read, FileShare.Read, 32 * 1024, useAsync: true);
        var metadata = await JsonSerializer.DeserializeAsync<ClientArtifactMetadata>(stream, MetadataSerializerOptions, ct);
        if (metadata is null)
        {
            throw new InvalidOperationException($"Metadata file '{metadataPath}' could not be parsed.");
        }

        return metadata;
    }

    private async Task<ClientArtifactDownloadResult> BuildLegacyDownloadAsync(string rid, string version, ClientDownloadRequest? request, CancellationToken ct, bool quickZip)
    {
        var legacyRoot = ResolveLegacyRoot();
        var resolvedVersion = await ResolveLegacyVersionAsync(legacyRoot, rid, version, ct);
        var ridPath = Path.Combine(legacyRoot, resolvedVersion, rid);
        if (!Directory.Exists(ridPath))
        {
            throw new FileNotFoundException($"Legacy artifact layout not found for RID '{rid}' and version '{resolvedVersion}'.");
        }

        var archiveName = BuildFileName(rid, resolvedVersion, ".zip");
        var metadataSummary = new ClientArtifactSummaryDto
        {
            Rid = rid,
            Version = resolvedVersion,
            FileName = archiveName,
            Size = 0,
            Sha256 = string.Empty,
            UploadedAt = DateTimeOffset.UtcNow
        };

        if (quickZip)
        {
            var stream = await ZipDirectoryAsync(ridPath, ct);
            return new ClientArtifactDownloadResult(stream, "application/zip", archiveName, true, metadataSummary);
        }

        if (request is null)
        {
            throw new InvalidOperationException("Legacy download requires request details when quickZip is false.");
        }

        var legacyStream = await BuildLegacyClientZipAsync(ridPath, rid, resolvedVersion, request, ct);
        return new ClientArtifactDownloadResult(legacyStream, "application/zip", archiveName, true, metadataSummary);
    }

    private async Task<MemoryStream> ZipDirectoryAsync(string directory, CancellationToken ct)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                var entryName = Path.GetRelativePath(directory, file).Replace('\\', '/');
                archive.CreateEntryFromFile(file, entryName, CompressionLevel.SmallestSize);
            }

        }

        stream.Position = 0;
        return stream;
    }

    private async Task<MemoryStream> BuildLegacyClientZipAsync(string ridPath, string rid, string version, ClientDownloadRequest request, CancellationToken ct)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "netratel-legacy-client", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var binaryName = GetLegacyBinaryName(rid);
            var binaryPath = Path.Combine(ridPath, binaryName);
            if (!File.Exists(binaryPath))
            {
                throw new FileNotFoundException($"Legacy client binary missing: {binaryPath}");
            }

            var outputBinary = Path.Combine(tempDir, binaryName);
            File.Copy(binaryPath, outputBinary, overwrite: true);

            var settingsJson = BuildClientSettings(request);
            await File.WriteAllTextAsync(Path.Combine(tempDir, "clientsettings.json"), settingsJson, Encoding.UTF8, ct);

            var stream = new MemoryStream();
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                archive.CreateEntryFromFile(outputBinary, binaryName, CompressionLevel.SmallestSize);
                var entry = archive.CreateEntry("clientsettings.json", CompressionLevel.SmallestSize);
                await using var entryStream = entry.Open();
                var bytes = Encoding.UTF8.GetBytes(settingsJson);
                await entryStream.WriteAsync(bytes, ct);
            }

            stream.Position = 0;
            return stream;
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch (Exception cleanupException)
            {
                _logger.LogWarning(cleanupException, "Failed to remove temporary client artifact package directory {TemporaryPath}.", tempDir);
            }
        }
    }

    private string BuildClientSettings(ClientDownloadRequest request)
    {
        var devUrl = _configuration["ClientDefaults:Dev:ApiBaseUrl"] ?? "https://localhost:5005";
        var prodUrl = _configuration["ClientDefaults:Prod:ApiBaseUrl"] ?? "https://orch.example";
        var prodOrFallback = string.IsNullOrWhiteSpace(prodUrl) ? devUrl : prodUrl;

        var apiUrl = request.Environment switch
        {
            ClientEnvironment.Dev => devUrl,
            ClientEnvironment.Prod => prodOrFallback,
            ClientEnvironment.Both => prodOrFallback,
            _ => devUrl
        };

        var payload = new
        {
            TenantId = request.TenantId,
            Environment = (int)request.Environment,
            ApiBaseUrl = apiUrl
        };

        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    }

    private async Task<string> ResolveLegacyVersionAsync(string legacyRoot, string rid, string version, CancellationToken ct)
    {
        if (!Directory.Exists(legacyRoot))
        {
            throw new DirectoryNotFoundException($"Legacy artifacts root '{legacyRoot}' does not exist.");
        }

        if (!IsLatest(version))
        {
            var specificDir = Path.Combine(legacyRoot, version, rid);
            if (Directory.Exists(specificDir))
            {
                return version;
            }

            throw new FileNotFoundException($"Legacy artifact not found for version '{version}' and rid '{rid}'.");
        }

        var candidateVersions = Directory.EnumerateDirectories(legacyRoot)
            .Select(Path.GetFileName)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!)
            .Select(v => new { Version = v, Parsed = ParseSemanticVersion(v) })
            .OrderByDescending(x => x.Parsed)
            .Select(x => x.Version);

        foreach (var candidate in candidateVersions)
        {
            ct.ThrowIfCancellationRequested();
            var ridDir = Path.Combine(legacyRoot, candidate, rid);
            if (Directory.Exists(ridDir))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"Legacy artifact not found for RID '{rid}'.");
    }

    private string EnsureStorageRoot()
    {
        var root = _options.StorageRoot;
        if (!Path.IsPathRooted(root))
        {
            root = Path.Combine(_environment.ContentRootPath, root);
        }

        Directory.CreateDirectory(root);
        return root;
    }

    private string ResolveLegacyRoot()
    {
        var root = _options.LegacyRoot;
        if (!Path.IsPathRooted(root))
        {
            root = Path.Combine(_environment.ContentRootPath, root);
        }

        return root;
    }

    private static string NormalizeRid(string rid)
    {
        if (string.IsNullOrWhiteSpace(rid))
        {
            throw new RequestValidationException("rid", "RID is required.");
        }

        var normalized = rid.Trim().ToLowerInvariant();
        if (!AllowedRids.Contains(normalized))
        {
            throw new RequestValidationException("rid", $"Unsupported runtime identifier '{rid}'.");
        }

        return normalized;
    }

    private static string NormalizeVersion(string version)
    {
        if (string.IsNullOrWhiteSpace(version) || string.Equals(version.Trim(), "latest", StringComparison.OrdinalIgnoreCase))
        {
            return "latest";
        }

        var trimmed = version.Trim();
        if (!SemanticVersion.TryParse(trimmed, out _))
        {
            throw new RequestValidationException("version", "Version must be a valid semantic version.");
        }

        return trimmed;
    }

    private static bool IsLatest(string? value)
        => string.IsNullOrWhiteSpace(value) || string.Equals(value.Trim(), "latest", StringComparison.OrdinalIgnoreCase);

    private static SemanticVersion ParseSemanticVersion(string version)
        => SemanticVersion.TryParse(version, out var parsed) ? parsed : SemanticVersion.Parse("0.0.0");

    private static string BuildFileName(string rid, string version, string extension)
    {
        extension = extension.StartsWith('.') ? extension : $".{extension}";
        return $"NetRatel.Client-{rid}-{version}{extension}";
    }

    private static string NormalizeExtension(string fileName)
    {
        if (fileName.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
        {
            return ".tar.gz";
        }

        return Path.GetExtension(fileName).ToLowerInvariant();
    }

    private static string ResolveContentType(string extension, string? provided)
    {
        if (!string.IsNullOrWhiteSpace(provided))
        {
            return provided;
        }

        return extension switch
        {
            ".zip" => "application/zip",
            ".7z" => "application/x-7z-compressed",
            ".tar.gz" => "application/gzip",
            _ => "application/octet-stream"
        };
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string GetLegacyBinaryName(string rid)
        => rid.StartsWith("win-", StringComparison.OrdinalIgnoreCase)
            ? "NetRatel.Client.exe"
            : "NetRatel.Client";

    public static string BuildRawDownloadUrl(IConfiguration configuration, string rid, string version)
    {
        var baseUrl = ResolvePublicBaseUrl(configuration);

        return $"{baseUrl.TrimEnd('/')}/api/v1/client-artifacts/{Uri.EscapeDataString(rid)}/{Uri.EscapeDataString(version)}/raw-download";
    }

    private string BuildRawDownloadUrl(string rid, string version)
        => BuildRawDownloadUrl(_configuration, rid, version);

    private static string ResolvePublicBaseUrl(IConfiguration configuration)
    {
        var candidates = new[]
        {
            configuration["ClientArtifacts:PublicBaseUrl"],
            configuration["NetRatelApi:BaseUrl"],
            configuration["ApiBaseUrl"],
            configuration["AgentAuth:Issuer"],
            configuration["ClientDefaults:Dev:ApiBaseUrl"],
            "https://netratel.example.invalid"
        };

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            if (!Uri.TryCreate(candidate.Trim(), UriKind.Absolute, out var uri))
            {
                continue;
            }

            if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (uri.IsLoopback)
            {
                continue;
            }

            return uri.GetLeftPart(UriPartial.Authority);
        }

        return "https://netratel.example.invalid";
    }

    private sealed class ClientArtifactMetadata
    {
        public string Rid { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public long Size { get; set; }
        public string Sha256 { get; set; } = string.Empty;
        public DateTimeOffset UploadedAt { get; set; }
        public string? UploadedBy { get; set; }
        public string? Notes { get; set; }
        public string? ContentType { get; set; }
        public Guid? ImportOperationId { get; set; }
        public string? SourceRepository { get; set; }
        public string? SourceTag { get; set; }
        public long? SourceAssetId { get; set; }
        public string? SourceAssetName { get; set; }
        public string? SourceSha256 { get; set; }
        public string? SourceCommit { get; set; }
        public string? ImportAdapterContract { get; set; }

        public ClientArtifactSummaryDto ToSummary() => new()
        {
            Rid = Rid,
            Version = Version,
            FileName = FileName,
            Size = Size,
            Sha256 = Sha256,
            UploadedAt = UploadedAt,
            Notes = Notes
        };
    }

    private sealed record ClientInjectedEnrollmentPayload(
        string Schema,
        int TenantId,
        string EnrollmentCode,
        string Issuer,
        DateTime CreatedAtUtc,
        DateTime ValidToUtc);
}

public sealed class ClientArtifactConflictException(string rid, string version)
    : Exception($"An artifact for RID '{rid}' and version '{version}' already exists.")
{
    public string Rid { get; } = rid;
    public string Version { get; } = version;
}
