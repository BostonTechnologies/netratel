using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using NetRatel.API.Security.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NetRatel.API.Endpoints.Monitoring;
using NetRatel.API.Gateway;
using NetRatel.API.Services.Monitoring;
using NetRatel.Application.Agents;
using NetRatel.Application.Monitoring;
using NetRatel.Application.Presence;
using NetRatel.Application.Services;
using NetRatel.Application.Telemetry;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Shared.Contracts.Monitoring;
using NetRatel.Shared.Contracts.Services;
using Xunit;
using NetRatel.API.Middleware;
using System.Text;
using System.Collections.Concurrent;

namespace NetRatel.Tests.API;

public sealed class MonitoringApiTests
{
    private static readonly Guid OperatorId = Guid.Parse("a4b9a726-b36b-48f3-9b59-c6b7f5cd9467");
    private static ClaimsPrincipal User => new(new ClaimsIdentity([new Claim("netratel_principal_id", OperatorId.ToString("N"))], "Test"));

    [Fact]
    public void Legacy_monitoring_pages_roundtrip_without_new_projections_and_preserve_explicit_empty_arrays()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        const string legacyPage = "{\"items\":[],\"nextCursor\":null}";
        var legacySeries = JsonSerializer.Deserialize<MonitoringSeriesPageDto>(legacyPage, options)!;
        var legacyEvents = JsonSerializer.Deserialize<MonitoringEventPageDto>(legacyPage, options)!;
        var seriesJson = JsonSerializer.SerializeToUtf8Bytes(legacySeries, options);
        var eventJson = JsonSerializer.SerializeToUtf8Bytes(legacyEvents, options);
        JsonSerializer.Deserialize<MonitoringSeriesPageDto>(seriesJson, options)!.Items.Should().BeEmpty();
        JsonSerializer.Deserialize<MonitoringEventPageDto>(eventJson, options)!.Items.Should().BeEmpty();
        using var legacyDocument = JsonDocument.Parse(eventJson);
        legacyDocument.RootElement.TryGetProperty("clientIdentities", out _).Should().BeFalse();
        legacyDocument.RootElement.TryGetProperty("audits", out _).Should().BeFalse();

        const string projectedPage = "{\"items\":[],\"nextCursor\":null,\"clientIdentities\":[],\"audits\":[]}";
        var projectedSeries = JsonSerializer.Deserialize<MonitoringSeriesPageDto>(projectedPage, options)!;
        var projectedEvents = JsonSerializer.Deserialize<MonitoringEventPageDto>(projectedPage, options)!;
        projectedSeries.ClientIdentities.IsDefault.Should().BeFalse();
        projectedEvents.ClientIdentities.IsDefault.Should().BeFalse();
        projectedEvents.Audits.IsDefault.Should().BeFalse();
        using var projectedDocument = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(projectedEvents, options));
        projectedDocument.RootElement.GetProperty("clientIdentities").GetArrayLength().Should().Be(0);
        projectedDocument.RootElement.GetProperty("audits").GetArrayLength().Should().Be(0);

        var legacyPreview = new MonitoringTargetPreviewDto([], 1, Details: []);
        var preview = JsonSerializer.Deserialize<MonitoringTargetPreviewDto>(JsonSerializer.SerializeToUtf8Bytes(legacyPreview, options), options)!;
        preview.AgentIds.Should().BeEmpty();
        preview.ClientIdentities.IsDefault.Should().BeTrue();
    }

    [Fact]
    public async Task OmittedOptionalImmutableCollectionsAreInitializedBeforePersistenceAndActualHttpSerialization()
    {
        var fixture = new Fixture();
        var omitted = fixture.Rule with { Condition = fixture.Rule.Condition with { ExpectedServiceStates = default } };
        await fixture.Api.SaveRuleAsync(7, omitted.RuleId, new(omitted, 0, "save numeric condition with omitted optional states"), User, default);
        fixture.Config.SavedRule!.Rule.Condition.ExpectedServiceStates.IsDefault.Should().BeFalse();
        fixture.Config.SavedRule.Rule.Condition.ExpectedServiceStates.Should().BeEmpty();
        // Exercise an omitted optional collection in a cached DTO as well.
        fixture.Config.Current = fixture.Config.Current with { Rules = [omitted] };
        using var host = await HostAsync(fixture);
        var http = host.GetTestClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test");
        var configuration = await http.GetFromJsonAsync<MonitoringConfigurationDto>("/api/v2/tenants/7/monitoring/configuration");
        configuration!.Rules.Single().Condition.ExpectedServiceStates.IsDefault.Should().BeFalse();
        using var result = await http.PostAsJsonAsync($"/api/v2/tenants/7/monitoring/agents/{fixture.AgentId}/rules/{omitted.RuleId}/clear?resourceKey=cpu",
            new MonitoringOperatorActionDto(Guid.NewGuid(), "clear via actual HTTP"));
        result.StatusCode.Should().Be(HttpStatusCode.OK);
        var state = await result.Content.ReadFromJsonAsync<MonitoringSeriesState>();
        state!.ApplicableBypassIds.IsDefault.Should().BeFalse();
        state.ApplicableBypassIds.Should().BeEmpty();
        var invalid = fixture.Rule with { RuleId = Guid.NewGuid(), Targets = fixture.Rule.Targets with { AgentIds = default } };
        Func<Task> invalidTargets = async () => await fixture.Api.SaveRuleAsync(7, invalid.RuleId, new(invalid, 1, "missing required target collection"), User, default);
        await invalidTargets.Should().ThrowAsync<ArgumentException>();
        fixture.Config.Writes.Should().Be(1);
    }

    [Fact]
    public async Task HttpReadRequiresAuthenticationAndExactTenantPermissionBeforeStorage()
    {
        var fixture = new Fixture();
        using var host = await HostAsync(fixture);
        var http = host.GetTestClient();
        (await http.GetAsync("/api/v2/tenants/7/monitoring/configuration")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test");
        (await http.GetAsync("/api/v2/tenants/8/monitoring/configuration")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        fixture.Config.Reads.Should().Be(0);
        var configuration = await http.GetFromJsonAsync<MonitoringConfigurationDto>("/api/v2/tenants/7/monitoring/configuration");
        configuration!.TenantId.Should().Be(7);
        fixture.Authority.Requests.Should().Contain(item => item.Permission == NetRatelPermissions.MonitoringRead && item.Resource.TenantId == 8);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OversizedMonitoringBodyIsRejectedBeforeJsonBindingOrStorage(bool chunked)
    {
        var fixture = new Fixture();
        using var host = await HostAsync(fixture);
        var http = host.GetTestClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test");
        var json = "{\"reason\":\"" + new string('a', MonitoringApiService.MaximumHttpBytes) + "\"}";
        using HttpContent content = chunked ? new ChunkedJsonContent(Encoding.UTF8.GetBytes(json)) : new StringContent(json, Encoding.UTF8, "application/json");
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        (await http.PutAsync($"/api/v2/tenants/7/monitoring/rules/{fixture.Rule.RuleId}", content)).StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        fixture.Config.Reads.Should().Be(0);
        fixture.Config.Writes.Should().Be(0);
    }

    [Theory]
    [InlineData("read")]
    [InlineData("manage")]
    [InlineData("ack")]
    [InlineData("clear")]
    [InlineData("bypass")]
    public async Task UnrelatedOrForeignPermissionCannotReadManageOrOperateMonitoring(string operation)
    {
        var fixture = new Fixture();
        fixture.Authority.Granted.Clear();
        fixture.Authority.Granted.Add((NetRatelPermissions.TelemetryRead, 7));
        fixture.Authority.Granted.Add((NetRatelPermissions.MonitoringManage, 8));
        Func<Task> action = operation switch
        {
            "read" => async () => await fixture.Api.GetConfigurationAsync(7, User, default),
            "manage" => async () => await fixture.Api.SaveRuleAsync(7, fixture.Rule.RuleId, new(fixture.Rule, 0, "change"), User, default),
            "ack" => async () => await fixture.Api.ActAsync(7, fixture.AgentId, fixture.Rule.RuleId, "cpu", new(Guid.NewGuid(), "ack"), false, User, default),
            "clear" => async () => await fixture.Api.ActAsync(7, fixture.AgentId, fixture.Rule.RuleId, "cpu", new(Guid.NewGuid(), "clear"), true, User, default),
            _ => async () => await fixture.Api.SaveBypassAsync(7, Guid.NewGuid(), new(0, "silence", AgentId: fixture.AgentId), User, default)
        };
        (await action.Should().ThrowAsync<MonitoringApiException>()).Which.StatusCode.Should().Be(403);
        fixture.Config.Reads.Should().Be(0);
        fixture.Config.Writes.Should().Be(0);
        fixture.Runtime.Commands.Should().BeEmpty();
    }

    [Fact]
    public async Task TargetsDeduplicateOverlapRejectForeignAgentsAndRequireFutureClientAuthority()
    {
        var fixture = new Fixture();
        var group = new MonitoringGroupDto(7, Guid.NewGuid(), 1, "SQL servers", [fixture.AgentId]);
        fixture.Config.Current = fixture.Config.Current with { Groups = [group] };
        var selection = new MonitoringTargetSelectionDto(MonitoringTargetMode.Selected, [fixture.AgentId], [group.GroupId]);
        var preview = await fixture.Api.PreviewTargetsAsync(7, new(selection), User, default);
        preview.AgentIds.Should().Equal(fixture.AgentId);
        preview.Total.Should().Be(1);
        preview.Details.Should().ContainSingle().Which.Support.Should().Be(MonitoringTargetSupport.Unknown);
        fixture.Services.Collections.Should().Be(0);
        var foreign = new MonitoringTargetSelectionDto(MonitoringTargetMode.Selected, [Guid.NewGuid()], []);
        Func<Task> foreignPreview = async () => await fixture.Api.PreviewTargetsAsync(7, new(foreign), User, default);
        await foreignPreview.Should().ThrowAsync<ArgumentException>();
        fixture.Authority.Granted.Remove((NetRatelPermissions.MonitoringAllTargets, 7));
        Func<Task> allPreview = async () => await fixture.Api.PreviewTargetsAsync(7, new(new(MonitoringTargetMode.AllEligible, [], [])), User, default);
        (await allPreview.Should().ThrowAsync<MonitoringApiException>()).Which.StatusCode.Should().Be(403);
        fixture.Authority.DeniedAgents.Add(fixture.AgentId);
        Func<Task> deniedPreview = async () => await fixture.Api.PreviewTargetsAsync(7, new(selection), User, default);
        await deniedPreview.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [Fact]
    public async Task ServerAttributesOperatorAndIgnoresForgedFlowAuthorityAndUnavailableVersion()
    {
        var fixture = new Fixture();
        var body = fixture.Rule with { ExecutionPrincipalId = "attacker", ExecutionCredentialId = "foreign-credential" };
        var result = await fixture.Api.SaveRuleAsync(7, body.RuleId, new(body, 0, "add rule"), User, default);
        result.Revision.Should().Be(1);
        fixture.Config.SavedRule!.OperatorId.Should().Be(OperatorId);
        fixture.Config.SavedRule.Rule.ExecutionPrincipalId.Should().BeNull();
        fixture.Config.SavedRule.Rule.ExecutionCredentialId.Should().BeNull();
        fixture.Access.Granted.Add((NetRatelPermissions.FlowRead, 7));
        fixture.Access.Granted.Add((NetRatelPermissions.FlowExecute, 7));
        var flowRule = body with { RuleId = Guid.NewGuid(), PublishedFlowVersionId = Guid.NewGuid() };
        Func<Task> selectFlow = async () => await fixture.Api.SaveRuleAsync(7, flowRule.RuleId, new(flowRule, 1, "select flow"), User, default);
        (await selectFlow.Should().ThrowAsync<MonitoringApiException>()).Which.Code.Should().Be("published_flow_unavailable");
        fixture.Config.Writes.Should().Be(1);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(7, 0)]
    [InlineData(0, 7)]
    [InlineData(7, 8)]
    [InlineData(8, 7)]
    public async Task FlowChoicesAndSelectionRequireReadAndExecutePairedWithTheExactMonitoringTenant(int readTenant, int executeTenant)
    {
        var fixture = new Fixture();
        fixture.Flows.Published = true;
        if (readTenant != 0) fixture.Access.Granted.Add((NetRatelPermissions.FlowRead, readTenant));
        if (executeTenant != 0) fixture.Access.Granted.Add((NetRatelPermissions.FlowExecute, executeTenant));
        (await fixture.Api.GetPublishedFlowsAsync(7, User, default)).Should().BeEmpty();
        fixture.Flows.ListCalls.Should().Be(0);
        (await fixture.Api.GetConfigurationAsync(7, User, default)).TenantId.Should().Be(7);
        var rule = fixture.Rule with { PublishedFlowVersionId = fixture.Flows.VersionId, ExecutionPrincipalId = "forged" };
        Func<Task> save = async () => await fixture.Api.SaveRuleAsync(7, rule.RuleId, new(rule, 0, "select published flow"), User, default);
        (await save.Should().ThrowAsync<MonitoringApiException>()).Which.Code.Should().Be("flow_selection_permission_required");
        fixture.Flows.PublishedCalls.Should().Be(0);
        fixture.Config.Writes.Should().Be(0);
    }

    [Fact]
    public async Task SelectingARealVersionServerStampsAuthorityAndUnchangedSelectionKeepsItAfterGrantRevocation()
    {
        var fixture = new Fixture();
        fixture.Flows.Published = true;
        fixture.Access.Granted.Add((NetRatelPermissions.FlowRead, 7));
        fixture.Access.Granted.Add((NetRatelPermissions.FlowExecute, 7));
        (await fixture.Api.GetPublishedFlowsAsync(7, User, default)).Should().ContainSingle().Which.PublishedFlowVersionId.Should().Be(fixture.Flows.VersionId);
        var selectingUser = new ClaimsPrincipal(new ClaimsIdentity([new Claim("netratel_principal_id", OperatorId.ToString("N")),
            new Claim("netratel_integration_credential_id", "current-configuring-credential")], "Test"));
        var rule = fixture.Rule with { PublishedFlowVersionId = fixture.Flows.VersionId, ExecutionPrincipalId = "attacker", ExecutionCredentialId = "foreign" };
        await fixture.Api.SaveRuleAsync(7, rule.RuleId, new(rule, 0, "select published flow"), selectingUser, default);
        var saved = fixture.Config.SavedRule!.Rule;
        saved.ExecutionPrincipalId.Should().Be(OperatorId.ToString("N"));
        saved.ExecutionCredentialId.Should().Be("current-configuring-credential");
        fixture.Access.Granted.Remove((NetRatelPermissions.FlowRead, 7));
        fixture.Access.Granted.Remove((NetRatelPermissions.FlowExecute, 7));
        var renamingUser = new ClaimsPrincipal(new ClaimsIdentity([new Claim("netratel_principal_id", Guid.NewGuid().ToString("N"))], "Test"));
        var renamed = saved with { Revision = 2, Name = "Renamed threshold", ExecutionPrincipalId = "replacement-attacker", ExecutionCredentialId = "replacement-foreign" };
        await fixture.Api.SaveRuleAsync(7, saved.RuleId, new(renamed, 1, "rename unchanged action"), renamingUser, default);
        fixture.Config.SavedRule!.Rule.ExecutionPrincipalId.Should().Be(saved.ExecutionPrincipalId);
        fixture.Config.SavedRule.Rule.ExecutionCredentialId.Should().Be(saved.ExecutionCredentialId);
        fixture.Flows.PublishedCalls.Should().Be(1, "retaining a trusted immutable action does not select a new version");
        var changed = renamed with { Revision = 3, PublishedFlowVersionId = Guid.NewGuid() };
        Func<Task> replace = async () => await fixture.Api.SaveRuleAsync(7, changed.RuleId, new(changed, 2, "replace action"), renamingUser, default);
        (await replace.Should().ThrowAsync<MonitoringApiException>()).Which.StatusCode.Should().Be(403);
        fixture.Config.Writes.Should().Be(2);
    }

    [Fact]
    public async Task ClearAndGroupBypassRequireReasonsAndUseExactResourcesAndServerIdentity()
    {
        var fixture = new Fixture();
        var group = new MonitoringGroupDto(7, Guid.NewGuid(), 1, "SQL servers", [fixture.AgentId]);
        fixture.Config.Current = fixture.Config.Current with { Rules = [fixture.Rule], Groups = [group] };
        Func<Task> clearWithoutReason = async () => await fixture.Api.ActAsync(7, fixture.AgentId, fixture.Rule.RuleId, "cpu", new(Guid.NewGuid(), ""), true, User, default);
        await clearWithoutReason.Should().ThrowAsync<ArgumentException>();
        fixture.Runtime.Commands.Should().BeEmpty();
        var occurrence = Guid.NewGuid();
        await fixture.Api.ActAsync(7, fixture.AgentId, fixture.Rule.RuleId, "cpu", new(occurrence, "maintenance", 4), true, User, default);
        fixture.Runtime.Commands.Should().ContainSingle().Which.Should().Be(new MonitoringOperatorCommand(new(7, fixture.Rule.RuleId, fixture.AgentId, "cpu"), occurrence, OperatorId, "maintenance", 4));
        await fixture.Api.SaveBypassAsync(7, Guid.NewGuid(), new(0, "group maintenance", GroupId: group.GroupId), User, default);
        fixture.Config.SavedBypass!.Bypass.OperatorId.Should().Be(OperatorId);
        fixture.Config.SavedBypass.Bypass.GroupId.Should().Be(group.GroupId);
        fixture.Config.SavedBypass.Bypass.StartsAtUtc.Should().Be(fixture.Clock.GetUtcNow());
        fixture.Authority.Requests.Should().Contain(item => item.Permission == NetRatelPermissions.MonitoringClear && item.Resource == new MonitoringResource(7, MonitoringResourceKind.Series, fixture.Rule.RuleId, fixture.AgentId, "cpu"));
        fixture.Authority.Requests.Should().Contain(item => item.Permission == NetRatelPermissions.MonitoringBypass && item.Resource.Kind == MonitoringResourceKind.Agent && item.Resource.AgentId == fixture.AgentId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommittedConfigurationReturnsSavedRevisionAndPendingFlagWhenWatchQueueFails(bool group)
    {
        var fixture = new Fixture();
        fixture.Sessions.Connected = true;
        fixture.Sessions.QueueSucceeds = false;
        var groupId = Guid.NewGuid();
        var result = group
            ? await fixture.Api.SaveGroupAsync(7, groupId, new(new(7, groupId, 1, "SQL servers", [fixture.AgentId]), 0, "add group"), User, default)
            : await fixture.Api.SaveRuleAsync(7, fixture.Rule.RuleId, new(fixture.Rule, 0, "add rule"), User, default);
        result.Revision.Should().Be(1);
        result.WatchPolicyUpdatePending.Should().BeTrue();
        if (group) result.Groups.Should().ContainSingle().Which.GroupId.Should().Be(groupId);
        else result.Rules.Should().ContainSingle().Which.RuleId.Should().Be(fixture.Rule.RuleId);
        fixture.Config.Writes.Should().Be(1);
        fixture.Sessions.PolicyAttempts.Should().Be(1);
        var read = await fixture.Api.GetConfigurationAsync(7, User, default);
        if (group) read.Groups.Should().ContainSingle();
        else read.Rules.Should().ContainSingle();
        fixture.Config.Writes.Should().Be(1, "a post-commit policy queue failure must not ask callers to repeat the save");
    }

    [Fact]
    public async Task PendingWatchReconciliationCannotQueueToReplacementRegistration()
    {
        var fixture = new Fixture();
        fixture.Sessions.Connected = true;
        fixture.Config.Current = fixture.Config.Current with { Rules = [fixture.ServiceRule("Spooler")] };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Services.BeforePolicyUpdate = async () => { entered.TrySetResult(); await release.Task; };
        var pending = fixture.Reconciler.ReconcileTenantAsync(7, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var old = fixture.Sessions.RegistrationId;
        fixture.Sessions.ReplaceRegistration();
        release.TrySetResult();
        await pending.WaitAsync(TimeSpan.FromSeconds(3));
        fixture.Sessions.PolicyAttempts.Should().Be(0, "an awaited selection for an old stream cannot target its replacement");
        await fixture.Reconciler.ReconcileTenantAsync(7, default);
        fixture.Sessions.PolicyAttempts.Should().Be(1);
        fixture.Sessions.LastExpectedRegistrationId!.Value.Should().Be(fixture.Sessions.RegistrationId).And.NotBe(old);
        fixture.Sessions.LastPolicy!.ServiceNames.Should().Equal("SPOOLER");
        fixture.Services.Collections.Should().Be(0);
    }

    [Fact]
    public async Task PolicyRenewalExtendsExpiryWithoutRevisingSelectionOrCollectingInventory()
    {
        var fixture = new Fixture();
        fixture.Sessions.Connected = true;
        fixture.Config.Current = fixture.Config.Current with { Rules = [fixture.ServiceRule("Spooler")] };
        await fixture.Reconciler.ReconcileTenantAsync(7, default);
        var first = fixture.Sessions.LastPolicy!;
        fixture.Clock.Now = fixture.Clock.Now.AddMinutes(30);
        await fixture.Reconciler.ReconcileTenantAsync(7, default);
        var renewed = fixture.Sessions.LastPolicy!;
        renewed.Revision.Should().Be(first.Revision);
        renewed.ServiceNames.Should().Equal(first.ServiceNames);
        renewed.ExpiresAtUtc.Should().BeAfter(first.ExpiresAtUtc);
        renewed.RefreshRequestId.Should().BeNull();
        fixture.Services.Collections.Should().Be(0);
    }

    [Fact]
    public async Task LargeTenantSaveReturnsPendingAndKeysetRenewalReachesEveryRegistration()
    {
        var fixture = new Fixture(257);
        fixture.Sessions.Connected = true;
        var groupId = Guid.NewGuid();
        var saved = await fixture.Api.SaveGroupAsync(7, groupId,
            new(new(7, groupId, 1, "All SQL servers", fixture.AgentIds), 0, "add group"), User, default);
        saved.Revision.Should().Be(1);
        saved.WatchPolicyUpdatePending.Should().BeTrue();
        fixture.Sessions.QueuedClients.Distinct().Should().HaveCount(64, "foreground saves have a fixed registration budget");
        fixture.Clock.Now = fixture.Clock.Now.AddMinutes(1);
        for (var index = 0; index < 3; index++) await fixture.Reconciler.ReconcileNextBatchAsync(default);
        fixture.Sessions.QueuedClients.Distinct().Select(client => client.AgentId).Should().BeEquivalentTo(fixture.AgentIds);
        fixture.Config.Writes.Should().Be(1);
        fixture.Services.Collections.Should().Be(0);
    }

    [Fact]
    public async Task EffectiveServiceWatchesAreExactEnabledRuleUnionAndCapacityIsRejectedBeforeSave()
    {
        var fixture = new Fixture();
        var group = new MonitoringGroupDto(7, Guid.NewGuid(), 1, "SQL servers", [fixture.AgentId]);
        var first = fixture.ServiceRule("Spooler");
        var duplicate = fixture.ServiceRule("spooler") with { Targets = new(MonitoringTargetMode.Selected, [], [group.GroupId]) };
        var disabled = fixture.ServiceRule("Disabled") with { Enabled = false };
        fixture.Config.Current = fixture.Config.Current with { Rules = [first, duplicate, disabled], Groups = [group] };
        var policy = await fixture.Source.GetPolicyAsync(new(7, fixture.AgentId), default);
        policy.Policy.ServiceNames.Should().Equal("SPOOLER");
        var oldRevision = policy.Policy.Revision;
        fixture.Config.Current = fixture.Config.Current with { Rules = [] };
        (await fixture.Source.GetPolicyAsync(new(7, fixture.AgentId), default)).Policy.Should().Match<ClientServiceWatchPolicyDto>(value => value.ServiceNames.Count == 0 && value.Revision > oldRevision);
        fixture.Config.Current = fixture.Config.Current with { Rules = Enumerable.Range(0, 64).Select(index => fixture.ServiceRule("service-" + index)).ToImmutableArray() };
        var overflow = fixture.ServiceRule("sixty-fifth");
        Func<Task> save = async () => await fixture.Api.SaveRuleAsync(7, overflow.RuleId, new(overflow, 0, "add service"), User, default);
        await save.Should().ThrowAsync<ArgumentException>();
        fixture.Config.Writes.Should().Be(0);
    }

    [Fact]
    public async Task WorstCaseTargetConfigurationExceedingHttpBudgetIsRejectedWithoutTruncationOrWrite()
    {
        var fixture = new Fixture();
        var targets = Enumerable.Range(0, 4096).Select(_ => Guid.NewGuid()).ToImmutableArray();
        var rules = Enumerable.Range(0, 28).Select(_ => fixture.Rule with { RuleId = Guid.NewGuid(), Targets = new(MonitoringTargetMode.Selected, targets, []) }).ToImmutableArray();
        fixture.Config.Current = fixture.Config.Current with { Rules = rules };
        Func<Task> read = async () => await fixture.Api.GetConfigurationAsync(7, User, default);
        (await read.Should().ThrowAsync<MonitoringApiException>()).Which.StatusCode.Should().Be(413);
        var groupId = Guid.NewGuid();
        Func<Task> save = async () => await fixture.Api.SaveGroupAsync(7, groupId, new(new(7, groupId, 1, "group", [fixture.AgentId]), 0, "change"), User, default);
        (await save.Should().ThrowAsync<MonitoringApiException>()).Which.StatusCode.Should().Be(413);
        fixture.Config.Writes.Should().Be(0);
    }

    private static Task<IHost> HostAsync(Fixture fixture) => new HostBuilder().ConfigureWebHost(web => web.UseTestServer()
        .ConfigureServices(services =>
        {
            services.AddRouting();
            services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, TestAuth>("Test", _ => { })
                .AddPolicyScheme("Bearer", null, options => options.ForwardDefault = "Test");
            services.AddAuthorization(MonitoringAuthorization.AddPolicies);
            services.AddSingleton<IEffectiveAccessService>(fixture.Access);
            services.AddScoped<IAuthorizationHandler, EffectiveAccessHandler>();
            MonitoringFlowPermissionAuthorization.AddHandlers(services);
            services.AddSingleton(fixture.Api);
        }).Configure(app => { app.UseRouting(); app.UseAuthentication(); app.UseAuthorization(); app.UseMiddleware<MonitoringHttpBoundsMiddleware>(); app.UseEndpoints(endpoints => endpoints.MapMonitoringEndpoints()); })).StartAsync();

    private sealed class TestAuth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(Request.Headers.ContainsKey("Authorization")
            ? AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(User.Claims, "Oidc")), Scheme.Name)) : AuthenticateResult.NoResult());
    }
    private sealed class ChunkedJsonContent(byte[] payload) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(payload).AsTask();
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }

    private sealed class Fixture
    {
        public Guid AgentId { get; } = Guid.NewGuid();
        public ImmutableArray<Guid> AgentIds { get; }
        public Clock Clock { get; } = new();
        public Authority Authority { get; } = new();
        public Access Access { get; }
        public PublishedFlows Flows { get; } = new();
        public ConfigStore Config { get; } = new();
        public Runtime Runtime { get; } = new();
        public ServicesRouter Services { get; } = new();
        public Sessions Sessions { get; } = new();
        public MonitoringApiService Api { get; }
        public MonitoringServiceWatchPolicySource Source { get; }
        public MonitoringWatchPolicyReconciler Reconciler { get; }
        public MonitoringRuleDto Rule => new(7, _ruleId, 1, 1, "CPU high", true, MonitoringSeverity.Warning,
            new(MonitoringTargetMode.Selected, [AgentId], []), new(MonitoringMetricKind.CpuUsagePercent, MonitoringNumericUnit.Percent, 80, 70, null, null, []),
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2));
        private readonly Guid _ruleId = Guid.NewGuid();
        public MonitoringRuleDto ServiceRule(string name) => Rule with
        {
            RuleId = Guid.NewGuid(), Condition = new(MonitoringMetricKind.ServiceExpectedState, null, null, null, name, ClientServicePlatform.Windows, [ClientServiceState.Running])
        };
        public Fixture(int clientCount = 1)
        {
            Access = new(Authority);
            AgentIds = Enumerable.Range(1, clientCount - 1).Select(_ => Guid.NewGuid()).Prepend(AgentId).ToImmutableArray();
            Sessions.AgentIds = AgentIds;
            var directory = new ClientDirectory(AgentIds, Sessions);
            Source = new(Config, directory, Services, Clock);
            Reconciler = new MonitoringWatchPolicyReconciler(Source, Services, directory, Sessions, Clock, NullLogger<MonitoringWatchPolicyReconciler>.Instance);
            Api = new(Authority, Access, new TenantCatalog(), Config, Runtime, directory, new AgentDirectory(AgentIds),
                Flows, Services, new TelemetryRouter(), Sessions, Reconciler, Clock, NullLogger<MonitoringApiService>.Instance);
        }
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 2, 18, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Authority : IMonitoringResourceAuthorizer
    {
        public HashSet<(string Permission, int Tenant)> Granted { get; } = NetRatelPermissions.All.Where(permission => permission.StartsWith("monitoring.", StringComparison.Ordinal)).Select(permission => (permission, 7)).ToHashSet();
        public HashSet<Guid> DeniedAgents { get; } = [];
        public List<(string Permission, MonitoringResource Resource)> Requests { get; } = [];
        public Task<bool> AuthorizeAsync(ClaimsPrincipal user, string permission, MonitoringResource resource, CancellationToken ct)
        { Requests.Add((permission, resource)); return Task.FromResult(Granted.Contains((permission, resource.TenantId)) && (resource.AgentId is null || !DeniedAgents.Contains(resource.AgentId.Value))); }
    }
    // This service-level HTTP fixture uses the actual named policies and handlers.
    // Its synthetic OIDC identity is only a unit-host seam; the separate ApiFactory
    // tests exercise production token verification and durable current authority.
    private sealed class Access(Authority authority) : IEffectiveAccessService
    {
        public HashSet<(string Permission, int Tenant)> Granted { get; } = [(NetRatelPermissions.MonitoringRead, 7)];
        public Task<bool> AuthorizeAsync(ClaimsPrincipal user, string permission, int? tenant, CancellationToken ct = default) => tenant is int id
            ? permission.StartsWith("monitoring.", StringComparison.Ordinal)
                ? authority.AuthorizeAsync(user, permission, new(id, MonitoringResourceKind.Tenant), ct)
                : Task.FromResult(Granted.Contains((permission, id)))
            : Task.FromResult(false);
        public async Task<EffectiveAccessSnapshot> GetSnapshotAsync(ClaimsPrincipal user, int? tenant, CancellationToken ct = default)
        {
            var permissions = new HashSet<string>(StringComparer.Ordinal);
            foreach (var permission in NetRatelPermissions.All)
                if (await AuthorizeAsync(user, permission, tenant, ct)) permissions.Add(permission);
            return new(user.FindFirst("netratel_principal_id")?.Value, false, false, permissions);
        }
        public Task<int[]?> GetAuthorizedTenantIdsAsync(ClaimsPrincipal user, string permission, CancellationToken ct = default) =>
            Task.FromResult<int[]?>((permission.StartsWith("monitoring.", StringComparison.Ordinal) ? authority.Granted : Granted)
                .Where(grant => grant.Permission == permission).Select(grant => grant.Tenant).Distinct().Order().ToArray());
        public Task ReconcileBuiltInRolesAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
    private sealed class PublishedFlows : IMonitoringPublishedFlowProvider
    {
        public Guid VersionId { get; } = Guid.NewGuid();
        public bool Published { get; set; }
        public int PublishedCalls { get; private set; }
        public int ListCalls { get; private set; }
        public Task<bool> IsPublishedAsync(int tenantId, Guid versionId, CancellationToken cancellationToken)
        { PublishedCalls++; return Task.FromResult(Published && tenantId == 7 && versionId == VersionId); }
        public Task<ImmutableArray<MonitoringPublishedFlowDto>> ListPublishedAsync(int tenantId, int maximumCount, CancellationToken cancellationToken)
        { ListCalls++; return Task.FromResult<ImmutableArray<MonitoringPublishedFlowDto>>(Published && tenantId == 7 ? [new(VersionId, "Published immutable fixture", 1)] : []); }
    }
    private sealed class TenantCatalog : IMonitoringTenantCatalog
    { public Task<ImmutableArray<MonitoringTenantDto>> GetAsync(int[]? ids, int max, CancellationToken ct) => Task.FromResult<ImmutableArray<MonitoringTenantDto>>([new(7, "Tenant seven")]); }
    private sealed class ClientDirectory(ImmutableArray<Guid> agentIds, Sessions sessions) : IMonitoringClientDirectory
    {
        public Task<ImmutableArray<Guid>> GetEligibleAgentsAsync(int tenant, CancellationToken ct) => Task.FromResult(tenant == 7 ? agentIds : ImmutableArray<Guid>.Empty);
        public Task<MonitoringEvidenceFence?> GetCurrentEvidenceAsync(ClientKey client, CancellationToken ct) => Task.FromResult<MonitoringEvidenceFence?>(sessions.Connected ? new(client, sessions.ConnectionId, 1, sessions.RegistrationId) : null);
    }
    private sealed class Sessions : IAgentTelemetryGatewaySessionRegistry
    {
        public Guid RegistrationId { get; private set; } = Guid.NewGuid();
        public Guid ConnectionId { get; } = Guid.NewGuid();
        public bool Connected { get; set; }
        public bool QueueSucceeds { get; set; } = true;
        private int _policyAttempts;
        public int PolicyAttempts => Volatile.Read(ref _policyAttempts);
        public ImmutableArray<Guid> AgentIds { get; set; } = [];
        public ConcurrentQueue<ClientKey> QueuedClients { get; } = new();
        public Guid? LastExpectedRegistrationId { get; private set; }
        public ClientServiceWatchPolicyDto? LastPolicy { get; private set; }
        public void ReplaceRegistration() => RegistrationId = Guid.NewGuid();
        public AgentTelemetryGatewaySessionRegistration Register(ClientKey client, Guid connection, ulong epoch, bool dynamic, string version) => throw new NotSupportedException();
        public AgentTelemetryGatewaySessionStatus GetStatus(ClientKey client) => new(Connected, false, "test", Connected ? 1 : null) { SupportsServices = Connected, ConnectionId = Connected ? ConnectionId : null, RegistrationId = Connected ? RegistrationId : null };
        public void PublishPolicy(ClientKey client, TelemetrySamplingPolicyState policy) { }
        public bool TryPublishServicesPolicy(ClientKey client, ClientServiceWatchPolicyDto policy, Guid expectedRegistrationId)
        {
            Interlocked.Increment(ref _policyAttempts); LastExpectedRegistrationId = expectedRegistrationId; LastPolicy = policy;
            if (!QueueSucceeds || expectedRegistrationId != RegistrationId) return false;
            QueuedClients.Enqueue(client); return true;
        }
        public AgentTelemetryGatewayServicesSessionPage GetServicesSessions(int maximumCount, ClientKey? after = null)
        {
            var items = AgentIds.Order().Where(id => after is null || id.CompareTo(after.Value.AgentId) > 0)
                .Select(id => new AgentTelemetryGatewayServicesSession(new(7, id), RegistrationId)).Take(maximumCount + 1).ToArray();
            var page = items.Take(maximumCount).ToArray();
            return new(page, items.Length > maximumCount ? page[^1].Client : null);
        }
    }
    private sealed class AgentDirectory(ImmutableArray<Guid> agentIds) : IAgentManagementService
    {
        public Task<AgentDetailDto?> GetAsync(int tenant, Guid id, CancellationToken ct) => Task.FromResult<AgentDetailDto?>(tenant == 7 && agentIds.Contains(id) ? new(tenant, id, "Synthetic client", true, null, DateTimeOffset.UtcNow, null, null, null, null) : null);
        public Task<AgentListResponse> ListAsync(int tenant, AgentListQuery query, CancellationToken ct) => throw new NotSupportedException();
        public Task DisableAsync(int tenant, Guid id, string reason, string actor, CancellationToken ct) => throw new NotSupportedException();
        public Task EnableAsync(int tenant, Guid id, string actor, CancellationToken ct) => throw new NotSupportedException();
        public Task DeleteAsync(int tenant, Guid id, string reason, string actor, CancellationToken ct) => throw new NotSupportedException();
    }
    private sealed class ServicesRouter : IClientServicesRouter
    {
        private readonly ConcurrentDictionary<ClientKey, ClientServicesState> _states = [];
        public int Collections { get; private set; }
        public Func<Task>? BeforePolicyUpdate { get; set; }
        public Task<ClientServicesMessageResult> RecordAsync(RecordClientServicesChunk input, CancellationToken ct) { Collections++; throw new NotSupportedException(); }
        public Task<ClientServicesState> GetSnapshotAsync(ClientKey client, CancellationToken ct) => Task.FromResult(_states.GetValueOrDefault(client) ?? ClientServicesState.Empty(client));
        public async Task<ClientServicesState> UpdateWatchPolicyAsync(ClientServiceWatchPolicy policy, CancellationToken ct)
        {
            if (BeforePolicyUpdate is not null) await BeforePolicyUpdate();
            var current = _states.GetValueOrDefault(policy.Client) ?? ClientServicesState.Empty(policy.Client);
            var updated = current with { MonitoredServiceNames = policy.Policy.ServiceNames, WatchPolicyRevision = policy.Policy.Revision };
            _states[policy.Client] = updated;
            return updated;
        }
    }
    private sealed class TelemetryRouter : IClientTelemetryRouter
    {
        public Task<ClientTelemetryState> GetSnapshotAsync(ClientKey client, CancellationToken ct) => Task.FromResult(new ClientTelemetryState(client, null));
        public Task<TelemetryMessageResult> RecordAsync(RecordTelemetrySnapshot message, CancellationToken ct) => throw new NotSupportedException();
        public Task<ClientTelemetryReadModelSnapshot> GetReadModelAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<ClientTelemetryRouteStatus> ProbeAsync(CancellationToken ct) => throw new NotSupportedException();
    }
    private sealed class ConfigStore : IMonitoringConfigurationStore
    {
        public MonitoringConfigurationSnapshot Current { get; set; } = MonitoringConfigurationSnapshot.Empty(7, DateTimeOffset.UtcNow);
        public int Reads { get; private set; }
        public int Writes { get; private set; }
        public MonitoringRuleSaveRequest? SavedRule { get; private set; }
        public MonitoringBypassSaveRequest? SavedBypass { get; private set; }
        public Task<MonitoringConfigurationSnapshot> GetAsync(int tenant, CancellationToken ct) { Reads++; return Task.FromResult(Current); }
        public Task<MonitoringConfigurationWriteResult> SaveRuleAsync(MonitoringRuleSaveRequest request, CancellationToken ct)
        { Writes++; SavedRule = request; Current = Current with { Revision = Current.Revision + 1, Rules = Current.Rules.Where(rule => rule.RuleId != request.Rule.RuleId).Append(request.Rule).ToImmutableArray() }; return Task.FromResult(new MonitoringConfigurationWriteResult(MonitoringConfigurationWriteDisposition.Stored, Current)); }
        public Task<MonitoringConfigurationWriteResult> SaveGroupAsync(MonitoringGroupSaveRequest request, CancellationToken ct)
        { Writes++; Current = Current with { Revision = Current.Revision + 1, Groups = Current.Groups.Append(request.Group).ToImmutableArray() }; return Task.FromResult(new MonitoringConfigurationWriteResult(MonitoringConfigurationWriteDisposition.Stored, Current)); }
        public Task<MonitoringConfigurationWriteResult> SaveBypassAsync(MonitoringBypassSaveRequest request, CancellationToken ct)
        { Writes++; SavedBypass = request; Current = Current with { Revision = Current.Revision + 1, Bypasses = Current.Bypasses.Append(request.Bypass).ToImmutableArray() }; return Task.FromResult(new MonitoringConfigurationWriteResult(MonitoringConfigurationWriteDisposition.Stored, Current)); }
        public Task<MonitoringConfigurationWriteResult> DeleteRuleAsync(MonitoringConfigurationDeleteRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task<MonitoringConfigurationWriteResult> DeleteGroupAsync(MonitoringConfigurationDeleteRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task<MonitoringConfigurationWriteResult> DeleteBypassAsync(MonitoringConfigurationDeleteRequest request, CancellationToken ct) => throw new NotSupportedException();
    }
    private sealed class Runtime : IMonitoringRuntime
    {
        public List<MonitoringOperatorCommand> Commands { get; } = [];
        public Task<MonitoringStoreWriteResult> ClearAsync(MonitoringOperatorCommand command, CancellationToken ct) => Apply(command);
        public Task<MonitoringStoreWriteResult> AcknowledgeAsync(MonitoringOperatorCommand command, CancellationToken ct) => Apply(command);
        private Task<MonitoringStoreWriteResult> Apply(MonitoringOperatorCommand command)
        { Commands.Add(command); return Task.FromResult(new MonitoringStoreWriteResult(MonitoringStoreWriteDisposition.Stored, new(command.Series, 5, 1, MonitoringPhase.Cleared, MonitoringEvidenceQuality.Unknown))); }
        public Task<MonitoringInputResult> BeginEvidenceStreamAsync(MonitoringEvidenceFence fence, CancellationToken ct) => throw new NotSupportedException();
        public Task<MonitoringInputResult> EndEvidenceStreamAsync(MonitoringEvidenceFence fence, CancellationToken ct) => throw new NotSupportedException();
        public Task<MonitoringInputResult> RecordTelemetryAsync(MonitoringTelemetryInput input, CancellationToken ct) => throw new NotSupportedException();
        public Task<MonitoringInputResult> RecordServicesAsync(MonitoringServicesInput input, CancellationToken ct) => throw new NotSupportedException();
        public Task<ImmutableArray<MonitoringSeriesState>> GetClientAsync(ClientKey client, CancellationToken ct) => Task.FromResult(ImmutableArray<MonitoringSeriesState>.Empty);
        public Task<MonitoringSeriesPageDto> ReadTenantAsync(int tenant, int max, string? cursor, CancellationToken ct) => Task.FromResult(new MonitoringSeriesPageDto([], null));
        public Task<MonitoringEventPageDto> ReadTenantEventsAsync(int tenant, int max, string? cursor, CancellationToken ct) => Task.FromResult(new MonitoringEventPageDto([], null));
        public Task<MonitoringSummaryDto> ReadTenantSummaryAsync(int tenant, CancellationToken ct) => Task.FromResult(new MonitoringSummaryDto(tenant, 0, 0, 0, 0, 0, 0, DateTimeOffset.UtcNow));
        public Task RefreshAsync(ClientKey client, CancellationToken ct) => Task.CompletedTask;
    }
}
