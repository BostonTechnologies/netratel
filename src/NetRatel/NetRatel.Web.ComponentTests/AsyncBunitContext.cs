using Bunit;
using Xunit;

namespace NetRatel.Web.ComponentTests;

public abstract class AsyncBunitContext : BunitContext, IAsyncLifetime
{
    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    async ValueTask IAsyncDisposable.DisposeAsync() => await base.DisposeAsync();
}
