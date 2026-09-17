using FluentAssertions;
using Xunit;

namespace NetRatel.Tests.Platform;

public sealed class Net10TargetFrameworkTests
{
    private static readonly string[] RetainedProjects =
    [
        "src/NetRatel/NetRatel.AgentClient/NetRatel.AgentClient.csproj",
        "src/NetRatel/NetRatel.AgentGateway.Contracts/NetRatel.AgentGateway.Contracts.csproj",
        "src/NetRatel/NetRatel.Akka/NetRatel.Akka.csproj",
        "src/NetRatel/NetRatel.API/NetRatel.API.csproj",
        "src/NetRatel/NetRatel.AppHost/NetRatel.AppHost.csproj",
        "src/NetRatel/NetRatel.Application/NetRatel.Application.csproj",
        "src/NetRatel/NetRatel.Cli/NetRatel.Cli.csproj",
        "src/NetRatel/NetRatel.Client/NetRatel.Client.csproj",
        "src/NetRatel/NetRatel.Infrastructure/NetRatel.Infrastructure.csproj",
        "src/NetRatel/NetRatel.Mcp/NetRatel.Mcp.csproj",
        "src/NetRatel/NetRatel.ServiceDefaults/NetRatel.ServiceDefaults.csproj",
        "src/NetRatel/NetRatel.Shared/NetRatel.Shared.csproj",
        "src/NetRatel/NetRatel.Tests/NetRatel.Tests.csproj",
        "src/NetRatel/NetRatel.Web/NetRatel.Web.csproj",
        "src/NetRatel/NetRatel.API.IntegrationTests/NetRatel.API.IntegrationTests.csproj",
        "src/NetRatel/NetRatel.Web.ComponentTests/NetRatel.Web.ComponentTests.csproj"
    ];

    [Fact]
    public void RetainedProjects_TargetNet10()
    {
        var repositoryRoot = GetRepositoryRoot();

        foreach (var project in RetainedProjects)
        {
            File.ReadAllText(Path.Combine(repositoryRoot, project))
                .Should().Contain("<TargetFramework>net10.0</TargetFramework>", project);
        }
    }

    [Fact]
    public void GlobalJson_PinsTheNet10Sdk()
    {
        File.ReadAllText(Path.Combine(GetRepositoryRoot(), "global.json"))
            .Should().Contain("\"version\": \"10.0.302\"")
            .And.Contain("\"rollForward\": \"disable\"");
    }

    [Theory]
    [InlineData("docker/api/Dockerfile")]
    [InlineData("docker/web/Dockerfile")]
    [InlineData("docker/client/Dockerfile.public")]
    public void DockerBuildStages_PinTheSdkRequiredByGlobalJson(string dockerfile)
    {
        File.ReadAllText(Path.Combine(GetRepositoryRoot(), dockerfile))
            .Should().Contain("mcr.microsoft.com/dotnet/sdk:10.0.302-noble", dockerfile);
    }

    private static string GetRepositoryRoot() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../"));
}
