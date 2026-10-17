using System.Text.Json;
using Microsoft.Playwright;
using NetRatel.Shared.Contracts.Jobs;

namespace NetRatel.Web.PlaywrightTests;

/// <summary>Optional Chromium native saved-password acceptance; third-party manager extensions are outside this check.</summary>
[Collection(PlaywrightCollection.Name)]
[Trait("Category", "BrowserAcceptance")]
public sealed class JobsAutofillAcceptanceTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Saved_account_password_autofills_login_but_not_job_inputs_or_parameter_editor()
    {
        const string accountName = "saved-account@example.invalid";
        const string accountPassword = "Disposable-account-password-8734!";
        var accepted = new TaskCompletionSource<RunJobRequest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runCount = 0;
        await using var fixture = await ClientsManagementFixtureHost.StartAsync(services =>
        {
            NewTaskWorkspaceFixture.AddServices(services);
            new ScriptLibraryWorkspaceFixture().Register(services);
            JobsWorkspaceFixture.Configure(services, parameters: [
                new(1, 42, "additional_text", "string", false, null, null, null),
                new(2, 42, "dummy_secret", "secret", false, null, null, null),
                new(3, 42, "following_text", "string", false, "intended default", null, null),
                new(4, 42, "Count", "int", true, "0", null, null),
                new(5, 42, "Enabled", "bool", true, "false", null, null)],
                runAccepted: request => { Interlocked.Increment(ref runCount); accepted.TrySetResult(request); });
        });
        using var playwright = await Playwright.CreateAsync();
        var profile = Path.Combine(Path.GetTempPath(), "netratel-autofill-" + Guid.NewGuid().ToString("N"));
        var options = new BrowserTypeLaunchPersistentContextOptions
        {
            Headless = true,
            ExecutablePath = Environment.GetEnvironmentVariable("NETRATEL_PLAYWRIGHT_CHROMIUM_PATH") ?? playwright.Chromium.ExecutablePath,
            IgnoreDefaultArgs = ["--enable-automation"],
            Args = ["--password-store=basic"],
            ViewportSize = new() { Width = 1366, Height = 900 }
        };
        try
        {
            // Seed Chromium's real password store through its own privileged password-manager API.
            // No script or Playwright Fill call writes values into the login or operational inputs.
            await using (var seed = await playwright.Chromium.LaunchPersistentContextAsync(profile, options))
            {
                var manager = await seed.NewPageAsync();
                await manager.GotoAsync("chrome://password-manager/passwords");
                var error = await manager.EvaluateAsync<string?>("""
                    data => new Promise(resolve => {
                      let added = false, observed = false, completed = false;
                      const finish = error => {
                        if (completed) return;
                        completed = true;
                        clearTimeout(timeout);
                        chrome.passwordsPrivate.onSavedPasswordsListChanged.removeListener(accept);
                        resolve(error);
                      };
                      const accept = entries => {
                        observed = entries.length === 1 && entries[0].username === data.username;
                        if (added && observed) finish(null);
                      };
                      const timeout = setTimeout(() => finish('Native saved-password list did not become ready within 15 seconds.'), 15_000);
                      // The add callback precedes asynchronous password-list/model updates on some hosts.
                      chrome.passwordsPrivate.onSavedPasswordsListChanged.addListener(accept);
                      chrome.passwordsPrivate.addPassword({
                        url: data.url, username: data.username, password: data.password,
                        useAccountStore: false, note: 'Disposable NetRatel browser acceptance'
                      }, () => {
                        const error = chrome.runtime.lastError?.message;
                        if (error) return finish(error);
                        added = true;
                        if (observed) finish(null);
                        else chrome.passwordsPrivate.getSavedPasswordList(accept);
                      });
                    })
                    """, new { url = fixture.BaseAddress, username = accountName, password = accountPassword });
                Assert.True(error is null, "Native password store must be available; use an isolated test browser with password saving enabled. " + error);
                Assert.Equal(1, await manager.EvaluateAsync<int>("""
                    () => new Promise(resolve => chrome.passwordsPrivate.getSavedPasswordList(entries => resolve(entries.length)))
                    """));
            }

            // A second browser process proves the credential survives in the persistent profile.
            await using var context = await playwright.Chromium.LaunchPersistentContextAsync(profile, options);
            var page = await context.NewPageAsync();
            page.SetDefaultTimeout(20_000);
            await page.RouteAsync("**/native-autofill-login", route => route.FulfillAsync(new()
            {
                ContentType = "text/html",
                Body = """
                    <!doctype html><title>Disposable native autofill positive control</title>
                    <form action="/native-autofill-login" method="post">
                    <label>Account<input name="username" autocomplete="username"></label>
                    <label>Password<input name="password" type="password" autocomplete="current-password"></label>
                    <button>Sign in</button></form>
                    """
            }));
            await page.GotoAsync(fixture.BaseAddress + "/native-autofill-login");
            await Assertions.Expect(page.GetByLabel("Account", new() { Exact = true })).ToHaveValueAsync(accountName);
            await Assertions.Expect(page.GetByLabel("Password", new() { Exact = true })).ToHaveValueAsync(accountPassword);
            Assert.Equal(2, await page.Locator("input:autofill").CountAsync());
            var browserVersion = await (await context.NewCDPSessionAsync(page)).SendAsync("Browser.getVersion");
            output.WriteLine($"Native positive control: saved username and password autofilled by {browserVersion!.Value.GetProperty("product").GetString()}; persistent profile reopened in a fresh process.");

            await page.GotoAsync(fixture.BaseAddress + "/jobs", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
            await page.GetByTestId("app-main-content").WaitForAsync(new() { Timeout = 90_000 });
            var run = page.GetByRole(AriaRole.Button, new() { Name = "Run Nightly backup", Exact = true });
            for (var opening = 0; opening < 2; opening++)
            {
                await run.ClickAsync();
                var form = page.Locator(".run-job-dialog");
                await form.GetByLabel("dummy_secret", new() { Exact = true }).WaitForAsync();
                await ObserveNativeAutofillAsync(page, form);
                await Assertions.Expect(form.GetByLabel("additional_text", new() { Exact = true })).ToHaveValueAsync("");
                await Assertions.Expect(form.GetByLabel("dummy_secret", new() { Exact = true })).ToHaveValueAsync("");
                await Assertions.Expect(form.GetByLabel("following_text", new() { Exact = true })).ToHaveValueAsync("intended default");
                await Assertions.Expect(form.GetByLabel("Count", new() { Exact = true })).ToHaveValueAsync("0");
                await Assertions.Expect(form.Locator("input[type=checkbox]")).Not.ToBeCheckedAsync();
                Assert.Equal(0, await form.Locator("input:autofill").CountAsync());
                Assert.Equal("FORM", await form.EvaluateAsync<string>("element => element.tagName"));
                await Assertions.Expect(form).ToHaveAttributeAsync("autocomplete", "off");
                await AssertOperationalInputsAsync(form);
                if (opening == 0)
                    await page.Locator(".mud-dialog").Last.GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true }).ClickAsync();
            }

            await page.GetByLabel("additional_text", new() { Exact = true }).PressSequentiallyAsync("operator text");
            await page.GetByLabel("dummy_secret", new() { Exact = true }).PressSequentiallyAsync("operator secret");
            await page.GetByRole(AriaRole.Button, new() { Name = "Start Run", Exact = true }).ClickAsync();
            var request = await accepted.Task.WaitAsync(TimeSpan.FromSeconds(20));
            using var payload = JsonDocument.Parse(request.InputsJson!);
            Assert.Equal(5, payload.RootElement.EnumerateObject().Count());
            Assert.Equal("operator text", payload.RootElement.GetProperty("additional_text").GetString());
            Assert.Equal("operator secret", payload.RootElement.GetProperty("dummy_secret").GetString());
            Assert.Equal("intended default", payload.RootElement.GetProperty("following_text").GetString());
            Assert.Equal("0", payload.RootElement.GetProperty("Count").GetString());
            Assert.False(payload.RootElement.GetProperty("Enabled").GetBoolean());
            Assert.Equal("ui", request.StartedBy);
            Assert.Equal(1, runCount);
            await page.WaitForURLAsync("**/jobs/42/runs/9");

            await page.GotoAsync(fixture.BaseAddress + "/jobs");
            await page.GetByRole(AriaRole.Button, new() { Name = "Edit Nightly backup", Exact = true }).ClickAsync();
            var parent = page.Locator(".jobs-edit-dialog");
            await parent.GetByRole(AriaRole.Tab, new() { Name = "Parameters", Exact = false }).ClickAsync();
            await parent.GetByRole(AriaRole.Button, new() { Name = "Edit parameter dummy_secret", Exact = true }).ClickAsync();
            var editor = page.Locator(".param-editor");
            await editor.GetByLabel("Default value", new() { Exact = true }).WaitForAsync();
            await ObserveNativeAutofillAsync(page, editor);
            await Assertions.Expect(editor.GetByLabel("Name", new() { Exact = true })).ToHaveValueAsync("dummy_secret");
            await Assertions.Expect(editor.GetByLabel("Default value", new() { Exact = true })).ToHaveValueAsync("");
            Assert.Equal(0, await editor.Locator("input:autofill").CountAsync());
            await Assertions.Expect(editor).ToHaveAttributeAsync("autocomplete", "off");
            await AssertOperationalInputsAsync(editor);
            output.WriteLine("Operational acceptance: ordinary inputs before/after a masked secret stayed free of saved account data across reopening; exact original-key request contained only defaults/operator values; secret parameter editor stayed free of native autofill.");
        }
        finally
        {
            if (Directory.Exists(profile)) Directory.Delete(profile, recursive: true);
        }
    }

    private static async Task ObserveNativeAutofillAsync(IPage page, ILocator form)
    {
        // Native password filling is asynchronous and not an application network request. Allow
        // the same bounded observation window after opening, plus actual user focus/keyboard input.
        await page.WaitForTimeoutAsync(1_500);
        await form.Locator("input[type=password]").ClickAsync();
        await page.Keyboard.PressAsync("Tab");
        await page.WaitForTimeoutAsync(500);
    }

    private static async Task AssertOperationalInputsAsync(ILocator form)
    {
        foreach (var input in await form.Locator("input:not([type=hidden]):not([type=checkbox]), textarea").AllAsync())
        {
            var masked = await input.GetAttributeAsync("type") == "password";
            await Assertions.Expect(input).ToHaveAttributeAsync("autocomplete", masked ? "new-password" : "off");
            var name = await input.GetAttributeAsync("name");
            Assert.StartsWith("job-operation-", name);
            Assert.Equal(name, await input.GetAttributeAsync("id"));
        }
    }
}
