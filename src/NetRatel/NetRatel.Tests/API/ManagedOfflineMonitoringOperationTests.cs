using System.Collections.Immutable;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using NetRatel.API.Endpoints.Client;
using NetRatel.API.Security.Integration;
using NetRatel.API.Services.Monitoring;
using NetRatel.Application.Monitoring;
using NetRatel.Application.Operations;
using NetRatel.Application.Presence;
using NetRatel.Infrastructure.Identity;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using NetRatel.Shared.Contracts.Monitoring;
using NetRatel.Shared.Operations;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class ManagedOfflineMonitoringOperationTests
{
    [Theory]
    [InlineData("acknowledge_offline", false)]
    [InlineData("clear_offline", true)]
    public async Task Verified_aggregate_is_reviewed_then_dispatched_with_canonical_occurrence_fences(string operation, bool clear)
    {
        await using var scenario = await Scenario.CreateAsync(operation);
        // An exact-client deny is not a deny of this distinct tenant-rule operation.
        scenario.Db.McpOperatorPolicies.Add(scenario.Policy(McpOperatorPolicyEffect.Deny, McpOperatorTargetSelectorKind.ExactAgent));
        await scenario.Db.SaveChangesAsync();

        var preview = await scenario.InvokeAsync(confirm: false);

        ((IStatusCodeHttpResult)preview).StatusCode.Should().Be(200);
        scenario.Runtime.Command.Should().BeNull();
        (await scenario.Db.McpOperatorAcceptedAudits.CountAsync()).Should().Be(0);

        var result = await scenario.InvokeAsync(confirm: true);

        ((IStatusCodeHttpResult)result).StatusCode.Should().Be(200);
        scenario.Runtime.Clear.Should().Be(clear);
        var command = Assert.IsType<MonitoringOperatorCommand>(scenario.Runtime.Command);
        command.Series.Should().Be(MonitoringOfflinePresentation.Series(7, scenario.Rule.RuleId));
        command.OccurrenceId.Should().Be(scenario.Occurrence.OccurrenceId);
        command.OperatorId.Should().Be(scenario.OwnerId);
        command.ExpectedStateRevision.Should().Be(23);
        command.ExpectedConfigurationRevision.Should().Be(17);
        command.ExpectedOperatorRevision.Should().Be(3);
        command.Reason.Should().Be("Reviewed aggregate availability");
        (await scenario.Db.McpOperatorAcceptedAudits.CountAsync()).Should().Be(1);
    }

    [Theory]
    [InlineData("foreign-rule")]
    [InlineData("missing-occurrence")]
    [InlineData("foreign-occurrence")]
    [InlineData("client-occurrence")]
    [InlineData("replaced-occurrence")]
    public async Task Unverified_aggregate_is_denied_before_review_or_dispatch(string mismatch)
    {
        await using var scenario = await Scenario.CreateAsync("acknowledge_offline");
        switch (mismatch)
        {
            case "foreign-rule":
                scenario.Db.MonitoringRules.Remove(scenario.Rule);
                scenario.Db.MonitoringRules.Add(new MonitoringRuleRecord { TenantId = 8, RuleId = scenario.Rule.RuleId, Revision = 1, DefinitionJson = "{}" });
                break;
            case "missing-occurrence": scenario.Db.MonitoringOccurrences.Remove(scenario.Occurrence); break;
            case "foreign-occurrence": scenario.Occurrence.TenantId = 8; break;
            case "client-occurrence": scenario.Occurrence.AgentId = Guid.NewGuid(); break;
            case "replaced-occurrence": scenario.Series.LatestOccurrenceId = Guid.NewGuid(); break;
        }
        await scenario.Db.SaveChangesAsync();

        foreach (var confirmed in new[] { false, true })
        {
            var result = await scenario.InvokeAsync(confirmed);
            ((IStatusCodeHttpResult)result).StatusCode.Should().Be(403);
        }
        scenario.Runtime.Command.Should().BeNull();
        (await scenario.Db.McpOperatorAcceptedAudits.CountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData("acknowledge_offline")]
    [InlineData("clear_offline")]
    public async Task Each_aggregate_action_requires_its_own_current_application_permission(string operation)
    {
        await using var scenario = await Scenario.CreateAsync(operation);
        scenario.Access.Permission = operation == "clear_offline" ? NetRatelPermissions.MonitoringAcknowledge : NetRatelPermissions.MonitoringClear;

        var result = await scenario.InvokeAsync(confirm: true);

        ((IStatusCodeHttpResult)result).StatusCode.Should().Be(403);
        scenario.Runtime.Command.Should().BeNull();
        (await scenario.Db.McpOperatorAcceptedAudits.CountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData(McpOperatorTargetSelectorKind.ExactAgent)]
    [InlineData(McpOperatorTargetSelectorKind.ClientTag)]
    public async Task Client_or_tag_only_policy_cannot_authorize_a_tenant_rule_aggregate(McpOperatorTargetSelectorKind selector)
    {
        await using var scenario = await Scenario.CreateAsync("clear_offline", selector);

        var result = await scenario.InvokeAsync(confirm: true);

        ((IStatusCodeHttpResult)result).StatusCode.Should().Be(403);
        scenario.Runtime.Command.Should().BeNull();
        (await scenario.Db.McpOperatorAcceptedAudits.CountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Current_occurrence_and_tenant_policy_are_rechecked_before_acceptance(bool replaceOccurrence)
    {
        await using var scenario = await Scenario.CreateAsync("clear_offline");
        var admitted = await scenario.Admission.EvaluateAsync(scenario.Descriptor, scenario.Request(), scenario.Http, default);
        admitted.FailureCode.Should().BeNull();
        if (replaceOccurrence) scenario.Series.LatestOccurrenceId = Guid.NewGuid();
        else scenario.Db.McpOperatorPolicies.Add(scenario.Policy(McpOperatorPolicyEffect.Deny, McpOperatorTargetSelectorKind.Tenant));
        await scenario.Db.SaveChangesAsync();

        await Assert.ThrowsAsync<McpOperatorAdmissionRejectedException>(() => scenario.Admission.RecordAcceptedAsync(admitted, default));

        (await scenario.Db.McpOperatorAcceptedAudits.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_closed_latest_occurrence_can_reach_the_canonical_clear_retry_handler()
    {
        await using var scenario = await Scenario.CreateAsync("clear_offline");
        scenario.Series.ActiveOccurrenceId = null;
        scenario.Occurrence.EndedAtUtc = DateTimeOffset.UtcNow;
        await scenario.Db.SaveChangesAsync();

        var result = await scenario.InvokeAsync(confirm: true);

        ((IStatusCodeHttpResult)result).StatusCode.Should().Be(200);
        scenario.Runtime.Command!.OccurrenceId.Should().Be(scenario.Occurrence.OccurrenceId);
        scenario.Runtime.Clear.Should().BeTrue();
    }

    private sealed class Scenario : IAsyncDisposable
    {
        public Guid OwnerId { get; } = Guid.NewGuid();
        public OrchestratorDbContext Db { get; } = new(new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
        public required ManagedOperationDescriptor Descriptor { get; init; }
        public CurrentAccess Access { get; private set; } = null!;
        public ManagedOperationAdmission Admission { get; private set; } = null!;
        public DefaultHttpContext Http { get; } = new() { TraceIdentifier = "aggregate-monitoring-request" };
        public MonitoringRuleRecord Rule { get; } = new() { TenantId = 7, RuleId = Guid.NewGuid(), Revision = 1, DefinitionJson = "{}" };
        public MonitoringOccurrenceRecord Occurrence { get; private set; } = null!;
        public MonitoringSeriesRecord Series { get; private set; } = null!;
        public RecordingRuntime Runtime { get; } = new();
        private ServiceProvider _provider = null!;

        public static async Task<Scenario> CreateAsync(string operation, McpOperatorTargetSelectorKind selector = McpOperatorTargetSelectorKind.Tenant)
        {
            var scenario = new Scenario { Descriptor = ManagedOperationCatalog.Find("netratel_monitoring", operation)! };
            scenario.Access = new(scenario.OwnerId, scenario.Descriptor.Permission);
            scenario.Db.Tenants.AddRange(new Tenant { Id = 7, Name = "Selected tenant" }, new Tenant { Id = 8, Name = "Other tenant" });
            scenario.Occurrence = new()
            {
                TenantId = 7, RuleId = scenario.Rule.RuleId, OccurrenceId = Guid.NewGuid(), AgentId = Guid.Empty,
                ResourceKey = MonitoringOfflinePresentation.ResourceKey, RaisedEventId = Guid.NewGuid(), RaisedAtUtc = DateTimeOffset.UtcNow, OccurrenceJson = "{}"
            };
            scenario.Series = new()
            {
                TenantId = 7, RuleId = scenario.Rule.RuleId, AgentId = Guid.Empty, ResourceKey = MonitoringOfflinePresentation.ResourceKey,
                ActiveOccurrenceId = scenario.Occurrence.OccurrenceId, LatestOccurrenceId = scenario.Occurrence.OccurrenceId,
                StateRevision = 23, StateJson = "{}", UpdatedAtUtc = DateTimeOffset.UtcNow
            };
            scenario.Db.AddRange(scenario.Rule, scenario.Series, scenario.Occurrence, scenario.Policy(McpOperatorPolicyEffect.Allow, selector));
            await scenario.Db.SaveChangesAsync();
            var authority = new McpOperatorAuthorization(scenario.Db);
            scenario.Admission = new(scenario.Access, new NoExternalIdentity(), authority,
                new McpOperatorRouteAdmissionService(scenario.Db, authority), scenario.Db, new OfflinePresence(),
                new HostingEnvironment { EnvironmentName = "Production" });
            // ActAsync needs configuration and runtime only; unrelated collection seams
            // stay absent so a fabricated client lookup cannot silently succeed.
            var service = new MonitoringApiService(new MonitoringResourceAuthorizer(scenario.Access), scenario.Access,
                null!, new OwnedConfiguration(scenario.Rule.RuleId), scenario.Runtime, null!, null!, null!, null!, null!, null!, null!,
                TimeProvider.System, NullLogger<MonitoringApiService>.Instance);
            scenario._provider = new ServiceCollection().AddSingleton<IEffectiveAccessService>(scenario.Access)
                .AddSingleton(scenario.Db).AddSingleton(service).BuildServiceProvider();
            scenario.Http.RequestServices = scenario._provider;
            scenario.Http.User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim("netratel_principal_id", scenario.OwnerId.ToString("D")),
                new Claim(IntegrationCredentialAuthenticationHandler.CredentialIdClaimType, "credential-1"),
                new Claim("auth_mode", "integration_credential"), new Claim("integration_credential_purpose", "api")
            ], IntegrationCredentialAuthenticationHandler.SchemeName));
            return scenario;
        }

        public McpOperatorPolicyRecord Policy(McpOperatorPolicyEffect effect, McpOperatorTargetSelectorKind selector) => new()
        {
            Id = Guid.NewGuid(), Name = "Aggregate alert authority", Environment = McpOperatorEnvironment.Production, Effect = effect,
            PrincipalSelectorKind = McpOperatorPrincipalSelectorKind.OAuthSubject, PrincipalSelectorValue = OwnerId.ToString("D"),
            TargetSelectorKind = selector, TenantId = 7, AgentId = selector == McpOperatorTargetSelectorKind.ExactAgent ? Guid.NewGuid() : null,
            ClientTag = selector == McpOperatorTargetSelectorKind.ClientTag ? "linux" : null,
            OperationFamily = McpOperatorOperationCatalog.Find(Descriptor.Tool, Descriptor.Operation)!.OperationFamily,
            Operation = $"{Descriptor.Tool}/{Descriptor.Operation}", ConstraintsJson = "{}", CreatedAtUtc = DateTimeOffset.UtcNow, CreatedBy = "policy-admin", Version = 1
        };

        public JsonElement Request() => JsonSerializer.SerializeToElement(new
        {
            tenantId = 7, ruleId = Rule.RuleId,
            data = new MonitoringOperatorActionDto(Occurrence.OccurrenceId, "Reviewed aggregate availability", 23, 17, 3)
        }, ManagedOperationCatalog.JsonOptions);

        public async Task<IResult> InvokeAsync(bool confirm)
        {
            Http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { request = Request(), confirm })));
            return await ManagedOperationEndpoints.InvokeAsync(Descriptor.Tool, Descriptor.Operation, Http, Admission, default);
        }

        public async ValueTask DisposeAsync()
        {
            await _provider.DisposeAsync();
            await Db.DisposeAsync();
        }
    }

    private sealed class CurrentAccess(Guid ownerId, string permission) : IEffectiveAccessService
    {
        public string Permission { get; set; } = permission;
        public Task<bool> AuthorizeAsync(ClaimsPrincipal principal, string required, int? tenantId, CancellationToken cancellationToken = default) =>
            Task.FromResult(principal.FindFirstValue("netratel_principal_id") == ownerId.ToString("D") && tenantId == 7 && required == Permission);
        public Task<EffectiveAccessSnapshot> GetSnapshotAsync(ClaimsPrincipal principal, int? tenantId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new EffectiveAccessSnapshot(ownerId.ToString("D"), false, false, new HashSet<string> { Permission }));
        public Task ReconcileBuiltInRolesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class NoExternalIdentity : IApplicationPrincipalResolver
    {
        public Task<string?> ResolveExternalAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
    }

    private sealed class OfflinePresence : IClientPresenceReadModel
    {
        public Task<ClientPresenceReadModelSnapshot> GetSnapshotAsync(CancellationToken cancellationToken) => Task.FromResult(new ClientPresenceReadModelSnapshot(0, []));
        public Task<ClientPresenceSnapshot?> GetClientSnapshotAsync(ClientKey client, CancellationToken cancellationToken) => throw new NotSupportedException("A tenant-rule aggregate has no client presence target.");
    }

    private sealed class OwnedConfiguration(Guid ruleId) : IMonitoringConfigurationStore
    {
        public Task<MonitoringConfigurationSnapshot> GetAsync(int tenantId, CancellationToken cancellationToken) => Task.FromResult(new MonitoringConfigurationSnapshot(tenantId, 17,
            [new(tenantId, ruleId, 1, 1, "Availability", true, MonitoringSeverity.Warning, new(MonitoringTargetMode.Selected, [], []),
                new(MonitoringMetricKind.CpuUsagePercent, MonitoringNumericUnit.Percent, 80, 70, null, null, []),
                TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), AlertIfOffline: true)], [], [], DateTimeOffset.UtcNow));
        public Task<MonitoringConfigurationWriteResult> SaveRuleAsync(MonitoringRuleSaveRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task<MonitoringConfigurationWriteResult> SaveGroupAsync(MonitoringGroupSaveRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task<MonitoringConfigurationWriteResult> SaveBypassAsync(MonitoringBypassSaveRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task<MonitoringConfigurationWriteResult> DeleteRuleAsync(MonitoringConfigurationDeleteRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task<MonitoringConfigurationWriteResult> DeleteGroupAsync(MonitoringConfigurationDeleteRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task<MonitoringConfigurationWriteResult> DeleteBypassAsync(MonitoringConfigurationDeleteRequest request, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class RecordingRuntime : IMonitoringRuntime
    {
        public MonitoringOperatorCommand? Command { get; private set; }
        public bool Clear { get; private set; }
        public Task<MonitoringStoreWriteResult> AcknowledgeAsync(MonitoringOperatorCommand command, CancellationToken ct) => OperateAsync(command, false);
        public Task<MonitoringStoreWriteResult> ClearAsync(MonitoringOperatorCommand command, CancellationToken ct) => OperateAsync(command, true);
        private Task<MonitoringStoreWriteResult> OperateAsync(MonitoringOperatorCommand command, bool clear)
        {
            Command = command;
            Clear = clear;
            return Task.FromResult(new MonitoringStoreWriteResult(MonitoringStoreWriteDisposition.Stored,
                new(command.Series, 24, 1, MonitoringPhase.Firing, MonitoringEvidenceQuality.Fresh, ApplicableBypassIds: [])));
        }
        public Task<MonitoringInputResult> BeginEvidenceStreamAsync(MonitoringEvidenceFence fence, CancellationToken ct) => throw new NotSupportedException();
        public Task<MonitoringInputResult> EndEvidenceStreamAsync(MonitoringEvidenceFence fence, CancellationToken ct) => throw new NotSupportedException();
        public Task<MonitoringInputResult> RecordTelemetryAsync(MonitoringTelemetryInput input, CancellationToken ct) => throw new NotSupportedException();
        public Task<MonitoringInputResult> RecordServicesAsync(MonitoringServicesInput input, CancellationToken ct) => throw new NotSupportedException();
        public Task<ImmutableArray<MonitoringSeriesState>> GetClientAsync(ClientKey client, CancellationToken ct) => throw new NotSupportedException();
        public Task<MonitoringSeriesPageDto> ReadTenantAsync(int tenantId, int count, string? cursor, CancellationToken ct) => throw new NotSupportedException();
        public Task<MonitoringEventPageDto> ReadTenantEventsAsync(int tenantId, int count, string? cursor, CancellationToken ct) => throw new NotSupportedException();
        public Task<MonitoringSummaryDto> ReadTenantSummaryAsync(int tenantId, CancellationToken ct) => throw new NotSupportedException();
        public Task RefreshAsync(ClientKey client, CancellationToken ct) => throw new NotSupportedException();
    }
}
