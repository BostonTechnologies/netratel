using NetRatel.Application.Flows;
using NetRatel.Shared.Contracts.Flows;
using VeloxDev.Core.WorkflowSystem.CompilerEx;
using VeloxDev.WorkflowSystem;

namespace NetRatel.Infrastructure.Flows;

public sealed partial class VeloxFlowRuntimeAdapter
{
    public async Task<T> ExecuteNodeAsync<T>(Guid runId, FlowNodeDto definition,
        Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(operation);
        if (runId == Guid.Empty || FlowNodeCatalogue.Find(definition.Kind) is null)
            throw new ArgumentException("An allowlisted node and a real run identity are required.");
        var tree = new TreeDefaultViewModel();
        var state = new BoundaryState<T>();
        try
        {
            var node = new NodeDefaultViewModel();
            node.SetHelper(new BoundaryHelper<T>(operation, state));
            await CompleteCommandAsync(tree.CreateNodeCommand, node, cancellationToken).ConfigureAwait(false);
            foreach (var port in FlowNodeCatalogue.Find(definition.Kind)!.Ports)
            {
                var slot = new SlotDefaultViewModel();
                await CompleteCommandAsync(node.CreateSlotCommand, slot, cancellationToken).ConfigureAwait(false);
                await CompleteCommandAsync(slot.SetChannelCommand,
                    port.Direction == FlowPortDirection.Output ? SlotChannel.OneTarget : SlotChannel.OneSource,
                    cancellationToken).ConfigureAwait(false);
            }
            var plans = await new CompilerViewModel().CompileAsync(node, CompileRole.Root, cancellationToken).ConfigureAwait(false);
            if (plans.Count != 1) throw new InvalidOperationException("runtime-node-plan");
            var context = new RuntimeContext { Uid = runId };
            await new RuntimeEngine().RunAsync(plans[0], context, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (context.EndedWithError || state.Calls != 1 || !state.HasResult)
                throw new InvalidOperationException("runtime-node-failed");
            return state.Result!;
        }
        finally { await tree.GetHelper().CloseAsync().ConfigureAwait(false); }
    }

    private sealed class BoundaryState<T>
    {
        public int Calls;
        public bool HasResult { get; set; }
        public T? Result { get; set; }
    }
    private sealed class BoundaryHelper<T>(Func<CancellationToken, Task<T>> operation, BoundaryState<T> state) : NodeHelper
    {
        public override async Task<object?> ReceiveAsync(ITaskContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.CompareExchange(ref state.Calls, 1, 0) != 0)
                throw new InvalidOperationException("runtime-node-repeat");
            state.Result = await operation(cancellationToken).ConfigureAwait(false);
            state.HasResult = true;
            return state.Result;
        }
    }
}
