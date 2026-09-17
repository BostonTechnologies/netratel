using FluentAssertions;
using NetRatel.API.Services.Orchestration;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class NetRatelCatalogMetadataProjectionTests
{
    [Fact]
    public void BuildClientDisplayName_Prefers_HostName_For_Catalog_Display()
    {
        var display = NetRatelCatalogMetadataProjection.BuildClientDisplayName(
            "btstodevagent01",
            "NetRatel Dev",
            "C2009662A43E");

        display.Should().Be("btstodevagent01");
    }

    [Fact]
    public void BuildClientDisplayName_Falls_Back_To_Client_Name_Then_Short_Id()
    {
        NetRatelCatalogMetadataProjection.BuildClientDisplayName(null, "NetRatel Dev", "C2009662A43E")
            .Should().Be("NetRatel Dev");

        NetRatelCatalogMetadataProjection.BuildClientDisplayName(null, null, "C2009662A43E")
            .Should().Be("C2009662A43E");
    }

    [Fact]
    public void BuildClientShortId_Truncates_Long_Client_Identity()
    {
        var shortId = NetRatelCatalogMetadataProjection.BuildClientShortId(
            "C2009662A43EF5AF08CC173C5E32E73447A98BD99CC7DDA641F3C466B95CDAD7");

        shortId.Should().Be("C2009662A43E");
    }

    [Fact]
    public void ResolveScriptType_Returns_Single_Type_Or_Mixed()
    {
        NetRatelCatalogMetadataProjection.ResolveScriptType([" Bash ", "bash", null])
            .Should().Be("Bash");

        NetRatelCatalogMetadataProjection.ResolveScriptType(["Bash", "PowerShell"])
            .Should().Be("Mixed");
    }

    [Fact]
    public void ResolveScriptType_Returns_Null_When_No_Linked_Library_Type_Exists()
    {
        NetRatelCatalogMetadataProjection.ResolveScriptType([null, "", " "])
            .Should().BeNull();
    }
}
