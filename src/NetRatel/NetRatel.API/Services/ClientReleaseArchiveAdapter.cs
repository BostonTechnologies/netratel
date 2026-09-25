using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace NetRatel.API.Services;

/// <summary>
/// Converts a verified upstream transport archive into the flat ZIP layout consumed by
/// deployed installers and updater versions. Callers retain the upstream bytes and hash
/// separately; this adapter never asserts that the output has the source archive's hash.
/// </summary>
public static class ClientReleaseArchiveAdapter
{
    public const string Contract = "netratel.client.import-adapter.v1";
    private const int MaxEntries = 10_000;
    private const long MaxSourceBytes = 1L << 30;
    private const long MaxUncompressedBytes = 2L << 30;
    private const long MaxEntryBytes = 1L << 30;
    private const long MaxManifestBytes = 64L << 10;
    private static readonly DateTimeOffset ZipTimestamp = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public sealed record Result(string Contract, string Sha256, long SizeBytes, string ManifestJson);

    public static async Task<Result> NormalizeAsync(
        string sourcePath,
        string outputPath,
        string runtimeId,
        string version,
        string expectedCommit,
        CancellationToken cancellationToken)
    {
        if (new FileInfo(sourcePath).Length is <= 0 or > MaxSourceBytes)
            throw new InvalidDataException("Client release source archive exceeds its size limit.");
        if (!IsCommit(expectedCommit))
            throw new InvalidDataException("The release publication record does not identify a full build commit.");
        if (runtimeId is not ("linux-x64" or "win-x64" or "win-arm64" or "osx-x64" or "osx-arm64"))
            throw new InvalidDataException("The release runtime is unsupported.");

        var outputDirectory = Path.GetDirectoryName(Path.GetFullPath(outputPath))
            ?? throw new InvalidOperationException("The distribution archive needs a parent directory.");
        Directory.CreateDirectory(outputDirectory);
        var stage = Path.Combine(outputDirectory, $".client-adapter-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stage);
        try
        {
            var files = new List<StagedFile>();
            var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var explicitEntries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var fileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool? wrapped = null;
            long expandedBytes = 0;
            int entries = 0;
            var prefix = $"netratel-client-{runtimeId}";

            if (sourcePath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
            {
                await using var source = File.OpenRead(sourcePath);
                await using var gzip = new GZipStream(source, CompressionMode.Decompress);
                using var tar = new TarReader(gzip);
                TarEntry? item;
                while ((item = await tar.GetNextEntryAsync(cancellationToken: cancellationToken).ConfigureAwait(false)) is not null)
                {
                    if (++entries > MaxEntries) throw new InvalidDataException("Client archive has too many entries.");
                    var directory = item.EntryType == TarEntryType.Directory;
                    if (!directory && item.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                        throw new InvalidDataException("Client archive contains a link or unsupported entry.");
                    var relative = NormalizeName(item.Name, prefix, directory, ref wrapped);
                    if (relative.Length == 0) continue;
                    RegisterName(relative, directory, names, explicitEntries, fileNames);
                    if (directory) continue;
                    if (item.DataStream is null || item.Length is < 0 or > MaxEntryBytes)
                        throw new InvalidDataException("Client archive entry is missing or exceeds its size limit.");
                    var path = StagePath(stage, relative);
                    expandedBytes = checked(expandedBytes + await CopyEntryAsync(item.DataStream, path,
                        item.Length, MaxUncompressedBytes - expandedBytes, cancellationToken).ConfigureAwait(false));
                    files.Add(new StagedFile(relative, path, (int)item.Mode & 0x1FF));
                }
            }
            else if (sourcePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                await using var source = File.OpenRead(sourcePath);
                using var zip = new ZipArchive(source, ZipArchiveMode.Read);
                foreach (var item in zip.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (++entries > MaxEntries) throw new InvalidDataException("Client archive has too many entries.");
                    var directory = item.FullName.EndsWith("/", StringComparison.Ordinal);
                    var unixKind = (item.ExternalAttributes >> 16) & 0xF000;
                    if (unixKind is not (0 or 0x4000 or 0x8000) || directory && unixKind == 0x8000 ||
                        !directory && unixKind == 0x4000)
                        throw new InvalidDataException("Client archive contains a link or conflicting entry type.");
                    var relative = NormalizeName(item.FullName, prefix, directory, ref wrapped);
                    if (relative.Length == 0) continue;
                    RegisterName(relative, directory, names, explicitEntries, fileNames);
                    if (directory) continue;
                    if (item.Length is < 0 or > MaxEntryBytes)
                        throw new InvalidDataException("Client archive entry exceeds its size limit.");
                    var path = StagePath(stage, relative);
                    await using var entry = item.Open();
                    expandedBytes = checked(expandedBytes + await CopyEntryAsync(entry, path,
                        item.Length, MaxUncompressedBytes - expandedBytes, cancellationToken).ConfigureAwait(false));
                    var mode = (item.ExternalAttributes >> 16) & 0x1FF;
                    files.Add(new StagedFile(relative, path, mode == 0 ? 0x1A4 : mode));
                }
            }
            else
            {
                throw new InvalidDataException("Only ZIP and TAR.GZ client release sources are supported.");
            }

            var expectedExecutable = runtimeId.StartsWith("win-", StringComparison.Ordinal)
                ? "NetRatel.Client.exe" : "NetRatel.Client";
            if (files.Count(file => string.Equals(Path.GetFileName(file.Name),
                    ClientArtifactManifestValidator.FileName, StringComparison.OrdinalIgnoreCase)) != 1)
                throw new InvalidDataException("The client release has duplicate or missing build manifests.");
            var manifestFile = files.SingleOrDefault(file =>
                string.Equals(file.Name, ClientArtifactManifestValidator.FileName, StringComparison.Ordinal))
                ?? throw new InvalidDataException("The client release has no root build manifest.");
            if (new FileInfo(manifestFile.Path).Length > MaxManifestBytes)
                throw new InvalidDataException("The client build manifest exceeds its size limit.");
            await using var manifestStream = File.OpenRead(manifestFile.Path);
            var manifest = await JsonSerializer.DeserializeAsync<ClientArtifactManifest>(
                manifestStream, new JsonSerializerOptions(JsonSerializerDefaults.Web), cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("The client build manifest is empty.");
            if (manifest.Schema != "netratel.client.manifest.v1" || manifest.Product != "NetRatel.Client" ||
                !string.Equals(manifest.Version, version, StringComparison.Ordinal) ||
                !string.Equals(manifest.RuntimeId, runtimeId, StringComparison.Ordinal) ||
                !string.Equals(manifest.CommitSha, expectedCommit, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(manifest.Executable, expectedExecutable, StringComparison.Ordinal) ||
                files.Count(file => string.Equals(file.Name, expectedExecutable, StringComparison.Ordinal)) != 1)
                throw new InvalidDataException("The client build manifest does not match the verified release.");

            var stagedZip = Path.Combine(stage, "distribution.zip");
            await using (var destination = new FileStream(stagedZip, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 64 * 1024, FileOptions.Asynchronous))
            {
                using var zip = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);
                foreach (var file in files.OrderBy(file => file.Name, StringComparer.Ordinal))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var entry = zip.CreateEntry(file.Name, CompressionLevel.Optimal);
                    entry.LastWriteTime = ZipTimestamp;
                    entry.ExternalAttributes = (0x8000 | file.Mode) << 16;
                    await using var input = File.OpenRead(file.Path);
                    await using var output = entry.Open();
                    await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                }
            }

            await ClientArtifactManifestValidator.ValidateFileAsync(stagedZip, runtimeId, version, cancellationToken)
                .ConfigureAwait(false);
            await using var digestInput = File.OpenRead(stagedZip);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(digestInput, cancellationToken)
                .ConfigureAwait(false)).ToLowerInvariant();
            var size = new FileInfo(stagedZip).Length;
            File.Move(stagedZip, outputPath);
            return new Result(Contract, hash, size,
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        }
        finally
        {
            Directory.Delete(stage, recursive: true);
        }
    }

    private static string NormalizeName(string name, string prefix, bool directory, ref bool? wrapped)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 1024 || name.Contains('\\') ||
            name.StartsWith('/') || name.Contains(':') || name.Any(char.IsControl))
            throw new InvalidDataException("Client archive contains an unsafe path.");
        var value = directory ? name.TrimEnd('/') : name;
        var hasPrefix = value == prefix || value.StartsWith(prefix + '/', StringComparison.Ordinal);
        if (wrapped.HasValue && wrapped.Value != hasPrefix)
            throw new InvalidDataException("Client archive mixes wrapped and flat paths.");
        wrapped ??= hasPrefix;
        if (hasPrefix) value = value.Length == prefix.Length ? string.Empty : value[(prefix.Length + 1)..];
        if (value.Length == 0)
        {
            if (!directory) throw new InvalidDataException("Client archive root is a file.");
            return value;
        }
        foreach (var segment in value.Split('/'))
        {
            if (segment is "" or "." or ".." || segment.Length > 255 ||
                segment.EndsWith(' ') || segment.EndsWith('.'))
                throw new InvalidDataException("Client archive contains an unsafe path segment.");
        }
        return value;
    }

    private static void RegisterName(string name, bool directory, Dictionary<string, string> names,
        HashSet<string> explicitEntries, HashSet<string> fileNames)
    {
        var parts = name.Split('/');
        for (var index = 1; index <= parts.Length; index++)
        {
            var path = string.Join('/', parts.Take(index));
            if (names.TryGetValue(path, out var existing) && existing != path)
                throw new InvalidDataException("Client archive contains conflicting path casing.");
            if (index < parts.Length && fileNames.Contains(path))
                throw new InvalidDataException("Client archive contains a file/directory conflict.");
            names[path] = path;
        }
        if (!explicitEntries.Add(name))
            throw new InvalidDataException("Client archive contains a duplicate entry.");
        if (directory)
        {
            if (fileNames.Contains(name))
                throw new InvalidDataException("Client archive contains a file/directory conflict.");
        }
        else
        {
            if (explicitEntries.Any(path => path.StartsWith(name + '/', StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Client archive contains a file/directory conflict.");
            fileNames.Add(name);
        }
    }

    private static string StagePath(string stage, string relative)
    {
        var path = Path.GetFullPath(Path.Combine(stage, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(Path.GetFullPath(stage) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException("Client archive path escapes staging storage.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }

    private static async Task<long> CopyEntryAsync(Stream source, string path, long reportedLength,
        long remainingBytes, CancellationToken cancellationToken)
    {
        await using var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write,
            FileShare.None, 64 * 1024, FileOptions.Asynchronous);
        var buffer = new byte[64 * 1024];
        long copied = 0;
        while (true)
        {
            var count = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            copied = checked(copied + count);
            if (copied > MaxEntryBytes || copied > remainingBytes)
                throw new InvalidDataException("Client archive exceeds its uncompressed size limit.");
            await target.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
        }
        if (copied != reportedLength)
            throw new InvalidDataException("Client archive entry length is inconsistent.");
        return copied;
    }

    private static bool IsCommit(string value) => value.Length == 40 && value.All(Uri.IsHexDigit);

    private sealed record StagedFile(string Name, string Path, int Mode);
}
