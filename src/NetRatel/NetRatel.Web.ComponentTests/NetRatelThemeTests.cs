using FluentAssertions;
using NetRatel.Web.Themes;
using Xunit;

namespace NetRatel.Web.ComponentTests;

public class NetRatelThemeTests
{
    [Fact]
    public void NetRatelTheme_UsesTheProfessionalProductPaletteAndLayoutTokens()
    {
        var theme = new NetRatelTheme();

        theme.PaletteLight.Primary.ToString().Should().Be("rgba(184,78,46,1)");
        theme.PaletteLight.AppbarBackground.ToString().Should().Be("rgba(255,255,255,1)");
        theme.PaletteLight.Background.ToString().Should().Be("rgba(245,247,250,1)");
        theme.PaletteDark.Primary.ToString().Should().Be("rgba(210,118,85,1)");
        theme.PaletteDark.AppbarBackground.ToString().Should().Be("rgba(21,25,31,1)");
        theme.PaletteDark.Background.ToString().Should().Be("rgba(12,15,19,1)");
        theme.LayoutProperties.DefaultBorderRadius.Should().Be("7px");
        theme.LayoutProperties.DrawerWidthLeft.Should().Be("272px");
        theme.LayoutProperties.AppbarHeight.Should().Be("56px");
    }

    [Fact]
    public void NetRatelTheme_DoesNotRetainTheLegacySaturatedPalette()
    {
        var theme = new NetRatelTheme();

        theme.PaletteLight.Primary.ToString().Should().NotBe("rgba(255,54,14,1)");
        theme.PaletteLight.PrimaryLighten.ToString().Should().NotBe("rgba(255,106,61,1)");
        theme.PaletteDark.Background.ToString().Should().NotBe("rgba(5,5,7,1)");
        theme.PaletteDark.BackgroundGray.ToString().Should().NotBe("rgba(11,12,16,1)");
        theme.PaletteDark.DrawerBackground.ToString().Should().NotBe("rgba(13,14,19,1)");
    }
}
