using System.Net;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.Shared.Contracts.Flows;
using NetRatel.Web.Components.Pages.Flows;
using NetRatel.Web.Services.Flows;
using Xunit;

namespace NetRatel.Web.ComponentTests;

public sealed class FlowEditorTests : AsyncBunitContext
{
    private readonly FakeFlowApi _api = new();
    public FlowEditorTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        JSInterop.SetupModule("./_content/VeloxDev.Razor/veloxdev.workflow.js").Mode = JSRuntimeMode.Loose;
        var editorModule = JSInterop.SetupModule("./js/flows-editor.js");
        editorModule.Mode = JSRuntimeMode.Loose;
        editorModule.Setup<double[]>("geometry", _ => true).SetResult([0, 0, 1000, 800, 1200, 1000]);
        Services.AddSingleton<IFlowApiService>(_api);
    }

    [Fact]
    public async Task Dirty_Close_Preserves_Edits_And_Explicit_Discard_Closes()
    {
        var closed = 0;
        var cut = RenderEditor(p => p.Add(e => e.Closed, () => closed++));
        await cut.WaitForAssertionAsync(() => cut.Find("[data-testid=flow-name]").GetAttribute("value").Should().Be("Incident flow"));
        await cut.Find("[data-testid=flow-name]").InputAsync(new() { Value = "Edited flow" });
        cut.Find("[data-testid=flow-dirty]").TextContent.Should().Contain("Unsaved");
        await cut.Find("[data-testid=flow-close]").ClickAsync(new());
        cut.Find("[data-testid=flow-unsaved]").TextContent.Should().Contain("Unsaved changes");
        closed.Should().Be(0); _api.SaveCalls.Should().Be(0);
        await cut.Find("[data-testid=flow-discard]").ClickAsync(new());
        closed.Should().Be(1);
    }

    [Fact]
    public async Task Double_Save_Is_Fenced_And_Conflict_Preserves_Draft_Name_And_Revision()
    {
        _api.SavePending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var cut = RenderEditor();
        await cut.WaitForAssertionAsync(() => cut.FindAll("[data-testid=flow-name]").Count.Should().Be(1));
        await cut.Find("[data-testid=flow-name]").InputAsync(new() { Value = "My unsaved flow" });
        var save = cut.Find("[data-testid=flow-save]").ClickAsync(new());
        await cut.Find("[data-testid=flow-save]").ClickAsync(new());
        await cut.WaitForAssertionAsync(() => _api.SaveCalls.Should().Be(1)); _api.LastSave!.ExpectedRevision.Should().Be(7);
        _api.SavePending.SetException(new FlowApiException(HttpStatusCode.Conflict));
        await save;
        await cut.WaitForAssertionAsync(() => cut.Find("[data-testid=flow-error]").TextContent.Should().Contain("changed in another session"));
        cut.Find("[data-testid=flow-name]").GetAttribute("value").Should().Be("My unsaved flow");
        cut.Find("[data-testid=flow-dirty]").TextContent.Should().Contain("Unsaved");
        _api.PublishCalls.Should().Be(0);
    }

    [Fact]
    public async Task Old_Tenant_Save_Response_Cannot_Replace_The_New_Editor()
    {
        _api.SavePending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var cut = RenderEditor();
        await cut.WaitForAssertionAsync(() => cut.FindAll("[data-testid=flow-name]").Count.Should().Be(1));
        await cut.Find("[data-testid=flow-name]").InputAsync(new() { Value = "Old scope" });
        var save = cut.Find("[data-testid=flow-save]").ClickAsync(new());
        await cut.WaitForAssertionAsync(() => _api.SaveCalls.Should().Be(1));
        var replacement = Definition() with { Id = Guid.NewGuid(), TenantId = 23, Name = "Tenant 23", Revision = 1 };
        cut.Render(p => p.Add(e => e.Definition, replacement).Add(e => e.CanEdit, true));
        await cut.WaitForAssertionAsync(() => cut.Find("[data-testid=flow-name]").GetAttribute("value").Should().Be("Tenant 23"));
        _api.SavePending.SetResult(Definition() with { Name = "Late old response", Revision = 8 });
        await save;
        cut.Find("[data-testid=flow-name]").GetAttribute("value").Should().Be("Tenant 23");
        cut.Find("[data-testid=flow-dirty]").TextContent.Should().Contain("revision 1");
    }

    [Fact]
    public async Task Native_Changes_During_Save_Response_Remain_Dirty_And_Are_Not_Overwritten()
    {
        _api.SavePending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var cut = RenderEditor();
        await cut.WaitForAssertionAsync(() => cut.FindAll("[data-testid=flow-name]").Count.Should().Be(1));
        await cut.Find("[data-testid=flow-name]").InputAsync(new() { Value = "Submitted name" });
        var save = cut.Find("[data-testid=flow-save]").ClickAsync(new());
        await cut.WaitForAssertionAsync(() => _api.SaveCalls.Should().Be(1));
        var adapter = cut.FindComponent<FlowCanvas>().Instance.Adapter;
        var mapping = adapter.Capture().Nodes.Single(n => n.Kind == FlowNodeKind.MapIncident);
        await cut.InvokeAsync(() => adapter.Update(mapping with { Mapping = mapping.Mapping! with { TitleTemplate = "Newer native change" } }));
        _api.SavePending.SetResult(Definition() with { Name = _api.LastSave!.Name, Draft = _api.LastSave.Graph, Revision = 8 });
        await save;
        await cut.WaitForAssertionAsync(() => cut.Find("[data-testid=flow-dirty]").TextContent.Should().Contain("Unsaved"));
        adapter.Capture().Nodes.Single(n => n.Id == mapping.Id).Mapping!.TitleTemplate.Should().Be("Newer native change");
        cut.Find("[data-testid=flow-result]").TextContent.Should().Contain("Newer canvas changes remain unsaved");
    }

    [Fact]
    public async Task Validate_And_Sample_Dry_Run_Stay_Inert_And_Empty_Catalog_Does_Not_Invent_A_Connector()
    {
        var cut = RenderEditor();
        await cut.WaitForAssertionAsync(() => cut.FindAll("[data-testid=flow-name]").Count.Should().Be(1));
        await cut.Find("[data-testid=flow-validate]").ClickAsync(new());
        await cut.WaitForAssertionAsync(() => cut.Find("[data-testid=flow-result]").TextContent.Should().Contain("validation found issues"));
        await cut.Find("[data-testid=flow-dry-run-toggle]").ClickAsync(new());
        await cut.Find("[data-testid=flow-dry-run]").ClickAsync(new());
        await cut.WaitForAssertionAsync(() => cut.Find("[data-testid=flow-preview]").TextContent.Should().Contain("Preview could not validate"));
        var action = Definition().Draft.Nodes.Single(n => n.Kind == FlowNodeKind.CreateIncident);
        await cut.Find("[data-testid=flow-select-node]").ChangeAsync(new() { Value = action.Id.ToString() });
        cut.Find("[data-testid=flow-connector-status]").TextContent.Should().Contain("No owned connector");
        cut.Find("[data-testid=flow-connector]").Children.Should().ContainSingle();
        _api.SaveCalls.Should().Be(0); _api.PublishCalls.Should().Be(0); _api.ValidateCalls.Should().Be(1); _api.DryRunCalls.Should().Be(1);
    }

    [Fact]
    public async Task Published_View_Is_Immutable_And_Has_No_Save_Or_Publish_Action()
    {
        var cut = RenderEditor(p => p.Add(e => e.ReadOnly, true).Add(e => e.VersionNumber, 3));
        await cut.WaitForAssertionAsync(() => cut.Find("[data-testid=flow-dirty]").TextContent.Should().Contain("Immutable version 3"));
        cut.FindAll("[data-testid=flow-save]").Should().BeEmpty(); cut.FindAll("[data-testid=flow-publish]").Should().BeEmpty();
        cut.Find("[data-testid=flow-name]").HasAttribute("disabled").Should().BeTrue();
        cut.Find("[data-testid=flow-connect]").Closest("fieldset")!.HasAttribute("disabled").Should().BeTrue();
        _api.SaveCalls.Should().Be(0);
    }

    [Theory]
    [InlineData("1e309")]
    [InlineData("NaN")]
    public async Task Non_Finite_Condition_Input_Preserves_The_Last_Valid_Value_And_Can_Be_Corrected(string input)
    {
        var cut = RenderEditor();
        await cut.WaitForAssertionAsync(() => cut.FindAll("[data-testid=flow-name]").Count.Should().Be(1));
        await cut.Find("[data-testid=flow-add-Condition]").ClickAsync(new());
        var adapter = cut.FindComponent<FlowCanvas>().Instance.Adapter;
        var before = System.Text.Json.JsonSerializer.Serialize(adapter.Capture());
        await cut.Find("[data-testid=flow-condition-value]").ChangeAsync(new() { Value = input });
        cut.Find("[data-testid=flow-error]").TextContent.Should().Contain("finite numbers").And.Contain("previous valid value is preserved");
        adapter.Capture().Nodes.Single(n => n.Kind == FlowNodeKind.Condition).Condition!.NumericValue.Should().Be(90);
        System.Text.Json.JsonSerializer.Serialize(adapter.Capture()).Should().Be(before);
        await cut.Find("[data-testid=flow-condition-value]").ChangeAsync(new() { Value = "12.5" });
        cut.FindAll("[data-testid=flow-error]").Should().BeEmpty();
        adapter.Capture().Nodes.Single(n => n.Kind == FlowNodeKind.Condition).Condition!.NumericValue.Should().Be(12.5);
        System.Text.Json.JsonSerializer.Serialize(adapter.Capture()).Should().Contain("12.5");
        _api.SaveCalls.Should().Be(0); _api.PublishCalls.Should().Be(0);
    }

    private IRenderedComponent<FlowEditor> RenderEditor(Action<ComponentParameterCollectionBuilder<FlowEditor>>? extra = null) => Render<FlowEditor>(p =>
    { p.Add(e => e.Definition, Definition()).Add(e => e.CanEdit, true).Add(e => e.CanPublish, true).Add(e => e.TenantName, "Tenant 17"); extra?.Invoke(p); });
    private static readonly Guid FlowId = Guid.NewGuid();
    private static readonly FlowGraphDto Graph = FlowGraphTemplates.IncidentFromAlert();
    internal static FlowDefinitionDto Definition() => new(FlowId, 17, "Incident flow", 7, false, Graph, null, 0, DateTimeOffset.UtcNow);
}

internal sealed class FakeFlowApi : IFlowApiService
{
    public int SaveCalls, PublishCalls, ValidateCalls, DryRunCalls;
    public FlowSaveDraftRequest? LastSave;
    public TaskCompletionSource<FlowDefinitionDto>? SavePending;
    public Func<int, Guid, CancellationToken, Task<FlowVersionDto>>? VersionLookup;
    public Func<int, Guid, CancellationToken, Task<FlowRunDetailDto>>? RunLookup;
    public Task<IReadOnlyList<FlowTenantAccessDto>> GetTenantsAsync(CancellationToken token = default) => Task.FromResult<IReadOnlyList<FlowTenantAccessDto>>([new(17, "Tenant 17", true, true, true)]);
    public Task<IReadOnlyList<FlowDefinitionDto>> ListAsync(int tenantId, CancellationToken token = default) => Task.FromResult<IReadOnlyList<FlowDefinitionDto>>([FlowEditorTests.Definition()]);
    public Task<FlowDefinitionDto> GetAsync(int tenantId, Guid flowId, CancellationToken token = default) => Task.FromResult(FlowEditorTests.Definition());
    public Task<FlowDefinitionDto> CreateAsync(int tenantId, FlowCreateRequest request, CancellationToken token = default) => throw new NotSupportedException();
    public Task<FlowDefinitionDto> SaveAsync(int tenantId, Guid flowId, FlowSaveDraftRequest request, CancellationToken token = default)
    { SaveCalls++; LastSave = request; return SavePending?.Task ?? Task.FromResult(FlowEditorTests.Definition() with { Revision = request.ExpectedRevision + 1, Name = request.Name, Draft = request.Graph }); }
    public Task<FlowDefinitionDto> CloneAsync(int tenantId, Guid flowId, FlowCloneRequest request, CancellationToken token = default) => throw new NotSupportedException();
    public Task<FlowDefinitionDto> SetEnabledAsync(int tenantId, Guid flowId, FlowEnabledRequest request, CancellationToken token = default) => throw new NotSupportedException();
    public Task<FlowVersionDto> PublishAsync(int tenantId, Guid flowId, FlowRevisionRequest request, CancellationToken token = default) { PublishCalls++; throw new NotSupportedException(); }
    public Task<FlowValidationResultDto> ValidateAsync(int tenantId, FlowGraphDto graph, CancellationToken token = default) { ValidateCalls++; return Task.FromResult(FlowGraphValidator.ValidateComplete(graph)); }
    public Task<FlowDryRunResultDto> DryRunAsync(int tenantId, FlowDryRunRequest request, CancellationToken token = default) { DryRunCalls++; return Task.FromResult(new FlowDryRunResultDto(false, false, "connector-required", null, [], [new("connector-required")])); }
    public Task<IReadOnlyList<FlowConnectorReferenceDto>> GetConnectorsAsync(int tenantId, CancellationToken token = default) => Task.FromResult<IReadOnlyList<FlowConnectorReferenceDto>>([]);
    public Task<IReadOnlyList<FlowVersionDto>> GetVersionsAsync(int tenantId, Guid flowId, CancellationToken token = default) => Task.FromResult<IReadOnlyList<FlowVersionDto>>([]);
    public Task<IReadOnlyList<FlowRunSummaryDto>> GetRunsAsync(int tenantId, Guid flowId, CancellationToken token = default) => Task.FromResult<IReadOnlyList<FlowRunSummaryDto>>([]);
    public Task<FlowRunDetailDto> GetRunAsync(int tenantId, Guid flowId, Guid runId, CancellationToken token = default) => throw new NotSupportedException();
    public Task<FlowVersionDto> GetVersionAsync(int tenantId, Guid versionId, CancellationToken token = default) => VersionLookup?.Invoke(tenantId, versionId, token) ?? throw new NotSupportedException();
    public Task<FlowRunDetailDto> GetRunByIdAsync(int tenantId, Guid runId, CancellationToken token = default) => RunLookup?.Invoke(tenantId, runId, token) ?? throw new NotSupportedException();
}
