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
        // Identity and automation evidence are presented in the occurrence's sibling details row.
        var row = page.GetByTestId("monitoring-detail-row").Filter(new() { HasText = occurrence.OccurrenceId.ToString() });
        await OpenOccurrenceDetailsAsync();
        await row.WaitForAsync(new() { Timeout = 90_000 });
        var monitoringReceipt = row.GetByTestId("monitoring-incident-receipt");
        await Assertions.Expect(monitoringReceipt.Locator("dt")).ToHaveTextAsync(new[] { "Incident ID" });
        await Assertions.Expect(monitoringReceipt.Locator("dd code")).ToHaveTextAsync(receipt.IncidentId);
        await Assertions.Expect(monitoringReceipt).ToContainTextAsync(receipt.TrackingId!);
        var monitoringTicket = monitoringReceipt.GetByTestId("monitoring-rateldesk-ticket");
        await Assertions.Expect(monitoringTicket).ToHaveTextAsync("Open RatelDesk ticket");
        await Assertions.Expect(monitoringTicket).ToHaveAttributeAsync("href", receipt.SafeLink!);
        await Assertions.Expect(monitoringTicket).ToHaveAttributeAsync("rel", "noopener noreferrer");
        async Task OpenOccurrenceDetailsAsync()
        {
            var disclosure = page.GetByTestId("monitoring-series-row").First.GetByTestId("monitoring-alert-details");
            if (await disclosure.GetAttributeAsync("aria-expanded") != "true") await disclosure.ClickAsync();
            await Assertions.Expect(disclosure).ToHaveAttributeAsync("aria-expanded", "true");
        }
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
        var actionStatus = actionResult.GetByText("Status: Succeeded · Attempts: 1", new() { Exact = true });
        await Assertions.Expect(actionStatus).ToHaveTextAsync("Status: Succeeded · Attempts: 1");
        await Assertions.Expect(actionResult.GetByTestId("flow-action-code")).ToHaveTextAsync("incident-created");
        await Assertions.Expect(actionResult.GetByTestId("flow-incident-id")).ToHaveTextAsync(receipt.IncidentId);
        await Assertions.Expect(actionResult.GetByTestId("flow-incident-tracking")).ToHaveTextAsync(receipt.TrackingId!);
        await Assertions.Expect(actionResult.Locator("dt")).ToHaveTextAsync(new[] { "Incident ID", "Tracking number" });
        var incidentLink = actionResult.GetByRole(AriaRole.Link, new() { Name = "Open incident", Exact = true });
        await Assertions.Expect(incidentLink).ToHaveAttributeAsync("href", receipt.SafeLink!);
        foreach (var field in new[] { actionStatus, actionResult.GetByTestId("flow-action-code"), actionResult.GetByTestId("flow-incident-id"), actionResult.GetByTestId("flow-incident-tracking"), incidentLink })
        {
            await Assertions.Expect(field).ToBeVisibleAsync();
            await field.EvaluateAsync("e => e.scrollIntoView({block:'center',inline:'nearest'})");
            Assert.True(await field.EvaluateAsync<bool>("e => {const r=e.getBoundingClientRect();return r.width>0&&r.height>0&&r.left>=-1&&r.right<=innerWidth+1&&r.top>=-1&&r.bottom<=innerHeight+1}"), "Every receipt field must be visible and reachable inside the viewport.");
        }
        Assert.False(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth > innerWidth + 1"));
        Assert.False(await actionResult.EvaluateAsync<bool>("e => e.scrollWidth > e.clientWidth + 1"));
        await page.GetByTestId("flow-run-replay").FocusAsync();
        await page.Keyboard.PressAsync("Tab");
        await Assertions.Expect(incidentLink).ToBeFocusedAsync();
        Assert.True(await incidentLink.EvaluateAsync<bool>("e => e.matches(':focus-visible') && getComputedStyle(e).outlineStyle !== 'none' && parseFloat(getComputedStyle(e).outlineWidth) > 0"));
        Assert.Contains("underline", await incidentLink.EvaluateAsync<string>("e => getComputedStyle(e).textDecorationLine"));
        var hitEvidence = await incidentLink.EvaluateAsync<JsonElement>("""
            e => {
                const rect = element => {
                    const r = element.getBoundingClientRect();
                    return { x:r.x, y:r.y, width:r.width, height:r.height, right:r.right, bottom:r.bottom };
                };
                const describe = element => element ? {
                    tag:element.tagName, id:element.id, testId:element.getAttribute("data-testid"),
                    role:element.getAttribute("role"), classes:element.className?.toString(),
                    text:element.textContent?.trim().slice(0,120), bounds:rect(element)
                } : null;
                const r=e.getBoundingClientRect(), x=r.x+r.width/2, y=r.y+r.height/2;
                const hit=document.elementFromPoint(x,y);
                const ancestors=[];
                for(let parent=e.parentElement; parent && ancestors.length<12; parent=parent.parentElement) {
                    const style=getComputedStyle(parent);
                    ancestors.push({ element:describe(parent), overflowX:style.overflowX, overflowY:style.overflowY,
                        position:style.position, zIndex:style.zIndex, scrollTop:parent.scrollTop,
                        clientHeight:parent.clientHeight, scrollHeight:parent.scrollHeight });
                }
                return { reachable:hit===e||e.contains(hit), point:{x,y}, link:describe(e), hit:describe(hit),
                    stack:document.elementsFromPoint(x,y).slice(0,6).map(describe),
                    viewport:{width:innerWidth,height:innerHeight,dpr:devicePixelRatio}, ancestors };
            }
            """);
        var evidence = Environment.GetEnvironmentVariable("NETRATEL_PLAYWRIGHT_ARTIFACT_ROOT") ?? Path.GetFullPath("TestResults/playwright");
        Directory.CreateDirectory(evidence);
        var variant = $"{width}x{height}-dpr{dpr}";
        await File.WriteAllTextAsync(Path.Combine(evidence, $"monitoring-flows-hit-target-{variant}.json"), hitEvidence.GetRawText());
        await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, $"monitoring-flows-hit-target-{variant}.png"), Animations = ScreenshotAnimations.Disabled });
        Assert.True(hitEvidence.GetProperty("reachable").GetBoolean(), "The incident link center must receive pointer input. " + hitEvidence.GetRawText());
        Assert.Equal(1, flows.RunLookups); Assert.Equal(1, flows.VersionLookups); Assert.Equal(0, flows.HistoryReads);
        await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, $"monitoring-flows-run-receipt-{variant}.png"), FullPage = true });
        await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, $"monitoring-flows-run-receipt-{variant}-viewport.png") });
        await page.GoBackAsync(new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        await OpenOccurrenceDetailsAsync();
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
