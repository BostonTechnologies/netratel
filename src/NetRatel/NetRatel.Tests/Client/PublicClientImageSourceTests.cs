using FluentAssertions;
using Xunit;

namespace NetRatel.Tests.Client;

public sealed class PublicClientImageSourceTests
{
    [Fact]
    public void Public_client_image_has_no_private_operator_dependencies()
    {
        var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../"));
        var dockerfile = File.ReadAllText(Path.Combine(repositoryRoot, "docker", "client", "Dockerfile.public"));

        dockerfile.Should().Contain("NetRatel.Client.csproj");
        dockerfile.Should().Contain("--runtime linux-x64");
        dockerfile.Should().NotContain("PRIVATE_MCP_");
        dockerfile.Should().NotContain("external-service-github-token");
        dockerfile.Should().NotContain("netclaw-source");
        dockerfile.Should().NotContain("git clone");
    }
}
