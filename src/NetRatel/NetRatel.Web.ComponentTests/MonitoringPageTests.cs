using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using NetRatel.Shared.Contracts.Services;
using NetRatel.Shared.Contracts.Monitoring;
using NetRatel.Web.Components.Pages.Monitoring;
using NetRatel.Web.Services.Monitoring;
using NetRatel.Web.Services.Services;
using Xunit;

namespace NetRatel.Web.ComponentTests;

public sealed class MonitoringPageTests : AsyncBunitContext
{
    private readonly MonitoringTestApi _api = new();
    public MonitoringPageTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices(options => options.PopoverOptions.CheckForPopoverProvider = false);
        Services.AddSingleton<IMonitoringApiService>(_api);
        Services.AddSingleton<IClientServicesApiService>(new EmptyServices());
    }
    [Fact]
    public void FailedSaveRetainsDirtyInputsAndCancelDoesNotWrite()
    {
        var calls = 0;
        _api.RuleSave = (_, _, _) => { calls++; throw new HttpRequestException("Configuration conflict; edits retained"); };
        var cut = Render<MonitoringPage>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid='monitoring-tab-manage']")));
        cut.Find("[data-testid='monitoring-tab-manage']").Click();
        cut.Find("[data-testid='monitoring-new-rule']").Click();
        cut.Find("[data-testid='monitoring-rule-name']").Input("SQL CPU");
        cut.Find("[data-testid='monitoring-client-choice']").Change(true);
        cut.Find("[data-testid='monitoring-reason']").Input("add explicit CPU rule");
        cut.Find("[data-testid='monitoring-preview']").Click();
        cut.WaitForAssertion(() => Assert.Contains("eligible", cut.Find("[data-testid='monitoring-preview-result']").TextContent));
        cut.Find("[data-testid='monitoring-save']").Click();
        cut.WaitForAssertion(() => Assert.Contains("conflict", cut.Find("[data-testid='monitoring-editor-error']").TextContent));
        Assert.Equal("SQL CPU", cut.Find("[data-testid='monitoring-rule-name']").GetAttribute("value"));
        Assert.Contains("Unsaved", cut.Find("[data-testid='monitoring-dirty']").TextContent);
        Assert.Single(cut.FindAll("[data-testid='monitoring-save']"));
        Assert.Single(cut.FindAll("[data-testid='monitoring-cancel']"));
        cut.Find("[data-testid='monitoring-cancel']").Click();
        Assert.Empty(cut.FindAll("[data-testid='monitoring-editor']")); Assert.Equal(1, calls);
    }
    [Fact]
    public async Task DuplicateSubmissionIsGuardedWhileSaveIsOutstanding()
    {
        var calls = 0; var saving = new TaskCompletionSource<MonitoringConfigurationDto>();
        _api.RuleSave = (_, _, _) => { calls++; return saving.Task; };
        var cut = Render<MonitoringPage>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid='monitoring-tab-manage']")));
        cut.Find("[data-testid='monitoring-tab-manage']").Click(); cut.Find("[data-testid='monitoring-new-rule']").Click();
        cut.Find("[data-testid='monitoring-rule-name']").Input("SQL CPU"); cut.Find("[data-testid='monitoring-client-choice']").Change(true);
        cut.Find("[data-testid='monitoring-reason']").Input("configure CPU"); cut.Find("[data-testid='monitoring-preview']").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid='monitoring-preview-result']")));
        var first = cut.Find("[data-testid='monitoring-save']").ClickAsync(new());
        cut.WaitForAssertion(() => Assert.True(cut.Find("[data-testid='monitoring-save']").HasAttribute("disabled")));
        Assert.True(cut.Find("[data-testid='monitoring-tenant']").HasAttribute("disabled"));
        saving.SetResult(_api.Configuration(1) with { Revision = 2 }); await first;
        Assert.Equal(1, calls);
    }
    [Fact]
    public void PendingWatchActivationIsShownWithoutInventingFreshEvidence()
    {
        _api.ConfigurationRead = (tenant, _) => Task.FromResult(_api.Configuration(tenant) with { WatchPolicyUpdatePending = true });
        var cut = Render<MonitoringPage>();
        cut.WaitForAssertion(() => Assert.Contains("pending", cut.Find("[data-testid='monitoring-watch-pending']").TextContent));
        Assert.DoesNotContain("green", cut.Markup, StringComparison.OrdinalIgnoreCase);
    }
    [Fact]
    public void PendingAtSaveNoticeSurvivesDefaultGetAndRefreshButClearsOnTenantChange()
    {
        var calls = 0;
        _api.RuleSave = (tenant, request, _) =>
        {
            calls++;
            var saved = _api.Configuration(tenant) with { Revision = request.ExpectedConfigurationRevision + 1, Rules = [request.Rule] };
            _api.ConfigurationRead = (id, _) => Task.FromResult(id == tenant ? saved : _api.Configuration(id));
            return Task.FromResult(saved with { WatchPolicyUpdatePending = true });
        };
        var cut = Render<MonitoringPage>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid='monitoring-tab-manage']")));
        cut.Find("[data-testid='monitoring-tab-manage']").Click(); cut.Find("[data-testid='monitoring-new-rule']").Click();
        cut.Find("[data-testid='monitoring-rule-name']").Input("SQL CPU"); cut.Find("[data-testid='monitoring-client-choice']").Change(true);
        cut.Find("[data-testid='monitoring-reason']").Input("save selected targets"); cut.Find("[data-testid='monitoring-preview']").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid='monitoring-preview-result']")));
        cut.Find("[data-testid='monitoring-save']").Click();
        cut.WaitForAssertion(() => Assert.Contains("pending at save time", cut.Find("[data-testid='monitoring-watch-pending-at-save']").TextContent));
        Assert.Empty(cut.FindAll("[data-testid='monitoring-editor']"));
        Assert.Empty(cut.FindAll("[data-testid='monitoring-watch-pending']"));
        cut.Find("[data-testid='monitoring-refresh']").Click();
        cut.WaitForAssertion(() => Assert.Contains("revision 2", cut.Find("[data-testid='monitoring-watch-pending-at-save']").TextContent));
        cut.Find("[data-testid='monitoring-tenant']").Change("2");
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("[data-testid='monitoring-watch-pending-at-save']")));
        Assert.Equal(1, calls);
    }
    [Fact]
    public void SuccessfulIncidentReceiptIsShownWithTenantQualifiedRunLink()
    {
        var agent = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var draft = MonitoringRuleDraft.Create(); draft.Name = "SQL CPU"; draft.Enabled = true; draft.AgentIds.Add(agent);
        var rule = draft.Build(1) with { PublishedFlowVersionId = Guid.NewGuid(), ExecutionPrincipalId = "server-attributed-operator" };
        var now = DateTimeOffset.UtcNow;
        var evidence = new MonitoringEvidenceDto(new(1, 1), Guid.NewGuid(), now, now, MonitoringEvidenceQuality.Fresh, MonitoringClassification.Breach, 95, 0, null);
        var runId = Guid.NewGuid();
        var receipt = new MonitoringIncidentReceiptDto("incident-123", "NR-123", "https://desk.example/incidents/incident-123");
        var occurrence = new MonitoringOccurrenceDto(Guid.NewGuid(), Guid.NewGuid(), now, now.AddMinutes(-1), rule, evidence,
            MonitoringFlowDispatchDisposition.Completed, FlowOutcome: new(runId, MonitoringFlowOutcomeKind.Succeeded, now, Receipt: receipt));
        var state = new MonitoringSeriesState(new(1, rule.RuleId, agent, "cpu"), 1, 1, MonitoringPhase.Firing, MonitoringEvidenceQuality.Fresh,
            LatestEvidence: evidence, Occurrence: occurrence, ApplicableBypassIds: []);
        _api.ConfigurationRead = (tenant, _) => Task.FromResult(_api.Configuration(tenant) with { Rules = [rule] });
        _api.SeriesRead = (_, _, _) => Task.FromResult(new MonitoringSeriesPageDto([state], null));
        var cut = Render<MonitoringPage>();
        cut.WaitForAssertion(() => Assert.Contains("NR-123", cut.Find("[data-testid='monitoring-incident-receipt']").TextContent));
        Assert.Equal(receipt.IncidentUrl, cut.Find("[data-testid='monitoring-incident-receipt'] a").GetAttribute("href"));
        Assert.Contains("noopener", cut.Find("[data-testid='monitoring-incident-receipt'] a").GetAttribute("rel"));
        Assert.Contains(cut.FindAll("a"), link => link.GetAttribute("href") == $"/flows?tenantId=1&run={runId:D}");
    }
    private sealed class EmptyServices : IClientServicesApiService
    {
        public Task<ClientServicesReadModelDto?> GetAsync(int tenantId, Guid agentId, CancellationToken cancellationToken = default) => Task.FromResult<ClientServicesReadModelDto?>(null);
        public Task<ClientServicesRefreshResponse> RefreshAsync(int tenantId, Guid agentId, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Read-only editor must not request collection.");
    }
}
