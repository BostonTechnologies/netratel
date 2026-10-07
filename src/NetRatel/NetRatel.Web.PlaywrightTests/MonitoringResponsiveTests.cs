using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using NetRatel.Shared.Contracts.Monitoring;
using NetRatel.Shared.Contracts.Services;
using NetRatel.Web.Services.Monitoring;

namespace NetRatel.Web.PlaywrightTests;

[Collection(PlaywrightCollection.Name)]
public sealed class MonitoringResponsiveTests(ClientsManagementBrowserFixture browserFixture) : IClassFixture<ClientsManagementBrowserFixture>, IAsyncLifetime
{
    private ClientsManagementFixtureHost? _host;
    private readonly FixtureMonitoringApi _api = new();
    [Theory]
    [InlineData(1280, 800, "light", 1d)]
    [InlineData(1280, 800, "dark", 1d)]
    [InlineData(390, 844, "system", 1d)]
    [InlineData(390, 844, "light", 1d)]
    [InlineData(195, 422, "light", 2d)] // 200% zoom equivalent, physical390×844; every CSS unit reflows.
    [InlineData(640, 400, "dark", 2d)] // 200% zoom equivalent, physical1280×800.
    public async Task MonitoringAndEditorsRemainReachableAndPreserveErrors(int width, int height, string theme, double scale)
    {
        await using var context = await browserFixture.Browser.NewContextAsync(new() { ViewportSize = new() { Width = width, Height = height }, DeviceScaleFactor = (float)scale });
        var page = await context.NewPageAsync(); page.SetDefaultTimeout(30_000);
        await page.GotoAsync(_host!.BaseAddress + "/monitoring"); await page.GetByTestId("monitoring-interactive").WaitForAsync(new() { State = WaitForSelectorState.Attached });
        await page.GetByTestId("monitoring-series-row").First.WaitForAsync();
        await SetTheme(page, theme);
        if (width >= 1280 && await page.GetByTestId("app-navigation-drawer").EvaluateAsync<bool>("e => e.classList.contains('mud-drawer--closed')")) await page.GetByTestId("navigation-toggle").ClickAsync();
        if (width >= 1280) await Assertions.Expect(page.GetByTestId("app-navigation-drawer")).ToBeVisibleAsync();
        Assert.Equal(0, _api.Writes);
        Assert.False(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth > innerWidth + 1"));
        Assert.Equal("16px", await page.EvaluateAsync<string>("() => getComputedStyle(document.documentElement).fontSize"));
        await Assertions.Expect(page.GetByTestId("monitoring-series-row").Last).ToHaveAttributeAsync("data-health", "Unknown");

        var serviceButton = page.GetByTestId("monitoring-services").First.Locator("button");
        await page.EvaluateAsync("""
            () => {
                const launcher=document.querySelector('[data-testid="monitoring-services"] button');
                window.__monitoringServicesFocus={launcher, events:[],nativeRestoreDepth:0,opened:false,manualRestoresBeforeRemoval:[]};
                const describe=e=>({tag:e?.tagName,id:e?.id,testId:e?.dataset?.testid,label:e?.getAttribute?.('aria-label'),text:e?.textContent?.trim().slice(0,60),connected:e?.isConnected});
                for(const name of ['saveFocus','restoreFocus','focus']) {
                    const original=window.mudElementRef[name];
                    window.mudElementRef[name]=function(element,...args) {
                        window.__monitoringServicesFocus.events.push({kind:'mud.'+name,at:performance.now(),target:describe(element),saved:describe(element?.mudblazor_savedFocus),active:describe(document.activeElement),dialogs:document.querySelectorAll('.mud-dialog').length});
                        if(name==='restoreFocus') window.__monitoringServicesFocus.nativeRestoreDepth++;
                        try { return original.call(this,element,...args); }
                        finally { if(name==='restoreFocus') window.__monitoringServicesFocus.nativeRestoreDepth--; }
                    };
                }
                const originalFocus=HTMLElement.prototype.focus;
                HTMLElement.prototype.focus=function(...args) {
                    const trace=window.__monitoringServicesFocus;
                    if(this===trace.launcher&&trace.opened&&trace.nativeRestoreDepth===0&&document.getElementById(trace.dialogId))
                        trace.manualRestoresBeforeRemoval.push({at:performance.now(),dialogId:trace.dialogId});
                    return originalFocus.apply(this,args);
                };
                for(const kind of ['focusin','focusout']) document.addEventListener(kind,e=>{
                    const trace=window.__monitoringServicesFocus;
                    trace.events.push({kind,at:performance.now(),target:describe(e.target),related:describe(e.relatedTarget),launcherConnected:trace.launcher.isConnected});
                },true);
            }
            """);
        await serviceButton.FocusAsync(); await Assertions.Expect(serviceButton).ToBeFocusedAsync();
        await page.Keyboard.PressAsync("Enter");
        await page.GetByTestId("client-services-dialog").WaitForAsync(); await page.GetByTestId("service-row").First.WaitForAsync();
        await page.GetByTestId("client-services-dialog").EvaluateAsync("e => {const trace=window.__monitoringServicesFocus;trace.dialogId=e.closest('.mud-dialog').id;trace.opened=true}");
        await page.WaitForFunctionAsync("""
            () => { const e = document.querySelector('[data-testid="client-services-dialog"]');
                if (!e) return false; const dialog=e.closest('.mud-dialog')??e, r=e.getBoundingClientRect();
                if(dialog.getAnimations({subtree:true}).some(a=>a.playState==='running') ||
                    Math.abs(r.x)>1 || Math.abs(r.y)>1 || r.width<innerWidth*.99 || r.height<innerHeight*.99) {
                    e.__netratelViewportFrames=0; return false;
                }
                return (e.__netratelViewportFrames=(e.__netratelViewportFrames??0)+1)>=8; }
            """);
        var dialogWidth = await page.GetByTestId("client-services-dialog").EvaluateAsync<double>("e => e.getBoundingClientRect().width");
        Assert.InRange(dialogWidth, width - 2, width + 2);
        var evidenceRoot = EvidenceRoot(); Directory.CreateDirectory(evidenceRoot);
        var servicesGeometry = await page.GetByTestId("client-services-dialog").EvaluateAsync<string>("e => JSON.stringify({viewport:[innerWidth,innerHeight],rect:e.getBoundingClientRect().toJSON(),position:getComputedStyle(e).position,transform:getComputedStyle(e).transform},null,2)");
        await File.WriteAllTextAsync(Path.Combine(evidenceRoot, $"monitoring-services-{width}-{height}-{theme}-dpr{scale}.json"), servicesGeometry);
        await page.ScreenshotAsync(new() { Path = Path.Combine(evidenceRoot, $"monitoring-services-{width}-{height}-{theme}-dpr{scale}.png"), Animations = ScreenshotAnimations.Disabled });
        try
        {
            await page.GetByTestId("close-services").ClickAsync();
            await page.GetByTestId("client-services-dialog").WaitForAsync(new() { State = WaitForSelectorState.Detached });
            await Assertions.Expect(serviceButton).ToBeFocusedAsync();
            Assert.Equal(0, await page.EvaluateAsync<int>("() => window.__monitoringServicesFocus.manualRestoresBeforeRemoval.length"));
        }
        finally
        {
            var trace = await page.EvaluateAsync<string>("""
                () => { const trace=window.__monitoringServicesFocus,e=document.activeElement;
                    return JSON.stringify({active:{tag:e?.tagName,id:e?.id,testId:e?.dataset?.testid,label:e?.getAttribute?.('aria-label')},
                      launcherConnected:trace.launcher.isConnected,launcherIsOriginal:trace.launcher===document.querySelector('[data-testid="monitoring-services"] button'),
                      dialogCount:document.querySelectorAll('.mud-dialog').length,manualRestoresBeforeRemoval:trace.manualRestoresBeforeRemoval,events:trace.events},null,2); }
                """);
            await File.WriteAllTextAsync(Path.Combine(evidenceRoot, $"monitoring-services-focus-{width}-{height}-{theme}-dpr{scale}.json"), trace);
        }

        await page.GetByTestId("monitoring-tab-manage").ClickAsync();
        await page.GetByTestId("monitoring-new-rule").FocusAsync(); await page.Keyboard.PressAsync("Enter");
        var editor = page.GetByTestId("monitoring-editor"); await editor.WaitForAsync();
        await page.GetByTestId("monitoring-rule-name").FillAsync("SQL CPU draft");
        await page.GetByTestId("monitoring-client-choice").First.CheckAsync();
        await page.GetByTestId("monitoring-reason").FillAsync("explicit operator choice");
        await page.GetByTestId("monitoring-preview").ClickAsync();
        await Assertions.Expect(page.GetByTestId("monitoring-preview-result")).ToContainTextAsync("eligible");
        await page.GetByTestId("monitoring-save").ClickAsync();
        await Assertions.Expect(page.GetByTestId("monitoring-editor-error")).ToContainTextAsync("conflict");
        await Assertions.Expect(page.GetByTestId("monitoring-rule-name")).ToHaveValueAsync("SQL CPU draft");
        await Assertions.Expect(page.GetByTestId("monitoring-dirty")).ToContainTextAsync("Unsaved");
        Assert.Equal(1, _api.Writes);
        Assert.Equal(1, await page.GetByTestId("monitoring-save").CountAsync());
        Assert.Equal(1, await page.GetByTestId("monitoring-cancel").CountAsync());
        foreach (var control in await editor.Locator("input,select,textarea,button").AllAsync())
        {
            await control.EvaluateAsync("e => e.scrollIntoView({block:'center',inline:'nearest'})");
            var reachable = await control.EvaluateAsync<bool>("e => { const r=e.getBoundingClientRect(); return r.left>=-1 && r.right<=innerWidth+1 && r.top>=-1 && r.bottom<=innerHeight+1; }");
            Assert.True(reachable, "Every editor control must be reachable within the viewport.");
        }
        Assert.False(await editor.EvaluateAsync<bool>("e => e.scrollWidth > e.clientWidth + 1"));
        var evidence = EvidenceRoot(); Directory.CreateDirectory(evidence);
        await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, $"monitoring-{width}-{height}-{theme}-dpr{scale}.png"), Animations = ScreenshotAnimations.Disabled });
        await page.GetByTestId("monitoring-cancel").ClickAsync(); await editor.WaitForAsync(new() { State = WaitForSelectorState.Hidden });
        await Assertions.Expect(page.GetByTestId("monitoring-new-rule")).ToBeFocusedAsync();
        Assert.Equal(1, _api.Writes);
        Assert.False(await page.Locator("#blazor-error-ui").IsVisibleAsync());
        if (theme == "system") { await page.EmulateMediaAsync(new() { ColorScheme = ColorScheme.Light }); await page.Locator("html[data-netratel-theme='light']").WaitForAsync(); }
    }
    [Fact]
    public async Task ClearAndBypassHaveDistinctSemanticsAndHistory()
    {
        var page = await browserFixture.Browser.NewPageAsync();
        try
        {
            await page.GotoAsync(_host!.BaseAddress + "/monitoring"); await page.GetByTestId("monitoring-clear").First.WaitForAsync();
            var oldOccurrence = _api.Active.Occurrence!.OccurrenceId;
            await page.GetByTestId("monitoring-clear").First.ClickAsync();
            await Assertions.Expect(page.GetByTestId("monitoring-editor")).ToContainTextAsync("new full breach window");
            await page.GetByTestId("monitoring-save").ClickAsync();
            await Assertions.Expect(page.GetByTestId("monitoring-editor-error")).ToContainTextAsync("reason");
            Assert.Equal(0, _api.Writes);
            await page.GetByTestId("monitoring-reason").FillAsync("validated manually"); await page.GetByTestId("monitoring-save").ClickAsync();
            await page.GetByTestId("monitoring-editor").WaitForAsync(new() { State = WaitForSelectorState.Hidden });
            Assert.Equal(MonitoringPhase.Cleared, _api.Active.Phase);
            _api.Refire(); await page.GetByTestId("monitoring-refresh").ClickAsync(); await page.GetByTestId("monitoring-bypass").First.WaitForAsync();
            Assert.NotEqual(oldOccurrence, _api.Active.Occurrence!.OccurrenceId);
            await page.GetByTestId("monitoring-bypass").First.ClickAsync();
            await Assertions.Expect(page.GetByTestId("monitoring-editor")).ToContainTextAsync("evaluation continues");
            await page.GetByTestId("monitoring-reason").FillAsync("SQL maintenance"); await page.GetByTestId("monitoring-save").ClickAsync();
            await page.GetByTestId("monitoring-editor").WaitForAsync(new() { State = WaitForSelectorState.Hidden });
            await Assertions.Expect(page.GetByTestId("monitoring-series-row").First).ToContainTextAsync("Suppressed");
            await Assertions.Expect(page.GetByTestId("monitoring-series-row").First).ToHaveAttributeAsync("data-health", "Breach");
            await page.GetByTestId("monitoring-tab-history").ClickAsync();
            await Assertions.Expect(page.GetByTestId("monitoring-history-row").First).ToContainTextAsync("ManuallyCleared");
        }
        finally { await page.CloseAsync(); }
    }
    [Fact]
    public async Task DelayedOldTenantReadIsCancelledAndCannotReplaceNewTenant()
    {
        var delayed = new TaskCompletionSource<MonitoringConfigurationDto>();
        _api.DelayedTenantOne = delayed;
        var page = await browserFixture.Browser.NewPageAsync();
        try
        {
            await page.GotoAsync(_host!.BaseAddress + "/monitoring");
            await page.GetByTestId("monitoring-tenant").WaitForAsync();
            await page.GetByTestId("monitoring-tenant").SelectOptionAsync("2");
            await page.GetByTestId("monitoring-empty").WaitForAsync();
            Assert.True(_api.DelayedReadToken.IsCancellationRequested);
            delayed.SetResult(_api.Configuration(1));
            await Assertions.Expect(page.GetByTestId("monitoring-tenant")).ToHaveValueAsync("2");
            await Assertions.Expect(page.GetByTestId("monitoring-series-row")).ToHaveCountAsync(0);
            Assert.Equal(0, _api.Writes);
        }
        finally { await page.CloseAsync(); }
    }
    [Fact]
    public async Task OutstandingSavePreventsDuplicateBrowserSubmission()
    {
        var delayed = new TaskCompletionSource<MonitoringConfigurationDto>(); _api.DelayedRuleSave = delayed;
        var page = await browserFixture.Browser.NewPageAsync();
        try
        {
            await page.GotoAsync(_host!.BaseAddress + "/monitoring"); await page.GetByTestId("monitoring-tab-manage").WaitForAsync();
            await page.GetByTestId("monitoring-tab-manage").ClickAsync(); await page.GetByTestId("monitoring-new-rule").ClickAsync();
            await page.GetByTestId("monitoring-rule-name").FillAsync("SQL CPU"); await page.GetByTestId("monitoring-client-choice").First.CheckAsync();
            await page.GetByTestId("monitoring-reason").FillAsync("explicit change"); await page.GetByTestId("monitoring-preview").ClickAsync();
            await page.GetByTestId("monitoring-preview-result").WaitForAsync(); await page.GetByTestId("monitoring-save").ClickAsync();
            await Assertions.Expect(page.GetByTestId("monitoring-save")).ToBeDisabledAsync();
            await page.GetByTestId("monitoring-save").EvaluateAsync("e => e.click()");
            Assert.Equal(1, _api.Writes);
            delayed.SetException(new HttpRequestException("Configuration conflict; draft retained"));
            await Assertions.Expect(page.GetByTestId("monitoring-editor-error")).ToContainTextAsync("conflict");
            await Assertions.Expect(page.GetByTestId("monitoring-rule-name")).ToHaveValueAsync("SQL CPU");
        }
        finally { delayed.TrySetResult(_api.Configuration(1) with { Revision = 2 }); await page.CloseAsync(); }
    }
    private static async Task SetTheme(IPage page, string theme)
    {
        var mobile = await page.GetByTestId("mobile-overflow").IsVisibleAsync();
        var menu = page.GetByTestId(mobile ? "mobile-overflow" : "theme-preference-menu");
        await menu.ClickAsync();
        var option = page.GetByTestId($"{(mobile ? "mobile-theme-option" : "theme-option")}-{theme}");
        await option.ClickAsync();
        // The theme handler runs before native menu dismissal restores its activator.
        // Complete that keyboard handoff before focusing the next control.
        await Assertions.Expect(option).ToBeHiddenAsync();
        await Assertions.Expect(menu.Locator("button").First).ToBeFocusedAsync();
        if (theme == "system") await page.EmulateMediaAsync(new() { ColorScheme = ColorScheme.Dark });
        await page.Locator($"html[data-netratel-theme='{(theme == "system" ? "dark" : theme)}']").WaitForAsync();
    }
    private static string EvidenceRoot() => Environment.GetEnvironmentVariable("NETRATEL_PLAYWRIGHT_ARTIFACT_ROOT") ?? Path.GetFullPath("TestResults/playwright");
    public async ValueTask InitializeAsync() => _host = await ClientsManagementFixtureHost.StartAsync(services =>
    { services.AddSingleton(_api); services.AddSingleton<IMonitoringApiService>(_api); });
    public async ValueTask DisposeAsync() { if (_host is not null) await _host.DisposeAsync(); }
}

internal sealed class FixtureMonitoringApi : IMonitoringApiService
{
    private static readonly Guid Agent = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private readonly MonitoringRuleDto _cpu;
    private readonly MonitoringRuleDto _disk;
    private readonly MonitoringRuleDto _service;
    private readonly MonitoringGroupDto _group;
    private ImmutableArray<MonitoringBypassDto> _bypasses = [];
    private ImmutableArray<MonitoringEventIntent> _history = [];
    public MonitoringSeriesState Active { get; private set; }
    public int Writes { get; private set; }
    private ulong _revision = 1;
    public TaskCompletionSource<MonitoringConfigurationDto>? DelayedTenantOne { get; set; }
    public TaskCompletionSource<MonitoringConfigurationDto>? DelayedRuleSave { get; set; }
    public CancellationToken DelayedReadToken { get; private set; }
    public FixtureMonitoringApi()
    {
        _group = new(1, Guid.NewGuid(), 1, "SQL servers", [Agent]);
        _cpu = Rule("SQL CPU", new(MonitoringMetricKind.CpuUsagePercent, MonitoringNumericUnit.Percent, 90, 80, null, null, []));
        _disk = Rule("SQL disk", new(MonitoringMetricKind.DiskFreeSpace, MonitoringNumericUnit.GiB, 5, 8, "/", null, []));
        _service = Rule("SQL selected service", new(MonitoringMetricKind.ServiceExpectedState, null, null, null, "MSSQLSERVER", ClientServicePlatform.Windows, [ClientServiceState.Running]));
        Active = NewOccurrence();
    }
    private MonitoringRuleDto Rule(string name, MonitoringConditionDto condition) => new(1, Guid.NewGuid(), 1, 1, name, true, MonitoringSeverity.Warning,
        new(MonitoringTargetMode.Selected, [], [_group.GroupId]), condition, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2));
    private MonitoringSeriesState NewOccurrence()
    {
        var now = DateTimeOffset.UtcNow;
        var evidence = new MonitoringEvidenceDto(new(1, 100), Guid.NewGuid(), now, now, MonitoringEvidenceQuality.Fresh, MonitoringClassification.Breach, 96, 0.1, null);
        return new(new(1, _cpu.RuleId, Agent, "cpu"), 1, 1, MonitoringPhase.Firing, MonitoringEvidenceQuality.Fresh, evidence.Cursor, evidence,
            Occurrence: new(Guid.NewGuid(), Guid.NewGuid(), now, now.AddSeconds(-60), _cpu, evidence, MonitoringFlowDispatchDisposition.NoFlowSelected), ApplicableBypassIds: []);
    }
    public void Refire() => Active = NewOccurrence();
    public void AttachFlowReceipt(Guid versionId, Guid runId, MonitoringIncidentReceiptDto receipt)
    {
        var occurrence = Active.Occurrence!;
        Active = Active with { Occurrence = occurrence with
        {
            PinnedRule = occurrence.PinnedRule with { PublishedFlowVersionId = versionId },
            FlowDispatchDisposition = MonitoringFlowDispatchDisposition.Completed,
            FlowOutcome = new(runId, MonitoringFlowOutcomeKind.Succeeded, DateTimeOffset.UtcNow, "incident-created", receipt)
        } };
    }
    public Task<IReadOnlyList<MonitoringTenantDto>> GetTenantsAsync(CancellationToken token = default) => Task.FromResult<IReadOnlyList<MonitoringTenantDto>>([new(1, "SQL tenant"), new(2, "Other tenant")]);
    public Task<MonitoringPermissionsDto> GetPermissionsAsync(int tenantId, CancellationToken token = default) => Task.FromResult(new MonitoringPermissionsDto(tenantId, true, true, true, true, true, true));
    public MonitoringConfigurationDto Configuration(int tenantId) => new(tenantId, _revision,
        tenantId == 1 ? [_cpu, _disk, _service] : [], tenantId == 1 ? [_group] : [], tenantId == 1 ? _bypasses : [], DateTimeOffset.UtcNow);
    public Task<MonitoringConfigurationDto> GetConfigurationAsync(int tenantId, CancellationToken token = default)
    {
        if (tenantId == 1 && DelayedTenantOne is not null) { DelayedReadToken = token; return DelayedTenantOne.Task; }
        return Task.FromResult(Configuration(tenantId));
    }
    public Task<MonitoringSummaryDto> GetSummaryAsync(int tenantId, CancellationToken token = default) => Task.FromResult(new MonitoringSummaryDto(tenantId, 2, Active.Occurrence?.EndedAtUtc is null ? 1 : 0, 0, 1, 0, Active.Suppressed ? 1 : 0, DateTimeOffset.UtcNow));
    public Task<MonitoringSeriesPageDto> GetSeriesAsync(int tenantId, string? cursor = null, CancellationToken token = default) => Task.FromResult(new MonitoringSeriesPageDto(tenantId == 1 ? [Active,
        new(new(1, _disk.RuleId, Agent, "disk:/"), 1, 1, MonitoringPhase.Healthy, MonitoringEvidenceQuality.Unknown)] : [], null));
    public Task<MonitoringEventPageDto> GetEventsAsync(int tenantId, string? cursor = null, CancellationToken token = default) => Task.FromResult(new MonitoringEventPageDto(tenantId == 1 ? _history : [], null));
    public Task<MonitoringClientPageDto> GetClientsAsync(int tenantId, string? cursor = null, CancellationToken token = default) => Task.FromResult(new MonitoringClientPageDto([new(Agent, "SQL Server with a long stable host name", ClientServicePlatform.Windows, MonitoringTargetSupport.Supported, "fixture")], null, 1));
    public Task<IReadOnlyList<MonitoringPublishedFlowDto>> GetPublishedFlowsAsync(int tenantId, CancellationToken token = default) => Task.FromResult<IReadOnlyList<MonitoringPublishedFlowDto>>([]);
    public Task<MonitoringTargetPreviewDto> PreviewTargetsAsync(int tenantId, MonitoringTargetPreviewRequest request, CancellationToken token = default) => Task.FromResult(new MonitoringTargetPreviewDto([Agent], _revision, [new(Agent, "SQL Server", MonitoringTargetSupport.Unknown, "cached_evidence_only")], 1));
    public Task<MonitoringConfigurationDto> SaveRuleAsync(int tenantId, MonitoringRuleWriteDto request, CancellationToken token = default) { Writes++; if (DelayedRuleSave is not null) return DelayedRuleSave.Task; throw new HttpRequestException("Configuration conflict; edits retained."); }
    public Task<MonitoringConfigurationDto> SaveGroupAsync(int tenantId, MonitoringGroupWriteDto request, CancellationToken token = default) { Writes++; _revision++; return Task.FromResult(Configuration(tenantId)); }
    public Task<MonitoringConfigurationDto> SaveBypassAsync(int tenantId, Guid bypassId, MonitoringBypassWriteDto request, CancellationToken token = default)
    {
        Writes++; _revision++; var bypass = new MonitoringBypassDto(bypassId, tenantId, request.RuleId, request.AgentId, request.ResourceKey, Guid.NewGuid(), request.Reason, DateTimeOffset.UtcNow, request.ExpiresAtUtc, request.GroupId);
        _bypasses = _bypasses.Add(bypass); Active = Active with { Suppressed = true, ApplicableBypassIds = [bypassId] }; return Task.FromResult(Configuration(tenantId));
    }
    public Task<MonitoringConfigurationDto> DeleteAsync(int tenantId, string collection, Guid entityId, MonitoringDeleteDto request, CancellationToken token = default) { Writes++; _revision++; return Task.FromResult(Configuration(tenantId)); }
    public Task AcknowledgeAsync(int tenantId, MonitoringSeriesState series, string reason, CancellationToken token = default) { Writes++; return Task.CompletedTask; }
    public Task ClearAsync(int tenantId, MonitoringSeriesState series, string reason, CancellationToken token = default)
    {
        Writes++; var now = DateTimeOffset.UtcNow; var occurrence = Active.Occurrence! with { EndedAtUtc = now, ClosureDisposition = MonitoringClosureDisposition.ManuallyCleared };
        _history = _history.Add(new(Guid.NewGuid(), MonitoringEventKind.AlertCleared, Active.Series, occurrence.OccurrenceId, now, _cpu, Active.LatestEvidence!, MonitoringClosureDisposition.ManuallyCleared, reason));
        Active = Active with { Phase = MonitoringPhase.Cleared, Occurrence = occurrence }; return Task.CompletedTask;
    }
}
