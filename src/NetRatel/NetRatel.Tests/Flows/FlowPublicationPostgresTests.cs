using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NetRatel.Application.Flows;
using NetRatel.Infrastructure.Flows;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using NetRatel.Shared.Contracts.Flows;
using NetRatel.Tests.Infrastructure;
using Xunit;

namespace NetRatel.Tests.Flows;

[Collection(PostgreSqlPersistenceCollection.Name)]
public sealed class FlowPublicationPostgresTests(PostgreSqlPersistenceFixture postgres)
{
    private const string Root = "/api/v1/tenants/17/flows";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData("request-script", false)]
    [InlineData("script-request", true)]
    [InlineData("legacy-request", true)]
    public async Task Http_publication_checks_native_source_capability_without_reinterpreting_legacy_filters(string mode, bool supported)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var ct = deadline.Token;
        var grants = new PublicationAuthority(scriptSource: true);
        using var host = await FlowEndpointTests.HostAsync(await postgres.CreateDatabaseAsync(ct), grants, grants);
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new("Test", "all");
        client.DefaultRequestHeaders.Add("X-NetRatel-Flow-Schema", "2");
        var createdResponse = await client.PostAsJsonAsync(Root, new FlowCreateRequest("Synthetic native source capability"), ct);
        Assert.Equal(HttpStatusCode.OK, createdResponse.StatusCode);
        var created = (await createdResponse.Content.ReadFromJsonAsync<FlowDefinitionDto>(ct))!;
        var graph = TypedGraph();
        var subscription = new FlowExecutionTriggerSettingsDto(
            mode == "script-request" ? [FlowExecutionSourceKind.Script, FlowExecutionSourceKind.Request] : [FlowExecutionSourceKind.Request],
            [FlowExecutionOutcome.Failed], FlowExecutionScope.SelectedDefinitions)
        {
            DefinitionReferences = mode == "legacy-request" ? null : [new(FlowExecutionSourceKind.Script, "42")],
            DefinitionIds = mode == "legacy-request" ? ["42"] : null
        };
        graph = graph with { Nodes = graph.Nodes.Select(node => node.Kind == FlowNodeKind.ExecutionEvent
            ? node with { ExecutionTrigger = subscription } : node).ToArray() };
        // The generic contract remains useful for pure simulation and future producer capabilities.
        Assert.True(FlowGraphV2Validator.ValidateComplete(graph).Valid);
        var savedResponse = await client.PutAsJsonAsync($"{Root}/{created.Id}/draft",
            new FlowSaveDraftRequest(created.Revision, created.Name, graph), ct);
        Assert.Equal(HttpStatusCode.OK, savedResponse.StatusCode);
        var saved = (await savedResponse.Content.ReadFromJsonAsync<FlowDefinitionDto>(ct))!;
        var publication = await client.PostAsJsonAsync($"{Root}/{created.Id}/publish",
            new FlowRevisionRequest(saved.Revision), ct);
        Assert.Equal(supported ? HttpStatusCode.OK : HttpStatusCode.BadRequest, publication.StatusCode);
        if (supported)
        {
            var version = (await publication.Content.ReadFromJsonAsync<FlowVersionDto>(ct))!;
            Assert.Equal(FlowJsonCanonical.Serialize(subscription),
                FlowJsonCanonical.Serialize(version.Graph.Nodes.Single(node => node.Kind == FlowNodeKind.ExecutionEvent).ExecutionTrigger));
        }
        else
        {
            using var error = JsonDocument.Parse(await publication.Content.ReadAsStringAsync(ct));
            Assert.Equal("execution-trigger-source-unavailable", error.RootElement.GetProperty("code").GetString());
        }
        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        Assert.Equal(supported ? 1 : 0, await db.FlowVersions.CountAsync(ct));
        Assert.Equal(0, await db.FlowRuns.CountAsync(ct));
        Assert.Equal(0, await db.FlowActions.CountAsync(ct));
        Assert.Equal(0, await db.JobRuns.CountAsync(ct));
    }

    [Fact]
    public async Task Http_published_typed_graph_keeps_one_hash_through_jsonb_reload_pure_execution_and_recorded_API()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var ct = deadline.Token;
        var grants = new PublicationAuthority();
        using var host = await FlowEndpointTests.HostAsync(await postgres.CreateDatabaseAsync(ct), grants, grants);
        // This regression explicitly drives exact native IDs through the real ingress.
        // The source delivery background loop is stopped before any subscription exists.
        foreach (var worker in host.Services.GetServices<IHostedService>().OfType<ExecutionLifecycleSubscriptionWorker>())
            await worker.StopAsync(ct);
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new("Test", "all");
        client.DefaultRequestHeaders.Add("X-NetRatel-Flow-Schema", "2");
        var capabilities = (await client.GetFromJsonAsync<FlowSchemaCapabilitiesDto>(Root + "/schema-capabilities", ct))!;
        Assert.Contains(2, capabilities.ExecutableSchemas);
        var createdResponse = await client.PostAsJsonAsync(Root, new FlowCreateRequest("Synthetic canonical publication"), ct);
        Assert.Equal(HttpStatusCode.OK, createdResponse.StatusCode);
        var created = (await createdResponse.Content.ReadFromJsonAsync<FlowDefinitionDto>(ct))!;
        var graph = TypedGraph();
        Assert.True(FlowGraphV2Validator.ValidateComplete(graph).Valid);
        var originalRepresentation = JsonSerializer.Serialize(graph, Json);
        var expectedHash = FlowGraphSerialization.Hash(graph);
        var saveResponse = await client.PutAsJsonAsync($"{Root}/{created.Id}/draft",
            new FlowSaveDraftRequest(created.Revision, created.Name, graph), ct);
        Assert.Equal(HttpStatusCode.OK, saveResponse.StatusCode);
        var saved = (await saveResponse.Content.ReadFromJsonAsync<FlowDefinitionDto>(ct))!;
        var publication = await client.PostAsJsonAsync($"{Root}/{created.Id}/publish",
            new FlowRevisionRequest(saved.Revision), ct);
        Assert.True(publication.StatusCode == HttpStatusCode.OK, await publication.Content.ReadAsStringAsync(ct));
        var published = (await publication.Content.ReadFromJsonAsync<FlowVersionDto>(ct))!;
        Assert.Equal(expectedHash, published.ConfigurationHash);
        Assert.Equal(expectedHash, FlowGraphSerialization.Hash(published.Graph));
        var configured = (await client.GetFromJsonAsync<FlowDefinitionDto>($"{Root}/{created.Id}", ct))!;
        if (!configured.Enabled)
        {
            var enabled = await client.PutAsJsonAsync($"{Root}/{created.Id}/enabled",
                new FlowEnabledRequest(configured.Revision, true), ct);
            Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);
        }

        var reloaded = (await client.GetFromJsonAsync<FlowVersionDto>($"{Root}/versions/{published.Id}", ct))!;
        Assert.Equal(expectedHash, reloaded.ConfigurationHash);
        Assert.Equal(expectedHash, FlowGraphSerialization.Hash(reloaded.Graph));
        // PostgreSQL normalizes key order and numeric spellings. A raw DTO hash would diverge.
        Assert.NotEqual(originalRepresentation, JsonSerializer.Serialize(reloaded.Graph, Json));
        Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(graph, Json),
            JsonSerializer.SerializeToElement(reloaded.Graph, Json)));

        FlowExecutionEventDto fact;
        FlowExecutionSubscriptionDeliveryDto delivery;
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            var requests = new RequestService(db, scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>());
            var request = await requests.CreateAsync(new("synthetic-gateway", "", "4242", "{}", 17, Guid.NewGuid()), ct);
            Assert.NotNull(await requests.UpdateAsync(new(request.Id, null, null, null, null, "Failed",
                "Synthetic native admission failure before a job existed.", null, null, null), ct));
            var requestId = request.Id.ToString(CultureInfo.InvariantCulture);
            var source = await db.OutboxMessages.AsNoTracking().SingleAsync(row =>
                row.Type == FlowExecutionEventLimits.OutboxType && row.EntityId == requestId, ct);
            fact = JsonSerializer.Deserialize(source.PayloadJson,
                FlowExecutionEventJsonContext.Default.FlowExecutionEventDto)!;
            var intent = await db.OutboxMessages.AsNoTracking().SingleAsync(row =>
                row.Type == FlowExecutionSubscriptionLimits.OutboxType, ct);
            delivery = ExecutionLifecycleSubscriptions.ReadDelivery(intent.PayloadJson)!;
            Assert.Equal(published.Id, delivery.FlowVersionId);
            Assert.Equal(expectedHash, delivery.ConfigurationHash);
            Assert.Equal(fact.EventId, delivery.SourceEventId);
            Assert.Equal(0, await db.FlowRuns.CountAsync(ct));
        }

        var previewResponse = await client.PostAsJsonAsync(Root + "/dry-run-execution",
            new FlowExecutionDryRunRequest(reloaded.Graph, fact), ct);
        Assert.Equal(HttpStatusCode.OK, previewResponse.StatusCode);
        var preview = (await previewResponse.Content.ReadFromJsonAsync<FlowDryRunResultDto>(ct))!;
        Assert.True(preview.Valid, preview.Code);
        Assert.Equal("inert-preview", preview.Code);
        Assert.Equal(expectedHash, preview.Trace!.ConfigurationHash);
        Assert.Equal(FlowTraceProvenance.Simulated, preview.Trace.Provenance);
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            Assert.Equal(0, await db.FlowRuns.CountAsync(ct));
            Assert.Equal(0, await db.FlowActions.CountAsync(ct));
            Assert.Equal(0, await db.JobRuns.CountAsync(ct));
            Assert.Equal(1, await db.FlowVersions.CountAsync(ct));
        }

        var admitted = await host.Services.GetRequiredService<IFlowExecutionEventIngress>().EnqueueExecutionAsync(
            new(17, published.Id, fact.EventId, [fact.EventId], delivery.DeliveryId), ct);
        Assert.Equal(FlowIngressDisposition.Enqueued, admitted.Disposition);
        var store = host.Services.GetRequiredService<IFlowExecutionStore>();
        var lease = await store.ClaimAsync(Guid.NewGuid(), ct);
        Assert.NotNull(lease);
        Assert.Equal(expectedHash, lease.Version.ConfigurationHash);
        var actionCalls = 0;
        var result = await host.Services.GetRequiredService<FlowGraphV2Runtime>().ExecuteAsync(lease,
            (_, _) => { Interlocked.Increment(ref actionCalls); throw new InvalidOperationException("Pure publication dispatched an external action."); }, ct);
        Assert.Equal(FlowRunStatus.Succeeded, result.Status);
        Assert.Equal("flow-finished", result.Code);
        Assert.True(await store.CompleteRunAsync(lease, result, ct));
        Assert.Equal(0, actionCalls);

        var recorded = (await client.GetFromJsonAsync<FlowRunDetailDto>($"{Root}/runs/{lease.RunId}", ct))!;
        Assert.Equal(published.Id, recorded.Run.FlowVersionId);
        Assert.Equal(FlowRunStatus.Succeeded, recorded.Run.Status);
        Assert.NotNull(recorded.Trace);
        Assert.Equal(expectedHash, recorded.Trace.ConfigurationHash);
        Assert.Equal(FlowTraceProvenance.Actual, recorded.Trace.Provenance);
        Assert.Null(recorded.DataUnavailableReason);
        var map = Assert.Single(recorded.Trace.Steps, step => step.NodeId == graph.Nodes[1].Id);
        var inputs = map.Input.Value!.Value;
        Assert.Equal("9007199254740993", inputs.GetProperty("zzzz").GetProperty("id").GetString());
        Assert.Equal(10m, inputs.GetProperty("zzzz").GetProperty("zz").GetDecimal());
        Assert.Equal(2m, inputs.GetProperty("zzzz").GetProperty("a").GetProperty("zz").GetDecimal());
        Assert.Equal(JsonValueKind.Null, inputs.GetProperty("zzzz").GetProperty("a").GetProperty("a").ValueKind);
        Assert.Equal(FlowNodeCatalogue.True, recorded.Trace.Steps.Single(step => step.NodeId == graph.Nodes[2].Id).SelectedPort);
        Assert.Equal("Synthetic canonical publication: unchanged text", map.Output.Value!.Value.GetProperty("title").GetString());

        var current = (await client.GetFromJsonAsync<FlowDefinitionDto>($"{Root}/{created.Id}", ct))!;
        var edited = graph with { Nodes = graph.Nodes.Select(node => node.Id == graph.Nodes[1].Id
            ? node with { Mapping = new("Edited current draft", "Current draft does not rewrite recorded history.", 1) } : node).ToArray() };
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"{Root}/{created.Id}/draft",
            new FlowSaveDraftRequest(current.Revision, current.Name, edited), ct)).StatusCode);
        var historical = (await client.GetFromJsonAsync<FlowVersionDto>($"{Root}/versions/{published.Id}", ct))!;
        var replay = (await client.GetFromJsonAsync<FlowRunDetailDto>($"{Root}/runs/{lease.RunId}", ct))!;
        Assert.Equal(expectedHash, FlowGraphSerialization.Hash(historical.Graph));
        Assert.Equal(expectedHash, historical.ConfigurationHash);
        Assert.Equal(expectedHash, replay.Trace!.ConfigurationHash);
        Assert.Equal("Synthetic canonical publication: unchanged text",
            replay.Trace.Steps.Single(step => step.NodeId == graph.Nodes[1].Id).Output.Value!.Value.GetProperty("title").GetString());
        await using var finalScope = host.Services.CreateAsyncScope();
        var final = finalScope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        Assert.Equal(1, await final.FlowVersions.CountAsync(ct));
        Assert.Equal(0, await final.FlowActions.CountAsync(ct));
        Assert.Equal(0, await final.JobRuns.CountAsync(ct));
        Assert.Equal(0, grants.MonitoringReads);
    }

    private static FlowGraphDto TypedGraph()
    {
        using var nested = JsonDocument.Parse("""{"zz":1e1,"a":{"zz":2.00,"a":null},"id":"9007199254740993"}""");
        var trigger = new FlowNodeDto(Guid.NewGuid(), FlowNodeKind.ExecutionEvent, "Synthetic request failure", new(0, 0))
        { ExecutionTrigger = new([FlowExecutionSourceKind.Request], [FlowExecutionOutcome.Failed], FlowExecutionScope.AllInTenant) };
        var map = new FlowNodeDto(Guid.NewGuid(), FlowNodeKind.MapIncident, "Preserve typed inputs", new(300, 0),
            Mapping: new("Synthetic canonical publication: {input.a}", "{input.zzzz}", 1))
        {
            Inputs = new Dictionary<string, FlowInputBindingDto>
            {
                ["zzzz"] = new(FlowInputSource.Literal, FlowDataType.Json, Literal: nested.RootElement.Clone()),
                ["a"] = new(FlowInputSource.Literal, FlowDataType.Text, Literal: JsonSerializer.SerializeToElement("unchanged text"))
            }
        };
        var condition = new FlowNodeDto(Guid.NewGuid(), FlowNodeKind.Condition, "Actual mapped priority", new(600, 0))
        { Predicate = new(new(FlowInputSource.NodeOutput, FlowDataType.Number, "priority", map.Id),
            FlowComparison.Equal, JsonSerializer.SerializeToElement(1)) };
        var success = new FlowNodeDto(Guid.NewGuid(), FlowNodeKind.Finish, "Chosen successful finish", new(900, 0));
        var other = new FlowNodeDto(Guid.NewGuid(), FlowNodeKind.Finish, "Unchosen finish", new(900, 300));
        FlowEdgeDto Edge(Guid source, string port, Guid target) => new(source, port, target, FlowNodeCatalogue.Input) { Id = Guid.NewGuid() };
        return new(2, [trigger, map, condition, success, other],
            [Edge(trigger.Id, FlowNodeCatalogue.Event, map.Id), Edge(map.Id, FlowNodeCatalogue.Result, condition.Id),
                Edge(condition.Id, FlowNodeCatalogue.True, success.Id), Edge(condition.Id, FlowNodeCatalogue.False, other.Id)], new());
    }

    // The owning test is about publication/hash persistence. Exact synthetic tenant/principal grants
    // are the explicit authority seam; source/admission security has separate real-permission coverage.
    private sealed class PublicationAuthority(bool scriptSource = false) : IEffectiveAccessService, IFlowExecutionAuthorityVerifier
    {
        public int MonitoringReads;
        public Task<bool> AuthorizeAsync(int tenantId, FlowExecutionAuthorityDto authority, CancellationToken cancellationToken = default) =>
            Task.FromResult(tenantId == 17 && authority == FlowTestData.Authority);
        public Task<bool> AuthorizeAsync(ClaimsPrincipal principal, string permission, int? tenantId, CancellationToken cancellationToken = default)
        {
            if (permission == NetRatelPermissions.MonitoringRead) Interlocked.Increment(ref MonitoringReads);
            return Task.FromResult(tenantId == 17 && principal.FindFirstValue("netratel_principal_id") == FlowTestData.Authority.PrincipalId &&
                (permission is NetRatelPermissions.FlowRead or NetRatelPermissions.FlowEdit or NetRatelPermissions.FlowPublish or
                    NetRatelPermissions.FlowExecute or NetRatelPermissions.JobManagement ||
                    scriptSource && permission == NetRatelPermissions.ScriptExecute));
        }
        public Task<int[]?> GetAuthorizedTenantIdsAsync(ClaimsPrincipal principal, string permission, CancellationToken cancellationToken = default) =>
            Task.FromResult<int[]?>([17]);
        public Task<EffectiveAccessSnapshot> GetSnapshotAsync(ClaimsPrincipal principal, int? tenantId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task ReconcileBuiltInRolesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
