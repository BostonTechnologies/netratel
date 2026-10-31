using AwesomeAssertions;
using NetRatel.Client.Service.SoftwareInventory;
using NetRatel.Shared.Contracts.SoftwareInventory;
using Xunit;

namespace NetRatel.Tests.Client;

public sealed class SoftwareInventoryCollectorTests
{
    [Theory]
    [InlineData("dpkg", "tool:amd64\t1.2\tExample Maintainer\tamd64\tinstalled\nremoved\t1\tExample\tx64\tconfig-files\n", "tool:amd64", "1.2")]
    [InlineData("rpm", "tool\t1.2-3\tExample Vendor\tx86_64\n", "tool", "1.2-3")]
    [InlineData("pacman", "tool 1.2-3\n", "tool", "1.2-3")]
    [InlineData("snap", "Name Version Rev Tracking Publisher Notes\ntool 1.2 33 latest/stable example -\n", "tool", "1.2")]
    [InlineData("flatpak", "org.example.Tool\tExample Tool\t1.2\tx86_64\tflathub\n", "org.example.Tool", "1.2")]
    public void Linux_package_databases_preserve_identity_version_and_source(string source, string output, string id, string version)
    {
        var collection = LinuxSoftwareInventoryCollector.Parse(source, output);
        collection.Complete.Should().BeTrue();
        collection.Packages.Should().ContainSingle().Which.Should().Match<SoftwarePackage>(package => package.PackageId == id && package.Version == version && package.Source == source);
    }

    [Fact]
    public async Task Multiple_package_managers_and_user_flatpak_failures_remain_partial()
    {
        var runner = new Runner();
        var collection = await new LinuxSoftwareInventoryCollector(runner).CollectAsync(default);
        collection.Complete.Should().BeFalse();
        collection.Packages.Select(package => package.Source).Should().Contain("dpkg").And.Contain("rpm").And.Contain("pacman").And.Contain("snap").And.Contain("flatpak");
        collection.Errors.Should().Contain("flatpak_scan_failed");
        runner.Commands.Should().HaveCount(6);
        LinuxSoftwareInventoryCollector.Parse("rpm", "malformed\n").Complete.Should().BeFalse();
    }

    [Fact]
    public async Task Windows_collects_both_views_and_user_scopes_without_reading_license_values()
    {
        var entries = new[] { ("x86", "machine"), ("x64", "machine"), ("x64", "S-1-5-21-example") }
            .Select(scope => new WindowsSoftwareRegistryEntry("Example.Tool", scope.Item1, scope.Item2, new Dictionary<string, string>
            {
                ["DisplayName"] = "Example Tool", ["DisplayVersion"] = "2.0", ["Publisher"] = "Example",
                ["InstallLocation"] = @"C:\Example", ["InstallDate"] = "20261031", ["LicenseKey"] = "must-never-collect"
            })).ToArray();
        var collection = await new WindowsSoftwareInventoryCollector(new Registry(entries), new NoCommands()).CollectAsync(default);
        collection.Complete.Should().BeTrue();
        collection.Packages.Should().HaveCount(3);
        collection.Packages.Select(package => package.Identity).Distinct().Should().HaveCount(3);
        System.Text.Json.JsonSerializer.Serialize(collection).Should().NotContain("must-never-collect");
    }

    [Fact]
    public void Wire_roundtrip_and_bounded_decompression_preserve_metadata()
    {
        var package = new SoftwarePackage("Example", "1", "Vendor", "x64", "example", "rpm");
        var chunk = SoftwareInventoryWire.Encode(Guid.NewGuid(), new([package], true, "linux", []), DateTimeOffset.UtcNow).Single();
        SoftwareInventoryWire.Decode(chunk.Payload).Should().Equal(package);
        using var bytes = new MemoryStream();
        using (var gzip = new System.IO.Compression.GZipStream(bytes, System.IO.Compression.CompressionLevel.Fastest, true)) gzip.Write(new byte[SoftwareInventoryWire.MaximumChunkBytes + 1]);
        Assert.Throws<ArgumentException>(() => SoftwareInventoryWire.Decode(Convert.ToBase64String(bytes.ToArray())));
    }

    private sealed class Registry(WindowsSoftwareRegistryEntry[] entries) : IWindowsSoftwareRegistry
    { public WindowsSoftwareRegistryResult Read() => new(entries, []); }
    private sealed class NoCommands : ISoftwareCommandRunner
    {
        public bool Exists(string executable) => false;
        public Task<SoftwareCommandResult> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken ct) => throw new InvalidOperationException();
    }
    private sealed class Runner : ISoftwareCommandRunner
    {
        public List<string> Commands { get; } = [];
        public bool Exists(string executable) => true;
        public Task<SoftwareCommandResult> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken ct)
        {
            Commands.Add(executable + " " + string.Join(' ', arguments));
            return Task.FromResult(executable switch
            {
                "dpkg-query" => new(0, "tool\t1\tVendor\tx64\tinstalled\n"), "rpm" => new(0, "tool\t1\tVendor\tx64\n"),
                "pacman" => new(0, "tool 1\n"), "snap" => new(0, "tool 1 1 stable vendor -\n"),
                _ => new SoftwareCommandResult(arguments.Contains("--user") ? 1 : 0, "org.example.Tool\tTool\t1\tx64\tflathub\n")
            });
        }
    }
}
