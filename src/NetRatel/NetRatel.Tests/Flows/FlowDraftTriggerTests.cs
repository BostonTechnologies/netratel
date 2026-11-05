using NetRatel.Shared.Contracts.Flows;
using Xunit;

namespace NetRatel.Tests.Flows;

public sealed class FlowDraftTriggerTests
{
    [Fact]
    public void Empty_selected_definition_scope_is_editable_but_never_means_all_tenant_at_publication()
    {
        var trigger = new FlowNodeDto(Guid.NewGuid(), FlowNodeKind.ExecutionEvent, "Choose a script", new(0, 0))
        { ExecutionTrigger = new([FlowExecutionSourceKind.Script], [FlowExecutionOutcome.Failed], FlowExecutionScope.SelectedDefinitions) };
        var finish = new FlowNodeDto(Guid.NewGuid(), FlowNodeKind.Finish, "Finish", new(300, 0));
        var graph = new FlowGraphDto(2, [trigger, finish],
            [new(trigger.Id, FlowNodeCatalogue.Event, finish.Id, FlowNodeCatalogue.Input) { Id = Guid.NewGuid() }], new());
        Assert.True(FlowGraphV2Validator.ValidateDraft(graph).Valid);
        Assert.Contains(FlowGraphV2Validator.ValidateComplete(graph).Issues, issue => issue.Code == "execution-trigger");
        Assert.Contains(FlowGraphV2Validator.ValidatePublished(graph).Issues, issue => issue.Code == "execution-trigger");
    }
}
