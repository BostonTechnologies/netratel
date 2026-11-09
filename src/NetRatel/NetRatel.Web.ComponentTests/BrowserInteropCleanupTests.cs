using Microsoft.JSInterop;
using NetRatel.Web.Services;
using Xunit;

namespace NetRatel.Web.ComponentTests;

public sealed class BrowserInteropCleanupTests
{
    [Fact]
    public async Task Observer_cleanup_failure_still_releases_its_handle_and_every_module()
    {
        var observer = new FailingReference(true); var first = new FailingReference(false); var second = new FailingReference(false);
        await BrowserInteropCleanup.ReleaseAsync(Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, observer, first, second);
        Assert.Equal(1, observer.CleanupCalls);
        Assert.Equal(1, observer.Releases); Assert.Equal(1, first.Releases); Assert.Equal(1, second.Releases);
    }
    private sealed class FailingReference(bool failCleanup) : IJSObjectReference
    {
        public int CleanupCalls { get; private set; }
        public int Releases { get; private set; }
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, default, args);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            Assert.Equal("dispose", identifier); CleanupCalls++;
            return failCleanup ? ValueTask.FromException<TValue>(new JSException("Synthetic viewport cleanup failure")) : ValueTask.FromResult(default(TValue)!);
        }
        public ValueTask DisposeAsync() { Releases++; return ValueTask.FromException(new JSException("Synthetic handle release failure")); }
    }
}
