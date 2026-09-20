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
        var initializationError = await page.Locator("#setup-client-error").TextContentAsync();
        Assert.True(string.IsNullOrWhiteSpace(initializationError), initializationError);

        await page.GetByTestId("local-login-email").WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 30_000 });
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

        await VerifyDeploymentBrandingAsync(page, webUrl);

        var credentialOutputPath = Environment.GetEnvironmentVariable("NETRATEL_LOCAL_FIRST_INTEGRATION_CREDENTIALS_FILE");
        if (!string.IsNullOrWhiteSpace(credentialOutputPath))
        {
            var permitted = await CreateIntegrationCredentialAsync(page, webUrl, "CI telemetry read", "telemetry.read");
            var denied = await CreateIntegrationCredentialAsync(page, webUrl, "CI file read", "file.read");
            await File.WriteAllLinesAsync(credentialOutputPath, [permitted, denied]);
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
            await preview.ScreenshotAsync(new LocatorScreenshotOptions
            {
                Path = Path.Combine(directory, $"custom-{theme}-{name}.png"),
                Animations = ScreenshotAnimations.Disabled
            });
        }

        await page.SetViewportSizeAsync(1440, 900);
        await page.GotoAsync(webUrl.ToString(), new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        var applicationBrand = page.Locator(".netratel-appbar-brand img");
        await applicationBrand.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
        Assert.Equal("Browser branding example", await applicationBrand.GetAttributeAsync("alt"));
    }

    private static async Task<string> CreateIntegrationCredentialAsync(IPage page, Uri webUrl, string name, string permission)
    {
        await page.GotoAsync(new Uri(webUrl, "account/integration-credentials").ToString(), new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.GetByTestId("integration-credentials-page").WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
        // The initial page is server prerendered; wait for the InteractiveServer
        // circuit before relying on component event callbacks.
        await page.WaitForTimeoutAsync(500);
        await page.GetByTestId("credential-name").FillAsync(name);
        await page.GetByTestId("credential-name").PressAsync("Tab");
        await page.GetByRole(AriaRole.Combobox, new PageGetByRoleOptions { Name = "Permission" }).ClickAsync();
        await page.GetByRole(AriaRole.Option, new PageGetByRoleOptions { Name = permission, Exact = true }).ClickAsync();
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
        await page.GetByText($"1:{permission}", new PageGetByTextOptions { Exact = true })
            .WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible });
        await reveal.GetByText("I stored it safely", new LocatorGetByTextOptions { Exact = true }).ClickAsync();
        return secret;
    }
}
