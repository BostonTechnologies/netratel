using FluentAssertions;
using NetRatel.Web.Themes;
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
    public void Prepaint_palette_is_derived_from_the_runtime_theme_for_explicit_and_system_modes()
    {
        var css = NetRatelPrepaintTheme.Css;
        var script = File.ReadAllText(FindRepoFile("src/NetRatel/NetRatel.Web/wwwroot/js/theme-preference.js"));

        css.Should().Contain("html[data-netratel-theme=\"dark\"]");
        css.Should().Contain("@media (prefers-color-scheme: dark)");
        css.Should().Contain("--mud-palette-background:rgba(12,15,19,1);");
        css.Should().Contain("--mud-palette-background:rgba(245,247,250,1);");
        css.Should().Contain("--mud-palette-appbar-background");
        css.Should().Contain("--mud-palette-surface");
        script.Should().Contain("try {");
        script.Should().Contain("matchMedia?.");
        script.Should().Contain("normalize");
    }

    [Fact]
    public void App_emits_the_canonical_prepaint_palette_without_relaxing_csp()
    {
        var app = File.ReadAllText(FindRepoFile("src/NetRatel/NetRatel.Web/Components/App.razor"));

        app.Should().Contain("netratel-prepaint-theme");
        app.Should().Contain("NetRatelPrepaintTheme.Css");
        app.Should().NotContain("unsafe-inline");
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
