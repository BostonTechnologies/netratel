using System.Buffers.Binary;
using System.Security.Cryptography;
using Microsoft.Playwright;

namespace NetRatel.Web.PlaywrightTests;

/// <summary>Exercises the real loopback Compose profile; credentials exist only for this disposable run.</summary>
[Trait("category", "compose")]
public sealed class LocalFirstComposeBrowserSmokeTests
{
    [Fact]
    public async Task Cold_instance_guides_setup_then_allows_a_local_administrator_to_use_the_application()
    {
        var webUrl = RequireUri("NETRATEL_LOCAL_FIRST_WEB_URL");
        var setupProof = RequireValue("NETRATEL_LOCAL_FIRST_SETUP_PROOF");
        var email = RequireValue("NETRATEL_LOCAL_FIRST_ADMIN_EMAIL");
        var password = RequireValue("NETRATEL_LOCAL_FIRST_ADMIN_PASSWORD");
        var pageErrors = new List<string>();

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions { ViewportSize = new ViewportSize { Width = 390, Height = 844 } });
        var page = await context.NewPageAsync();
        page.SetDefaultTimeout(20_000);
        page.PageError += (_, error) => pageErrors.Add(error);

        var response = await page.GotoAsync(webUrl.ToString(), new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        Assert.NotNull(response);
        await page.GetByTestId("setup-wizard").WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
        foreach (var themeCase in FirstPaintCases)
        {
            await AssertFirstPaintAsync(browser, webUrl, themeCase, string.Empty, ".netratel-login-panel");
        }
        await page.GetByTestId("setup-client-ready").WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Attached });
        Assert.True(await page.EvaluateAsync<bool>("() => typeof window.netratelSetup?.claim === 'function'"));
        Assert.False(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth > innerWidth"));

        await page.Locator("body").PressAsync("Tab");
        Assert.NotEqual("BODY", await page.EvaluateAsync<string>("() => document.activeElement?.tagName ?? ''"));

        await page.GetByTestId("setup-proof").FillAsync(setupProof);
        await page.GetByTestId("setup-proof").PressAsync("Tab");
        await page.GetByTestId("setup-claim").ClickAsync();
        await page.GetByTestId("setup-tenant").WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
        await page.GetByTestId("setup-client-ready").WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Attached });
        Assert.True(await page.EvaluateAsync<bool>("() => typeof window.netratelSetup?.initialize === 'function'"));
        await page.GetByTestId("setup-tenant").FillAsync("Browser smoke tenant");
        await page.GetByTestId("setup-display-name").FillAsync("Browser smoke administrator");
        await page.GetByTestId("setup-email").FillAsync(email);
        await page.GetByTestId("setup-password").FillAsync(password);
        await page.GetByTestId("setup-confirm-password").FillAsync(password);
        Assert.Equal("Browser smoke tenant", await page.GetByTestId("setup-tenant").InputValueAsync());
        Assert.Equal("Browser smoke administrator", await page.GetByTestId("setup-display-name").InputValueAsync());
        Assert.Equal(email, await page.GetByTestId("setup-email").InputValueAsync());
        Assert.Equal(password, await page.GetByTestId("setup-password").InputValueAsync());
        Assert.Equal(password, await page.GetByTestId("setup-confirm-password").InputValueAsync());
        await page.GetByTestId("setup-initialize").ClickAsync();
        await page.WaitForTimeoutAsync(500);
        var initializationErrorLocator = page.Locator("#setup-client-error");
        var initializationError = await initializationErrorLocator.CountAsync() == 0
            ? null
            : await initializationErrorLocator.TextContentAsync();
        Assert.True(string.IsNullOrWhiteSpace(initializationError), initializationError);

        await page.GetByTestId("local-login-email").WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 30_000 });
        await AssertFirstPaintAsync(browser, webUrl, FirstPaintCases[1], "login", ".netratel-login-panel");
        await page.GetByTestId("local-login-client-ready").WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Attached });
        await page.GetByTestId("local-login-email").FillAsync(email);
        await page.GetByTestId("local-login-password").FillAsync("incorrect local passphrase");
        await page.GetByTestId("local-login-password").PressAsync("Tab");
        await page.GetByTestId("local-login-submit").ClickAsync();
        await page.GetByRole(AriaRole.Alert).WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });

        await page.GetByTestId("local-login-password").FillAsync(password);
        await page.GetByTestId("local-login-password").PressAsync("Tab");
        var signedIn = page.WaitForURLAsync(
            "**/",
            new PageWaitForURLOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60_000 });
        await page.GetByTestId("local-login-submit").ClickAsync();
        await signedIn;
        // The navigation waiter starts before the click so it cannot miss the
        // force-loaded return to '/'. Confirm the authenticated API boundary too.
        await page.WaitForFunctionAsync(
            "async () => (await fetch('/api/v1/tenants')).status === 200",
            null,
            new PageWaitForFunctionOptions { Timeout = 60_000 });

        await AssertFirstPaintAsync(
            browser,
            webUrl,
            FirstPaintCases[2],
            string.Empty,
            ".netratel-app-bar",
            await context.CookiesAsync(),
            requireApplicationSurfaces: true,
            requireInput: false);

        await VerifyDeploymentBrandingAsync(page, webUrl);
        await VerifyLocalAccountSecurityJourneyAsync(browser, page, webUrl);

        var credentialOutputPath = Environment.GetEnvironmentVariable("NETRATEL_LOCAL_FIRST_INTEGRATION_CREDENTIALS_FILE");
        if (!string.IsNullOrWhiteSpace(credentialOutputPath))
        {
            var permitted = await CreateIntegrationCredentialAsync(page, webUrl, "CI telemetry read", "telemetry.read");
            var denied = await CreateIntegrationCredentialAsync(page, webUrl, "CI file read", "file.read");
            var httpMcpPermitted = await CreateIntegrationCredentialAsync(
                page, webUrl, "CI HTTP MCP discovery", "telemetry.read", "HTTP MCP gateway", "https://mcp.local.test/mcp", "mcp.discovery.read");
            var httpMcpDenied = await CreateIntegrationCredentialAsync(
                page, webUrl, "CI HTTP MCP denied discovery", "telemetry.read", "HTTP MCP gateway", "https://mcp.local.test/mcp");
            await File.WriteAllLinesAsync(credentialOutputPath, [permitted, denied, httpMcpPermitted, httpMcpDenied]);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(credentialOutputPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        await page.GotoAsync(new Uri(webUrl, "setup").ToString(), new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.GetByText("This installation is ready.").WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
        Assert.Equal(0, await page.GetByTestId("setup-proof").CountAsync());
        Assert.Empty(pageErrors);
    }

    private static Uri RequireUri(string name) => Uri.TryCreate(RequireValue(name).TrimEnd('/') + "/", UriKind.Absolute, out var uri)
        ? uri
        : throw new InvalidOperationException($"{name} must be an absolute URI.");

    private static string RequireValue(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
        ? value
        : throw new InvalidOperationException($"{name} is required by the local-first Compose browser smoke test.");

    private static readonly FirstPaintCase[] FirstPaintCases =
    [
        new(ColorScheme.Dark, " system ", "dark", "rgb(12, 15, 19)", "rgba(23,28,35,1)", "rgba(5, 13, 34, 0.84)", "rgb(21, 25, 31)", "rgb(18, 23, 29)", "rgb(237, 241, 245)", "rgb(255, 255, 255)"),
        new(ColorScheme.Light, " DARK ", "dark", "rgb(12, 15, 19)", "rgba(23,28,35,1)", "rgba(5, 13, 34, 0.84)", "rgb(21, 25, 31)", "rgb(18, 23, 29)", "rgb(237, 241, 245)", "rgb(255, 255, 255)"),
        new(ColorScheme.Dark, " light ", "light", "rgb(245, 247, 250)", "rgba(255,255,255,1)", "rgba(252, 254, 255, 0.92)", "rgb(255, 255, 255)", "rgb(255, 255, 255)", "rgb(21, 34, 51)", "rgb(0, 0, 0)"),
    ];

    private static async Task AssertFirstPaintAsync(
        IBrowser browser,
        Uri webUrl,
        FirstPaintCase themeCase,
        string path,
        string surfaceSelector,
        IReadOnlyList<BrowserContextCookiesResult>? cookies = null,
        bool requireApplicationSurfaces = false,
        bool requireInput = true)
    {
        await using var firstPaintContext = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ColorScheme = themeCase.ColorScheme,
            ViewportSize = new ViewportSize { Width = 1280, Height = 900 },
        });
        if (cookies is { Count: > 0 })
        {
            await firstPaintContext.AddCookiesAsync(cookies.Select(cookie => new Cookie
            {
                Name = cookie.Name,
                Value = cookie.Value,
                Domain = cookie.Domain,
                Path = cookie.Path,
                Expires = cookie.Expires,
                HttpOnly = cookie.HttpOnly,
                Secure = cookie.Secure,
                SameSite = cookie.SameSite,
                PartitionKey = cookie.PartitionKey,
            }));
        }

        var serializedPreference = System.Text.Json.JsonSerializer.Serialize(themeCase.Preference);
        var serializedSurfaceSelector = System.Text.Json.JsonSerializer.Serialize(surfaceSelector);
        await firstPaintContext.AddInitScriptAsync($$"""
            (() => {
                const preference = {{serializedPreference}};
                window.__netratelThemeFirstPaintSurface = {{serializedSurfaceSelector}};
                localStorage.setItem('netratel.theme.preference', preference);
                const samples = [];
                let observing = true;
                const capture = () => {
                    const body = document.body;
                    const surface = document.querySelector(window.__netratelThemeFirstPaintSurface);
                    const appbar = document.querySelector('.netratel-app-bar');
                    const drawer = document.querySelector('.netratel-app-drawer');
                    const textSurface = document.querySelector('h1, .netratel-app-bar, .netratel-login-panel');
                    const input = document.querySelector('input');
                    if (body && surface && surface.getBoundingClientRect().height > 0) {
                        samples.push({
                            theme: document.documentElement.dataset.netratelTheme,
                            scheme: getComputedStyle(document.documentElement).colorScheme,
                            body: getComputedStyle(body).backgroundColor,
                            text: textSurface ? getComputedStyle(textSurface).color : '',
                            surface: getComputedStyle(document.documentElement).getPropertyValue('--mud-palette-surface').trim(),
                            visibleSurface: getComputedStyle(surface).backgroundColor,
                            appbar: appbar ? getComputedStyle(appbar).backgroundColor : '',
                            drawer: drawer ? getComputedStyle(drawer).backgroundColor : '',
                            input: input ? getComputedStyle(input).color : ''
                        });
                    }
                    if (observing) requestAnimationFrame(capture);
                };
                window.__netratelThemeFirstPaint = { samples, stop: () => { observing = false; } };
                requestAnimationFrame(capture);
            })();
            """);

        var releaseRuntime = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await firstPaintContext.RouteAsync("**/_framework/blazor.web.js", async route =>
        {
            await releaseRuntime.Task;
            await route.ContinueAsync();
        });

        var page = await firstPaintContext.NewPageAsync();
        page.SetDefaultTimeout(20_000);
        try
        {
            await page.GotoAsync(new Uri(webUrl, path).ToString(), new PageGotoOptions { WaitUntil = WaitUntilState.Commit });
            await page.Locator(surfaceSelector).WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
            await page.WaitForFunctionAsync("() => window.__netratelThemeFirstPaint.samples.length > 0");
            var expectedVisibleSurface = requireApplicationSurfaces ? themeCase.Appbar : themeCase.VisibleSurface;

            var firstPaint = await page.EvaluateAsync<string[]>("""
                () => {
                    const sample = window.__netratelThemeFirstPaint.samples.at(-1);
                    return [sample.theme, sample.scheme, sample.body, sample.text, sample.surface, sample.visibleSurface, sample.appbar, sample.drawer, sample.input];
                }
                """);
            Assert.Equal(themeCase.ExpectedTheme, firstPaint[0]);
            Assert.Equal(themeCase.ExpectedTheme, firstPaint[1]);
            Assert.Equal(themeCase.Background, firstPaint[2]);
            Assert.Equal(themeCase.Text, firstPaint[3]);
            Assert.Equal(themeCase.Surface, firstPaint[4]);
            Assert.Equal(expectedVisibleSurface, firstPaint[5]);
            if (requireInput)
            {
                Assert.Equal(themeCase.Input, firstPaint[8]);
            }
            else
            {
                Assert.Empty(firstPaint[8]);
            }
            if (requireApplicationSurfaces)
            {
                Assert.Equal(themeCase.Appbar, firstPaint[6]);
                Assert.Equal(themeCase.Drawer, firstPaint[7]);
            }

            releaseRuntime.TrySetResult();
            await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
            await page.WaitForFunctionAsync("""
                expected => {
                    const surface = document.querySelector(window.__netratelThemeFirstPaintSurface);
                    return surface && getComputedStyle(document.body).backgroundColor === expected.background &&
                        getComputedStyle(document.documentElement).getPropertyValue('--mud-palette-surface').trim() === expected.surface &&
                        getComputedStyle(surface).backgroundColor === expected.visibleSurface &&
                        document.documentElement.dataset.netratelTheme === expected.theme;
                }
                """, new { background = themeCase.Background, surface = themeCase.Surface, visibleSurface = expectedVisibleSurface, theme = themeCase.ExpectedTheme });
        }
        finally
        {
            releaseRuntime.TrySetResult();
            await page.EvaluateAsync("() => window.__netratelThemeFirstPaint?.stop()").ConfigureAwait(false);
        }
    }

    private sealed record FirstPaintCase(
        ColorScheme ColorScheme,
        string Preference,
        string ExpectedTheme,
        string Background,
        string Surface,
        string VisibleSurface,
        string Appbar,
        string Drawer,
        string Text,
        string Input);

    private static async Task VerifyDeploymentBrandingAsync(IPage page, Uri webUrl)
    {
        await page.GotoAsync(new Uri(webUrl, "admin/branding").ToString(), new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.GetByTestId("deployment-branding-page").WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
        await page.WaitForTimeoutAsync(500);
        var applicationName = page.GetByLabel("Application name");
        await applicationName.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
        await applicationName.FillAsync("Browser branding example");
        await applicationName.PressAsync("Tab");
        await page.GetByTestId("branding-save").ClickAsync();
        await page.GetByText("Branding saved.", new PageGetByTextOptions { Exact = false })
            .WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });

        var directory = Path.Combine("TestResults", "branding");
        Directory.CreateDirectory(directory);
        foreach (var (theme, width, height, name) in new[]
                 {
                     ("light", 1440, 900, "desktop"),
                     ("dark", 390, 844, "mobile")
                 })
        {
            await page.EvaluateAsync<bool>("mode => { localStorage.setItem('netratel.theme.preference', mode); return true; }", theme);
            await page.ReloadAsync(new PageReloadOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
            await page.GetByTestId("deployment-branding-page").WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
            await page.SetViewportSizeAsync(width, height);
            var preview = page.GetByTestId("branding-preview");
            await preview.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
            Assert.Contains("Browser branding example", await preview.InnerTextAsync());
            await page.ScreenshotAsync(new PageScreenshotOptions
            {
                Path = Path.Combine(directory, $"custom-{theme}-{name}.png"),
                Animations = ScreenshotAnimations.Disabled
            });
        }

        await page.SetViewportSizeAsync(1440, 900);
        await page.GotoAsync(webUrl.ToString(), new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        var applicationBrand = page.Locator(".netratel-appbar-brand img");
        await applicationBrand.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
        await page.WaitForFunctionAsync("() => document.querySelector('.netratel-appbar-brand img')?.getAttribute('alt') === 'Browser branding example'");
        // Let any overlapping prerender/interactive branding read complete. A
        // stale response must not revert the accepted persisted presentation.
        await page.WaitForTimeoutAsync(500);
        Assert.Equal("Browser branding example", await applicationBrand.GetAttributeAsync("alt"));
    }

    private static async Task<string> CreateIntegrationCredentialAsync(
        IPage page,
        Uri webUrl,
        string name,
        string permission,
        string? purpose = null,
        string? resource = null,
        string? instancePermission = null)
    {
        await page.GotoAsync(new Uri(webUrl, "account/integration-credentials").ToString(), new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.GetByTestId("integration-credentials-page").WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
        // The initial page is server prerendered; wait for the InteractiveServer
        // circuit before relying on component event callbacks.
        await page.WaitForTimeoutAsync(500);
        await page.GetByTestId("credential-name").FillAsync(name);
        await page.GetByTestId("credential-name").PressAsync("Tab");
        await page.GetByTestId("credential-tenant")
            .GetByRole(AriaRole.Combobox, new LocatorGetByRoleOptions { Name = "Tenant", Exact = true })
            .ClickAsync();
        await page.GetByRole(AriaRole.Option, new PageGetByRoleOptions { Name = "Browser smoke tenant", Exact = true }).ClickAsync();
        if (!string.IsNullOrWhiteSpace(purpose))
        {
            await page.GetByRole(AriaRole.Combobox, new PageGetByRoleOptions { Name = "Purpose" }).ClickAsync();
            await page.GetByRole(AriaRole.Option, new PageGetByRoleOptions { Name = purpose, Exact = true }).ClickAsync();
            await page.GetByTestId("credential-resource").FillAsync(resource ?? throw new InvalidOperationException("An HTTP MCP resource is required."));
        }
        await page.GetByRole(AriaRole.Combobox, new PageGetByRoleOptions { Name = "Permission", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Option, new PageGetByRoleOptions { Name = permission, Exact = true }).ClickAsync();
        if (!string.IsNullOrWhiteSpace(instancePermission))
        {
            await page.GetByRole(AriaRole.Combobox, new PageGetByRoleOptions { Name = "Optional instance permission" }).ClickAsync();
            await page.GetByRole(AriaRole.Option, new PageGetByRoleOptions { Name = instancePermission, Exact = true }).ClickAsync();
        }
        await page.WaitForTimeoutAsync(250);
        await page.GetByTestId("create-credential").ClickAsync();
        var reveal = page.GetByTestId("credential-one-time-secret");
        try
        {
            await reveal.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
        }
        catch (TimeoutException)
        {
            var error = page.GetByTestId("credential-error");
            throw new InvalidOperationException(await error.CountAsync() == 1
                ? await error.TextContentAsync()
                : "Credential creation did not reveal a secret or report a safe error.");
        }
        var secret = await reveal.Locator("input").InputValueAsync();
        Assert.StartsWith("nrt_ic_", secret);
        await reveal.GetByText("I stored it safely", new LocatorGetByTextOptions { Exact = true }).ClickAsync();
        return secret;
    }

    private static async Task VerifyLocalAccountSecurityJourneyAsync(IBrowser browser, IPage page, Uri webUrl)
    {
        const string secondUserEmail = "browser-local-operator@example.test";
        const string secondUserPassword = "browser local operator passphrase";

        await page.GotoAsync(new Uri(webUrl, "admin/access").ToString(), new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.GetByTestId("create-local-user").WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
        await page.WaitForTimeoutAsync(500);
        await page.GetByTestId("local-user-display-name").FillAsync("Browser local operator");
        await page.GetByTestId("local-user-display-name").PressAsync("Tab");
        await page.GetByTestId("local-user-email").FillAsync(secondUserEmail);
        await page.GetByTestId("local-user-email").PressAsync("Tab");
        await page.GetByTestId("create-local-user").ClickAsync();
        var activation = page.GetByTestId("local-user-activation");
        await activation.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
        var activationToken = await page.GetByTestId("local-user-activation-token").InputValueAsync();
        Assert.False(string.IsNullOrWhiteSpace(activationToken));

        await page.GetByRole(AriaRole.Combobox, new PageGetByRoleOptions { Name = "Manage tenant", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Option, new PageGetByRoleOptions { Name = "Browser smoke tenant", Exact = true }).ClickAsync();
        var localOperator = page.GetByText("Browser local operator", new PageGetByTextOptions { Exact = true });
        await localOperator.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
        await localOperator.ClickAsync();
        await page.GetByTestId("access-assignment-scope").WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
        Assert.Equal("Browser smoke tenant", await page.GetByTestId("access-assignment-scope").InnerTextAsync());
        await page.GetByRole(AriaRole.Combobox, new PageGetByRoleOptions { Name = "Role", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Option, new PageGetByRoleOptions { Name = "Operator", Exact = true }).ClickAsync();
        await page.GetByTestId("access-add-assignment").ClickAsync();
        var assignmentScope = page.Locator("[data-testid^='access-assignment-scope-assignment-']");
        await assignmentScope.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
        Assert.Equal("Browser smoke tenant", await assignmentScope.InnerTextAsync());

        // Simulate a separate browser receiving the handoff. Clearing cookies on the
        // administrator's page would retain its interactive Blazor circuit.
        await using var operatorContext = await browser.NewContextAsync();
        page = await operatorContext.NewPageAsync();
        page.SetDefaultTimeout(20_000);
        await page.GotoAsync(new Uri(webUrl, "activate").ToString(), new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.GetByTestId("local-account-activation-page").WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
        await page.WaitForTimeoutAsync(500);
        await page.GetByTestId("activation-email").FillAsync(secondUserEmail);
        await page.GetByTestId("activation-email").PressAsync("Tab");
        await page.GetByTestId("activation-token").FillAsync(activationToken);
        await page.GetByTestId("activation-token").PressAsync("Tab");
        await page.GetByTestId("activation-password").FillAsync(secondUserPassword);
        await page.GetByTestId("activation-password").PressAsync("Tab");
        await page.GetByTestId("activation-confirm-password").FillAsync(secondUserPassword);
        await page.GetByTestId("activation-confirm-password").PressAsync("Tab");
        var activated = page.WaitForURLAsync("**/login", new PageWaitForURLOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30_000 });
        await page.GetByTestId("activate-local-account").ClickAsync();
        await activated;

        await SignInLocallyAsync(page, secondUserEmail, secondUserPassword);
        await page.GotoAsync(new Uri(webUrl, "account/security").ToString(), new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.GetByTestId("account-security-page").WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
        await page.WaitForTimeoutAsync(500);
        await page.GetByTestId("mfa-setup-current-password").FillAsync(secondUserPassword);
        await page.GetByTestId("mfa-setup-current-password").PressAsync("Tab");
        await page.GetByTestId("begin-mfa-setup").ClickAsync();
        var enrollmentSecret = page.GetByTestId("mfa-authenticator-secret");
        await enrollmentSecret.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
        var sharedKey = await enrollmentSecret.Locator("input").First.InputValueAsync();
        var enrollmentCode = CreateTotp(sharedKey);
        await page.GetByTestId("mfa-enrollment-code").FillAsync(enrollmentCode);
        await page.GetByTestId("mfa-enrollment-code").PressAsync("Tab");
        await page.GetByTestId("enable-mfa").ClickAsync();
        var recoveryCodes = page.GetByTestId("mfa-recovery-codes");
        await recoveryCodes.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
        var recoveryCode = await page.GetByTestId("mfa-recovery-code").First.InnerTextAsync();
        Assert.False(string.IsNullOrWhiteSpace(recoveryCode));
        var signedOutAfterEnrollment = page.WaitForURLAsync("**/login", new PageWaitForURLOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30_000 });
        await page.GetByTestId("finish-mfa-enrollment").ClickAsync();
        await signedOutAfterEnrollment;

        await SignInWithSecondFactorAsync(page, secondUserEmail, secondUserPassword, recoveryCode, expectSuccess: true);
        await page.GotoAsync(new Uri(webUrl, "account/security").ToString(), new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.GetByTestId("account-security-page").WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
        await page.WaitForTimeoutAsync(500);
        await page.GetByTestId("disable-mfa-current-password").FillAsync(secondUserPassword);
        await page.GetByTestId("disable-mfa-current-password").PressAsync("Tab");
        await page.GetByTestId("disable-mfa-code").FillAsync(CreateTotp(sharedKey));
        await page.GetByTestId("disable-mfa-code").PressAsync("Tab");
        var signedOutAfterDisable = page.WaitForURLAsync("**/login", new PageWaitForURLOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30_000 });
        await page.GetByTestId("disable-mfa").ClickAsync();
        await signedOutAfterDisable;

        await SignInLocallyAsync(page, secondUserEmail, secondUserPassword);
    }

    private static async Task SignInLocallyAsync(IPage page, string email, string password)
    {
        await page.GotoAsync(new Uri(RequireUri("NETRATEL_LOCAL_FIRST_WEB_URL"), "login").ToString(), new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.GetByTestId("local-login-client-ready").WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Attached });
        await page.GetByTestId("local-login-email").WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
        await page.GetByTestId("local-login-email").FillAsync(email);
        await page.GetByTestId("local-login-email").PressAsync("Tab");
        await page.GetByTestId("local-login-password").FillAsync(password);
        await page.GetByTestId("local-login-password").PressAsync("Tab");
        var signedIn = page.WaitForURLAsync("**/", new PageWaitForURLOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30_000 });
        await page.GetByTestId("local-login-submit").ClickAsync();
        await signedIn;
    }

    private static async Task SignInWithSecondFactorAsync(IPage page, string email, string password, string code, bool expectSuccess)
    {
        await page.GotoAsync(new Uri(RequireUri("NETRATEL_LOCAL_FIRST_WEB_URL"), "login").ToString(), new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.GetByTestId("local-login-client-ready").WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Attached });
        await page.GetByTestId("local-login-email").FillAsync(email);
        await page.GetByTestId("local-login-email").PressAsync("Tab");
        await page.GetByTestId("local-login-password").FillAsync(password);
        await page.GetByTestId("local-login-password").PressAsync("Tab");
        await page.GetByTestId("local-login-submit").ClickAsync();
        await page.GetByTestId("local-login-two-factor").WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
        await page.GetByTestId("local-login-client-ready").WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Attached });
        await page.GetByTestId("local-login-two-factor").FillAsync(code);
        await page.GetByTestId("local-login-two-factor").PressAsync("Tab");
        if (expectSuccess)
        {
            var signedIn = page.WaitForURLAsync("**/", new PageWaitForURLOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30_000 });
            await page.GetByTestId("local-login-two-factor-submit").ClickAsync();
            await signedIn;
            return;
        }

        await page.GetByTestId("local-login-two-factor-submit").ClickAsync();
        await page.GetByRole(AriaRole.Alert).WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
    }

    private static string CreateTotp(string base32Secret)
    {
        var secret = DecodeBase32(base32Secret);
        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30);
        using var hmac = new HMACSHA1(secret);
        var hash = hmac.ComputeHash(counter.ToArray());
        var offset = hash[^1] & 0x0f;
        var value = BinaryPrimitives.ReadInt32BigEndian(hash.AsSpan(offset, 4)) & 0x7fffffff;
        return (value % 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static byte[] DecodeBase32(string value)
    {
        var buffer = 0;
        var bits = 0;
        var decoded = new List<byte>();
        foreach (var character in value.Where(character => character is not (' ' or '-')).Select(char.ToUpperInvariant))
        {
            var digit = character switch
            {
                >= 'A' and <= 'Z' => character - 'A',
                >= '2' and <= '7' => character - '2' + 26,
                _ => throw new InvalidOperationException("The authenticator secret was not valid Base32.")
            };
            buffer = (buffer << 5) | digit;
            bits += 5;
            if (bits < 8) continue;
            decoded.Add((byte)(buffer >> (bits - 8)));
            bits -= 8;
        }

        return decoded.ToArray();
    }
}
