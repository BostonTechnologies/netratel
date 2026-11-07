using System.Net;
using System.Collections.Concurrent;
using System.Text.Json;
using NetRatel.Application.Flows;
using NetRatel.Shared.Contracts.Flows;
using NetRatel.Web.Services.Flows;

namespace NetRatel.Web.PlaywrightTests;

internal sealed class FixtureFlowApiService : IFlowApiService
{
    private readonly Dictionary<Guid, FlowSummaryDto> _deleted = [];
    private readonly ConcurrentQueue<FlowQueryDto> _managementQueries = new();
    public IReadOnlyList<FlowQueryDto> ManagementQueries => _managementQueries.ToArray();
    public void SeedManagementList()
    {
        _definitions.Clear();
        for (var i = 0; i < 61; i++)
        {
            var id = Guid.NewGuid();
            _definitions[id] = new(id, _tenantId, i == 0 ? "A synthetic flow with a deliberately long readable name " + new string('x', 70) : $"Synthetic flow {i:D3}",
                1, i % 2 != 0, FlowGraphTemplates.IncidentFromAlert(), null, 0, DateTimeOffset.UtcNow.AddMinutes(-i));
        }
    }
    private static FlowSummaryDto Summary(FlowDefinitionDto row) => new(row.Id, row.TenantId, row.Name, row.Revision,
        row.Enabled, row.PublishedVersionId, row.PublishedVersionNumber, row.UpdatedAtUtc, row.LatestRun);
    public async Task<FlowPageDto> QueryAsync(int tenantId, FlowQueryDto query, CancellationToken token = default)
    {
        _managementQueries.Enqueue(query);
        IEnumerable<FlowSummaryDto> rows = (await ListAsync(tenantId, token)).Select(Summary)
            .Where(row => string.IsNullOrWhiteSpace(query.Search) || row.Name.Contains(query.Search, StringComparison.OrdinalIgnoreCase))
            .Where(row => query.Enabled == FlowEnabledFilter.All || row.Enabled == (query.Enabled == FlowEnabledFilter.Enabled))
            .Where(row => query.Publication == FlowPublicationFilter.All || (row.PublishedVersionId is not null) == (query.Publication == FlowPublicationFilter.Published));
        rows = query.Sort switch {
            FlowSort.UpdatedAt => query.Descending ? rows.OrderByDescending(row => row.UpdatedAtUtc).ThenBy(row => row.Id) : rows.OrderBy(row => row.UpdatedAtUtc).ThenBy(row => row.Id),
            FlowSort.LatestRunAt => query.Descending ? rows.OrderBy(row => row.LatestRun is null).ThenByDescending(row => row.LatestRun?.CreatedAtUtc).ThenBy(row => row.Id) : rows.OrderBy(row => row.LatestRun is null).ThenBy(row => row.LatestRun?.CreatedAtUtc).ThenBy(row => row.Id),
            _ => query.Descending ? rows.OrderByDescending(row => row.Name).ThenBy(row => row.Id) : rows.OrderBy(row => row.Name).ThenBy(row => row.Id)
        };
        var items = rows.ToArray();
        return new(items.Skip(query.PageIndex * query.PageSize).Take(query.PageSize).ToArray(), items.Length, query.PageIndex, query.PageSize);
    }
    public Task<FlowSummaryDto> GetMetadataAsync(int tenantId, Guid flowId, CancellationToken token = default) =>
        Task.FromResult(_deleted.TryGetValue(flowId, out var deleted) && deleted.TenantId == tenantId ? deleted : Summary(Find(tenantId, flowId)));
    public Task<FlowDeleteResultDto> DeleteAsync(int tenantId, Guid flowId, FlowDeleteRequest request, CancellationToken token = default)
    {
        var flow = Find(tenantId, flowId);
        if (flow.Revision != request.ExpectedRevision) throw new FlowApiException(HttpStatusCode.Conflict, "flow-revision-conflict");
        var now = DateTimeOffset.UtcNow;
        _deleted[flowId] = Summary(flow) with { Revision = flow.Revision + 1, Enabled = false, DeletedAtUtc = now };
        _definitions.Remove(flowId);
        return Task.FromResult(new FlowDeleteResultDto(tenantId, flowId, flow.Revision + 1, FlowDeleteDisposition.Deleted, "flow-deleted", now));
    }
    private readonly Dictionary<Guid, FlowDefinitionDto> _definitions = new();
    private readonly Dictionary<Guid, List<FlowVersionDto>> _versions = new();
    private readonly Dictionary<Guid, FlowRunDetailDto> _runs = new();
    public Guid? SeededVersionId { get; private set; }
    public Guid? SeededRunId { get; private set; }
    public int VersionLookups, RunLookups, HistoryReads;
    public int SaveCalls, ValidateCalls, DryRunCalls, PublishCalls;
    public int CapabilityReads, ExecutionDefinitionReads;
    private readonly Dictionary<(FlowExecutionSourceKind Source, string Id), FlowExecutionActionSettingsDto> _executionDefinitions = [];
    private readonly ConcurrentQueue<(FlowExecutionSourceKind Source, string Search)> _executionSearches = new();
    public IReadOnlyList<(FlowExecutionSourceKind Source, string Search)> ExecutionSearches => _executionSearches.ToArray();
    public FlowGraphDto? SavedGraph { get; private set; }
    public TaskCompletionSource<FlowDefinitionDto>? SavePending { get; set; }
    public bool ConflictNextSave { get; set; }
    private readonly int _tenantId;
    public FixtureFlowApiService(int tenantId = 17)
    {
        _tenantId = tenantId;
        var flow = new FlowDefinitionDto(Guid.NewGuid(), tenantId, "Incident from alert", 1, false, FlowGraphTemplates.IncidentFromAlert(), null, 0, DateTimeOffset.UtcNow);
        _definitions[flow.Id] = flow;
    }
    public Task<FlowSchemaCapabilitiesDto> GetSchemaCapabilitiesAsync(int tenantId, CancellationToken token = default)
    { CapabilityReads++; return Task.FromResult(FlowSchemaCapabilities.Current); }
    public (FlowExecutionActionSettingsDto SourceJob, FlowExecutionActionSettingsDto Job, FlowExecutionActionSettingsDto Script) SeedExecutionAuthoringCatalogue()
    {
        var source = new FlowExecutionActionSettingsDto(FlowExecutionSourceKind.Job,
            new("42", "Synthetic source job", "3", new string('c', 64)))
        { Target = new(Guid.NewGuid(), "Source synthetic endpoint", "source.example.invalid"), Parameters = [] };
        var job = new FlowExecutionActionSettingsDto(FlowExecutionSourceKind.Job,
            new("9007199254740993", "Synthetic maintenance job", "7", new string('a', 64)))
        { Target = new(Guid.NewGuid(), "Bound synthetic repair endpoint", "repair.example.invalid"), Parameters = [new("message", FlowDataType.Text, true)] };
        var script = new FlowExecutionActionSettingsDto(FlowExecutionSourceKind.Script,
            new("9007199254740995", "Synthetic diagnostic script", "11", new string('b', 64)))
        { Parameters = [new("message", FlowDataType.Text, true)] };
        foreach (var definition in new[] { source, job, script })
            _executionDefinitions[(definition.SourceKind, definition.Definition.Id)] = definition;
        return (source, job, script);
    }
    public Task<FlowExecutionDefinitionsPageDto> SearchExecutionDefinitionsAsync(int tenantId, FlowExecutionSourceKind sourceKind,
        string? search = null, string? afterId = null, int limit = 50, CancellationToken token = default)
    {
        RequireCatalogueTenant(tenantId); token.ThrowIfCancellationRequested();
        _executionSearches.Enqueue((sourceKind, search ?? ""));
        var items = _executionDefinitions.Values.Where(action => action.SourceKind == sourceKind &&
            (string.IsNullOrWhiteSpace(search) || action.Definition.Name!.Contains(search, StringComparison.OrdinalIgnoreCase)) &&
            (afterId is null || StringComparer.Ordinal.Compare(action.Definition.Id, afterId) > 0))
            .OrderBy(action => action.Definition.Id, StringComparer.Ordinal).Take(limit)
            .Select(action => new FlowExecutionDefinitionOptionDto(action.SourceKind, action.Definition, action.Target)).ToArray();
        return Task.FromResult(new FlowExecutionDefinitionsPageDto(items));
    }
    public Task<FlowExecutionActionSettingsDto> GetExecutionDefinitionAsync(int tenantId, FlowExecutionSourceKind sourceKind,
        string definitionId, CancellationToken token = default)
    {
        RequireCatalogueTenant(tenantId); token.ThrowIfCancellationRequested(); ExecutionDefinitionReads++;
        return Task.FromResult(_executionDefinitions.GetValueOrDefault((sourceKind, definitionId)) ??
            throw new FlowApiException(HttpStatusCode.NotFound, "execution-definition-unavailable"));
    }
    public Task<FlowExecutionTargetsPageDto> SearchExecutionTargetsAsync(int tenantId, FlowExecutionSourceKind sourceKind,
        string? search = null, string? afterId = null, int limit = 50, CancellationToken token = default)
    {
        RequireCatalogueTenant(tenantId); token.ThrowIfCancellationRequested();
        var targets = _executionDefinitions.Values.Where(action => action.SourceKind == sourceKind).Select(action => action.Target)
            .OfType<FlowExecutionTargetDto>().Where(target => string.IsNullOrWhiteSpace(search) ||
                (target.DisplayName ?? target.HostName ?? "").Contains(search, StringComparison.OrdinalIgnoreCase)).Take(limit).ToArray();
        return Task.FromResult(new FlowExecutionTargetsPageDto(targets));
    }
    public Task<IReadOnlyList<FlowExecutionTargetSelectionDto>> GetSelectedExecutionTargetsAsync(int tenantId,
        FlowExecutionSourceKind sourceKind, IReadOnlyList<Guid> ids, CancellationToken token = default)
    {
        RequireCatalogueTenant(tenantId); token.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<FlowExecutionTargetSelectionDto>>(ids.Select(id =>
        {
            var target = _executionDefinitions.Values.Where(action => action.SourceKind == sourceKind)
                .Select(action => action.Target).FirstOrDefault(target => target?.AgentId == id);
            return new FlowExecutionTargetSelectionDto(target ?? new(id), target is not null);
        }).ToArray());
    }
    private void RequireCatalogueTenant(int tenantId)
    { if (tenantId != _tenantId) throw new FlowApiException(HttpStatusCode.Forbidden); }

    public Task<IReadOnlyList<FlowTemplateDescriptorDto>> GetTemplatesAsync(int tenantId, CancellationToken token = default) =>
        Task.FromResult(FlowOverhaulTemplates.Descriptors);
    public Task<FlowDefinitionDto> CreateFromTemplateAsync(int tenantId, FlowCreateFromTemplateRequest request, CancellationToken token = default)
    {
        var definition = new FlowDefinitionDto(Guid.NewGuid(), tenantId, request.Name, 1, false,
            FlowOverhaulTemplates.CreateDraft(request.TemplateId), null, 0, DateTimeOffset.UtcNow);
        _definitions[definition.Id] = definition; return Task.FromResult(definition);
    }
    public Task<IReadOnlyList<FlowTenantAccessDto>> GetTenantsAsync(CancellationToken token = default) => Task.FromResult<IReadOnlyList<FlowTenantAccessDto>>([new(_tenantId, $"Tenant {_tenantId}", true, true, true) { CanDelete = true }, new(23, "Tenant 23", true, false, false)]);
    public Task<IReadOnlyList<FlowDefinitionDto>> ListAsync(int tenantId, CancellationToken token = default) => Task.FromResult<IReadOnlyList<FlowDefinitionDto>>(_definitions.Values.Where(f => f.TenantId == tenantId).ToArray());
    public Task<FlowDefinitionDto> GetAsync(int tenantId, Guid flowId, CancellationToken token = default) => Task.FromResult(Find(tenantId, flowId));
    public Task<FlowDefinitionDto> CreateAsync(int tenantId, FlowCreateRequest request, CancellationToken token = default)
    {
        var flow = new FlowDefinitionDto(Guid.NewGuid(), tenantId, request.Name, 1, false, request.UseTemplate ? FlowGraphTemplates.IncidentFromAlert() : new(1, [], [], new()), null, 0, DateTimeOffset.UtcNow);
        _definitions[flow.Id] = flow; return Task.FromResult(flow);
    }
    public Task<FlowDefinitionDto> SaveAsync(int tenantId, Guid flowId, FlowSaveDraftRequest request, CancellationToken token = default)
    {
        SaveCalls++; SavedGraph = request.Graph;
        if (SavePending is not null) return SavePending.Task;
        var flow = Find(tenantId, flowId);
        if (ConflictNextSave || request.ExpectedRevision != flow.Revision) { ConflictNextSave = false; throw new FlowApiException(HttpStatusCode.Conflict); }
        var saved = flow with { Revision = flow.Revision + 1, Name = request.Name, Draft = request.Graph, UpdatedAtUtc = DateTimeOffset.UtcNow };
        _definitions[flowId] = saved; return Task.FromResult(saved);
    }
    public Task<FlowDefinitionDto> CloneAsync(int tenantId, Guid flowId, FlowCloneRequest request, CancellationToken token = default)
    {
        var flow = Find(tenantId, flowId); var clone = flow with { Id = Guid.NewGuid(), Name = request.Name, Revision = 1, Enabled = false, PublishedVersionId = null, PublishedVersionNumber = 0 };
        _definitions[clone.Id] = clone; return Task.FromResult(clone);
    }
    public Task<FlowDefinitionDto> SetEnabledAsync(int tenantId, Guid flowId, FlowEnabledRequest request, CancellationToken token = default)
    {
        var flow = Find(tenantId, flowId); var saved = flow with { Enabled = request.Enabled, Revision = flow.Revision + 1 }; _definitions[flow.Id] = saved; return Task.FromResult(saved);
    }
    public Task<FlowVersionDto> PublishAsync(int tenantId, Guid flowId, FlowRevisionRequest request, CancellationToken token = default)
    {
        PublishCalls++; var flow = Find(tenantId, flowId);
        if (!FlowGraphValidator.ValidateComplete(flow.Draft).Valid) throw new FlowApiException(HttpStatusCode.BadRequest);
        var version = new FlowVersionDto(Guid.NewGuid(), flowId, tenantId, flow.PublishedVersionNumber + 1, flow.Draft, "fixture-hash", "Fixture operator", DateTimeOffset.UtcNow);
        if (!_versions.TryGetValue(flowId, out var list)) _versions[flowId] = list = [];
        list.Add(version); _definitions[flowId] = flow with { Revision = flow.Revision + 1, PublishedVersionId = version.Id, PublishedVersionNumber = version.VersionNumber };
        return Task.FromResult(version);
    }
    public void SeedPublishedVersion()
    {
        var flow = _definitions.Values.First();
        var graph = flow.Draft with { Nodes = flow.Draft.Nodes.Select(n => n.Kind == FlowNodeKind.CreateIncident ? n with { ConnectorId = Guid.NewGuid(), ConnectorRevision = 1 } : n).ToArray() };
        var version = new FlowVersionDto(Guid.NewGuid(), flow.Id, flow.TenantId, 1, graph, "fixture-immutable", "Fixture operator", DateTimeOffset.UtcNow);
        _versions[flow.Id] = [version];
        SeededVersionId = version.Id;
        _definitions[flow.Id] = flow with { PublishedVersionId = version.Id, PublishedVersionNumber = 1, Name = "Changed draft name" };
    }
    public Task<FlowValidationResultDto> ValidateAsync(int tenantId, FlowGraphDto graph, CancellationToken token = default) { ValidateCalls++; return Task.FromResult(graph.SchemaVersion == FlowGraphV2Limits.SchemaVersion ? FlowGraphV2Validator.ValidateComplete(graph) : FlowGraphValidator.ValidateComplete(graph)); }
    public Task<FlowDryRunResultDto> DryRunAsync(int tenantId, FlowDryRunRequest request, CancellationToken token = default)
    { DryRunCalls++; return Task.FromResult(FlowPureEvaluation.DryRun(request)); }
    public void SeedPreviewGraph()
    {
        var flow = _definitions.Values.First();
        var graph = flow.Draft with { Nodes = flow.Draft.Nodes.Select(node => node.Kind == FlowNodeKind.CreateIncident
            ? node with { ConnectorId = Guid.NewGuid(), ConnectorRevision = 1 } : node).ToArray() };
        _definitions[flow.Id] = flow with { Draft = graph };
    }
    public Task<FlowDryRunResultDto> DryRunExecutionAsync(int tenantId, FlowExecutionDryRunRequest request, CancellationToken token = default)
    { DryRunCalls++; return Task.FromResult(FlowExecutionPreview.DryRun(request, tenantId)); }
    public Task<IReadOnlyList<FlowConnectorReferenceDto>> GetConnectorsAsync(int tenantId, CancellationToken token = default) => Task.FromResult<IReadOnlyList<FlowConnectorReferenceDto>>([]);
    public Task<IReadOnlyList<FlowVersionDto>> GetVersionsAsync(int tenantId, Guid flowId, CancellationToken token = default) { HistoryReads++; Find(tenantId, flowId); return Task.FromResult<IReadOnlyList<FlowVersionDto>>(_versions.GetValueOrDefault(flowId, []).ToArray()); }
    public Task<IReadOnlyList<FlowRunSummaryDto>> GetRunsAsync(int tenantId, Guid flowId, CancellationToken token = default) { HistoryReads++; Find(tenantId, flowId); return Task.FromResult<IReadOnlyList<FlowRunSummaryDto>>(_runs.Values.Select(r => r.Run).Where(r => r.FlowId == flowId).ToArray()); }
    public Task<FlowRunDetailDto> GetRunAsync(int tenantId, Guid flowId, Guid runId, CancellationToken token = default)
    { Find(tenantId, flowId); return Task.FromResult(_runs.TryGetValue(runId, out var run) && run.Run.FlowId == flowId ? run : throw new FlowApiException(HttpStatusCode.NotFound)); }
    public Task<FlowVersionDto> GetVersionAsync(int tenantId, Guid versionId, CancellationToken token = default)
    { VersionLookups++; return Task.FromResult(_versions.Values.SelectMany(v => v).FirstOrDefault(v => v.TenantId == tenantId && v.Id == versionId) ?? throw new FlowApiException(HttpStatusCode.NotFound)); }
    public Task<FlowRunDetailDto> GetRunByIdAsync(int tenantId, Guid runId, CancellationToken token = default)
    { RunLookups++; if (_runs.TryGetValue(runId, out var run) && Find(tenantId, run.Run.FlowId).TenantId == tenantId) return Task.FromResult(run); throw new FlowApiException(HttpStatusCode.NotFound); }
    public FlowRunDetailDto SeedRun(FlowEventDataDto? input = null, Guid? eventId = null, Guid? occurrenceId = null)
    {
        SeedPublishedVersion(); var flow = _definitions.Values.First();
        input ??= new(Guid.NewGuid(), Guid.NewGuid(), "Disk capacity", "Fixture client", "/", "disk.used.percent", "warning", 95, null, DateTimeOffset.UtcNow);
        var summary = new FlowRunSummaryDto(Guid.NewGuid(), flow.Id, SeededVersionId!.Value, eventId ?? Guid.NewGuid(), occurrenceId ?? Guid.NewGuid(), FlowRunStatus.Succeeded, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "incident-created");
        SeededRunId = summary.Id;
        _runs[summary.Id] = new(summary, input,
            [new(flow.Draft.Nodes.Single(n => n.Kind == FlowNodeKind.CreateIncident).Id, "fixture-action", FlowActionStatus.Succeeded, 1, 1, "incident-created", new("123", TrackingId: "NR-2026-000123", SafeLink: "https://fixture.invalid/incidents/123"))]);
        return _runs[summary.Id];
    }


    public void SeedExecutionPreviewGraph()
    {
        var flow = _definitions.Values.First();
        var triggerId = Guid.NewGuid(); var repairId = Guid.NewGuid(); var conditionId = Guid.NewGuid();
        var mapId = Guid.NewGuid(); var finishId = Guid.NewGuid(); var unresolvedId = Guid.NewGuid();
        var agentId = Guid.Parse("77777777-7777-4777-8777-777777777777");
        FlowNodeDto[] nodes =
        [
            new(triggerId, FlowNodeKind.ExecutionEvent, "Synthetic job failure", new(40, 100))
            { ExecutionTrigger = new([FlowExecutionSourceKind.Job], [FlowExecutionOutcome.Failed], FlowExecutionScope.SelectedDefinitions)
                { DefinitionReferences = [new(FlowExecutionSourceKind.Job, "12")], AgentIds = [agentId] } },
            new(repairId, FlowNodeKind.RunLibraryScript, "Pinned synthetic repair", new(340, 100))
            { ExecutionAction = new(FlowExecutionSourceKind.Script, new("42", "Synthetic repair", "7", new string('a', 64)))
                { Target = new(agentId, "Synthetic endpoint"), Parameters = [] } },
            new(conditionId, FlowNodeKind.Condition, "Inspect sample exit code", new(640, 100))
            { Predicate = new(new(FlowInputSource.NodeOutput, FlowDataType.Number, "exitCode", repairId), FlowComparison.Equal, System.Text.Json.JsonSerializer.SerializeToElement(0)) },
            new(mapId, FlowNodeKind.MapIncident, "Map explicit sample result", new(940, 100),
                Mapping: new("{definitionName}: {input.executionId}", "Original {rawStatus} on {targetName}"))
            { Inputs = new Dictionary<string, FlowInputBindingDto> { ["executionId"] = new(FlowInputSource.NodeOutput, FlowDataType.Text, "executionId", repairId) } },
            new(finishId, FlowNodeKind.Finish, "Finish simulated successful branch", new(1240, 100)),
            new(unresolvedId, FlowNodeKind.Finish, "Finish explicit unresolved branch", new(940, 380))
        ];
        FlowEdgeDto Edge(Guid source, string port, Guid target) => new(source, port, target, FlowNodeCatalogue.Input) { Id = Guid.NewGuid() };
        var edges = new List<FlowEdgeDto>
        {
            Edge(triggerId, FlowNodeCatalogue.Event, repairId), Edge(repairId, FlowNodeCatalogue.Succeeded, conditionId),
            Edge(conditionId, FlowNodeCatalogue.True, mapId), Edge(conditionId, FlowNodeCatalogue.False, unresolvedId),
            Edge(mapId, FlowNodeCatalogue.Result, finishId)
        };
        foreach (var outcome in new[] { FlowNodeCatalogue.Failed, FlowNodeCatalogue.TimedOut, FlowNodeCatalogue.Cancelled,
            FlowNodeCatalogue.Unknown, FlowNodeCatalogue.Unsupported, FlowNodeCatalogue.Denied })
            edges.Add(Edge(repairId, outcome, unresolvedId));
        var graph = new FlowGraphDto(2, nodes, edges, new());
        var validation = FlowGraphV2Validator.ValidateComplete(graph);
        if (!validation.Valid) throw new InvalidOperationException("Execution preview fixture must be a complete graph.");
        _definitions[flow.Id] = flow with { Name = "Synthetic execution preview", Draft = graph };
    }


    public FlowRunDetailDto SeedRecordedRun(bool snapshots = true)
    {
        var detail = SeedRun();
        var flow = _definitions[detail.Run.FlowId];
        var version = _versions[flow.Id].Single(value => value.Id == detail.Run.FlowVersionId);
        var hash = FlowGraphSerialization.Hash(version.Graph);
        _versions[flow.Id] = [version with { ConfigurationHash = hash }];
        _definitions[flow.Id] = flow with { Draft = flow.Draft with
            { Nodes = flow.Draft.Nodes.Select(node => node.Kind == FlowNodeKind.MapIncident ? node with { Name = "Current mutable draft mapping" } : node).ToArray() } };
        if (!snapshots) return detail;
        var trigger = version.Graph.Nodes.Single(node => node.Kind == FlowNodeKind.AlertRaised);
        var mapping = version.Graph.Nodes.Single(node => node.Kind == FlowNodeKind.MapIncident);
        var action = version.Graph.Nodes.Single(node => node.Kind == FlowNodeKind.CreateIncident);
        var captured = new FlowIncidentFieldsDto("Recorded synthetic incident", "Original captured alert for Fixture client", 2);
        FlowTraceSnapshotDto Snapshot(object value) => new(FlowTraceDataState.Present)
        { Value = JsonSerializer.SerializeToElement(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)) };
        var trace = new FlowTraceDto(version.Graph.SchemaVersion, hash, FlowTraceProvenance.Recorded,
        [
            new(0, trigger.Id, trigger.Name, FlowTraceStepStatus.Completed, "event", FlowTraceProvenance.Recorded, Snapshot(detail.Input), Snapshot(detail.Input))
            { SelectedPort = "out", NextNodeId = mapping.Id },
            new(1, mapping.Id, mapping.Name, FlowTraceStepStatus.Completed, "mapped", FlowTraceProvenance.Recorded, Snapshot(detail.Input), Snapshot(captured))
            { SelectedPort = "out", NextNodeId = action.Id },
            new(2, action.Id, action.Name, FlowTraceStepStatus.Completed, "succeeded", FlowTraceProvenance.Recorded, Snapshot(captured),
                Snapshot(new { incidentId = "123", trackingId = "NR-2026-000123" }))
        ]);
        _runs[detail.Run.Id] = detail with { Trace = trace };
        return _runs[detail.Run.Id];
    }
    private FlowDefinitionDto Find(int tenant, Guid id) => _definitions.TryGetValue(id, out var flow) && flow.TenantId == tenant ? flow : throw new FlowApiException(HttpStatusCode.NotFound);
}
