using System.IO.Compression;
using NetRatel.Application.Artifacts;

namespace NetRatel.Infrastructure.Artifacts;

public sealed class ZipInjectionService : IArtifactZipInjectionService
{
    public async Task<byte[]> InjectAsync(Stream baseZip, byte[] injectedContent, string targetEntryName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(targetEntryName))
        {
            throw new ArgumentException("Target entry name is required.", nameof(targetEntryName));
        }

        using var source = new ZipArchive(baseZip, ZipArchiveMode.Read, leaveOpen: true);
        using var outputMs = new MemoryStream();
        using (var destination = new ZipArchive(outputMs, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in source.Entries)
            {
                ct.ThrowIfCancellationRequested();
                if (string.Equals(entry.FullName, targetEntryName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var newEntry = destination.CreateEntry(entry.FullName, CompressionLevel.Optimal);
                await using var input = entry.Open();
                await using var output = newEntry.Open();
                await input.CopyToAsync(output, ct);
            }

            var injectedEntry = destination.CreateEntry(targetEntryName, CompressionLevel.Optimal);
            await using var stream = injectedEntry.Open();
            await stream.WriteAsync(injectedContent, ct);
        }

        return outputMs.ToArray();
    }
}
