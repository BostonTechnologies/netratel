using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NetRatel.Shared.Contracts.SoftwareInventory;

public sealed record SoftwareInventoryPolicy(bool Enabled = true, int Interval = 7, string Unit = "days")
{
    public void Validate()
    {
        var maximum = Unit switch { "hours" => 8760, "days" => 365, "weeks" => 52, "months" => 12, _ => 0 };
        if (Interval < 1 || Interval > maximum) throw new ArgumentException("Inventory cadence must be between one hour and one year; use hours, days, weeks or months.");
    }

    public DateTimeOffset NextDue(DateTimeOffset completed, Guid agentId)
    {
        Validate();
        var due = Unit switch
        {
            "hours" => completed.AddHours(Interval), "days" => completed.AddDays(Interval),
            "weeks" => completed.AddDays(7 * Interval), _ => completed.AddMonths(Interval)
        };
        // Stable positive jitter (0–10%) survives restarts and never violates the minimum cadence.
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(agentId.ToString("N") + completed.ToString("O", CultureInfo.InvariantCulture)));
        return due.AddTicks((long)((due - completed).Ticks * (hash[0] / 2550d)));
    }
}

public sealed record SoftwareInventoryTenant(int TenantId, string Name);

public sealed record SoftwarePackage(string Name, string Version, string Publisher, string Architecture,
    string PackageId, string Source, string? InstallLocation = null, string? InstallDate = null, string Scope = "machine")
{
    public SoftwarePackage Normalize() => this with
    {
        Name = Clean(Name, 512), Version = Clean(Version, 256), Publisher = Clean(Publisher, 512),
        Architecture = Clean(Architecture, 64).ToLowerInvariant(), PackageId = Clean(PackageId, 512),
        Source = Clean(Source, 64).ToLowerInvariant(), Scope = Clean(Scope, 256),
        InstallLocation = InstallLocation is null ? null : Clean(InstallLocation, 2048),
        InstallDate = InstallDate is null ? null : Clean(InstallDate, 64)
    };

    public string Identity => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
        new[] { Source, string.IsNullOrWhiteSpace(PackageId) ? Name + "\u001f" + Publisher : PackageId, Architecture, Scope }
            .Select(value => value.Normalize(NormalizationForm.FormKC).Trim().ToLowerInvariant())))));

    private static string Clean(string value, int limit)
    {
        if (value is null || value.Length > limit || value.Any(char.IsControl)) throw new ArgumentException("Invalid software metadata.");
        return value.Normalize(NormalizationForm.FormKC).Trim();
    }
}

public sealed record SoftwareCollection(IReadOnlyList<SoftwarePackage> Packages, bool Complete, string Os, IReadOnlyList<string> Errors);
public sealed record SoftwareScanDto(Guid CollectionId, string Status, DateTimeOffset RequestedAtUtc,
    DateTimeOffset? CompletedAtUtc, int ReceivedChunks, int TotalChunks, IReadOnlyList<string> Errors);
public sealed record SoftwareInventoryStatus(int TenantId, Guid AgentId, DateTimeOffset? LastSuccessfulScanUtc,
    DateTimeOffset? NextDueUtc, SoftwareScanDto? LatestScan, SoftwareInventoryPolicy Policy, bool CanRefresh = false);
public sealed record SoftwareScanWork(SoftwareInventoryPolicy Policy, Guid? CollectionId, DateTimeOffset? NextDueUtc);
public sealed record SoftwareRefreshRequest(bool Force = true);
public sealed record SoftwareChunkUpload(Guid CollectionId, int Index, int Count, bool Complete,
    string Os, DateTimeOffset DetectedAtUtc, IReadOnlyList<string> Errors, string Payload);
public sealed record SoftwareChunkAcknowledgement(Guid CollectionId, int Index, bool Accepted, string Status);
public sealed record SoftwareInstallationDto(Guid AgentId, string Client, string Identity, SoftwarePackage Package,
    string Os, DateTimeOffset DetectedAtUtc, DateTimeOffset LastSuccessfulScanUtc);
public sealed record SoftwarePrevalenceDto(string Name, string Version, string Publisher, string Source, int Devices, int Installations);
public sealed record SoftwareChangeDto(Guid CollectionId, Guid AgentId, string Kind, string Name, string? PreviousVersion,
    string? Version, DateTimeOffset DetectedAtUtc);
public sealed record SoftwarePage<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize, int Devices = 0);
public sealed record SoftwareInventoryQuery(int Page = 0, int PageSize = 50, string? Search = null,
    string? Name = null, string? Version = null, string? Publisher = null, string? Os = null, string? Source = null,
    Guid? AgentId = null, string? Status = null, string Sort = "name", bool Descending = false)
{
    public void Validate()
    {
        if (Page < 0 || Page > 100_000 || PageSize < 1 || PageSize > 100 || AgentId == Guid.Empty ||
            new[] { Search, Name, Version, Publisher, Os, Source, Status }.Any(value => value is { Length: > 256 } || value?.Any(char.IsControl) == true) ||
            Sort is not ("name" or "version" or "publisher" or "source" or "client" or "detected") ||
            Status is not (null or "fresh" or "stale" or "Pending" or "Collecting" or "Complete" or "Partial" or "Error"))
            throw new ArgumentException("Invalid inventory filters or pagination (pageSize 1–100).");
    }
}

public static class SoftwareInventoryWire
{
    public const int PackagesPerChunk = 100;
    public const int MaximumChunks = 1000;
    public const int MaximumChunkBytes = 1024 * 1024;
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static IReadOnlyList<SoftwareChunkUpload> Encode(Guid id, SoftwareCollection collection, DateTimeOffset detected)
    {
        if (collection.Packages.Count > PackagesPerChunk * MaximumChunks) throw new ArgumentException("Inventory exceeds the supported package count.");
        var pages = collection.Packages.Chunk(PackagesPerChunk).ToArray();
        if (pages.Length == 0) pages = [Array.Empty<SoftwarePackage>()];
        return pages.Select((page, index) =>
        {
            using var output = new MemoryStream();
            using (var gzip = new GZipStream(output, CompressionLevel.Fastest, true)) JsonSerializer.Serialize(gzip, page, JsonOptions);
            return new SoftwareChunkUpload(id, index, pages.Length, collection.Complete, collection.Os, detected,
                collection.Errors, Convert.ToBase64String(output.ToArray()));
        }).ToArray();
    }

    public static SoftwarePackage[] Decode(string payload)
    {
        if (payload is null || payload.Length > MaximumChunkBytes * 2) throw new ArgumentException("Inventory chunk is too large.");
        using var input = new MemoryStream(Convert.FromBase64String(payload));
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = gzip.Read(buffer)) > 0)
        {
            if (output.Length + read > MaximumChunkBytes) throw new ArgumentException("Expanded inventory chunk is too large.");
            output.Write(buffer, 0, read);
        }
        var packages = JsonSerializer.Deserialize<SoftwarePackage[]>(output.ToArray(), JsonOptions) ?? throw new ArgumentException("Missing packages.");
        if (packages.Length > PackagesPerChunk || packages.Any(package => package is null)) throw new ArgumentException("Too many packages in a chunk.");
        return packages.Select(package => package.Normalize()).Select(package =>
            string.IsNullOrEmpty(package.Name) || string.IsNullOrEmpty(package.Source) ? throw new ArgumentException("Package name and source are required.") : package).ToArray();
    }
}
