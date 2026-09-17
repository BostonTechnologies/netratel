using FluentAssertions;
using Xunit;

namespace NetRatel.Tests.Client;

public sealed class LegacyClientSpacetimeServiceRemovalTests
{
    [Fact]
    public void RemovedClientServices_HaveNoSourceFilesOrCallers()
    {
        var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../"));
        var removedFiles = new[]
        {
            "src/NetRatel/NetRatel.Client/Service/ClientConsoleManager.cs",
            "src/NetRatel/NetRatel.Client/Service/LogService.cs",
            "src/NetRatel/NetRatel.Client/Service/RundeckJob/IRundeckJobService.cs",
            "src/NetRatel/NetRatel.Client/Service/RundeckJob/RundeckJobService.cs",
            "src/NetRatel/NetRatel.Infrastructure/Auth/FileAuthTokenStore.cs"
        };

        foreach (var removedFile in removedFiles)
        {
            File.Exists(Path.Combine(repositoryRoot, removedFile)).Should().BeFalse();
        }

        var fileSystemSource = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src/NetRatel/NetRatel.Client/Service/FileSystemService.cs"));
        fileSystemSource.Should().NotContain("FileSystemRequest");
        fileSystemSource.Should().NotContain("EventContext");

        var clientOptionsSource = File.ReadAllText(Path.Combine(repositoryRoot, "src/NetRatel/NetRatel.Client/ClientOptions.cs"));
        clientOptionsSource.Should().NotContain("TokenPath");
    }
}
