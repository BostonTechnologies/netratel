using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Http;
using System.Security.Claims;
using NetRatel.API.Endpoints.Client;
using NetRatel.Shared.Operations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.Application.Flows;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.Flows;
using Xunit;

namespace NetRatel.Tests.Flows;

public sealed partial class FlowEndpointTests
{
    [Fact]
    public async Task Explicit_schema_two_drafts_roundtrip_null_and_clone_every_typed_identity_without_effects()
    {
        using var host = await HostAsync(); var http = host.GetTestClient();
        http.DefaultRequestHeaders.Authorization = new("Test", "all");
        var capabilities = (await http.GetFromJsonAsync<FlowSchemaCapabilitiesDto>(Path + "/schema-capabilities"))!;
        capabilities.SupportedDraftSchemas.Should().Equal(1, 2);
        capabilities.ExecutableSchemas.Except(capabilities.SupportedDraftSchemas).Should().BeEmpty();
        var flow = (await (await http.PostAsJsonAsync(Path, new FlowCreateRequest("Synthetic typed draft")))
            .Content.ReadFromJsonAsync<FlowDefinitionDto>())!;
        var graph = TypedDraft();
        http.DefaultRequestHeaders.Add("X-NetRatel-Flow-Schema", "2");
        var response = await http.PutAsJsonAsync($"{Path}/{flow.Id}/draft", new FlowSaveDraftRequest(flow.Revision, flow.Name, graph));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var saved = (await response.Content.ReadFromJsonAsync<FlowDefinitionDto>())!;
        var mapping = saved.Draft.Nodes.Single(node => node.Kind == FlowNodeKind.MapIncident);
        mapping.Inputs!["nullable"].Literal!.Value.ValueKind.Should().Be(JsonValueKind.Null);
        saved.Draft.Nodes.Single(node => node.Kind == FlowNodeKind.RunLibraryScript).ExecutionAction!.PinnedSnapshotReference.Should().BeNull();
        var validation = await (await http.PostAsJsonAsync(Path + "/validate", saved.Draft)).Content.ReadFromJsonAsync<FlowValidationResultDto>();
        validation!.Valid.Should().BeTrue();
        var clone = (await (await http.PostAsJsonAsync($"{Path}/{flow.Id}/clone", new FlowCloneRequest(saved.Revision, "Synthetic cloned draft")))
            .Content.ReadFromJsonAsync<FlowDefinitionDto>())!;
        clone.Enabled.Should().BeFalse(); clone.PublishedVersionId.Should().BeNull();
        clone.Draft.SchemaVersion.Should().Be(2);
        clone.Draft.Viewport.Should().Be(graph.Viewport);
        clone.Draft.Nodes.Select(node => node.Id).Intersect(saved.Draft.Nodes.Select(node => node.Id)).Should().BeEmpty();
        clone.Draft.Edges.Select(edge => edge.Id).Intersect(saved.Draft.Edges.Select(edge => edge.Id)).Should().BeEmpty();
        var clonedMap = clone.Draft.Nodes.Single(node => node.Kind == FlowNodeKind.MapIncident);
        clone.Draft.Nodes.Single(node => node.Kind == FlowNodeKind.Condition).Predicate!.Input.NodeId.Should().Be(clonedMap.Id);
        clone.Draft.Nodes.Single(node => node.Kind == FlowNodeKind.RunLibraryScript).Inputs!["message"].NodeId.Should().Be(clonedMap.Id);
        clone.Draft.Edges[0].Route!.Points.Should().Equal(saved.Draft.Edges[0].Route!.Points);
        (await http.PutAsJsonAsync($"{Path}/{flow.Id}/draft",
            new FlowSaveDraftRequest(saved.Revision, saved.Name, FlowTestData.Graph()))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        host.Services.GetRequiredService<ExecutionSentinel>().Calls.Should().Be(0);
        await using var scope = host.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        (await db.FlowRuns.CountAsync()).Should().Be(0); (await db.FlowVersions.CountAsync()).Should().Be(0); (await db.FlowActions.CountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Source_and_trace_only_AI_evidence_are_projected_before_the_no_action_fast_path(bool traceOnlyAi)
    {
        using var host = await HostAsync(); var http = host.GetTestClient();
        var (flowId, runId) = await SeedSourceHistoryAsync(host.Services, traceOnlyAi);
        http.DefaultRequestHeaders.Authorization = new("Test", "read");
        var hidden = (await http.GetFromJsonAsync<FlowRunDetailDto>($"{Path}/{flowId}/runs/{runId}"))!;
        hidden.Input.Should().BeNull(); hidden.ExecutionEvent.Should().BeNull(); hidden.Trace.Should().BeNull();
        hidden.DataUnavailableReason.Should().Be("source-read-denied");
        http.DefaultRequestHeaders.Authorization = new("Test", "source");
        var source = (await http.GetFromJsonAsync<FlowRunDetailDto>($"{Path}/{flowId}/runs/{runId}"))!;
        source.Input.Should().BeNull(); source.ExecutionEvent!.ExecutionId.Should().Be("9007199254740993");
        if (traceOnlyAi)
        { source.Trace.Should().BeNull(); source.DataUnavailableReason.Should().Be("ai-results-read-denied"); }
        else
        { source.Trace.Should().NotBeNull(); source.DataUnavailableReason.Should().BeNull(); }
        http.DefaultRequestHeaders.Authorization = new("Test", "all");
        var visible = (await http.GetFromJsonAsync<FlowRunDetailDto>($"{Path}/{flowId}/runs/{runId}"))!;
        visible.Trace.Should().NotBeNull(); visible.ExecutionEvent!.SourceKind.Should().Be(FlowExecutionSourceKind.Request);
        visible.Actions.Should().BeEmpty();
        host.Services.GetRequiredService<ExecutionSentinel>().Calls.Should().Be(0);
    }

    [Theory]
    [InlineData("publish")]
    [InlineData("set_enabled")]
    [InlineData("clone")]
    public async Task Legacy_managed_graphless_mutations_reject_a_schema_two_draft(string operation)
    {
        using var host = await HostAsync(); var client = host.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new("Test", "all");
        var flow = (await (await client.PostAsJsonAsync(Path, new FlowCreateRequest("Synthetic MCP boundary")))
            .Content.ReadFromJsonAsync<FlowDefinitionDto>())!;
        var saved = (await (await client.PutAsJsonAsync($"{Path}/{flow.Id}/draft",
            new FlowSaveDraftRequest(flow.Revision, flow.Name, TypedDraft()))).Content.ReadFromJsonAsync<FlowDefinitionDto>())!;
        await using var scope = host.Services.CreateAsyncScope();
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider,
            User = new(new ClaimsIdentity([new("mode", "all"), new("netratel_principal_id", FlowTestData.Authority.PrincipalId)], "Test")) };
        var data = operation switch
        {
            "clone" => JsonSerializer.SerializeToElement(new FlowCloneRequest(saved.Revision, "Unchanged clone")),
            "set_enabled" => JsonSerializer.SerializeToElement(new FlowEnabledRequest(saved.Revision, false)),
            _ => JsonSerializer.SerializeToElement(new FlowRevisionRequest(saved.Revision))
        };
        var request = JsonSerializer.SerializeToElement(new { tenantId = 17, flowId = flow.Id, data });
        var result = await ManagedOperationEndpoints.DispatchAsync(ManagedOperationCatalog.Find("netratel_flows", operation)!, request, context, default);
        Assert.Equal(StatusCodes.Status400BadRequest, Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode);
        host.Services.GetRequiredService<ExecutionSentinel>().Calls.Should().Be(0);
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        (await db.FlowVersions.CountAsync()).Should().Be(0);
        (await db.FlowDefinitions.CountAsync()).Should().Be(1);
        (await db.FlowDefinitions.SingleAsync()).Enabled.Should().BeTrue();
    }

    [Fact]
    public async Task Large_schema_two_save_requires_explicit_body_budget_opt_in_and_remains_inert()
    {
        using var host = await HostAsync(); var http = host.GetTestClient();
        http.DefaultRequestHeaders.Authorization = new("Test", "all");
        var flow = (await (await http.PostAsJsonAsync(Path, new FlowCreateRequest("Synthetic large graph")))
            .Content.ReadFromJsonAsync<FlowDefinitionDto>())!;
        var trigger = new FlowNodeDto(Guid.NewGuid(), FlowNodeKind.AlertRaised, "Alert", new(0, 0));
        var maps = Enumerable.Range(1, 20).Select(index => new FlowNodeDto(Guid.NewGuid(), FlowNodeKind.MapIncident,
            "Map " + index, new(index * 100, 0), Mapping: new("Synthetic title", new string('x', 2500)))).ToArray();
        var finish = new FlowNodeDto(Guid.NewGuid(), FlowNodeKind.Finish, "Finish", new(3000, 0));
        var nodes = new[] { trigger }.Concat(maps).Append(finish).ToArray();
        var graph = new FlowGraphDto(2, nodes, nodes.Take(nodes.Length - 1).Select((node, index) =>
            new FlowEdgeDto(node.Id, index == 0 ? FlowNodeCatalogue.Event : FlowNodeCatalogue.Result,
                nodes[index + 1].Id, FlowNodeCatalogue.Input) { Id = Guid.NewGuid() }).ToArray(), new());
        var request = new FlowSaveDraftRequest(flow.Revision, flow.Name, graph);
        (await http.PutAsJsonAsync($"{Path}/{flow.Id}/draft", request)).StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        http.DefaultRequestHeaders.Add("X-NetRatel-Flow-Schema", "2");
        var response = await http.PutAsJsonAsync($"{Path}/{flow.Id}/draft", request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<FlowDefinitionDto>())!.Draft.Nodes.Should().HaveCount(22);
        host.Services.GetRequiredService<ExecutionSentinel>().Calls.Should().Be(0);
    }

    private static FlowGraphDto TypedDraft()
    {
        var original = FlowNodeTestData.Graph(); var trigger = original.Nodes[0]; var finish = original.Nodes[2];
        var map = new FlowNodeDto(Guid.NewGuid(), FlowNodeKind.MapIncident, "Mapped values", new(150, 0),
            Mapping: new("Synthetic mapped title", "Synthetic mapped description", 1))
        { Inputs = new Dictionary<string, FlowInputBindingDto> { ["nullable"] =
            new(FlowInputSource.Literal, FlowDataType.Json, Literal: JsonSerializer.SerializeToElement((object?)null)) } };
        var condition = new FlowNodeDto(Guid.NewGuid(), FlowNodeKind.Condition, "Priority one", new(300, 0))
        { Predicate = new(new(FlowInputSource.NodeOutput, FlowDataType.Number, "priority", map.Id),
            FlowComparison.Equal, JsonSerializer.SerializeToElement(1)) };
        var script = original.Nodes[1] with
        { Inputs = new Dictionary<string, FlowInputBindingDto> { ["message"] = new(FlowInputSource.NodeOutput, FlowDataType.Text, "description", map.Id) },
            ExecutionAction = original.Nodes[1].ExecutionAction! with { Parameters = [new("message", FlowDataType.Text, true)] } };
        var edges = new List<FlowEdgeDto>
        {
            new(trigger.Id, FlowNodeCatalogue.Event, map.Id, FlowNodeCatalogue.Input) { Id = Guid.NewGuid(), Route = new([new(10, 20), new(30, 40)]) },
            new(map.Id, FlowNodeCatalogue.Result, condition.Id, FlowNodeCatalogue.Input) { Id = Guid.NewGuid() },
            new(condition.Id, FlowNodeCatalogue.True, script.Id, FlowNodeCatalogue.Input) { Id = Guid.NewGuid() },
            new(condition.Id, FlowNodeCatalogue.False, finish.Id, FlowNodeCatalogue.Input) { Id = Guid.NewGuid() }
        };
        edges.AddRange(FlowNodeCatalogue.Find(script.Kind)!.Ports.Where(port => port.Direction == FlowPortDirection.Output)
            .Select(port => new FlowEdgeDto(script.Id, port.Id, finish.Id, FlowNodeCatalogue.Input) { Id = Guid.NewGuid() }));
        return new(2, [trigger, map, condition, script, finish], edges, new(1.25, 10, 20));
    }

    private static async Task<(Guid FlowId, Guid RunId)> SeedSourceHistoryAsync(IServiceProvider services, bool ai)
    {
        await using var scope = services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var now = DateTimeOffset.UtcNow; var flow = Guid.NewGuid(); var version = Guid.NewGuid(); var run = Guid.NewGuid();
        var trigger = new FlowNodeDto(Guid.NewGuid(), FlowNodeKind.ExecutionEvent, "Request event", new(0, 0))
        { ExecutionTrigger = new([FlowExecutionSourceKind.Request], [FlowExecutionOutcome.Failed], FlowExecutionScope.AllInTenant) };
        var finish = new FlowNodeDto(Guid.NewGuid(), FlowNodeKind.Finish, "Finish", new(600, 0));
        var investigation = new FlowNodeDto(Guid.NewGuid(), FlowNodeKind.InvestigateWithAi, "Findings", new(300, 0), ConnectorId: Guid.NewGuid(), ConnectorRevision: 1);
        var nodes = ai ? new[] { trigger, investigation, finish } : new[] { trigger, finish };
        var edges = new List<FlowEdgeDto> { new(trigger.Id, FlowNodeCatalogue.Event, ai ? investigation.Id : finish.Id, FlowNodeCatalogue.Input) { Id = Guid.NewGuid() } };
        if (ai) edges.AddRange(FlowNodeCatalogue.Find(investigation.Kind)!.Ports.Where(port => port.Direction == FlowPortDirection.Output)
            .Select(port => new FlowEdgeDto(investigation.Id, port.Id, finish.Id, FlowNodeCatalogue.Input) { Id = Guid.NewGuid() }));
        var graph = new FlowGraphDto(2, nodes, edges, new());
        var json = JsonSerializer.Serialize(graph, FlowNodeTestData.Json);
        var hash = FlowContractValidation.Hash(Encoding.UTF8.GetBytes(json));
        var source = new FlowExecutionEventDto(Guid.NewGuid(), Guid.NewGuid(), 17, FlowExecutionSourceKind.Request,
            new("synthetic", "Synthetic admission"), "9007199254740993", "9007199254740993", new(Guid.NewGuid(), "Synthetic endpoint"),
            FlowExecutionOutcome.Failed, "Failed", now, now)
        { Provenance = FlowExecutionEventProvenance.RequestStateCommit, Reason = "Synthetic source failure", CompletedAtUtc = now };
        var envelope = new FlowEventEnvelope(17, source.EventId, source.OccurrenceId, version, now, null, FlowTestData.Authority)
        { ExecutionEvent = source, SourceAdmissionKey = Guid.NewGuid(), SourceDeliveryId = Guid.NewGuid(), SourceEventIds = [source.EventId] };
        var value = JsonSerializer.SerializeToElement(ai ? new { summary = "Synthetic sensitive findings" } : new { summary = "Synthetic source facts" });
        var active = ai ? investigation : trigger;
        var snapshot = new FlowTraceSnapshotDto(FlowTraceDataState.Present) { Value = value };
        var trace = new FlowTraceDto(2, hash, FlowTraceProvenance.Actual,
            [new(1, active.Id, active.Name, FlowTraceStepStatus.Completed, "recorded", FlowTraceProvenance.Actual, snapshot, snapshot)]);
        var checkpoint = new FlowNodeCheckpointDto(active.Id, 1, 1, FlowNodeProgress.Completed, "synthetic", now, now.AddHours(1)) { Trace = trace };
        var eventJson = JsonSerializer.Serialize(envelope, FlowNodeTestData.Json);
        db.FlowDefinitions.Add(new() { Id = flow, TenantId = 17, Name = "Synthetic source history", Revision = 1,
            Enabled = false, DraftJson = json, CreatedAtUtc = now, UpdatedAtUtc = now });
        db.FlowVersions.Add(new() { Id = version, FlowId = flow, TenantId = 17, VersionNumber = 1, GraphJson = json,
            ConfigurationHash = hash, PublishedBy = FlowTestData.Authority.PrincipalId, PublishedAtUtc = now });
        db.FlowRuns.Add(new() { Id = run, TenantId = 17, FlowId = flow, FlowVersionId = version, EventId = envelope.SourceAdmissionKey!.Value,
            OccurrenceId = source.OccurrenceId, EventJson = eventJson, EventFingerprint = FlowContractValidation.Hash(Encoding.UTF8.GetBytes(eventJson)),
            Status = FlowRunStatus.Failed, CreatedAtUtc = now, CompletedAtUtc = now,
            ContinuationJson = JsonSerializer.Serialize(new FlowContinuationDto(active.Id, null) { Node = checkpoint }, FlowNodeTestData.Json) });
        await db.SaveChangesAsync(); return (flow, run);
    }
}
