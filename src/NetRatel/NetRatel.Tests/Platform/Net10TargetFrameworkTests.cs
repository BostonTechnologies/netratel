using System.Text.Json;
using AwesomeAssertions;
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
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(GetRepositoryRoot(), "global.json")));
        var sdk = document.RootElement.GetProperty("sdk");
        sdk.GetProperty("version").GetString().Should().MatchRegex(@"^10\.0\.\d{3}$");
        sdk.GetProperty("rollForward").GetString().Should().Be("disable");
    }

    [Theory]
    [InlineData("docker/api/Dockerfile")]
    [InlineData("docker/web/Dockerfile")]
    [InlineData("docker/client/Dockerfile.public")]
    [InlineData("docker/mcp-http/Dockerfile")]
    [InlineData("docker/migrations/Dockerfile")]
    public void DockerBuildStages_PinTheSdkRequiredByGlobalJson(string dockerfile)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(GetRepositoryRoot(), "global.json")));
        var sdkVersion = document.RootElement.GetProperty("sdk").GetProperty("version").GetString();
        File.ReadAllText(Path.Combine(GetRepositoryRoot(), dockerfile))
            .Should().Contain($"mcr.microsoft.com/dotnet/sdk:{sdkVersion}-noble", dockerfile);
    }

    private static string GetRepositoryRoot() =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../"));
}
