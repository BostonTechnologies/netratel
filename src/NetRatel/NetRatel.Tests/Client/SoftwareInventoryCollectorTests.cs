using AwesomeAssertions;
using NetRatel.Client.Service.SoftwareInventory;
using NetRatel.Shared.Contracts.SoftwareInventory;
using Xunit;

namespace NetRatel.Tests.Client;

public sealed class SoftwareInventoryCollectorTests
{
    [Fact]
    public void Canonical_identity_and_package_JSON_preserve_legacy_bytes()
    {
        // Captured from the original reflective producer, including NFKC,
        // fallback package identity, nullable fields and escaped Unicode.
        var packages = new[]
        {
            new SoftwarePackage(" App<&\"é> 😀 ", " 1.0 ", " Públisher& ", " X64 ", " ＡＰＰ ", " DPKG "),
            new SoftwarePackage(" App\u0301<&> ", "2", "Pub\u212b", "AMD64", "", "rpm", Scope: "user:synthetic"),
            new SoftwarePackage("Optional", "3", "Vendor", "x64", "optional", "registry", "/synthetic/<&\"é>😀", "20261103")
        }.Select(package => package.Normalize()).ToArray();
        packages.Select(package => package.Identity).Should().Equal(
            "D2858F6107E7A1077216E86EEBFE358B254873DE7F354859AA502A31D819FE7E",
            "76B2BA5FE269CC41A273E7F51A646870FE030DAF46842FE3A56FB04873AF4CCF",
            "4C4AEACFCE15D4CDAAF61D704A94E917EFBAEF1BC54BBD5157BC1AB5920405F6");
        var chunk = SoftwareInventoryWire.Encode(Guid.Parse("12345678-90ab-cdef-8123-456789abcdef"),
            new(packages, true, "linux", []), new DateTimeOffset(2026, 11, 3, 12, 34, 56, TimeSpan.Zero)).Single();
        using var gzip = new System.IO.Compression.GZipStream(new MemoryStream(Convert.FromBase64String(chunk.Payload)),
            System.IO.Compression.CompressionMode.Decompress);
        using var bytes = new MemoryStream();
        gzip.CopyTo(bytes);
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes.ToArray())).Should().Be(
            "22764148F12739183FE613B000CE9F914F69FEA14C7EB8D06F5032BE2FA16F34");
        SoftwareInventoryWire.Decode(chunk.Payload).Should().Equal(packages);
    }

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

    [Fact]
    public void Long_unicode_metadata_is_split_by_expanded_bytes_as_well_as_package_count()
    {
        var packages = Enumerable.Range(0, 102).Select(index => new SoftwarePackage("Tool " + index + new string('\u754c', 500),
            new string('\u754c', 200), new string('\u754c', 500), "x64", "package-" + index + new string('\u754c', 500), "registry",
            new string('\u754c', 2000))).ToArray();
        var chunks = SoftwareInventoryWire.Encode(Guid.NewGuid(), new(packages, true, "windows", []), DateTimeOffset.UtcNow);
        chunks.Count.Should().BeGreaterThan(1);
        chunks.SelectMany(chunk => SoftwareInventoryWire.Decode(chunk.Payload)).Should().Equal(packages);
        chunks.Should().OnlyContain(chunk => chunk.Complete && chunk.Count == chunks.Count);
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
