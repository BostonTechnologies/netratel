using FluentAssertions;
using Xunit;

namespace NetRatel.Tests.Web;

public sealed class BrandAssetContractTests
{
    private static readonly string RepoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../"));

    [Theory]
    [InlineData("brand/netratel-wordmark-600.webp")]
    [InlineData("brand/netratel-mark-32.png")]
    [InlineData("brand/netratel-mark-64.png")]
    [InlineData("brand/apple-touch-icon.png")]
    public void Published_web_brand_asset_exists(string relativeAsset)
    {
        var asset = Path.Combine(RepoRoot, "src/NetRatel/NetRatel.Web/wwwroot", relativeAsset);

        File.Exists(asset).Should().BeTrue($"{relativeAsset} is referenced by the Web application");
        new FileInfo(asset).Length.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Application_references_the_approved_wordmark_and_icon_assets()
    {
        var app = File.ReadAllText(Path.Combine(RepoRoot, "src/NetRatel/NetRatel.Web/Components/App.razor"));
        var login = File.ReadAllText(Path.Combine(RepoRoot, "src/NetRatel/NetRatel.Web/Components/Pages/Login.razor"));
        var layout = File.ReadAllText(Path.Combine(RepoRoot, "src/NetRatel/NetRatel.Web/Components/Layout/MainLayout.razor"));

        app.Should().Contain("brand/netratel-mark-32.png").And.Contain("brand/apple-touch-icon.png");
        login.Should().Contain("brand/netratel-wordmark-600.webp");
        layout.Should().Contain("brand/netratel-wordmark-600.webp");
    }
}
