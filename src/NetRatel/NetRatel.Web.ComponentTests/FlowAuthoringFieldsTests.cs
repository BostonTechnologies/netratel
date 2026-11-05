using System.Net;
using System.Text.Json;
using Bunit;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.Shared.Contracts.Flows;
using NetRatel.Web.Components.Pages.Flows;
using NetRatel.Web.Services.Flows;
using Xunit;

namespace NetRatel.Web.ComponentTests;

public sealed class FlowAuthoringFieldsTests : AsyncBunitContext
{
    private readonly FakeFlowApi _api = new();
    public FlowAuthoringFieldsTests() => Services.AddSingleton<IFlowApiService>(_api);

    [Fact]
    public async Task Job_Selector_Uses_Meaningful_Names_And_Current_Details_With_String_Id_And_Bound_Target()
    {
        var target = new FlowExecutionTargetDto(Guid.NewGuid(), "Synthetic job endpoint", "job.example.invalid");
        var definition = new FlowExecutionDefinitionDto("9007199254740993", "Synthetic maintenance", "7", new string('a', 64));
        _api.DefinitionSearch = (_, source, _, _, _, _) => Task.FromResult(new FlowExecutionDefinitionsPageDto([new(source, definition, target)]));
        _api.DefinitionDetails = (_, source, id, _) => Task.FromResult(new FlowExecutionActionSettingsDto(source, definition with { Id = id }) { Target = target, Parameters = [] });
        FlowExecutionActionSettingsDto? selected = null;
        var cut = Render<FlowExecutionDefinitionPicker>(p => p.Add(component => component.TenantId, 17).Add(component => component.NodeId, Guid.NewGuid())
            .Add(component => component.SourceKind, FlowExecutionSourceKind.Job).Add(component => component.Selected, value => selected = value));
        await cut.WaitForAssertionAsync(() => Assert.Contains("Synthetic maintenance", cut.Markup, StringComparison.Ordinal));
        Assert.Contains("Synthetic job endpoint", cut.Markup, StringComparison.Ordinal);
        Assert.Equal("9007199254740993", cut.Find("[data-testid=flow-definition-choice]").GetAttribute("data-definition-id"));
        await cut.InvokeAsync(() => cut.Find("[data-testid=flow-definition-choice]").ClickAsync(new()));
        Assert.Equal("9007199254740993", selected!.Definition.Id); Assert.Equal(target, selected.Target);
        Assert.Equal(1, _api.DefinitionDetailsCalls); Assert.Equal(0, _api.SaveCalls); Assert.Equal(0, _api.PublishCalls);
    }

    [Fact]
    public async Task Late_Definition_Details_Cannot_Change_A_Different_Node_Or_Tenant()
    {
        var definition = new FlowExecutionDefinitionDto("42", "Synthetic library script");
        var pending = new TaskCompletionSource<FlowExecutionActionSettingsDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        var current = new FlowExecutionDefinitionDto("43", "Current tenant library script");
        _api.DefinitionSearch = (tenant, source, _, _, _, _) => Task.FromResult(new FlowExecutionDefinitionsPageDto([new(source, tenant == 17 ? definition : current)]));
        _api.DefinitionDetails = (_, source, id, _) => id == definition.Id ? pending.Task :
            Task.FromResult(new FlowExecutionActionSettingsDto(source, current));
        var selections = 0;
        var cut = Render<FlowExecutionDefinitionPicker>(p => p.Add(component => component.TenantId, 17).Add(component => component.NodeId, Guid.NewGuid())
            .Add(component => component.SourceKind, FlowExecutionSourceKind.Script).Add(component => component.Selected, _ => selections++));
        await cut.WaitForAssertionAsync(() => Assert.Single(cut.FindAll("[data-testid=flow-definition-choice]")));
        var choose = cut.InvokeAsync(() => cut.Find("[data-testid=flow-definition-choice]").ClickAsync(new()));
        await cut.WaitForAssertionAsync(() => Assert.Equal(1, _api.DefinitionDetailsCalls));
        cut.Render(p => p.Add(component => component.TenantId, 23).Add(component => component.NodeId, Guid.NewGuid()));
        pending.SetResult(new(FlowExecutionSourceKind.Script, definition));
        await choose;
        Assert.Equal(0, selections);
        await cut.WaitForAssertionAsync(() => Assert.False(cut.Find("[data-testid=flow-definition-choice]").HasAttribute("disabled")));
        Assert.Contains("Current tenant library script", cut.Markup, StringComparison.Ordinal);
        await cut.InvokeAsync(() => cut.Find("[data-testid=flow-definition-choice]").ClickAsync(new()));
        Assert.Equal(1, selections);
    }

    [Fact]
    public async Task Denied_Definition_Search_Is_Visible_And_Does_Not_Look_Like_An_Empty_Catalogue()
    {
        _api.DefinitionSearch = (_, _, _, _, _, _) => throw new FlowApiException(HttpStatusCode.Forbidden);
        var cut = Render<FlowExecutionDefinitionPicker>(p => p.Add(component => component.TenantId, 17).Add(component => component.NodeId, Guid.NewGuid())
            .Add(component => component.SourceKind, FlowExecutionSourceKind.Script));
        await cut.WaitForAssertionAsync(() => Assert.Contains("permission", cut.Find("[role=alert]").TextContent, StringComparison.Ordinal));
        Assert.DoesNotContain("No matching definitions", cut.Markup, StringComparison.Ordinal); Assert.Equal(0, _api.DefinitionDetailsCalls);
    }

    [Fact]
    public async Task Binding_Choices_Are_Typed_And_Json_Null_Is_A_Captured_Literal()
    {
        FlowInputBindingDto? binding = null;
        var cut = Render<FlowInputBindingField>(p => p.Add(component => component.NodeId, Guid.NewGuid()).Add(component => component.Label, "Execution identifier")
            .Add(component => component.DataType, FlowDataType.Text).Add(component => component.EventFields, FlowBindingCatalogue.ExecutionFields)
            .Add(component => component.ValueChanged, value => binding = value));
        await cut.InvokeAsync(() => cut.Find("[data-testid=flow-binding-source]").ChangeAsync(new() { Value = nameof(FlowInputSource.Event) }));
        Assert.DoesNotContain("event.exitCode", cut.Find("[data-testid=flow-binding-event]").InnerHtml, StringComparison.Ordinal);
        await cut.InvokeAsync(() => cut.Find("[data-testid=flow-binding-event]").ChangeAsync(new() { Value = "event.executionId" }));
        Assert.Equal("event.executionId", binding!.Path); Assert.Equal(FlowDataType.Text, binding.DataType);
        cut.Render(p => p.Add(component => component.DataType, FlowDataType.Json).Add(component => component.Value, (FlowInputBindingDto?)null));
        await cut.InvokeAsync(() => cut.Find("[data-testid=flow-binding-source]").ChangeAsync(new() { Value = nameof(FlowInputSource.Literal) }));
        Assert.True(binding!.Literal.HasValue); Assert.Equal(JsonValueKind.Null, binding.Literal.Value.ValueKind);
        var wire = JsonSerializer.SerializeToElement(binding, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(JsonValueKind.Null, wire.GetProperty("literal").ValueKind);
        await cut.InvokeAsync(() => cut.Find("[data-testid=flow-binding-literal]").ChangeAsync(new() { Value = "\"scalar\"" }));
        Assert.Contains("object, array or null", cut.Find("[role=alert]").TextContent, StringComparison.Ordinal);
        Assert.Equal(JsonValueKind.Null, binding.Literal.Value.ValueKind);

        cut.Render(p => p.Add(component => component.DataType, FlowDataType.Number).Add(component => component.Value, (FlowInputBindingDto?)null));
        await cut.InvokeAsync(() => cut.Find("[data-testid=flow-binding-source]").ChangeAsync(new() { Value = nameof(FlowInputSource.Literal) }));
        var source = new FlowNodeDto(Guid.NewGuid(), FlowNodeKind.ExecutionEvent, "Synthetic source", new(0, 0));
        var node = new FlowNodeDto(Guid.NewGuid(), FlowNodeKind.Condition, "Precise comparison", new(300, 0))
        { Predicate = new(new(FlowInputSource.Event, FlowDataType.Number, "event.exitCode"), FlowComparison.GreaterThan, JsonSerializer.SerializeToElement(0)) };
        FlowNodeDto? changed = null;
        var predicate = Render<FlowPredicateFields>(p => p.Add(component => component.Node, node)
            .Add(component => component.Graph, new FlowGraphDto(2, [source, node], [], new()))
            .Add(component => component.Changed, value => changed = value));
        foreach (var number in new[] { "9007199254740993", "1234567890.123456789012345678" })
        {
            await cut.InvokeAsync(() => cut.Find("[data-testid=flow-binding-literal]").ChangeAsync(new() { Value = number }));
            Assert.Equal(number, binding!.Literal!.Value.GetRawText());
            Assert.Equal(number, JsonSerializer.SerializeToElement(binding, new JsonSerializerOptions(JsonSerializerDefaults.Web)).GetProperty("literal").GetRawText());
            cut.Render(p => p.Add(component => component.Value, binding));
            Assert.Equal(number, cut.Find("[data-testid=flow-binding-literal]").GetAttribute("value"));

            await predicate.InvokeAsync(() => predicate.Find("[data-testid=flow-condition-match]").ChangeAsync(new() { Value = number }));
            Assert.Equal(number, changed!.Predicate!.Value.GetRawText());
            Assert.Equal(number, JsonSerializer.SerializeToElement(changed.Predicate).GetProperty("Value").GetRawText());
            predicate.Render(p => p.Add(component => component.Node, changed));
            Assert.Equal(number, predicate.Find("[data-testid=flow-condition-match]").GetAttribute("value"));
        }
        var previousBinding = binding; var previousPredicate = changed;
        foreach (var invalid in new[] { "1e309", "NaN", "\"12\"", "0." + new string('0', FlowGraphV2Limits.MaximumBindingBytes) + "1" })
        {
            await cut.InvokeAsync(() => cut.Find("[data-testid=flow-binding-literal]").ChangeAsync(new() { Value = invalid }));
            Assert.Same(previousBinding, binding); Assert.Single(cut.FindAll("[role=alert]"));
            await predicate.InvokeAsync(() => predicate.Find("[data-testid=flow-condition-match]").ChangeAsync(new() { Value = invalid }));
            Assert.Same(previousPredicate, changed); Assert.Single(predicate.FindAll("[role=alert]"));
        }
        Assert.Equal(0, _api.SaveCalls); Assert.Equal(0, _api.PublishCalls); Assert.Equal(0, _api.DryRunCalls);
    }

    [Fact]
    public async Task Trigger_Empty_Selected_Scope_Stays_Explicit_And_Request_Source_System_Is_Exact()
    {
        var node = new FlowNodeDto(Guid.NewGuid(), FlowNodeKind.ExecutionEvent, "Synthetic request events", new(0, 0))
        { ExecutionTrigger = new([FlowExecutionSourceKind.Request], [FlowExecutionOutcome.Failed, FlowExecutionOutcome.TimedOut, FlowExecutionOutcome.Unknown], FlowExecutionScope.AllInTenant) };
        FlowNodeDto? changed = null;
        var cut = Render<FlowExecutionTriggerFields>(p => p.Add(component => component.TenantId, 17).Add(component => component.Node, node)
            .Add(component => component.Changed, value => changed = value));
        await cut.InvokeAsync(() => cut.Find("[data-testid=flow-trigger-scope]").ChangeAsync(new() { Value = "2" }));
        Assert.Equal(FlowExecutionScope.SelectedDefinitions, changed!.ExecutionTrigger!.Scope);
        Assert.Empty(changed.ExecutionTrigger.DefinitionReferences!);
        cut.Render(p => p.Add(component => component.Node, changed));
        Assert.Contains("empty selection does not mean all", cut.Markup, StringComparison.Ordinal);
        await cut.InvokeAsync(() => cut.Find("[data-testid=flow-trigger-request-system]").InputAsync(new() { Value = "Canonical.Source" }));
        await cut.InvokeAsync(() => cut.Find("[data-testid=flow-trigger-request-system-add]").ClickAsync(new()));
        Assert.Equal("Canonical.Source", Assert.Single(changed!.ExecutionTrigger!.RequestKinds!));
        Assert.Contains(FlowExecutionOutcome.Unknown, changed.ExecutionTrigger.Outcomes);
        cut.Render(p => p.Add(component => component.Node, changed));
        await cut.InvokeAsync(() => cut.Find("[data-testid=flow-trigger-source-Request]").ChangeAsync(new() { Value = false }));
        Assert.Contains("Retain at least one execution source", cut.Find("[role=alert]").TextContent, StringComparison.Ordinal);
        Assert.Contains(FlowExecutionSourceKind.Request, changed.ExecutionTrigger.Sources);

        _api.DefinitionSearch = (_, family, _, _, _, _) => Task.FromResult(new FlowExecutionDefinitionsPageDto(
            [new(family, new("42", $"Synthetic {family} 42"))]));
        var legacy = node with { ExecutionTrigger = new([FlowExecutionSourceKind.Job], [FlowExecutionOutcome.Failed], FlowExecutionScope.SelectedDefinitions) { DefinitionIds = ["42"] } };
        cut.Render(p => p.Add(component => component.Node, legacy));
        changed = null;
        await cut.InvokeAsync(() => cut.Find("[data-testid=flow-trigger-preset]").ChangeAsync(new() { Value = nameof(FlowExecutionSourceKind.Script) }));
        Assert.Null(changed);
        Assert.Contains("Remove the saved legacy definition filters", cut.Find("[role=alert]").TextContent, StringComparison.Ordinal);
        Assert.Equal(FlowExecutionSourceKind.Job, Assert.Single(cut.Instance.Node.ExecutionTrigger!.Sources));
        Assert.Equal("42", Assert.Single(cut.Instance.Node.ExecutionTrigger.DefinitionIds!));
        await cut.InvokeAsync(() => cut.Find("[data-testid=flow-trigger-source-Script]").ChangeAsync(new() { Value = true }));
        Assert.Null(changed);
        Assert.Equal(FlowExecutionSourceKind.Job, Assert.Single(cut.Instance.Node.ExecutionTrigger.Sources));
        Assert.Equal("42", Assert.Single(cut.Instance.Node.ExecutionTrigger.DefinitionIds!));

        await cut.InvokeAsync(() => cut.Find(".flow-trigger-selected button").ClickAsync(new()));
        Assert.Empty(changed!.ExecutionTrigger!.DefinitionIds!);
        cut.Render(p => p.Add(component => component.Node, changed));
        await cut.WaitForAssertionAsync(() => Assert.Contains("Synthetic Job 42", cut.Markup, StringComparison.Ordinal));
        await cut.InvokeAsync(() => cut.Find("[data-testid=flow-trigger-preset]").ChangeAsync(new() { Value = nameof(FlowExecutionSourceKind.Script) }));
        Assert.Equal(FlowExecutionSourceKind.Script, Assert.Single(changed!.ExecutionTrigger!.Sources));
        Assert.Empty(changed.ExecutionTrigger.DefinitionIds!);
        cut.Render(p => p.Add(component => component.Node, changed));
        await cut.WaitForAssertionAsync(() => Assert.Contains("Synthetic Script 42", cut.Markup, StringComparison.Ordinal));
        await cut.InvokeAsync(() => cut.Find("[data-testid=flow-definition-choice]").ClickAsync(new()));
        var reference = Assert.Single(changed!.ExecutionTrigger!.DefinitionReferences!);
        Assert.Equal(FlowExecutionSourceKind.Script, reference.SourceKind); Assert.Equal("42", reference.Id);
        cut.Render(p => p.Add(component => component.Node, changed));
        changed = null;
        await cut.InvokeAsync(() => cut.Find("[data-testid=flow-trigger-preset]").ChangeAsync(new() { Value = nameof(FlowExecutionSourceKind.Request) }));
        Assert.Null(changed);
        Assert.Contains("Remove incompatible selected definitions", cut.Find("[role=alert]").TextContent, StringComparison.Ordinal);
        Assert.Equal(FlowExecutionSourceKind.Script, Assert.Single(cut.Instance.Node.ExecutionTrigger!.Sources));

        var imported = node with { ExecutionTrigger = new([FlowExecutionSourceKind.Request], [FlowExecutionOutcome.Failed], FlowExecutionScope.SelectedDefinitions) { DefinitionReferences = [reference] } };
        cut.Render(p => p.Add(component => component.Node, imported));
        Assert.Equal(nameof(FlowExecutionSourceKind.Job), Assert.Single(cut.FindAll("[data-testid=flow-trigger-definition-family] option")).GetAttribute("value"));
        Assert.Contains("Request events expose job-backed definitions", cut.Markup, StringComparison.Ordinal);
        var retained = Assert.Single(cut.Instance.Node.ExecutionTrigger!.DefinitionReferences!);
        Assert.Equal(FlowExecutionSourceKind.Script, retained.SourceKind); Assert.Equal("42", retained.Id);
        Assert.Equal(0, _api.SaveCalls); Assert.Equal(0, _api.PublishCalls); Assert.Equal(0, _api.DryRunCalls);
    }

    [Fact]
    public async Task Script_Source_Uses_Its_Own_Native_Target_Catalogue_And_Retained_Names()
    {
        var agent = Guid.NewGuid();
        var node = new FlowNodeDto(Guid.NewGuid(), FlowNodeKind.ExecutionEvent, "Synthetic script events", new(0, 0))
        { ExecutionTrigger = new([FlowExecutionSourceKind.Script], [FlowExecutionOutcome.Failed], FlowExecutionScope.AllInTenant) { AgentIds = [agent] } };
        _api.TargetSearch = (_, source, _, _, _, _) =>
        {
            Assert.Equal(FlowExecutionSourceKind.Script, source); return Task.FromResult(new FlowExecutionTargetsPageDto([]));
        };
        _api.TargetSelections = (_, source, ids, _) =>
        {
            Assert.Equal(FlowExecutionSourceKind.Script, source);
            return Task.FromResult<IReadOnlyList<FlowExecutionTargetSelectionDto>>([new(new(ids[0], "Retained synthetic script endpoint"), false)]);
        };
        var cut = Render<FlowExecutionTriggerFields>(p => p.Add(component => component.TenantId, 17).Add(component => component.Node, node));
        await cut.WaitForAssertionAsync(() => Assert.Contains("Retained synthetic script endpoint", cut.Markup, StringComparison.Ordinal));
        Assert.Contains("Unavailable for new actions", cut.Markup, StringComparison.Ordinal);
        Assert.Equal(1, _api.TargetSearchCalls); Assert.Equal(1, _api.TargetSelectionCalls); Assert.Equal(0, _api.SaveCalls);
    }

    [Fact]
    public async Task Job_Action_Shows_Its_Bound_Target_And_Does_Not_Offer_Event_Target_Substitution()
    {
        var target = new FlowExecutionTargetDto(Guid.NewGuid(), "Synthetic bound job endpoint");
        var node = new FlowNodeDto(Guid.NewGuid(), FlowNodeKind.RunJob, "Run job", new(0, 0))
        { ExecutionAction = new(FlowExecutionSourceKind.Job, new("42", "Synthetic maintenance", "7")) { Target = target, Parameters = [] } };
        var cut = Render<FlowExecutionActionFields>(p => p.Add(component => component.TenantId, 17).Add(component => component.Node, node)
            .Add(component => component.Graph, new FlowGraphDto(2, [node], [], new())));
        Assert.Contains("Synthetic bound job endpoint", cut.Find("[data-testid=flow-job-bound-target]").TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[data-testid=flow-script-target-mode]")); Assert.Equal(0, _api.TargetSearchCalls);
        Assert.Equal(0, _api.DefinitionSearchCalls); Assert.Equal(0, _api.SaveCalls);
        cut.Render(p => p.Add(component => component.Node, node with { ExecutionAction = node.ExecutionAction! with { Target = null } }));
        Assert.Contains("Bound endpoint not configured", cut.Find("[data-testid=flow-job-bound-target]").TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("Source event endpoint", cut.Find("[data-testid=flow-job-bound-target]").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Template_Picker_Discards_Late_Tenant_Catalogue_And_Only_Emits_Current_Descriptor()
    {
        var pending = new TaskCompletionSource<IReadOnlyList<FlowTemplateDescriptorDto>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var template = new FlowTemplateDescriptorDto("script-recovery", "Synthetic repair and rerun", "Configure a pinned repair.", 2, ["Source scripts", "Repair script"]);
        _api.Templates = (tenant, _) => tenant == 17 ? pending.Task : Task.FromResult<IReadOnlyList<FlowTemplateDescriptorDto>>([template]);
        FlowTemplateDescriptorDto? selected = null;
        var cut = Render<FlowTemplatePicker>(p => p.Add(component => component.TenantId, 17).Add(component => component.Selected, value => selected = value));
        cut.Render(p => p.Add(component => component.TenantId, 23));
        await cut.WaitForAssertionAsync(() => Assert.Single(cut.FindAll("[data-testid=flow-template-create]")));
        pending.SetResult([template with { Name = "Old tenant definition" }]);
        await cut.WaitForAssertionAsync(() => Assert.DoesNotContain("Old tenant definition", cut.Markup, StringComparison.Ordinal));
        await cut.InvokeAsync(() => cut.Find("[data-testid=flow-template-create]").ClickAsync(new()));
        Assert.Same(template, selected); Assert.Equal(0, _api.TemplateCreates); Assert.Equal(0, _api.SaveCalls);
    }

    [Fact]
    public void Earlier_Output_Choices_Require_A_Producer_On_Every_Path_To_The_Consumer()
    {
        var trigger = new FlowNodeDto(Guid.NewGuid(), FlowNodeKind.ExecutionEvent, "Synthetic source", new(0, 0));
        var condition = new FlowNodeDto(Guid.NewGuid(), FlowNodeKind.Condition, "Branch", new(0, 0));
        var repair = new FlowNodeDto(Guid.NewGuid(), FlowNodeKind.RunLibraryScript, "Conditional repair", new(0, 0));
        var mapping = new FlowNodeDto(Guid.NewGuid(), FlowNodeKind.MapIncident, "Consumer", new(0, 0));
        var graph = new FlowGraphDto(2, [trigger, condition, repair, mapping],
            [new(trigger.Id, "event", condition.Id, "in"), new(condition.Id, "true", repair.Id, "in"),
             new(condition.Id, "false", mapping.Id, "in"), new(repair.Id, "succeeded", mapping.Id, "in")], new());
        var producers = FlowAuthoringContext.AvailableProducers(graph, mapping.Id);
        Assert.Contains(producers, node => node.Id == condition.Id);
        Assert.DoesNotContain(producers, node => node.Id == repair.Id || node.Id == mapping.Id);
    }
}
