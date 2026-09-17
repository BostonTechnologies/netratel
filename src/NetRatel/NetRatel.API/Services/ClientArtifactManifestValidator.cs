using System.IO.Compression;
using System.Text.Json;
using NetRatel.API.Models;

namespace NetRatel.API.Services;

public sealed record ClientArtifactManifest(
    string Schema,
    string Product,
    string Version,
    string RuntimeId,
    string CommitSha,
    string Executable);

public static class ClientArtifactManifestValidator
{
    public const string FileName = "netratel-client-manifest.json";

    public static async Task<string> ValidateAsync(
        IClientArtifactsService artifacts,
        ClientArtifactSummaryDto artifact,
        CancellationToken cancellationToken)
    {
        if (!artifact.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Auto-update artifacts must be ZIP archives with an embedded build manifest.");
        }

        await using var download = (await artifacts.DownloadRawAsync(artifact.Rid, artifact.Version, cancellationToken)
            .ConfigureAwait(false)).Content;
        return await ValidateArchiveAsync(download, artifact.Rid, artifact.Version, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<string> ValidateFileAsync(
        string path,
        string runtimeId,
        string version,
        CancellationToken cancellationToken)
    {
        if (!path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Auto-update artifacts must be ZIP archives with an embedded build manifest.");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, true);
        return await ValidateArchiveAsync(stream, runtimeId, version, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> ValidateArchiveAsync(
        Stream stream,
        string runtimeId,
        string version,
        CancellationToken cancellationToken)
    {
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        var entry = archive.Entries.SingleOrDefault(x =>
            string.Equals(Path.GetFileName(x.FullName), FileName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Artifact does not contain {FileName}.");
        await using var manifestStream = entry.Open();
        using var document = await JsonDocument.ParseAsync(manifestStream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        var manifest = new ClientArtifactManifest(
            root.GetProperty("schema").GetString() ?? string.Empty,
            root.GetProperty("product").GetString() ?? string.Empty,
            root.GetProperty("version").GetString() ?? string.Empty,
            root.GetProperty("runtimeId").GetString() ?? string.Empty,
            root.GetProperty("commitSha").GetString() ?? string.Empty,
            root.GetProperty("executable").GetString() ?? string.Empty);
        if (manifest.Schema != "netratel.client.manifest.v1" ||
            manifest.Product != "NetRatel.Client" ||
            !string.Equals(manifest.Version, version, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(manifest.RuntimeId, runtimeId, StringComparison.OrdinalIgnoreCase) ||
            !IsFullCommitSha(manifest.CommitSha) ||
            string.IsNullOrWhiteSpace(manifest.Executable) ||
            archive.Entries.All(x => !string.Equals(
                Path.GetFileName(x.FullName), manifest.Executable, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("Artifact manifest does not match the uploaded RID, version, commit, or executable.");
        }

        return JsonSerializer.Serialize(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    private static bool IsFullCommitSha(string value)
    {
        if (value.Length != 40) return false;
        try { return Convert.FromHexString(value).Length == 20; }
        catch (FormatException) { return false; }
    }
}
