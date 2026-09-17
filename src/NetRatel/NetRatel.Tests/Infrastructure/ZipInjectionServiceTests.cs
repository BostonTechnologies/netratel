using System.IO.Compression;
using System.Text;
using FluentAssertions;
using NetRatel.Infrastructure.Artifacts;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

public sealed class ZipInjectionServiceTests
{
    [Fact]
    public async Task InjectAsync_AddsEnrollmentFile_AndPreservesOriginalEntries()
    {
        var service = new ZipInjectionService();
        var source = BuildZip(("NetRatel.Client.exe", "bin"), ("readme.txt", "hello"));

        var bytes = await service.InjectAsync(source, Encoding.UTF8.GetBytes("{\"schema\":\"netratel.enroll.v1\"}"), "netratel.enroll.json", CancellationToken.None);

        using var result = new MemoryStream(bytes);
        using var archive = new ZipArchive(result, ZipArchiveMode.Read);
        archive.GetEntry("NetRatel.Client.exe").Should().NotBeNull();
        archive.GetEntry("readme.txt").Should().NotBeNull();
        var enrollEntry = archive.GetEntry("netratel.enroll.json");
        enrollEntry.Should().NotBeNull();
        using var reader = new StreamReader(enrollEntry!.Open(), Encoding.UTF8);
        (await reader.ReadToEndAsync()).Should().Contain("netratel.enroll.v1");
    }

    [Fact]
    public async Task InjectAsync_ReplacesExistingEnrollmentFile()
    {
        var service = new ZipInjectionService();
        var source = BuildZip(("netratel.enroll.json", "old"), ("NetRatel.Client.exe", "bin"));

        var bytes = await service.InjectAsync(source, Encoding.UTF8.GetBytes("new"), "netratel.enroll.json", CancellationToken.None);

        using var result = new MemoryStream(bytes);
        using var archive = new ZipArchive(result, ZipArchiveMode.Read);
        archive.Entries.Count(e => e.FullName == "netratel.enroll.json").Should().Be(1);
        using var reader = new StreamReader(archive.GetEntry("netratel.enroll.json")!.Open(), Encoding.UTF8);
        (await reader.ReadToEndAsync()).Should().Be("new");
    }

    private static MemoryStream BuildZip(params (string Name, string Content)[] entries)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = archive.CreateEntry(name);
                using var writer = new StreamWriter(entry.Open(), Encoding.UTF8, leaveOpen: false);
                writer.Write(content);
            }
        }

        stream.Position = 0;
        return stream;
    }
}
