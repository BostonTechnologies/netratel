using Bunit;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using NetRatel.Shared.Contracts.Flows;
using NetRatel.Web.Components.Pages.Flows;
using NetRatel.Web.Services;
using NetRatel.Web.Services.Flows;
using Xunit;

namespace NetRatel.Web.ComponentTests;

public sealed class FlowTypedEditorTests : AsyncBunitContext
{
    private readonly FakeFlowApi _api = new();
    public FlowTypedEditorTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        JSInterop.SetupModule("./_content/VeloxDev.Razor/veloxdev.workflow.js").Mode = JSRuntimeMode.Loose;
        JSInterop.SetupModule("./js/flows-connections.js").Mode = JSRuntimeMode.Loose;
        var module = JSInterop.SetupModule("./js/flows-editor.js"); module.Mode = JSRuntimeMode.Loose;
        module.Setup<double[]>("geometry", _ => true).SetResult([0, 0, 1000, 800, 1200, 1000]);
        module.Setup<double[]>("centerPosition", _ => true).SetResult([350, 240]);
        Services.AddSingleton<IFlowApiService>(_api); Services.AddSingleton<IPromptLibraryApiService>(new FakePromptLibraryApi());
        Services.AddMudServices(options => options.PopoverOptions.CheckForPopoverProvider = false);
        Render<MudDialogProvider>();
    }

    [Fact]
    public async Task Explicit_Upgrade_Preserves_Saved_Schema_And_Stable_Nodes_Until_Save_Then_Can_Be_Undone()
    {
        var definition = FlowEditorTests.Definition();
        var cut = Editor(definition, true);
        await cut.WaitForAssertionAsync(() => cut.FindAll("[data-testid=flow-name]").Count.Should().Be(1));
        var ids = definition.Draft.Nodes.Select(node => node.Id).ToArray();
        await cut.InvokeAsync(() => cut.Find("[data-testid=flow-upgrade-draft]").ClickAsync(new()));
        await cut.WaitForAssertionAsync(() => cut.FindComponent<FlowCanvas>().Instance.Adapter.SchemaVersion.Should().Be(2));
        cut.FindComponent<FlowCanvas>().Instance.Adapter.Capture().Nodes.Select(node => node.Id).Should().Equal(ids);
        definition.Draft.SchemaVersion.Should().Be(1);
        cut.FindAll("[data-testid=flow-palette-RerunOriginalExecution]").Should().ContainSingle();
        cut.Find("[data-testid=flow-publish]").HasAttribute("disabled").Should().BeTrue();
        _api.SaveCalls.Should().Be(0); _api.PublishCalls.Should().Be(0);
        await cut.InvokeAsync(() => cut.Find("[data-testid=flow-save]").ClickAsync(new()));
        _api.LastSave!.Graph.SchemaVersion.Should().Be(2); _api.LastSave.ExpectedRevision.Should().Be(7);
        await cut.InvokeAsync(() => cut.Find("[data-testid=flow-undo]").ClickAsync(new()));
        await cut.WaitForAssertionAsync(() => cut.FindComponent<FlowCanvas>().Instance.Adapter.SchemaVersion.Should().Be(1));
        cut.FindComponent<FlowCanvas>().Instance.Adapter.Capture().Nodes.Select(node => node.Id).Should().Equal(ids);
        cut.FindAll("[data-testid=flow-palette-RerunOriginalExecution]").Should().BeEmpty();
        _api.PublishCalls.Should().Be(0);
    }

    [Fact]
    public async Task Typed_Job_Selection_Retains_Bound_Target_And_Event_Input_Through_Real_Editor_Save()
    {
        var trigger = new FlowNodeDto(Guid.NewGuid(), FlowNodeKind.ExecutionEvent, "Synthetic job failures", new(20, 100))
        { ExecutionTrigger = new([FlowExecutionSourceKind.Job], [FlowExecutionOutcome.Failed], FlowExecutionScope.AllInTenant) };
        var definition = FlowEditorTests.Definition() with { Draft = new(2, [trigger], [], new()) };
        var bound = new FlowExecutionTargetDto(Guid.NewGuid(), "Synthetic job endpoint");
        var job = new FlowExecutionDefinitionDto("9007199254740993", "Synthetic job", "7", new string('b', 64));
        _api.DefinitionSearch = (_, source, _, _, _, _) => Task.FromResult(new FlowExecutionDefinitionsPageDto([new(source, job, bound)]));
        _api.DefinitionDetails = (_, source, _, _) => Task.FromResult(new FlowExecutionActionSettingsDto(source, job)
        { Target = bound, Parameters = [new("exit", FlowDataType.Number, true)] });
        var cut = Editor(definition, true);
        await cut.WaitForAssertionAsync(() => cut.Find("[data-testid=flow-add-RunJob]").HasAttribute("disabled").Should().BeFalse());
        await cut.InvokeAsync(() => cut.Find("[data-testid=flow-add-RunJob]").ClickAsync(new()));
        await cut.WaitForAssertionAsync(() => cut.FindAll("[data-testid=flow-definition-choice]").Should().ContainSingle());
        await cut.InvokeAsync(() => cut.Find("[data-testid=flow-definition-choice]").ClickAsync(new()));
        await cut.WaitForAssertionAsync(() => cut.Find("[data-testid=flow-job-bound-target]").TextContent.Should().Contain("Synthetic job endpoint"));
        cut.FindAll("[data-testid=flow-script-target-mode]").Should().BeEmpty();
        await cut.InvokeAsync(() => cut.Find("[data-testid=flow-binding-source]").ChangeAsync(new() { Value = nameof(FlowInputSource.Event) }));
        await cut.InvokeAsync(() => cut.Find("[data-testid=flow-binding-event]").ChangeAsync(new() { Value = "event.exitCode" }));
        await cut.InvokeAsync(() => cut.Find("[data-testid=flow-save]").ClickAsync(new()));
        var saved = _api.LastSave!.Graph.Nodes.Single(node => node.Kind == FlowNodeKind.RunJob);
        saved.ExecutionAction!.Definition.Id.Should().Be("9007199254740993");
        saved.ExecutionAction.Target.Should().Be(bound);
        saved.Inputs!["exit"].Should().Be(new FlowInputBindingDto(FlowInputSource.Event, FlowDataType.Number, "event.exitCode"));
        _api.SaveCalls.Should().Be(1); _api.PublishCalls.Should().Be(0); _api.DryRunCalls.Should().Be(0);
        cut.FindComponent<FlowCanvas>().Instance.Adapter.ExecutionCalls.Should().Be(0);
    }

    [Fact]
    public async Task Legacy_Server_Capabilities_Keep_The_Old_Alert_Palette_And_Hide_Upgrade()
    {
        var cut = Editor(FlowEditorTests.Definition(), false);
        await cut.WaitForAssertionAsync(() => cut.FindAll("[data-testid=flow-name]").Count.Should().Be(1));
        cut.FindAll("[data-testid=flow-upgrade-draft]").Should().BeEmpty();
        cut.FindAll("[data-testid=flow-palette-ExecutionEvent]").Should().BeEmpty();
        cut.FindAll("[data-testid=flow-palette-RunJob]").Should().BeEmpty();
        cut.FindComponent<FlowCanvas>().Instance.Adapter.SchemaVersion.Should().Be(1);
        _api.SaveCalls.Should().Be(0); _api.PublishCalls.Should().Be(0);
    }

    [Fact]
    public async Task Template_Picker_Creates_And_Opens_Only_A_Disabled_Schema_Two_Draft()
    {
        _api.Templates = (_, _) => Task.FromResult(FlowOverhaulTemplates.Descriptors);
        _api.TemplateCreate = (tenant, request, _) => Task.FromResult(FlowEditorTests.Definition() with
        { Id = Guid.NewGuid(), TenantId = tenant, Name = request.Name, Revision = 1, Enabled = false, Draft = FlowOverhaulTemplates.CreateDraft(request.TemplateId) });
        var cut = Render<FlowsPage>();
        await cut.WaitForAssertionAsync(() => cut.FindAll("[data-testid=flows-new]").Should().ContainSingle());
        await cut.InvokeAsync(() => cut.Find("[data-testid=flows-new]").ClickAsync(new()));
        await cut.WaitForAssertionAsync(() => cut.FindAll("[data-testid=flow-template-create]").Count.Should().Be(5));
        await cut.InvokeAsync(() => cut.Find("[data-template-id=script-recovery]").ClickAsync(new()));
        await cut.WaitForAssertionAsync(() => cut.FindComponent<FlowCanvas>().Instance.Adapter.SchemaVersion.Should().Be(2));
        _api.TemplateCreates.Should().Be(1); _api.CapabilityReads.Should().BeGreaterThan(0);
        _api.SaveCalls.Should().Be(0); _api.PublishCalls.Should().Be(0); _api.DryRunCalls.Should().Be(0);
        cut.Find("[data-testid=flow-publish]").HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public async Task Investigation_Inputs_Use_Explicit_Typed_Earlier_Outputs_And_Never_Secret_References()
    {
        var source = new FlowNodeDto(Guid.NewGuid(), FlowNodeKind.ExecutionEvent, "Synthetic source", new(0, 0));
        var diagnostic = new FlowNodeDto(Guid.NewGuid(), FlowNodeKind.RunLibraryScript, "Synthetic permitted diagnostic", new(300, 0));
        var ai = new FlowNodeDto(Guid.NewGuid(), FlowNodeKind.InvestigateWithAi, "Investigate failure", new(600, 0));
        var graph = new FlowGraphDto(2, [source, diagnostic, ai],
            [new(source.Id, "event", diagnostic.Id, "in") { Id = Guid.NewGuid() },
             .. FlowNodeCatalogue.Find(diagnostic.Kind)!.Ports.Where(port => port.Direction == FlowPortDirection.Output)
                .Select(port => new FlowEdgeDto(diagnostic.Id, port.Id, ai.Id, "in") { Id = Guid.NewGuid() })], new());
        FlowNodeDto? changed = null;
        var cut = Render<FlowNamedInputFields>(p => p.Add(component => component.Node, ai).Add(component => component.Graph, graph)
            .Add(component => component.Changed, value => changed = value));
        await cut.InvokeAsync(() => cut.Find("[data-testid=flow-investigation-input-name]").ChangeAsync(new() { Value = "diagnosticSummary" }));
        await cut.InvokeAsync(() => cut.Find("[data-testid=flow-investigation-add-input]").ClickAsync(new()));
        cut.Render(p => p.Add(component => component.Node, changed!));
        await cut.InvokeAsync(() => cut.Find("[data-testid=flow-binding-source]").ChangeAsync(new() { Value = nameof(FlowInputSource.NodeOutput) }));
        await cut.InvokeAsync(() => cut.Find("[data-testid=flow-binding-node]").ChangeAsync(new() { Value = diagnostic.Id.ToString() }));
        await cut.InvokeAsync(() => cut.Find("[data-testid=flow-binding-output]").ChangeAsync(new() { Value = "summary" }));
        changed!.Inputs!["diagnosticSummary"].Should().Be(new FlowInputBindingDto(FlowInputSource.NodeOutput, FlowDataType.Text, "summary", diagnostic.Id));
        cut.FindAll("option").Select(option => option.GetAttribute("value")).Should().NotContain(nameof(FlowInputSource.SecretReference));
        _api.SaveCalls.Should().Be(0); _api.DryRunCalls.Should().Be(0); _api.PublishCalls.Should().Be(0);
    }

    [Fact]
    public async Task Native_Secret_Input_Stores_Only_A_Validated_Target_Environment_Variable_Name()
    {
        FlowInputBindingDto? changed = null;
        var cut = Render<FlowInputBindingField>(p => p.Add(component => component.NodeId, Guid.NewGuid()).Add(component => component.Label, "Native secret reference")
            .Add(component => component.DataType, FlowDataType.SecretReference).Add(component => component.ValueChanged, value => changed = value));
        await cut.InvokeAsync(() => cut.Find("[data-testid=flow-binding-source]").ChangeAsync(new() { Value = nameof(FlowInputSource.SecretReference) }));
        await cut.InvokeAsync(() => cut.Find("[data-testid=flow-binding-secret-reference]").ChangeAsync(new() { Value = "DB_PASSWORD" }));
        changed!.Path.Should().Be("DB_PASSWORD"); changed.Literal.Should().BeNull();
        cut.Markup.Should().Contain("Environment variable name");
        await cut.InvokeAsync(() => cut.Find("[data-testid=flow-binding-secret-reference]").ChangeAsync(new() { Value = "vault/private/key" }));
        cut.Find("[role=alert]").TextContent.Should().Contain("environment variable name");
        changed.Path.Should().Be("DB_PASSWORD");
        _api.SaveCalls.Should().Be(0); _api.DryRunCalls.Should().Be(0);
    }

    private IRenderedComponent<FlowEditor> Editor(FlowDefinitionDto definition, bool supportsTwo) => Render<FlowEditor>(p => p
        .Add(component => component.Definition, definition).Add(component => component.TenantName, "Synthetic tenant")
        .Add(component => component.CanEdit, true).Add(component => component.CanPublish, true)
        .Add(component => component.SupportedDraftSchemas, supportsTwo ? new[] { 1, 2 } : [1])
        .Add(component => component.ExecutableSchemas, new[] { 1 }));
}
