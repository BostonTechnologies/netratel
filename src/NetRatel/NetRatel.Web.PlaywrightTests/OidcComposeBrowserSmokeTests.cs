using Microsoft.Playwright;
using System.Reflection;
using NetRatel.Web.Components.Layout;

namespace NetRatel.Web.PlaywrightTests;

[Trait("category", "compose")]
public sealed class OidcComposeBrowserSmokeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GenericOidcStack_LoadsAssets_Authenticates_And_LogsOut(bool trustedProxy)
    {
        var webUrl = RequireEnvironmentUri(trustedProxy ? "NETRATEL_BROWSER_SMOKE_PROXY_URL" : "NETRATEL_BROWSER_SMOKE_WEB_URL");
        var username = RequireEnvironmentValue("NETRATEL_BROWSER_SMOKE_USERNAME");

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true,
            // The disposable issuer is exposed only on the hosted runner loopback.
            // Containers and the OIDC issuer use this stable authority hostname.
            Args = ["--host-resolver-rules=MAP host.docker.internal 127.0.0.1"]
        });
        // The HTTPS proxy uses the disposable smoke certificate, never a production certificate.
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = trustedProxy });
        var page = await context.NewPageAsync();
        page.SetDefaultTimeout(15_000);

        var bootstrap = await page.GotoAsync(webUrl.ToString(), new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        Assert.NotNull(bootstrap);
        Assert.True(bootstrap.Ok || bootstrap.Status is 302 or 303, $"The Web bootstrap returned HTTP {bootstrap.Status}.");

        var staticAsset = await context.APIRequest.GetAsync(new Uri(webUrl, "_framework/blazor.web.js").ToString());
        Assert.True(staticAsset.Ok, $"The Blazor bootstrap asset returned HTTP {staticAsset.Status}.");

        foreach (var (asset, contentType, maximumBytes) in new[]
        {
            ("brand/netratel-wordmark-600.webp", "image/webp", 150_000),
            ("brand/netratel-mark-32.png", "image/png", 20_000),
            ("brand/apple-touch-icon.png", "image/png", 150_000),
            ("brand/pwa-192x192.png", "image/png", 150_000),
            ("brand/pwa-512x512.png", "image/png", 500_000),
            ("brand/netratel-splash-1280.webp", "image/webp", 500_000),
            ("brand/netratel-splash-1600.webp", "image/webp", 500_000),
            ("site.webmanifest", "application/manifest+json", 10_000),
            ("favicon.ico", "image/", 150_000)
        })
        {
            var response = await context.APIRequest.GetAsync(new Uri(webUrl, asset).ToString());
            Assert.True(response.Ok, $"{asset} returned {response.Status}");
            Assert.StartsWith(contentType, response.Headers["content-type"]);
            Assert.InRange((await response.BodyAsync()).Length, 1, maximumBytes);
        }
        await page.GotoAsync(new Uri(webUrl, "login").ToString());
        await CaptureBrandingAsync(page, "login");

        await page.GotoAsync(new Uri(webUrl, "login?ReturnUrl=%2Ftenants").ToString());
        var signInAction = page.Locator(".netratel-login-primary-action");
        Assert.Equal("Sign in with your identity provider", (await signInAction.InnerTextAsync()).Trim());
        await signInAction.ClickAsync();
        await page.Locator("input[name='username']").FillAsync(username);
        var callbackResponse = page.WaitForResponseAsync(response =>
            Uri.TryCreate(response.Url, UriKind.Absolute, out var responseUri)
            && responseUri.GetLeftPart(UriPartial.Path) == new Uri(webUrl, "signin-oidc").ToString());
        await page.Locator("form").EvaluateAsync("form => form.submit()");
        Assert.Equal(302, (await callbackResponse).Status);
        await page.WaitForURLAsync(new Uri(webUrl, "tenants").ToString(),
            new PageWaitForURLOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        var sessionCookies = (await context.CookiesAsync()).Where(cookie => cookie.Name.StartsWith(".AspNetCore.Cookies", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(sessionCookies);
        if (trustedProxy)
            Assert.All(sessionCookies, cookie => Assert.True(cookie.Secure));

        var authenticatedStatus = await page.EvaluateAsync<int>("async () => (await fetch('/api/v1/tenants')).status");
        Assert.Equal(200, authenticatedStatus);
        await CaptureBrandingAsync(page, "navbar");
        await AssertProductVersionBadgesAsync(page);
        if (Environment.GetEnvironmentVariable("NETRATEL_BROWSER_SMOKE_EXPECT_CURRENT_SHELL") != "false")
        {
            await page.GotoAsync(new Uri(webUrl, "account/security").ToString(),
                new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
            await page.GetByTestId("account-security-managed").WaitForAsync(
                new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
            Assert.Equal(0, await page.GetByTestId("open-password-dialog").CountAsync());
            Assert.Equal(0, await page.GetByTestId("open-mfa-setup").CountAsync());
        }

        await page.GotoAsync(new Uri(webUrl, "auth/logout").ToString(), new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        var anonymousStatus = await page.EvaluateAsync<int>("async () => (await fetch('/api/v1/tenants')).status");
        Assert.Equal(401, anonymousStatus);
    }

    private static async Task CaptureBrandingAsync(IPage page, string view)
    {
        var expectCurrentShell = Environment.GetEnvironmentVariable("NETRATEL_BROWSER_SMOKE_EXPECT_CURRENT_SHELL") != "false";
        var directory = Path.Combine("TestResults", "branding");
        Directory.CreateDirectory(directory);
        await SetThemeAndReloadAsync(page, "system");
        Assert.Contains(await page.EvaluateAsync<string>("() => document.documentElement.dataset.netratelTheme"), new[] { "light", "dark" });
        if (view == "login" && expectCurrentShell) await AssertOidcActionAsync(page);

        foreach (var theme in new[] { "light", "dark" })
        {
            await SetThemeAndReloadAsync(page, theme);
            foreach (var (width, height, name) in new[] { (1440, 900, "desktop"), (768, 900, "tablet"), (390, 844, "mobile") })
            {
                await page.SetViewportSizeAsync(width, height);
                await page.Locator("img[src*='brand/']").First.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Attached });
                await page.WaitForFunctionAsync("() => Array.from(document.querySelectorAll('img[src*=\"brand/\"]')).every(image => image.complete && image.naturalWidth > 0)");
                Assert.False(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth > innerWidth"),
                    $"{view} overflows the {name} viewport.");

                var themeControl = page.Locator(view == "login"
                    ? ".netratel-public-theme-control"
                    : ".mud-appbar");
                await themeControl.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
                Assert.True(await themeControl.IsEnabledAsync());

                if (view == "login")
                {
                    var loginPanel = page.Locator(".netratel-login-panel");
                    await loginPanel.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
                    Assert.True(await page.Locator(".netratel-login-environment").IsVisibleAsync());
                    var signIn = page.Locator(".netratel-login-primary-action");
                    Assert.True(await signIn.IsVisibleAsync());
                    Assert.True(await signIn.IsEnabledAsync());
                    if (expectCurrentShell) await AssertOidcActionAsync(page);
                    Assert.True(await page.EvaluateAsync<bool>("() => getComputedStyle(document.querySelector('.netratel-public-layout')).backgroundImage.includes('netratel-splash')"));
                    if (expectCurrentShell)
                        Assert.True(await page.EvaluateAsync<bool>("() => { const bounds = document.querySelector('.netratel-login-panel').getBoundingClientRect(); return Math.abs((bounds.left + bounds.right) / 2 - innerWidth / 2) <= 12 && Math.abs((bounds.top + bounds.bottom) / 2 - innerHeight / 2) <= 16; }"),
                            $"The login panel is not centered in the {name} viewport.");
                }
                else if (width < 800)
                {
                    var compactBrand = page.Locator(".netratel-nav-brand");
                    if (!await compactBrand.IsVisibleAsync())
                        await page.GetByTestId("navigation-toggle").ClickAsync();
                    await compactBrand.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
                    if (expectCurrentShell) Assert.Equal(0, await page.Locator(".netratel-appbar-brand").CountAsync());
                }
                else
                {
                    Assert.True(await page.Locator(".netratel-nav-brand img").IsVisibleAsync());
                    if (expectCurrentShell) Assert.Equal(0, await page.Locator(".netratel-appbar-brand").CountAsync());
                    if (expectCurrentShell)
                    {
                        foreach (var heading in new[] { "Command", "Estate", "Signals" })
                            Assert.True(await page.Locator(".nav-section-heading", new PageLocatorOptions { HasText = heading }).IsVisibleAsync());
                    }
                }

                await page.ScreenshotAsync(new PageScreenshotOptions
                {
                    Path = Path.Combine(directory, $"{view}-{theme}-{name}.png"),
                    FullPage = true,
                    Animations = ScreenshotAnimations.Disabled
                });
            }
        }
    }

    private static async Task AssertOidcActionAsync(IPage page)
    {
        var action = page.Locator(".netratel-login-primary-action");
        Assert.True(await action.IsVisibleAsync());
        Assert.Equal("Sign in with your identity provider", (await action.InnerTextAsync()).Trim());
        // DOMContentLoaded can precede the stylesheet on the HTTPS image profile.
        // Measure the rendered filled button only after both opaque colors resolve.
        var contrastHandle = await page.WaitForFunctionAsync(@"() => {
            const element = document.querySelector('.netratel-login-primary-action');
            if (!element) return false;
            const style = getComputedStyle(element);
            const rgb = value => {
                const channels = value.match(/[\d.]+/g);
                return channels?.length >= 3 ? channels.slice(0, 3).map(Number) : null;
            };
            const luminance = value => rgb(value).map(channel => {
                const normalized = channel / 255;
                return normalized <= 0.04045 ? normalized / 12.92 : ((normalized + 0.055) / 1.055) ** 2.4;
            }).reduce((sum, channel, index) => sum + channel * [0.2126, 0.7152, 0.0722][index], 0);
            if (!rgb(style.color) || !rgb(style.backgroundColor)) return false;
            const text = luminance(style.color);
            const background = luminance(style.backgroundColor);
            return (Math.max(text, background) + 0.05) / (Math.min(text, background) + 0.05);
        }");
        var contrast = await contrastHandle.JsonValueAsync<double>();
        Assert.True(contrast >= 4.5, $"OIDC action contrast is {contrast:F2}:1.");
        var iconColorMatchesText = await action.EvaluateAsync<bool>("element => getComputedStyle(element.querySelector('svg')).color === getComputedStyle(element).color");
        Assert.True(iconColorMatchesText);
        await action.FocusAsync();
        await page.Keyboard.PressAsync("Shift+Tab");
        await page.Keyboard.PressAsync("Tab");
        Assert.True(await action.EvaluateAsync<bool>("element => document.activeElement === element"));
        Assert.True(await action.EvaluateAsync<bool>("element => { const style = getComputedStyle(element); return style.outlineStyle !== 'none' && parseFloat(style.outlineWidth) >= 2; }"));
    }

    private static async Task SetThemeAndReloadAsync(IPage page, string mode)
    {
        await page.EvaluateAsync<bool>("mode => { if (mode === 'system') localStorage.removeItem('netratel.theme.preference'); else localStorage.setItem('netratel.theme.preference', mode); return true; }", mode);
        await page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.WaitForFunctionAsync("() => document.documentElement.dataset.netratelTheme === 'light' || document.documentElement.dataset.netratelTheme === 'dark'");
    }

    private static async Task AssertProductVersionBadgesAsync(IPage page)
    {
        var expectedVersion = Environment.GetEnvironmentVariable("NETRATEL_BROWSER_SMOKE_EXPECTED_VERSION")
            ?? AppBarVersionResolver.FormatProductVersion(typeof(NetRatel.Web.Components.Pages.Login).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion);

        await page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.SetViewportSizeAsync(1440, 900);
        var desktopBadge = page.Locator(".netratel-appbar-version-chip");
        await desktopBadge.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
        Assert.Equal(expectedVersion, (await desktopBadge.InnerTextAsync()).Trim());

        await page.SetViewportSizeAsync(390, 844);
        var mobileOverflow = page.GetByTestId("mobile-overflow");
        await mobileOverflow.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
        await page.WaitForTimeoutAsync(250);
        await mobileOverflow.ClickAsync();
        var mobileBadge = page.GetByTestId("mobile-product-version");
        await mobileBadge.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
        Assert.Equal(expectedVersion, (await mobileBadge.InnerTextAsync()).Trim());
    }

    private static Uri RequireEnvironmentUri(string name)
    {
        var value = RequireEnvironmentValue(name);
        return Uri.TryCreate(value.EndsWith('/') ? value : $"{value}/", UriKind.Absolute, out var uri)
            ? uri
            : throw new InvalidOperationException($"{name} must be an absolute URI.");
    }

    private static string RequireEnvironmentValue(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"{name} is required by the Compose browser smoke test.");
}
