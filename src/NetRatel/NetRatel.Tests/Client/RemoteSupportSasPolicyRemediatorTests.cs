using NetRatel.Client.Service.RemoteSupport;
using System.Runtime.Versioning;
using Xunit;

namespace NetRatel.Tests.Client;

[SupportedOSPlatform("windows")]
public sealed class RemoteSupportSasPolicyRemediatorTests
{
    [Theory]
    [InlineData(null, 1)]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(2, 3)]
    [InlineData(3, 3)]
    public void EnablingServiceSasPreservesExistingEaseOfAccessPermission(int? current, int expected)
    {
        Assert.Equal(expected, RemoteSupportSasPolicyRemediator.EnabledValue(current));
    }
}
