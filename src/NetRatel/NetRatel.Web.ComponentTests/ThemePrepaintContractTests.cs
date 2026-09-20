using FluentAssertions;
using Xunit;

namespace NetRatel.Web.ComponentTests;

public class ThemePrepaintContractTests
{
    [Fact]
    public void Document_loads_the_external_theme_resolver_before_routes_render()
    {
        var app = File.ReadAllText(FindRepoFile("src/NetRatel/NetRatel.Web/Components/App.razor"));

        app.IndexOf("<script src=\"/js/theme-preference.js\"></script>", StringComparison.Ordinal)
            .Should().BeGreaterThanOrEqualTo(0);
        app.IndexOf("<script src=\"/js/theme-preference.js\"></script>", StringComparison.Ordinal)
            .Should().BeLessThan(app.IndexOf("<Routes", StringComparison.Ordinal));
    }

    [Fact]
    public void Prepaint_assets_cover_explicit_and_system_modes_without_unsafe_storage_access()
    {
        var css = File.ReadAllText(FindRepoFile("src/NetRatel/NetRatel.Web/wwwroot/app-site.css"));
        var script = File.ReadAllText(FindRepoFile("src/NetRatel/NetRatel.Web/wwwroot/js/theme-preference.js"));

        css.Should().Contain("html[data-netratel-theme=\"dark\"]");
        css.Should().Contain("@media (prefers-color-scheme: dark)");
        css.Should().Contain("--mud-palette-background: #0c0f13;");
        css.Should().Contain("--mud-palette-background: #f5f7fa;");
        css.Should().Contain("--mud-palette-appbar-background");
        css.Should().Contain("--mud-palette-surface");
        script.Should().Contain("try {");
        script.Should().Contain("matchMedia?.");
        script.Should().Contain("normalize");
    }

    private static string FindRepoFile(string relativePath)
    {
        for (var directory = new DirectoryInfo(Directory.GetCurrentDirectory()); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate)) return candidate;
        }

        throw new FileNotFoundException($"Could not find {relativePath}.");
    }
}
