using FluentAssertions;
using Xunit;

namespace NetRatel.Tests.Mcp;

public sealed class NetRatelMcpHttpDockerfileTests
{
    [Fact]
    public void Http_image_runs_unprivileged_and_has_an_internal_liveness_probe()
    {
        var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../"));
        var dockerfile = File.ReadAllText(Path.Combine(repositoryRoot, "docker", "mcp-http", "Dockerfile"));

        dockerfile.Should().Contain("ARG APP_UID=10001");
        dockerfile.Should().Contain("ASPNETCORE_URLS=http://+:9224");
        dockerfile.Should().Contain("http://127.0.0.1:9224/health/live");
        dockerfile.Should().Contain("USER netratel-mcp");
        dockerfile.Should().Contain("NetRatel.Mcp.Http.dll");
    }
}
