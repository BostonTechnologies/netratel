using System.Net;
using NetRatel.Shared.Contracts.Flows;
using NetRatel.Web.Services.Flows;

namespace NetRatel.Web.PlaywrightTests;

internal sealed class FixtureFlowApiService : IFlowApiService
{
    private readonly Dictionary<Guid, FlowDefinitionDto> _definitions = new();
    private readonly Dictionary<Guid, List<FlowVersionDto>> _versions = new();
    private readonly Dictionary<Guid, FlowRunDetailDto> _runs = new();
    public Guid? SeededVersionId { get; private set; }
    public Guid? SeededRunId { get; private set; }
    public int VersionLookups, RunLookups, HistoryReads;
    public int SaveCalls, ValidateCalls, DryRunCalls, PublishCalls;
    public FlowGraphDto? SavedGraph { get; private set; }
    public TaskCompletionSource<FlowDefinitionDto>? SavePending { get; set; }
    public bool ConflictNextSave { get; set; }
    public FixtureFlowApiService()
    {
        var flow = new FlowDefinitionDto(Guid.NewGuid(), 17, "Incident from alert", 1, false, FlowGraphTemplates.IncidentFromAlert(), null, 0, DateTimeOffset.UtcNow);
        _definitions[flow.Id] = flow;
    }
    public Task<IReadOnlyList<FlowTenantAccessDto>> GetTenantsAsync(CancellationToken token = default) => Task.FromResult<IReadOnlyList<FlowTenantAccessDto>>([new(17, "Tenant 17", true, true, true), new(23, "Tenant 23", true, false, false)]);
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
    public Task<FlowValidationResultDto> ValidateAsync(int tenantId, FlowGraphDto graph, CancellationToken token = default) { ValidateCalls++; return Task.FromResult(FlowGraphValidator.ValidateComplete(graph)); }
    public Task<FlowDryRunResultDto> DryRunAsync(int tenantId, FlowDryRunRequest request, CancellationToken token = default)
    { DryRunCalls++; var valid = FlowGraphValidator.ValidateComplete(request.Graph); return Task.FromResult(new FlowDryRunResultDto(valid.Valid, false, valid.Valid ? "preview" : "connector-required", null, [], valid.Issues)); }
    public Task<IReadOnlyList<FlowConnectorReferenceDto>> GetConnectorsAsync(int tenantId, CancellationToken token = default) => Task.FromResult<IReadOnlyList<FlowConnectorReferenceDto>>([]);
    public Task<IReadOnlyList<FlowVersionDto>> GetVersionsAsync(int tenantId, Guid flowId, CancellationToken token = default) { HistoryReads++; Find(tenantId, flowId); return Task.FromResult<IReadOnlyList<FlowVersionDto>>(_versions.GetValueOrDefault(flowId, []).ToArray()); }
    public Task<IReadOnlyList<FlowRunSummaryDto>> GetRunsAsync(int tenantId, Guid flowId, CancellationToken token = default) { HistoryReads++; Find(tenantId, flowId); return Task.FromResult<IReadOnlyList<FlowRunSummaryDto>>(_runs.Values.Select(r => r.Run).Where(r => r.FlowId == flowId).ToArray()); }
    public Task<FlowRunDetailDto> GetRunAsync(int tenantId, Guid flowId, Guid runId, CancellationToken token = default)
    { Find(tenantId, flowId); return Task.FromResult(_runs.TryGetValue(runId, out var run) && run.Run.FlowId == flowId ? run : throw new FlowApiException(HttpStatusCode.NotFound)); }
    public Task<FlowVersionDto> GetVersionAsync(int tenantId, Guid versionId, CancellationToken token = default)
    { VersionLookups++; return Task.FromResult(_versions.Values.SelectMany(v => v).FirstOrDefault(v => v.TenantId == tenantId && v.Id == versionId) ?? throw new FlowApiException(HttpStatusCode.NotFound)); }
    public Task<FlowRunDetailDto> GetRunByIdAsync(int tenantId, Guid runId, CancellationToken token = default)
    { RunLookups++; if (_runs.TryGetValue(runId, out var run) && Find(tenantId, run.Run.FlowId).TenantId == tenantId) return Task.FromResult(run); throw new FlowApiException(HttpStatusCode.NotFound); }
    public void SeedRun()
    {
        SeedPublishedVersion(); var flow = _definitions.Values.First();
        var summary = new FlowRunSummaryDto(Guid.NewGuid(), flow.Id, SeededVersionId!.Value, Guid.NewGuid(), Guid.NewGuid(), FlowRunStatus.Succeeded, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "incident-created");
        SeededRunId = summary.Id;
        _runs[summary.Id] = new(summary, new(Guid.NewGuid(), Guid.NewGuid(), "Disk capacity", "Fixture client", "/", "disk.used.percent", "warning", 95, null, DateTimeOffset.UtcNow),
            [new(flow.Draft.Nodes.Single(n => n.Kind == FlowNodeKind.CreateIncident).Id, "fixture-action", FlowActionStatus.Succeeded, 1, 1, "incident-created", new("123", SafeLink: "https://fixture.invalid/incidents/123"))]);
    }
    private FlowDefinitionDto Find(int tenant, Guid id) => _definitions.TryGetValue(id, out var flow) && flow.TenantId == tenant ? flow : throw new FlowApiException(HttpStatusCode.NotFound);
}
