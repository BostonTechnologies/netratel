using Microsoft.Playwright;

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
            ("favicon.ico", "image/", 150_000)
        })
        {
            var response = await context.APIRequest.GetAsync(new Uri(webUrl, asset).ToString());
            Assert.True(response.Ok, $"{asset} returned {response.Status}");
            Assert.StartsWith(contentType, response.Headers["content-type"]);
            Assert.InRange((await response.BodyAsync()).Length, 1, maximumBytes);
        }
        await page.GotoAsync(new Uri(webUrl, "login").ToString());
        await CaptureBrandingAsync(page, trustedProxy ? "proxy-login" : "login");

        var login = await page.GotoAsync(new Uri(webUrl, "auth/oidc?returnUrl=%2Ftenants").ToString(), new PageGotoOptions
        {
            WaitUntil = WaitUntilState.DOMContentLoaded
        });
        Assert.NotNull(login);
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
        await CaptureBrandingAsync(page, trustedProxy ? "proxy-navbar" : "navbar");
        await AssertProductVersionBadgesAsync(page);

        await page.GotoAsync(new Uri(webUrl, "auth/logout").ToString(), new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        var anonymousStatus = await page.EvaluateAsync<int>("async () => (await fetch('/api/v1/tenants')).status");
        Assert.Equal(401, anonymousStatus);
    }

    private static async Task CaptureBrandingAsync(IPage page, string view)
    {
        var directory = Path.Combine("TestResults", "branding");
        Directory.CreateDirectory(directory);
        foreach (var (width, height, name) in new[] { (1440, 900, "desktop"), (390, 844, "mobile") })
        {
            await page.SetViewportSizeAsync(width, height);
            await page.Locator("img[src*='brand/']").First.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Attached });
            if (width >= 800 || view.EndsWith("login", StringComparison.Ordinal))
                Assert.True(await page.Locator("img[src*='brand/']").First.IsVisibleAsync());
            await page.WaitForFunctionAsync("() => Array.from(document.querySelectorAll('img[src*=\"brand/\"]')).every(image => image.complete && image.naturalWidth > 0)");
            Assert.False(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth > innerWidth"),
                $"{view} overflows the {name} viewport.");
            await page.ScreenshotAsync(new PageScreenshotOptions
            {
                Path = Path.Combine(directory, $"{view}-{name}.png"),
                FullPage = true,
                Animations = ScreenshotAnimations.Disabled
            });
        }
    }

    private static async Task AssertProductVersionBadgesAsync(IPage page)
    {
        const string expectedVersion = "v0.1.0-rc.2";

        await page.SetViewportSizeAsync(1440, 900);
        var desktopBadge = page.Locator(".netratel-appbar-version-chip");
        await desktopBadge.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
        Assert.Equal(expectedVersion, (await desktopBadge.InnerTextAsync()).Trim());

        await page.SetViewportSizeAsync(390, 844);
        await page.GetByTestId("mobile-overflow").ClickAsync();
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
