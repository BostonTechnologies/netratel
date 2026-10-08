using VeloxDev.WorkflowSystem.AttachedBehaviors;

namespace NetRatel.Web.Components.Pages.Flows;

/// <summary>Keep the installed native surface's JS initialization alive until its teardown can safely run.</summary>
public sealed class FlowSurfaceBehavior : WorkflowSurfaceBehavior, IAsyncDisposable
{
    private Task _render = Task.CompletedTask;
    private bool _disposing;

    protected override bool ShouldRender() => !_disposing && base.ShouldRender();

    protected override Task OnAfterRenderAsync(bool firstRender) => _render = RenderAsync(_render, firstRender);

    private async Task RenderAsync(Task previous, bool firstRender)
    {
        await previous;
        if (!_disposing) await base.OnAfterRenderAsync(firstRender);
    }

    public new async ValueTask DisposeAsync()
    {
        _disposing = true;
        try { await _render; }
        finally { await base.DisposeAsync(); }
    }
}
