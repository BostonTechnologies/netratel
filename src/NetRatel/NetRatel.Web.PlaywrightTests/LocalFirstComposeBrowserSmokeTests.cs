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
        await page.GetByTestId("local-login-submit").ClickAsync();
        await page.WaitForURLAsync(webUrl.ToString(), new PageWaitForURLOptions { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30_000 });
        Assert.Equal(200, await page.EvaluateAsync<int>("async () => (await fetch('/api/v1/tenants')).status"));

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
}
