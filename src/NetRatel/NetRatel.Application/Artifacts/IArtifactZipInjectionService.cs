using System.IO;

namespace NetRatel.Application.Artifacts;

public interface IArtifactZipInjectionService
{
    Task<byte[]> InjectAsync(Stream baseZip, byte[] injectedContent, string targetEntryName, CancellationToken ct);
}
