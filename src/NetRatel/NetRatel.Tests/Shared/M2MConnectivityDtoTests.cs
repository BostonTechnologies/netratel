using System.Text.Json;
using NetRatel.Shared.Connectivity;
using Xunit;

public class M2MConnectivityDtoTests
{
    [Fact]
    public void SettingsDto_Serializes_And_Deserializes()
    {
        var dto = new M2MConnectivitySettingsDto(true, "https://remote", "aud", "ExternalService");
        var json = JsonSerializer.Serialize(dto);
        var back = JsonSerializer.Deserialize<M2MConnectivitySettingsDto>(json);
        Assert.NotNull(back);
        Assert.True(back!.Enabled);
        Assert.Equal("https://remote", back.RemoteBaseUrl);
        Assert.Equal("aud", back.RemoteAudience);
        Assert.Equal("ExternalService", back.RemoteSystemName);
    }
}
