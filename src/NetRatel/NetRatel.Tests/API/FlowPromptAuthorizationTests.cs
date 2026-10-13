using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.API.Endpoints;
using NetRatel.Application.Flows;
using NetRatel.Application.Netclaw;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.Flows;
using NetRatel.Shared.Contracts.Netclaw;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class FlowPromptAuthorizationTests
{
    [Theory]
    [InlineData("list")]
    [InlineData("get")]
    [InlineData("versions")]
    [InlineData("version")]
    public async Task Prompt_bodies_require_current_prompt_read_permission_at_every_flow_projection(string operation)
    {
        await using var rig = new Rig();
        Assert.Equal(403, Status(await rig.ReadAsync(operation)));
        rig.Access.Grant(NetRatelPermissions.TelemetryRead, 17);
        Assert.Equal(200, Status(await rig.ReadAsync(operation)));
        rig.Access.Revoke(NetRatelPermissions.TelemetryRead, 17);
        Assert.Equal(403, Status(await rig.ReadAsync(operation)));
        Assert.Equal(0, rig.Mutations);
    }

    [Theory]
    [InlineData("save")]
    [InlineData("clone")]
    [InlineData("publish")]
    [InlineData("validate")]
    [InlineData("dry-run")]
    public async Task Optional_prompt_use_cannot_bypass_prompt_permission_through_flow_authoring(string operation)
    {
        await using var rig = new Rig();
        var result = operation switch
        {
            "save" => await FlowEndpoints.SaveAsync(17, rig.FlowId, new(1, "AI flow", rig.Graph), rig.Http, rig.Access, rig.Db, rig.Flows, default),
            "clone" => await FlowEndpoints.CloneAsync(17, rig.FlowId, new(1, "Copy"), rig.Http, rig.Access, rig.Db, rig.Flows, default),
            "publish" => await FlowEndpoints.PublishAsync(17, rig.FlowId, new(1), rig.Http, rig.Access, rig.Db, rig.Flows, default),
            "validate" => await FlowEndpoints.ValidateAsync(17, rig.Graph, rig.Http, rig.Access, rig.Db, default),
            _ => await FlowEndpoints.DryRunAsync(17, new(rig.Graph, null!), rig.Http, rig.Access, rig.Db, default)
        };
        Assert.Equal(403, Status(result));
        Assert.Equal(0, rig.Mutations); Assert.Empty(rig.Prompts.Resolutions);
    }

    [Fact]
    public async Task Prompt_free_versions_retain_the_existing_flow_read_capability()
    {
        await using var rig = new Rig();
        rig.Graph = FlowGraphTemplates.IncidentFromAlert();
        Assert.Equal(200, Status(await rig.ReadAsync("version")));
        Assert.Empty(rig.Access.Checks.Where(check => check.Permission == NetRatelPermissions.TelemetryRead));
    }

    [Fact]
    public async Task Delegated_prompt_permission_in_another_tenant_does_not_authorize_a_flow_snapshot()
    {
        await using var rig = new Rig();
        rig.Access.Grant(NetRatelPermissions.TelemetryRead, 18);
        rig.Http.User = new(new ClaimsIdentity([new("netratel_principal_id", "operator"),
            new("netratel_integration_credential_id", "scoped-credential")], "IntegrationCredential"));
        Assert.Equal(403, Status(await rig.ReadAsync("version")));
        Assert.Contains(rig.Access.Checks, check => check.Permission == NetRatelPermissions.TelemetryRead && check.Tenant == 17 &&
            check.Principal.HasClaim("netratel_integration_credential_id", "scoped-credential"));
    }

    [Fact]
    public async Task Validation_resolves_prompt_references_in_the_target_tenant_instead_of_trusting_supplied_bodies()
    {
        await using var rig = new Rig(); rig.Access.Grant(NetRatelPermissions.TelemetryRead, 17);
        rig.Prompts.Available = false;
        var result = await FlowEndpoints.ValidateAsync(17, rig.Graph, rig.Http, rig.Access, rig.Db, default);
        var validation = Assert.IsType<FlowValidationResultDto>(((IValueHttpResult)result).Value);
        Assert.False(validation.Valid); Assert.Contains(validation.Issues, issue => issue.Code == "ai-prompt-unavailable");
        var resolution = Assert.Single(rig.Prompts.Resolutions);
        Assert.Equal(17, resolution.Tenant); Assert.Equal(rig.PromptId, resolution.Reference.PromptId);
        Assert.False(resolution.AllowArchived); Assert.Equal(0, rig.Mutations);
    }

    [Theory]
    [InlineData("run", "none")]
    [InlineData("run", "telemetry")]
    [InlineData("run", "monitoring")]
    [InlineData("run", "both")]
    [InlineData("run-by-id", "none")]
    [InlineData("run-by-id", "telemetry")]
    [InlineData("run-by-id", "monitoring")]
    [InlineData("run-by-id", "both")]
    public async Task Run_history_preserves_original_facts_and_requires_both_permissions_for_AI_discovery(string operation, string grants)
    {
        await using var rig = new Rig();
        if (grants is "telemetry" or "both") rig.Access.Grant(NetRatelPermissions.TelemetryRead, 17);
        if (grants is "monitoring" or "both") rig.Access.Grant(NetRatelPermissions.MonitoringRead, 17);
        var result = await rig.ReadAsync(operation);
        Assert.Equal(200, Status(result));
        var projected = Assert.IsType<FlowRunDetailDto>(((IValueHttpResult)result).Value);
        Assert.Equal(rig.Run.Run, projected.Run); Assert.Equal(rig.Run.Input, projected.Input);
        Assert.Equal(rig.Run.Actions.Select(action => action with { AiResult = null }), projected.Actions.Select(action => action with { AiResult = null }));
        if (grants == "both") Assert.Equal(rig.Run.Actions[0].AiResult, projected.Actions[0].AiResult);
        else Assert.All(projected.Actions, action => Assert.Null(action.AiResult));
        Assert.NotNull(rig.Run.Actions[0].AiResult); Assert.Equal(0, rig.Mutations);
        if (grants == "both")
        {
            rig.Access.Revoke(NetRatelPermissions.MonitoringRead, 17);
            projected = Assert.IsType<FlowRunDetailDto>(((IValueHttpResult)await rig.ReadAsync(operation)).Value);
            Assert.All(projected.Actions, action => Assert.Null(action.AiResult));
            rig.Access.Grant(NetRatelPermissions.MonitoringRead, 17); rig.Access.Revoke(NetRatelPermissions.TelemetryRead, 17);
            projected = Assert.IsType<FlowRunDetailDto>(((IValueHttpResult)await rig.ReadAsync(operation)).Value);
            Assert.All(projected.Actions, action => Assert.Null(action.AiResult));
        }
    }

    [Fact]
    public async Task Run_discovery_ignores_other_tenant_grants_and_foreign_embedded_results()
    {
        await using var rig = new Rig();
        rig.Access.Grant(NetRatelPermissions.TelemetryRead, 18); rig.Access.Grant(NetRatelPermissions.MonitoringRead, 18);
        var projected = Assert.IsType<FlowRunDetailDto>(((IValueHttpResult)await rig.ReadAsync("run-by-id")).Value);
        Assert.All(projected.Actions, action => Assert.Null(action.AiResult));
        rig.Access.Grant(NetRatelPermissions.TelemetryRead, 17); rig.Access.Grant(NetRatelPermissions.MonitoringRead, 17);
        rig.Run = rig.Run with { Actions = rig.Run.Actions.Select(action => action.AiResult is { } ai
            ? action with { AiResult = ai with { TenantId = 18 } } : action).ToArray() };
        projected = Assert.IsType<FlowRunDetailDto>(((IValueHttpResult)await rig.ReadAsync("run-by-id")).Value);
        Assert.All(projected.Actions, action => Assert.Null(action.AiResult));
        Assert.Equal(rig.Run.Input, projected.Input); Assert.Equal(0, rig.Mutations);
    }

    private static int? Status(IResult result) => result is ForbidHttpResult ? StatusCodes.Status403Forbidden : ((IStatusCodeHttpResult)result).StatusCode;
    private sealed class Rig : IAsyncDisposable
    {
        public Guid FlowId { get; } = Guid.NewGuid();
        public Guid VersionId { get; } = Guid.NewGuid();
        public Guid PromptId { get; } = Guid.NewGuid();
        public Guid RunId { get; } = Guid.NewGuid();
        public FlowGraphDto Graph { get; set; }
        public FlowRunDetailDto Run { get; set; }
        public DefaultHttpContext Http { get; } = new();
        public Access Access { get; } = new();
        public Prompts Prompts { get; } = new();
        public OrchestratorDbContext Db { get; }
        public IFlowDefinitionService Flows { get; }
        public int Mutations;
        private readonly ServiceProvider provider;
        private FlowDefinitionDto Definition => new(FlowId, 17, "AI flow", 1, true, Graph, VersionId, 1, DateTimeOffset.UtcNow);
        private FlowVersionDto Version => new(VersionId, FlowId, 17, 1, Graph, "test-hash", "operator", DateTimeOffset.UtcNow);
        public Rig()
        {
            var template = FlowGraphTemplates.IncidentFromAlert();
            Graph = template with { Nodes = template.Nodes.Select(node => node.Kind == FlowNodeKind.CreateIncident ? node with
            {
                Kind = FlowNodeKind.InvestigateWithAi, ConnectorId = Guid.NewGuid(), ConnectorRevision = 1,
                AiSettings = new() { OptionalPrompts = [new(PromptId, 1)],
                    PinnedPrompts = [new(PromptId, 1, "Guidance", null, "Supplied body is not authoritative.")] }
            } : node).ToArray() };
            var now = DateTimeOffset.UtcNow; var eventId = Guid.NewGuid();
            var aiNode = Graph.Nodes.Single(node => node.Kind == FlowNodeKind.InvestigateWithAi).Id;
            Run = new(new(RunId, FlowId, VersionId, eventId, Guid.NewGuid(), FlowRunStatus.Succeeded, now, now, "execution-completed"),
                new(Guid.NewGuid(), Guid.NewGuid(), "CPU alert", "Original worker", "cpu", "CPU", "Critical", 97, null, now),
                [new(aiNode, "frozen-action-key", FlowActionStatus.Succeeded, 1, 1, "ai-investigation-completed", null)
                {
                    Kind = FlowActionKind.NetclawInvestigation, Name = "AI investigation", AiFailureBehavior = FlowAiFailureBehavior.ContinueWithAlert,
                    AiResult = new(17, RunId, VersionId, aiNode, Guid.NewGuid(), eventId, FlowAiResultStatus.Completed, now, now,
                        "Restricted AI summary", "Restricted AI evidence", "Restricted AI recommendations")
                }]);
            Db = new(new DbContextOptionsBuilder<OrchestratorDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
            Db.Tenants.Add(new Tenant { Id = 17, Name = "Authorized" }); Db.SaveChanges();
            Access.Grant(NetRatelPermissions.FlowRead, 17); Access.Grant(NetRatelPermissions.FlowEdit, 17); Access.Grant(NetRatelPermissions.FlowPublish, 17);
            Http.User = new(new ClaimsIdentity([new("netratel_principal_id", "operator")], "Local"));
            provider = new ServiceCollection().AddSingleton<IPromptLibraryService>(Prompts).BuildServiceProvider(); Http.RequestServices = provider;
            Flows = DispatchProxy.Create<IFlowDefinitionService, InterfaceProxy>();
            ((InterfaceProxy)Flows).Handler = (method, _) => method.Name switch
            {
                nameof(IFlowDefinitionService.ListAsync) => Task.FromResult<IReadOnlyList<FlowDefinitionDto>>([Definition]),
                nameof(IFlowDefinitionService.GetAsync) => Task.FromResult<FlowDefinitionDto?>(Definition),
                nameof(IFlowDefinitionService.GetVersionsAsync) => Task.FromResult<IReadOnlyList<FlowVersionDto>>([Version]),
                nameof(IFlowDefinitionService.GetVersionAsync) => Task.FromResult<FlowVersionDto?>(Version),
                nameof(IFlowDefinitionService.GetRunAsync) or nameof(IFlowDefinitionService.GetRunByIdAsync) => Task.FromResult<FlowRunDetailDto?>(Run),
                _ => Mutation(method.Name)
            };
        }
        private object Mutation(string method) { ++Mutations; throw new InvalidOperationException($"Unexpected mutation: {method}"); }
        public Task<IResult> ReadAsync(string operation) => operation switch
        {
            "list" => FlowEndpoints.ListAsync(17, Http, Access, Db, Flows, default),
            "get" => FlowEndpoints.GetAsync(17, FlowId, Http, Access, Db, Flows, default),
            "versions" => FlowEndpoints.VersionsAsync(17, FlowId, Http, Access, Db, Flows, default),
            "run" => FlowEndpoints.RunAsync(17, FlowId, RunId, Http, Access, Db, Flows, default),
            "run-by-id" => FlowEndpoints.RunByIdAsync(17, RunId, Http, Access, Db, Flows, default),
            _ => FlowEndpoints.VersionByIdAsync(17, VersionId, Http, Access, Db, Flows, default)
        };
        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); await provider.DisposeAsync(); }
    }
    private sealed class Access : IEffectiveAccessService
    {
        private readonly HashSet<(string Permission, int Tenant)> grants = [];
        public List<(ClaimsPrincipal Principal, string Permission, int? Tenant)> Checks { get; } = [];
        public void Grant(string permission, int tenant) => grants.Add((permission, tenant));
        public void Revoke(string permission, int tenant) => grants.Remove((permission, tenant));
        public Task<bool> AuthorizeAsync(ClaimsPrincipal principal, string permission, int? tenantId, CancellationToken cancellationToken = default)
        { Checks.Add((principal, permission, tenantId)); return Task.FromResult(tenantId is { } tenant && grants.Contains((permission, tenant))); }
        public Task<EffectiveAccessSnapshot> GetSnapshotAsync(ClaimsPrincipal principal, int? tenantId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ReconcileBuiltInRolesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    private sealed class Prompts : IPromptLibraryService
    {
        public bool Available = true;
        public List<(int Tenant, AiPromptReference Reference, bool AllowArchived)> Resolutions { get; } = [];
        public Task<AiPromptSnapshot?> ResolveDefaultAsync(int tenantId, CancellationToken ct = default) => Task.FromResult<AiPromptSnapshot?>(null);
        public Task<AiPromptSnapshot> ResolveAsync(int tenantId, AiPromptReference reference, bool allowArchived = false, CancellationToken ct = default)
        {
            Resolutions.Add((tenantId, reference, allowArchived));
            return Available ? Task.FromResult(new AiPromptSnapshot(reference.PromptId, reference.Revision, "Guidance", null, "Authorized body")) :
                Task.FromException<AiPromptSnapshot>(new KeyNotFoundException());
        }
    }
    public class InterfaceProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Handler { get; set; } = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!, args);
    }
}
