using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;

namespace NetRatel.Web.PlaywrightTests;

[Collection(PlaywrightCollection.Name)]
[Trait("Category", "BrowserAcceptance")]
public sealed class NavigationResponsiveTests
{
    [Fact]
    public async Task Navigation_RendersSingleLineLabelsWithScrollbarAndKeyboardDisclosure()
    {
        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new()
        {
            Headless = true,
            IgnoreDefaultArgs = ["--hide-scrollbars"],
            ExecutablePath = Environment.GetEnvironmentVariable("NETRATEL_PLAYWRIGHT_CHROMIUM_PATH")
        });
        await using var fixture = await ClientsManagementFixtureHost.StartAsync(services =>
            services.AddScoped<AuthenticationStateProvider, LongAccountAuthenticationStateProvider>());
        var evidenceDirectory = Path.GetFullPath(Environment.GetEnvironmentVariable("NETRATEL_PLAYWRIGHT_ARTIFACT_ROOT")
            ?? Path.Combine(Directory.GetCurrentDirectory(), "TestResults", "playwright"));
        Directory.CreateDirectory(evidenceDirectory);

        foreach (var (name, width, textScale, theme) in new[]
        {
            ("desktop-light", 1280, 100, "light"),
            ("desktop-dark", 1280, 100, "dark"),
            ("narrow-zoom-light", 390, 200, "light"),
            ("narrow-zoom-dark", 390, 200, "dark")
        })
        {
            await using var context = await browser.NewContextAsync(new()
            {
                ViewportSize = new ViewportSize { Width = width, Height = 600 }
            });
            await context.AddInitScriptAsync($"localStorage.setItem('netratel.theme.preference', '{theme}');");
            var page = await context.NewPageAsync();
            await page.GotoAsync($"{fixture.BaseAddress}/clients/mgmt");
            var drawer = page.GetByTestId("app-navigation-drawer");
            var estate = drawer.Locator(".mud-nav-group[aria-label='Estate Management'] > button");
            await estate.WaitForAsync(new() { State = WaitForSelectorState.Attached });
            if (await drawer.EvaluateAsync<bool>("element => element.classList.contains('mud-drawer--closed')"))
                await page.GetByTestId("navigation-toggle").ClickAsync();
            await Assertions.Expect(drawer).ToBeVisibleAsync();
            await estate.ClickAsync();
            await Assertions.Expect(estate).ToHaveAttributeAsync("aria-expanded", "true");
            var admin = drawer.Locator(".mud-nav-group[aria-label='Admin'] > button");
            await admin.ClickAsync();
            await Assertions.Expect(admin).ToHaveAttributeAsync("aria-expanded", "true");
            await page.EvaluateAsync("scale => document.documentElement.style.fontSize = `${16 * scale / 100}px`", textScale);

            await Assertions.Expect(drawer.Locator("a[href='/account/integration-credentials']")).ToHaveCountAsync(1);
            Assert.Equal(new[] { "/account/integration-credentials", "/admin/access", "/admin/branding", "/admin/app-settings", "/clients/settings" },
                await drawer.Locator(".mud-nav-group[aria-label='Admin'] a").EvaluateAllAsync<string[]>("links => links.map(link => link.getAttribute('href'))"));
            var violations = await drawer.EvaluateAsync<string[]>("""
                drawer => {
                    const errors = [];
                    const scroller = drawer.querySelector('.mud-drawer-content');
                    if (scroller.scrollHeight <= scroller.clientHeight) errors.push('The fixture must exercise vertical scrolling.');
                    if (scroller.offsetWidth <= scroller.clientWidth) errors.push('The vertical scrollbar must consume real drawer width.');
                    if (scroller.scrollWidth > scroller.clientWidth + 1) {
                        const right = scroller.getBoundingClientRect().left + scroller.clientWidth;
                        const overflowing = [...scroller.querySelectorAll('*')]
                            .filter(element => element.getBoundingClientRect().right > right + 1)
                            .map(element => `${element.className.baseVal ?? element.className}:${Math.round(element.getBoundingClientRect().width)}`);
                        errors.push(`Horizontal drawer overflow (${scroller.scrollWidth}/${scroller.clientWidth}): ${overflowing.join(', ')}`);
                    }
                    for (const label of drawer.querySelectorAll('.mud-nav-link-text')) {
                        if (!label.checkVisibility()) continue;
                        const style = getComputedStyle(label);
                        if (style.whiteSpace !== 'nowrap' || style.overflowWrap !== 'normal' || style.textOverflow !== 'ellipsis')
                            errors.push(`Missing single-line overflow treatment: ${label.textContent}`);
                        if (label.scrollHeight > label.clientHeight + 1) errors.push(`Wrapped label: ${label.textContent}`);
                        const row = label.closest('.mud-nav-link');
                        const bounds = row.getBoundingClientRect();
                        for (const icon of row.querySelectorAll('svg')) {
                            const iconBounds = icon.getBoundingClientRect();
                            if (iconBounds.width < 19 || iconBounds.left < bounds.left || iconBounds.right > bounds.right)
                                errors.push(`Clipped icon/chevron: ${label.textContent}`);
                        }
                    }
                    return errors;
                }
                """);
            Assert.Empty(violations);
            if (textScale == 100)
            {
                Assert.True(await estate.Locator(".mud-nav-link-text").EvaluateAsync<bool>("label => label.scrollWidth <= label.clientWidth"),
                    "Estate Management must fit without truncation at the normal desktop width, including the scrollbar.");
            }
            var account = drawer.Locator("a[href='/account/security']");
            await Assertions.Expect(account).ToHaveAccessibleNameAsync(LongAccountAuthenticationStateProvider.AccountName);
            Assert.True(await account.Locator(".mud-nav-link-text").EvaluateAsync<bool>("label => label.scrollWidth > label.clientWidth"),
                "The long-account fixture must exercise truncation.");
            await estate.FocusAsync();
            await page.Keyboard.PressAsync("Space");
            await Assertions.Expect(estate).ToHaveAttributeAsync("aria-expanded", "false");
            await page.Keyboard.PressAsync("Space");
            await Assertions.Expect(estate).ToHaveAttributeAsync("aria-expanded", "true");
            Assert.True(await estate.EvaluateAsync<bool>("button => button === document.activeElement"), "Expansion must retain keyboard focus.");
            await AssertKeyboardDisclosureAsync(estate, "Estate Management");
            await page.ScreenshotAsync(new() { Path = Path.Combine(evidenceDirectory, $"navigation-{name}-estate.png"), Animations = ScreenshotAnimations.Disabled });
            await drawer.Locator("a[href='/clients']").ClickAsync();
            await Assertions.Expect(drawer.Locator("a.active")).ToHaveAttributeAsync("href", "/clients");
            await page.GoBackAsync();
            await Assertions.Expect(drawer.Locator("a.active")).ToHaveAttributeAsync("href", "/clients/mgmt");
            await page.GoForwardAsync();
            await Assertions.Expect(drawer.Locator("a.active")).ToHaveAttributeAsync("href", "/clients");
            await Assertions.Expect(estate).ToHaveAttributeAsync("aria-expanded", "true");
            if (await drawer.EvaluateAsync<bool>("element => element.classList.contains('mud-drawer--closed')"))
                await page.GetByTestId("navigation-toggle").ClickAsync();

            await account.FocusAsync();
            await page.Keyboard.PressAsync("Tab");
            await Assertions.Expect(admin).ToBeFocusedAsync();
            await page.Keyboard.PressAsync("Shift+Tab");
            await Assertions.Expect(account).ToBeFocusedAsync();
            await AssertKeyboardDisclosureAsync(account, LongAccountAuthenticationStateProvider.AccountName);
            await page.ScreenshotAsync(new() { Path = Path.Combine(evidenceDirectory, $"navigation-{name}-admin-account.png"), Animations = ScreenshotAnimations.Disabled });
            await account.EvaluateAsync("link => link.blur()");
            await drawer.Locator(".mud-drawer-content").EvaluateAsync("scroller => scroller.scrollTop = scroller.scrollHeight");
            await page.ScreenshotAsync(new() { Path = Path.Combine(evidenceDirectory, $"navigation-{name}-admin.png"), Animations = ScreenshotAnimations.Disabled });
        }
    }

    private static async Task AssertKeyboardDisclosureAsync(ILocator row, string expectedLabel)
    {
        var disclosure = await row.EvaluateAsync<string>("link => getComputedStyle(link, '::after').content");
        Assert.Contains(expectedLabel, disclosure);
        Assert.True(await row.EvaluateAsync<bool>("""
            row => {
                const tooltip = getComputedStyle(row, '::after');
                const style = getComputedStyle(row);
                return style.overflowY === 'visible' && tooltip.display !== 'none' &&
                    tooltip.visibility === 'visible' && Number(tooltip.opacity) > 0;
            }
            """), "The keyboard disclosure must be visible outside the row, including its ripple clipping.");
    }
}

internal sealed class LongAccountAuthenticationStateProvider : AuthenticationStateProvider
{
    internal const string AccountName = "An instance administrator with a very long account display name";

    public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(
        new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "fixture-operator"),
            new Claim(ClaimTypes.Name, AccountName),
            new Claim(ClaimTypes.Role, "Operator")
        }, "Fixture"))));
}
