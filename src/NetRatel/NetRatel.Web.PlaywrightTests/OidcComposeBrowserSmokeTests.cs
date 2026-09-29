using Microsoft.Playwright;
using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;
using NetRatel.Web.Components.Layout;

namespace NetRatel.Web.PlaywrightTests;

[Trait("category", "compose")]
public sealed class OidcComposeBrowserSmokeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GenericOidcStack_LoadsAssets_Authenticates_And_LogsOut(bool useProxy)
    {
        var webUrl = RequireEnvironmentUri(useProxy ? "NETRATEL_BROWSER_SMOKE_PROXY_URL" : "NETRATEL_BROWSER_SMOKE_WEB_URL");
        var username = RequireEnvironmentValue("NETRATEL_BROWSER_SMOKE_USERNAME");
        var nssDataHome = RequireEnvironmentValue("NETRATEL_BROWSER_SMOKE_NSS_DATA_HOME");
        var expectCurrentShell = Environment.GetEnvironmentVariable("NETRATEL_BROWSER_SMOKE_EXPECT_CURRENT_SHELL") != "false";
        Assert.True(Directory.Exists(Path.Combine(nssDataHome, "pki", "nssdb")),
            "The browser smoke must use its private, prepared Chromium NSS database.");

        using var playwright = await Playwright.CreateAsync();
        var executableVersion = await ReadChromiumExecutableVersionAsync(playwright.Chromium.ExecutablePath, nssDataHome);
        Assert.True(executableVersion.Major >= 146,
            $"Chromium {executableVersion} predates the supported XDG NSS database path.");

        var browserEnvironment = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && entry.Value is string value)
                browserEnvironment[key] = value;
        }
        browserEnvironment["XDG_DATA_HOME"] = nssDataHome;
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true,
            Env = browserEnvironment,
            // The disposable issuer is exposed only on the hosted runner loopback.
            // Containers and the OIDC issuer use this stable authority hostname.
            Args = ["--host-resolver-rules=MAP host.docker.internal 127.0.0.1"]
        });
        Assert.Equal(executableVersion.Major, int.Parse(browser.Version.Split('.')[0], System.Globalization.CultureInfo.InvariantCulture));
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions { IgnoreHTTPSErrors = false });
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
        await page.Locator("input[name='username']").FillAsync(username,
            new LocatorFillOptions { Timeout = 60_000 });
        var callbackResponse = page.WaitForResponseAsync(response =>
            Uri.TryCreate(response.Url, UriKind.Absolute, out var responseUri)
            && responseUri.GetLeftPart(UriPartial.Path) == new Uri(webUrl, "signin-oidc").ToString());
        await page.Locator("form").EvaluateAsync("form => form.submit()");
        Assert.Equal(302, (await callbackResponse).Status);
        await page.WaitForURLAsync(new Uri(webUrl, "tenants").ToString(),
            new PageWaitForURLOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        var sessionCookies = (await context.CookiesAsync()).Where(cookie => cookie.Name.StartsWith(".AspNetCore.Cookies", StringComparison.Ordinal)).ToArray();
        Assert.NotEmpty(sessionCookies);
        if (webUrl.Scheme == Uri.UriSchemeHttps)
            Assert.All(sessionCookies, cookie => Assert.True(cookie.Secure));

        var authenticatedStatus = await page.EvaluateAsync<int>("async () => (await fetch('/api/v1/tenants')).status");
        Assert.Equal(200, authenticatedStatus);
        await CaptureBrandingAsync(page, "navbar");
        await AssertProductVersionBadgesAsync(page);
        if (expectCurrentShell)
        {
            await page.GotoAsync(new Uri(webUrl, "account/security").ToString(),
                new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
            await page.GetByTestId("account-security-managed").WaitForAsync(
                new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
            Assert.Equal(0, await page.GetByTestId("open-password-dialog").CountAsync());
            Assert.Equal(0, await page.GetByTestId("open-mfa-setup").CountAsync());
        }

        if (expectCurrentShell)
        {
            var verifyDirectoryAuthorizationBoundaries =
                string.Equals(Environment.GetEnvironmentVariable("NETRATEL_BROWSER_SMOKE_DIRECTORY_AUTHZ"), "true", StringComparison.OrdinalIgnoreCase);
            await VerifyClientDirectoryCircuitAsync(
                page,
                webUrl,
                "OIDC Operator",
                returnToOriginalPage: !verifyDirectoryAuthorizationBoundaries);

            if (verifyDirectoryAuthorizationBoundaries)
            {
                await VerifyRestrictedDirectoryCircuitsAsync(browser, page, webUrl);
            }
        }

        await page.GotoAsync(new Uri(webUrl, "auth/logout").ToString(), new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        var anonymousStatus = await page.EvaluateAsync<int>("async () => (await fetch('/api/v1/tenants')).status");
        Assert.Equal(401, anonymousStatus);
    }

    internal static async Task VerifyClientDirectoryCircuitAsync(
        IPage page,
        Uri webUrl,
        string identityDescription,
        bool returnToOriginalPage = true)
    {
        var returnUrl = page.Url;
        var response = await page.GotoAsync(new Uri(webUrl, "clients").ToString(), new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        Assert.NotNull(response);
        Assert.True(response.Ok, $"The authenticated {identityDescription} client directory returned HTTP {response.Status}.");
        var directory = page.GetByTestId("client-directory-state");
        await page.GetByTestId("clients-page-interactive").WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Attached
        });
        await page.WaitForFunctionAsync(
            "() => { const state = document.querySelector('[data-testid=client-directory-state]'); return state?.getAttribute('data-last-load-trigger') === 'initial' && state?.getAttribute('data-loading') === 'false' && Number(state?.getAttribute('data-load-count') || 0) > 0; }",
            null,
            new PageWaitForFunctionOptions { Timeout = 30_000 });
        await AssertClientDirectorySucceededAsync(page, directory, identityDescription);

        var loadCount = await ReadClientDirectoryLoadCountAsync(directory);
        await page.GetByRole(AriaRole.Button, new() { Name = "Refresh clients" }).ClickAsync();
        await WaitForClientDirectoryLoadAfterAsync(page, loadCount, "manual");
        await AssertClientDirectorySucceededAsync(page, directory, identityDescription);

        loadCount = await ReadClientDirectoryLoadCountAsync(directory);
        await WaitForClientDirectoryLoadAfterAsync(page, loadCount, "periodic", timeoutMilliseconds: 25_000);
        await AssertClientDirectorySucceededAsync(page, directory, identityDescription);

        var evidenceDirectory = Path.Combine("TestResults", "playwright");
        Directory.CreateDirectory(evidenceDirectory);
        var identitySlug = string.Concat(identityDescription.Select(character => char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : '-'))
            .Trim('-');
        await page.ScreenshotAsync(new PageScreenshotOptions
        {
            Path = Path.Combine(evidenceDirectory, $"clients-directory-{identitySlug}-compose.png"),
            FullPage = true,
            Animations = ScreenshotAnimations.Disabled
        });
        if (returnToOriginalPage)
        {
            await page.GotoAsync(returnUrl, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        }
    }

    private static async Task VerifyRestrictedDirectoryCircuitsAsync(
        IBrowser browser,
        IPage operatorPage,
        Uri webUrl)
    {
        foreach (var (environmentName, identityDescription) in new[]
        {
            ("NETRATEL_BROWSER_SMOKE_TENANT_ADMIN_USERNAME", "OIDC tenant administrator"),
            ("NETRATEL_BROWSER_SMOKE_UNPRIVILEGED_USERNAME", "OIDC principal without a role")
        })
        {
            var username = RequireEnvironmentValue(environmentName);
            await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
            {
                IgnoreHTTPSErrors = false
            });
            var page = await context.NewPageAsync();
            page.SetDefaultTimeout(15_000);
            await SignInToClientsAsync(page, webUrl, username);
            await VerifyClientDirectoryDeniedCircuitAsync(page, webUrl, identityDescription);
        }

        // Keep the original operator circuit alive while the restricted users
        // authenticate and make requests. Its refresh proves the pooled
        // transport did not carry another circuit's credentials forward.
        var directory = operatorPage.GetByTestId("client-directory-state");
        var count = await ReadClientDirectoryLoadCountAsync(directory);
        await operatorPage.GetByRole(AriaRole.Button, new() { Name = "Refresh clients" }).ClickAsync();
        await WaitForClientDirectoryLoadAfterAsync(operatorPage, count, "manual");
        await AssertClientDirectorySucceededAsync(operatorPage, directory, "OIDC Operator after restricted-user requests");

        count = await ReadClientDirectoryLoadCountAsync(directory);
        await WaitForClientDirectoryLoadAfterAsync(operatorPage, count, "periodic", timeoutMilliseconds: 25_000);
        await AssertClientDirectorySucceededAsync(operatorPage, directory, "OIDC Operator after restricted-user requests");
    }

    private static async Task SignInToClientsAsync(IPage page, Uri webUrl, string username)
    {
        var loginResponse = await page.GotoAsync(
            new Uri(webUrl, "login?ReturnUrl=%2Fclients").ToString(),
            new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        Assert.NotNull(loginResponse);
        Assert.True(loginResponse.Ok, $"The login page returned HTTP {loginResponse.Status}.");
        await page.Locator(".netratel-login-primary-action").ClickAsync();
        await page.Locator("input[name='username']").FillAsync(username, new LocatorFillOptions { Timeout = 60_000 });

        var callbackResponse = page.WaitForResponseAsync(response =>
            Uri.TryCreate(response.Url, UriKind.Absolute, out var responseUri)
            && responseUri.GetLeftPart(UriPartial.Path) == new Uri(webUrl, "signin-oidc").ToString());
        await page.Locator("form").EvaluateAsync("form => form.submit()");
        Assert.Equal(302, (await callbackResponse).Status);
        await page.WaitForURLAsync(new Uri(webUrl, "clients").ToString(),
            new PageWaitForURLOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
    }

    internal static async Task VerifyClientDirectoryDeniedCircuitAsync(IPage page, Uri webUrl, string identityDescription)
    {
        var response = await page.GotoAsync(new Uri(webUrl, "clients").ToString(), new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        Assert.NotNull(response);
        Assert.True(response.Ok, $"The authenticated {identityDescription} page returned HTTP {response.Status}.");
        var directory = page.GetByTestId("client-directory-state");
        await page.GetByTestId("clients-page-interactive").WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Attached
        });
        await page.WaitForFunctionAsync(
            "() => { const state = document.querySelector('[data-testid=client-directory-state]'); return state?.getAttribute('data-last-load-trigger') === 'initial' && state?.getAttribute('data-loading') === 'false' && Number(state?.getAttribute('data-load-count') || 0) > 0; }",
            null,
            new PageWaitForFunctionOptions { Timeout = 30_000 });
        await AssertClientDirectoryDeniedAsync(page, directory, identityDescription);

        var count = await ReadClientDirectoryLoadCountAsync(directory);
        await page.GetByTestId("retry-client-directory").ClickAsync();
        await WaitForClientDirectoryLoadAfterAsync(page, count, "manual");
        await AssertClientDirectoryDeniedAsync(page, directory, identityDescription);

        count = await ReadClientDirectoryLoadCountAsync(directory);
        await WaitForClientDirectoryLoadAfterAsync(page, count, "periodic", timeoutMilliseconds: 25_000);
        await AssertClientDirectoryDeniedAsync(page, directory, identityDescription);

        var evidenceDirectory = Path.Combine("TestResults", "playwright");
        Directory.CreateDirectory(evidenceDirectory);
        var identitySlug = string.Concat(identityDescription.Select(character => char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : '-'))
            .Trim('-');
        await page.ScreenshotAsync(new PageScreenshotOptions
        {
            Path = Path.Combine(evidenceDirectory, $"clients-directory-denied-{identitySlug}-compose.png"),
            FullPage = true,
            Animations = ScreenshotAnimations.Disabled
        });
    }

    private static async Task AssertClientDirectoryDeniedAsync(IPage page, ILocator directory, string identityDescription)
    {
        var loadCount = await directory.GetAttributeAsync("data-load-count");
        Assert.True(int.TryParse(loadCount, out var count) && count > 0,
            $"The {identityDescription} directory request did not settle.");
        Assert.True(await page.GetByTestId("retry-client-directory").IsVisibleAsync(),
            $"The {identityDescription} must see a retryable permission error instead of an empty directory.");
        var message = await page.GetByRole(AriaRole.Alert).InnerTextAsync();
        Assert.Contains("does not have permission to read the client directory", message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, await page.GetByText("No registered clients yet.", new() { Exact = false }).CountAsync());
        Assert.Equal(0, await page.Locator(".client-card, .client-grid-view tbody tr").CountAsync());
    }

    private static async Task WaitForClientDirectoryLoadAfterAsync(
        IPage page,
        int previousLoadCount,
        string expectedTrigger,
        int timeoutMilliseconds = 20_000) =>
        await page.WaitForFunctionAsync(
            "expected => { const state = document.querySelector('[data-testid=client-directory-state]'); return Number(state?.getAttribute('data-load-count') || 0) > expected.previousLoadCount && state?.getAttribute('data-last-load-trigger') === expected.trigger && state?.getAttribute('data-loading') === 'false'; }",
            new { previousLoadCount, trigger = expectedTrigger },
            new PageWaitForFunctionOptions { Timeout = timeoutMilliseconds });

    private static async Task<int> ReadClientDirectoryLoadCountAsync(ILocator directory) =>
        int.Parse((await directory.GetAttributeAsync("data-load-count")) ?? "0", System.Globalization.CultureInfo.InvariantCulture);

    private static async Task AssertClientDirectorySucceededAsync(IPage page, ILocator directory, string identityDescription)
    {
        var loadCount = await directory.GetAttributeAsync("data-load-count");
        Assert.True(int.TryParse(loadCount, out var count) && count > 0, "The OIDC directory request did not settle.");
        var errorCount = await page.GetByTestId("retry-client-directory").CountAsync();
        if (errorCount != 0)
        {
            var message = await page.GetByRole(AriaRole.Alert).InnerTextAsync();
            Assert.Fail($"The authenticated {identityDescription} circuit could not read the client directory: {message}");
        }

        Assert.True(
            await page.GetByText("No registered clients yet.", new() { Exact = false }).CountAsync() > 0 ||
            await page.Locator(".client-card").CountAsync() > 0 ||
            await page.Locator(".client-grid-view tbody tr").CountAsync() > 0,
            $"The authenticated {identityDescription} directory should render either registered clients or its successful empty state.");
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
        await page.GetByTestId("local-login-client-ready").WaitForAsync(
            new LocatorWaitForOptions { State = WaitForSelectorState.Attached });
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
        await page.WaitForFunctionAsync("""
            () => {
                const element = document.querySelector('.netratel-login-primary-action');
                if (document.activeElement !== element) return false;
                const style = getComputedStyle(element);
                return style.outlineStyle !== 'none' && parseFloat(style.outlineWidth) >= 2;
            }
            """, null, new PageWaitForFunctionOptions { Timeout = 5_000 });
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

    private static async Task<Version> ReadChromiumExecutableVersionAsync(string executablePath, string nssDataHome)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("--version");
        startInfo.Environment["XDG_DATA_HOME"] = nssDataHome;

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The Playwright Chromium executable could not be started for version verification.");
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw new InvalidOperationException("The Playwright Chromium version probe did not finish within ten seconds.");
        }

        var output = $"{await standardOutput} {await standardError}";
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"The Playwright Chromium version probe exited with {process.ExitCode}.");

        var match = Regex.Match(output, @"(?<version>\d+\.\d+(?:\.\d+){1,2})", RegexOptions.CultureInvariant);
        return match.Success && Version.TryParse(match.Groups["version"].Value, out var version)
            ? version
            : throw new InvalidOperationException("The Playwright Chromium executable did not report a recognizable version.");
    }
}
