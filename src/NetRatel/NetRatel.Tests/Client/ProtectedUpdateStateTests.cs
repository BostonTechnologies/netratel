using NetRatel.Client.Service.Updates;
using Xunit;

namespace NetRatel.Tests.Client;

public sealed class ProtectedUpdateStateTests
{
    [Theory]
    [InlineData("S-1-5-18", true)]
    [InlineData("S-1-5-32-544", true)]
    [InlineData("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464", true)]
    [InlineData("S-1-5-21-1-2-3-1001", true)]
    [InlineData("S-1-5-80-1-2-3-4-5", true)]
    [InlineData("S-1-5-80-6-7-8-9-10", false)]
    [InlineData("S-1-5-21-1-2-3-1002", false)]
    [InlineData("S-1-5-32-545", false)]
    [InlineData("S-1-1-0", false)]
    [InlineData(null, false)]
    public void ResultReaderAcceptsNativeUpdaterTrustedIdentitiesButRejectsOtherWriters(string? sid, bool expected)
    {
        Assert.Equal(expected, ProtectedUpdateState.IsTrustedWindowsSid(sid,
            currentSid: "S-1-5-21-1-2-3-1001", serviceSid: "S-1-5-80-1-2-3-4-5"));
    }
}
