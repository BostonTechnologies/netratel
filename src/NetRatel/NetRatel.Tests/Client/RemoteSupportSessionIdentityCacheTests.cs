using NetRatel.Client.Service.RemoteSupport;
using Xunit;

namespace NetRatel.Tests.Client;

public sealed class RemoteSupportSessionIdentityCacheTests
{
    [Fact]
    public void TokenSuccessFailureSuccessKeepsExactSessionSidStable()
    {
        var cache = new RemoteSupportSessionIdentityCache();

        Assert.Equal("S-1-5-21-100", cache.ResolveSid(10, "HOST", "admin", "S-1-5-21-100", true));
        Assert.Equal("S-1-5-21-100", cache.ResolveSid(10, "HOST", "admin", null, true));
        Assert.Equal("S-1-5-21-100", cache.ResolveSid(10, "HOST", "admin", "S-1-5-21-100", true));
    }

    [Fact]
    public void FirstTokenFailureRemainsPendingUntilSidResolves()
    {
        var cache = new RemoteSupportSessionIdentityCache();

        Assert.Null(cache.ResolveSid(1, "HOST", "user", null, true));
        Assert.Equal("S-1-5-21-200", cache.ResolveSid(1, "HOST", "user", "S-1-5-21-200", true));
    }

    [Fact]
    public void MissingDomainCanReuseSameSessionAndUsernameIdentity()
    {
        var cache = new RemoteSupportSessionIdentityCache();

        cache.ResolveSid(4, "HOST", "user", "S-1-5-21-300", true);

        Assert.Equal("S-1-5-21-300", cache.ResolveSid(4, null, "USER", null, true));
    }

    [Fact]
    public void AccountChangeDisconnectAndMissingSessionEvictIdentity()
    {
        var cache = new RemoteSupportSessionIdentityCache();
        cache.ResolveSid(4, "HOST", "first", "S-1-5-21-400", true);

        Assert.Null(cache.ResolveSid(4, "HOST", "second", null, true));
        Assert.False(cache.Contains(4));

        cache.ResolveSid(4, "HOST", "second", "S-1-5-21-401", true);
        Assert.Null(cache.ResolveSid(4, "HOST", "second", null, false));
        Assert.False(cache.Contains(4));

        cache.ResolveSid(5, "HOST", "third", "S-1-5-21-500", true);
        cache.Reconcile([6]);
        Assert.False(cache.Contains(5));
    }
}
