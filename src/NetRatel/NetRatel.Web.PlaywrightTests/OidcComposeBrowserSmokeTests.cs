using Microsoft.Playwright;

namespace NetRatel.Web.PlaywrightTests;

[Trait("category", "compose")]
public sealed class OidcComposeBrowserSmokeTests
{
    [Fact]
    public async Task GenericOidcStack_LoadsAssets_Authenticates_And_LogsOut()
    {
        var webUrl = RequireEnvironmentUri("NETRATEL_BROWSER_SMOKE_WEB_URL");
        var username = RequireEnvironmentValue("NETRATEL_BROWSER_SMOKE_USERNAME");

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true
        });
        await using var context = await browser.NewContextAsync();
        var page = await context.NewPageAsync();
        page.SetDefaultTimeout(15_000);

        var bootstrap = await page.GotoAsync(webUrl.ToString(), new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        Assert.NotNull(bootstrap);
        Assert.True(bootstrap.Ok || bootstrap.Status is 302 or 303, $"The Web bootstrap returned HTTP {bootstrap.Status}.");

        var staticAsset = await context.APIRequest.GetAsync(new Uri(webUrl, "_framework/blazor.web.js").ToString());
        Assert.True(staticAsset.Ok, $"The Blazor bootstrap asset returned HTTP {staticAsset.Status}.");

        var login = await page.GotoAsync(new Uri(webUrl, "auth/oidc?returnUrl=%2Ftenants").ToString(), new PageGotoOptions
        {
            WaitUntil = WaitUntilState.DOMContentLoaded
        });
        Assert.NotNull(login);
        await page.Locator("input[name='username']").FillAsync(username);
        var callback = page.WaitForURLAsync($"{webUrl}**", new PageWaitForURLOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.Locator("form").EvaluateAsync("form => form.submit()");
        await callback;

        var authenticatedStatus = await page.EvaluateAsync<int>("async () => (await fetch('/api/v1/tenants')).status");
        Assert.Equal(200, authenticatedStatus);

        await page.GotoAsync(new Uri(webUrl, "auth/logout").ToString(), new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });
        var anonymousStatus = await page.EvaluateAsync<int>("async () => (await fetch('/api/v1/tenants')).status");
        Assert.Equal(401, anonymousStatus);
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
