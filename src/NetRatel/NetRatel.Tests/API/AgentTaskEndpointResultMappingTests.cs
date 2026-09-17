using FluentAssertions;
using NetRatel.API.Endpoints;
using NetRatel.Application.Jobs;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class AgentTaskEndpointResultMappingTests
{
    [Fact]
    public void Map_UsesPersistedResultJsonForReturnDataAndKeepsErrorAsStatusSummary()
    {
        const string result = "{\"stdout\":[\"done\"],\"exitCode\":0}";
        var task = AgentTaskEndpoints.Map(CreateActivity("Completed", null, result), null);

        task.ReturnData.Should().Be(result);
        task.StatusMessage.Should().BeNull();
        task.ExitCode.Should().Be(0);
    }

    [Fact]
    public void Map_ProjectsLegacyJsonStoredInErrorAsReturnDataWithoutExposingItAsStatus()
    {
        const string legacyResult = "{\"stdout\":[\"done\"],\"stderr\":[\"permission denied\"],\"exitCode\":1}";
        var task = AgentTaskEndpoints.Map(CreateActivity("Failed", legacyResult), null);

        task.ReturnData.Should().Be(legacyResult);
        task.StatusMessage.Should().Be("permission denied");
        task.ExitCode.Should().Be(1);
    }

    private static JobTaskActivityInfo CreateActivity(string status, string? error, string? resultJson = null) => new(
        42,
        "request-42",
        null,
        null,
        "client",
        3,
        "exec-library-script",
        status,
        error,
        DateTimeOffset.UtcNow,
        status == "Processing" ? null : DateTimeOffset.UtcNow,
        null,
        resultJson);
}
