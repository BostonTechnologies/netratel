using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace NetRatel.Tests.Web;

public sealed class BrandAssetContractTests
{
    private static readonly string RepoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../"));

    [Theory]
    [InlineData("favicon.ico")]
    [InlineData("brand/netratel-wordmark-600.webp")]
    [InlineData("brand/netratel-mark-32.png")]
    [InlineData("brand/netratel-mark-64.png")]
    [InlineData("brand/apple-touch-icon.png")]
    [InlineData("brand/pwa-192x192.png")]
    [InlineData("brand/pwa-512x512.png")]
    [InlineData("brand/netratel-splash-1280.webp")]
    [InlineData("brand/netratel-splash-1600.webp")]
    public void Published_web_brand_asset_exists(string relativeAsset)
    {
        var asset = Path.Combine(RepoRoot, "src/NetRatel/NetRatel.Web/wwwroot", relativeAsset);

        File.Exists(asset).Should().BeTrue($"{relativeAsset} is referenced by the Web application");
        new FileInfo(asset).Length.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Application_references_the_approved_brand_assets_and_manifest()
    {
        var app = File.ReadAllText(Path.Combine(RepoRoot, "src/NetRatel/NetRatel.Web/Components/App.razor"));
        var login = File.ReadAllText(Path.Combine(RepoRoot, "src/NetRatel/NetRatel.Web/Components/Pages/Login.razor"));
        var navigation = File.ReadAllText(Path.Combine(RepoRoot, "src/NetRatel/NetRatel.Web/Components/Layout/NavMenu.razor"));
        var css = File.ReadAllText(Path.Combine(RepoRoot, "src/NetRatel/NetRatel.Web/wwwroot/app-site.css"));

        app.Should().Contain("brand/netratel-mark-32.png").And.Contain("brand/apple-touch-icon.png").And.Contain("site.webmanifest");
        login.Should().Contain("brand/netratel-mark-64.png");
        navigation.Should().Contain("brand/netratel-mark-64.png");
        css.Should().Contain("brand/netratel-splash-1600.webp").And.Contain("brand/netratel-splash-1280.webp");

        var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot, "src/NetRatel/NetRatel.Web/wwwroot/site.webmanifest")));
        manifest.RootElement.GetProperty("name").GetString().Should().Be("NetRatel");
        manifest.RootElement.GetProperty("icons").EnumerateArray().Select(icon => icon.GetProperty("src").GetString())
            .Should().BeEquivalentTo("brand/pwa-192x192.png", "brand/pwa-512x512.png");
    }

    [Fact]
    public void Every_runtime_brand_reference_resolves_to_a_nonempty_local_asset()
    {
        var webRoot = Path.Combine(RepoRoot, "src/NetRatel/NetRatel.Web/wwwroot");
        var sources = Directory.EnumerateFiles(Path.Combine(RepoRoot, "src/NetRatel/NetRatel.Web"), "*.*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".razor", StringComparison.Ordinal) || path.EndsWith(".css", StringComparison.Ordinal))
            .Select(File.ReadAllText);
        var references = sources.SelectMany(source => Regex.Matches(source, @"brand/[a-z0-9-]+\.(?:png|webp)", RegexOptions.CultureInvariant)
            .Select(match => match.Value)).Distinct(StringComparer.Ordinal);

        foreach (var reference in references)
        {
            var asset = Path.Combine(webRoot, reference.Replace('/', Path.DirectorySeparatorChar));
            File.Exists(asset).Should().BeTrue($"{reference} is referenced by Web markup or CSS");
            new FileInfo(asset).Length.Should().BeGreaterThan(0);
        }
    }

    [Fact]
    public void Canonical_pack_manifest_accounts_for_every_image_and_documentation_copies()
    {
        var canonicalDirectory = Path.Combine(RepoRoot, "docs/assets/netratel/brand-pack");
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(canonicalDirectory, "asset-manifest.json")));
        var entries = manifest.RootElement.GetProperty("assets").EnumerateArray().ToArray();
        var names = entries.Select(entry => entry.GetProperty("filename").GetString()).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var copiedAssets = Directory.EnumerateFiles(canonicalDirectory)
            .Where(path => Path.GetExtension(path) is ".png" or ".webp" or ".ico")
            .Select(Path.GetFileName).ToHashSet(StringComparer.Ordinal);

        names.Should().BeEquivalentTo(copiedAssets);
        foreach (var entry in entries)
        {
            entry.TryGetProperty("role", out var role).Should().BeTrue();
            entry.TryGetProperty("dimensions", out var dimensions).Should().BeTrue();
            entry.TryGetProperty("format", out var format).Should().BeTrue();
            entry.TryGetProperty("intended_use", out var intendedUse).Should().BeTrue();
            role.GetString().Should().NotBeNullOrWhiteSpace();
            dimensions.GetString().Should().NotBeNullOrWhiteSpace();
            format.GetString().Should().NotBeNullOrWhiteSpace();
            intendedUse.GetString().Should().NotBeNullOrWhiteSpace();
        }

        File.ReadAllBytes(Path.Combine(canonicalDirectory, "netratel-readme-hero.webp"))
            .Should().Equal(File.ReadAllBytes(Path.Combine(RepoRoot, "docs/assets/netratel/netratel-readme-hero.webp")));
        File.ReadAllBytes(Path.Combine(canonicalDirectory, "netratel-social-preview.png"))
            .Should().Equal(File.ReadAllBytes(Path.Combine(RepoRoot, "docs/assets/netratel/netratel-social-preview.png")));
    }

    [Fact]
    public void Browser_assets_have_expected_sane_dimensions_and_large_login_assets_are_webp()
    {
        var webRoot = Path.Combine(RepoRoot, "src/NetRatel/NetRatel.Web/wwwroot");
        ReadPngDimensions(Path.Combine(webRoot, "brand/apple-touch-icon.png")).Should().Be((180, 180));
        ReadPngDimensions(Path.Combine(webRoot, "brand/pwa-192x192.png")).Should().Be((192, 192));
        ReadPngDimensions(Path.Combine(webRoot, "brand/pwa-512x512.png")).Should().Be((512, 512));
        ReadWebpVp8Dimensions(Path.Combine(webRoot, "brand/netratel-splash-1280.webp")).Should().Be((1280, 720));
        ReadWebpVp8Dimensions(Path.Combine(webRoot, "brand/netratel-splash-1600.webp")).Should().Be((1600, 900));
    }

    [Fact]
    public void Canonical_favicon_manifest_matches_its_ICO_frames()
    {
        var canonicalDirectory = Path.Combine(RepoRoot, "docs/assets/netratel/brand-pack");
        var expectedFrames = new (int Width, int Height)[] { (16, 16), (32, 32), (48, 48), (64, 64) };
        ReadIcoDimensions(Path.Combine(canonicalDirectory, "favicon.ico"))
            .Should().Equal(expectedFrames);

        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(canonicalDirectory, "asset-manifest.json")));
        var favicon = manifest.RootElement.GetProperty("assets").EnumerateArray()
            .Single(entry => entry.GetProperty("filename").GetString() == "favicon.ico");
        favicon.GetProperty("dimensions").GetString().Should().Be("16x16, 32x32, 48x48, 64x64");
    }

    [Fact]
    public void Public_sources_do_not_reference_the_external_brand_source_path()
    {
        var webProject = Path.Combine(RepoRoot, "src/NetRatel/NetRatel.Web");
        Directory.EnumerateFiles(webProject, "*.*", SearchOption.AllDirectories)
            .Where(path => Path.GetExtension(path) is ".razor" or ".cs" or ".css" or ".md" or ".json" or ".yml" or ".yaml")
            .Select(File.ReadAllText)
            .Should().NotContain(source => source.Contains("/mnt/d/repos/branding", StringComparison.Ordinal));
    }

    private static (int Width, int Height) ReadPngDimensions(string path)
    {
        var bytes = File.ReadAllBytes(path);
        return (BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4)), BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4)));
    }

    private static (int Width, int Height) ReadWebpVp8Dimensions(string path)
    {
        var bytes = File.ReadAllBytes(path);
        Encoding.ASCII.GetString(bytes, 0, 16).Should().Contain("RIFF").And.Contain("WEBP");
        Encoding.ASCII.GetString(bytes, 12, 4).Should().Be("VP8 ");
        return ((bytes[26] | (bytes[27] << 8)) & 0x3fff, (bytes[28] | (bytes[29] << 8)) & 0x3fff);
    }

    private static (int Width, int Height)[] ReadIcoDimensions(string path)
    {
        var bytes = File.ReadAllBytes(path);
        BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(0, 2)).Should().Be(0);
        BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(2, 2)).Should().Be(1);
        var imageCount = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4, 2));
        bytes.Length.Should().BeGreaterThanOrEqualTo(6 + imageCount * 16);

        return Enumerable.Range(0, imageCount)
            .Select(index =>
            {
                var offset = 6 + index * 16;
                return (bytes[offset] is 0 ? 256 : bytes[offset], bytes[offset + 1] is 0 ? 256 : bytes[offset + 1]);
            })
            .ToArray();
    }
}
