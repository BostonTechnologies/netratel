using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using NetRatel.Shared.Contracts.Flows;
using NetRatel.Shared.Contracts.Monitoring;
using NetRatel.Web.Services.Flows;
using NetRatel.Web.Services.Monitoring;

namespace NetRatel.Web.PlaywrightTests;

[Collection(PlaywrightCollection.Name)]
public sealed class MonitoringFlowsIntegrationTests(ClientsManagementBrowserFixture browserFixture) : IClassFixture<ClientsManagementBrowserFixture>
{
    [Theory]
    [InlineData(1280, 800, 1d)]
    [InlineData(195, 422, 2d)] // physical390x844 at200% browser zoom equivalent.
    public async Task Actual_Monitoring_Occurrence_Links_To_The_Paired_Run_Receipt_And_Immutable_Version(int width, int height, double dpr)
    {
        var monitoring = new FixtureMonitoringApi();
        var flows = new FixtureFlowApiService(tenantId: 1);
        var occurrence = monitoring.Active.Occurrence!;
        var input = new FlowEventDataDto(monitoring.Active.Series.AgentId, occurrence.PinnedRule.RuleId,
            occurrence.PinnedRule.Name, "SQL Server with a long stable host name", "cpu", "cpu.usage.percent",
            "warning", occurrence.RaisedEvidence.NumericValue, null, occurrence.RaisedAtUtc);
        var persistedRun = flows.SeedRun(input, occurrence.RaisedEventId, occurrence.OccurrenceId);
        var receipt = Assert.Single(persistedRun.Actions).Receipt!;
        monitoring.AttachFlowReceipt(persistedRun.Run.FlowVersionId, persistedRun.Run.Id,
            new MonitoringIncidentReceiptDto(receipt.IncidentId, receipt.TrackingId, receipt.SafeLink));
        await using var host = await ClientsManagementFixtureHost.StartAsync(services =>
        {
            services.AddSingleton(monitoring);
            services.AddSingleton<IMonitoringApiService>(monitoring);
            services.AddSingleton(flows);
            services.AddSingleton<IFlowApiService>(flows);
        });
        await using var context = await browserFixture.Browser.NewContextAsync(new()
        { ViewportSize = new() { Width = width, Height = height }, DeviceScaleFactor = (float)dpr });
        var page = await context.NewPageAsync(); page.SetDefaultTimeout(30_000);
        var errors = new List<string>(); page.PageError += (_, error) => errors.Add(error);
        await page.GotoAsync(host.BaseAddress + "/monitoring", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        var row = page.GetByTestId("monitoring-series-row").Filter(new() { HasText = occurrence.OccurrenceId.ToString() });
        await row.WaitForAsync(new() { Timeout = 90_000 });
        await Assertions.Expect(row.GetByTestId("monitoring-incident-receipt")).ToContainTextAsync($"Incident {receipt.IncidentId}");
        await Assertions.Expect(row.GetByTestId("monitoring-incident-receipt")).ToContainTextAsync(receipt.TrackingId!);
        await Assertions.Expect(row.GetByTestId("monitoring-incident-receipt").GetByRole(AriaRole.Link, new() { Name = "Open incident", Exact = true }))
            .ToHaveAttributeAsync("href", receipt.SafeLink!);
        var nav = page.GetByTestId("app-navigation-drawer");
        var signalLinks = await nav.Locator("a[href='/notifications'],a[href='/monitoring'],a[href='/flows']").AllTextContentsAsync();
        Assert.Equal(new[] { "Notifications", "Monitoring", "Flows" }, signalLinks.Select(text => text.Trim()).ToArray());
        var runLink = row.GetByRole(AriaRole.Link, new() { Name = "Flow run and receipt", Exact = true });
        await Assertions.Expect(runLink).ToHaveAttributeAsync("href", $"/flows?tenantId=1&run={flows.SeededRunId:D}");
        await runLink.ClickAsync();
        await Assertions.Expect(page.GetByTestId("flow-run-result")).ToContainTextAsync("Succeeded");
        await Assertions.Expect(page.GetByTestId("flow-run-result")).ToContainTextAsync(input.RuleName);
        await Assertions.Expect(page.GetByTestId("flow-run-result")).ToContainTextAsync(input.ClientName);
        await Assertions.Expect(page.GetByTestId("flow-run-result")).ToContainTextAsync("96");
        var actionResult = page.GetByTestId("flow-action-result");
        await Assertions.Expect(actionResult.Locator("p").First).ToHaveTextAsync("Status: Succeeded · Attempts: 1");
        await Assertions.Expect(actionResult.GetByTestId("flow-action-code")).ToHaveTextAsync("incident-created");
        await Assertions.Expect(actionResult.GetByTestId("flow-incident-id")).ToHaveTextAsync(receipt.IncidentId);
        await Assertions.Expect(actionResult.GetByTestId("flow-incident-tracking")).ToHaveTextAsync(receipt.TrackingId!);
        await Assertions.Expect(actionResult.Locator("dt")).ToHaveTextAsync(new[] { "Incident ID", "Tracking number" });
        var incidentLink = actionResult.GetByRole(AriaRole.Link, new() { Name = "Open incident", Exact = true });
        await Assertions.Expect(incidentLink).ToHaveAttributeAsync("href", receipt.SafeLink!);
        foreach (var field in new[] { actionResult.Locator("p").First, actionResult.GetByTestId("flow-action-code"), actionResult.GetByTestId("flow-incident-id"), actionResult.GetByTestId("flow-incident-tracking"), incidentLink })
        {
            await Assertions.Expect(field).ToBeVisibleAsync();
            await field.EvaluateAsync("e => e.scrollIntoView({block:'center',inline:'nearest'})");
            Assert.True(await field.EvaluateAsync<bool>("e => {const r=e.getBoundingClientRect();return r.width>0&&r.height>0&&r.left>=-1&&r.right<=innerWidth+1&&r.top>=-1&&r.bottom<=innerHeight+1}"), "Every receipt field must be visible and reachable inside the viewport.");
        }
        Assert.False(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth > innerWidth + 1"));
        Assert.False(await actionResult.EvaluateAsync<bool>("e => e.scrollWidth > e.clientWidth + 1"));
        await page.GetByTestId("flow-run-detail").FocusAsync();
        await page.Keyboard.PressAsync("Tab");
        await Assertions.Expect(incidentLink).ToBeFocusedAsync();
        Assert.True(await incidentLink.EvaluateAsync<bool>("e => e.matches(':focus-visible') && getComputedStyle(e).outlineStyle !== 'none' && parseFloat(getComputedStyle(e).outlineWidth) > 0"));
        Assert.Contains("underline", await incidentLink.EvaluateAsync<string>("e => getComputedStyle(e).textDecorationLine"));
        Assert.True(await incidentLink.EvaluateAsync<bool>("e => {const r=e.getBoundingClientRect();const hit=document.elementFromPoint(r.x+r.width/2,r.y+r.height/2);return hit===e||e.contains(hit)}"));
        Assert.Equal(1, flows.RunLookups); Assert.Equal(1, flows.VersionLookups); Assert.Equal(0, flows.HistoryReads);
        var evidence = Environment.GetEnvironmentVariable("NETRATEL_PLAYWRIGHT_ARTIFACT_ROOT") ?? Path.GetFullPath("TestResults/playwright");
        Directory.CreateDirectory(evidence);
        var variant = $"{width}x{height}-dpr{dpr}";
        await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, $"monitoring-flows-run-receipt-{variant}.png"), FullPage = true });
        await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, $"monitoring-flows-run-receipt-{variant}-viewport.png") });
        await page.GoBackAsync(new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        await row.WaitForAsync();
        var versionLink = row.GetByRole(AriaRole.Link, new() { Name = "Published flow", Exact = true });
        await Assertions.Expect(versionLink).ToHaveAttributeAsync("href", $"/flows?tenantId=1&version={flows.SeededVersionId:D}");
        await versionLink.ClickAsync();
        await Assertions.Expect(page.GetByTestId("flow-dirty")).ToContainTextAsync("Immutable version 1");
        await Assertions.Expect(page.GetByTestId("flow-node-title")).ToHaveCountAsync(3);
        await Assertions.Expect(page.GetByTestId("flow-name")).ToBeDisabledAsync();
        Assert.Equal(0, await page.GetByTestId("flow-save").CountAsync());
        Assert.Equal(0, await page.GetByTestId("flow-publish").CountAsync());
        Assert.Equal(0, flows.HistoryReads); Assert.Equal(0, flows.SaveCalls); Assert.Equal(0, flows.PublishCalls); Assert.Equal(0, monitoring.Writes);
        Assert.Empty(errors); Assert.False(await page.Locator("#blazor-error-ui").IsVisibleAsync());
        await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, $"monitoring-flows-published-version-{variant}.png"), FullPage = true });
        await File.WriteAllTextAsync(Path.Combine(evidence, $"monitoring-flows-integration-{variant}.json"), JsonSerializer.Serialize(new
        { tenantId = 1, cssViewport = new[] { width, height }, dpr, receipt, occurrence.OccurrenceId, occurrence.RaisedEventId, flows.SeededRunId, flows.SeededVersionId,
          input, flows.RunLookups, flows.VersionLookups, flows.HistoryReads, monitoring.Writes, flows.SaveCalls, flows.PublishCalls }));
    }
}
