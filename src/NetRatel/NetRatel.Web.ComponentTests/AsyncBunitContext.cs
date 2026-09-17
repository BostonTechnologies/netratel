using Bunit;
using Xunit;

namespace NetRatel.Web.ComponentTests;

public abstract class AsyncBunitContext : BunitContext, IAsyncLifetime
{
    public Task InitializeAsync() => Task.CompletedTask;

    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();
}
