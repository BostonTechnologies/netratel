using System.Text.Json;
using NetRatel.AgentClient;
using NetRatel.Setup;
using Xunit;

namespace NetRatel.Tests.Mcp;

public sealed class HttpHostingSetupCommandTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "netratel-host-command-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Noninteractive_generation_reports_whether_complete_deployment_preflight_was_possible(bool completeSource)
    {
        string? secretBefore = null;
        if (completeSource)
        {
            var request = HttpHostingDeploymentFixture.WithSource(new(_directory, "https://instance.example", "https://instance.example"));
            new NetRatelHttpHostingSetupService().Apply(request);
            secretBefore = File.ReadAllText(Path.Combine(_directory, "secrets", "outbound-m2m.secret"));
        }
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exit = await SetupCommand.RunAsync(["setup", "--target", "http-host", "--non-interactive", "--instance", "https://instance.example",
            "--auth-mode", "local", "--deployment-manager", "release-compose", "--output-directory", _directory, "--replace"],
            ConfigurationSurface.HttpMcpOutbound, output, error, interactive: false);
        Assert.Equal(0, exit);
        Assert.Equal("", error.ToString());
        using var document = JsonDocument.Parse(output.ToString());
        var receipt = document.RootElement;
        Assert.True(receipt.GetProperty("generationOnly").GetBoolean());
        Assert.False(receipt.GetProperty("deploymentApplied").GetBoolean());
        Assert.False(receipt.GetProperty("endToEndVerified").GetBoolean());
        Assert.Equal(completeSource ? "completed" : "pending", receipt.GetProperty("deploymentPreflight").GetString());
        if (completeSource)
        {
            Assert.Equal(64, receipt.GetProperty("registrationFingerprint").GetString()!.Length);
            Assert.NotEqual(JsonValueKind.Null, receipt.GetProperty("applyCommand").ValueKind);
            Assert.Equal(secretBefore, File.ReadAllText(Path.Combine(_directory, "secrets", "outbound-m2m.secret")));
        }
        else
        {
            Assert.False(receipt.TryGetProperty("registrationFingerprint", out var fingerprint) && fingerprint.ValueKind != JsonValueKind.Null);
            Assert.False(receipt.TryGetProperty("applyCommand", out var applyCommand) && applyCommand.ValueKind != JsonValueKind.Null);
            Assert.DoesNotContain(" up -d ", output.ToString());
            Assert.True(File.Exists(Path.Combine(_directory, "compose.http-host.yaml")));
            Assert.True(File.Exists(Path.Combine(_directory, "secrets", "outbound-m2m.secret")));
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
