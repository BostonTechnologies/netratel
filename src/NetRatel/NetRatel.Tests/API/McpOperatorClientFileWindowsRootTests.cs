using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace NetRatel.Tests.API;

public sealed partial class McpOperatorClientFileEndpointTests
{
    [Theory]
    [InlineData("C:/", @"C:\Windows\Temp\marker.txt", @"C:\")]
    [InlineData("C:/Windows/Temp", @"c:\windows\temp\marker.txt", @"C:\Windows\Temp")]
    [InlineData(@"C:\Windows\Temp", @"C:\Windows\Temp\marker.txt", @"C:\Windows\Temp")]
    public async Task WindowsPolicyRoots_AdmitCanonicalReadsAndPassNativeRootsToGateway(string policyRoot, string path, string gatewayRoot)
    {
        var agentId = Guid.NewGuid();
        using var app = await BuildAppAsync(agentId, includeReadRoot: true);
        app.Services.GetRequiredService<TestAdmission>().ReadRootsOverride = [policyRoot];
        var client = AuthorizedClient(app);
        var root = $"/api/v2/mcp/operator/agents/7/{agentId:D}/files";

        var stat = await client.GetAsync($"{root}/stat?path={Uri.EscapeDataString(path)}");
        var read = await client.GetAsync($"{root}/read?path={Uri.EscapeDataString(path)}");

        stat.StatusCode.Should().Be(HttpStatusCode.OK);
        read.StatusCode.Should().Be(HttpStatusCode.OK);
        (await read.Content.ReadAsStringAsync()).Should().Contain("aGVhbHRoeS1nYXRld2F5");
        app.Services.GetRequiredService<TestFileRegistry>().StatPolicies.Should().ContainSingle()
            .Which.AllowedRoots.Should().Equal(gatewayRoot);
        app.Services.GetRequiredService<TestFileRegistry>().ReadPolicies.Should().ContainSingle()
            .Which.AllowedRoots.Should().Equal(gatewayRoot);
    }

    [Fact]
    public async Task WindowsPolicyRoots_ConfirmedWritePreservesContentAndBindsWriteRoot()
    {
        var agentId = Guid.NewGuid();
        using var app = await BuildAppAsync(agentId, includeReadRoot: true, includeWriteRoot: true);
        app.Services.GetRequiredService<TestAdmission>().WriteRootsOverride = ["C:/Windows/Temp"];
        var client = AuthorizedClient(app);
        var root = $"/api/v2/mcp/operator/agents/7/{agentId:D}/files/write-text";
        const string path = @"C:\Windows\Temp\marker.txt";
        const string text = "  marker\r\n";

        (await client.PostAsJsonAsync($"{root}/preview", new { path, text })).StatusCode.Should().Be(HttpStatusCode.OK);
        var response = await client.PostAsJsonAsync($"{root}/confirm", new
        {
            path,
            text,
            planToken = TestConfirmations.PlanToken,
            idempotencyKey = TestConfirmations.IdempotencyKey
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var gateway = app.Services.GetRequiredService<TestFileRegistry>();
        gateway.WritePolicies.Should().ContainSingle().Which.AllowedRoots.Should().Equal(@"C:\Windows\Temp");
        gateway.WrittenContent.Should().Equal(System.Text.Encoding.UTF8.GetBytes(text));
    }

    [Fact]
    public async Task WindowsPolicyRoots_ArtifactFingerprintSurvivesEquivalentRootSpellingButRejectsRevokedRoot()
    {
        var agentId = Guid.NewGuid();
        using var app = await BuildAppAsync(agentId, includeReadRoot: true);
        var admission = app.Services.GetRequiredService<TestAdmission>();
        admission.ReadRootsOverride = ["C:/Windows/Temp"];
        var client = AuthorizedClient(app);
        var root = $"/api/v2/mcp/operator/agents/7/{agentId:D}/files/artifacts";
        const string path = @"C:\Windows\Temp\marker.txt";

        (await client.PostAsJsonAsync($"{root}/collect/preview", new { path })).StatusCode.Should().Be(HttpStatusCode.OK);
        var collect = await client.PostAsJsonAsync($"{root}/collect/confirm", new
        {
            path,
            planToken = TestConfirmations.PlanToken,
            idempotencyKey = TestConfirmations.IdempotencyKey
        });
        collect.StatusCode.Should().Be(HttpStatusCode.Created);
        var artifact = (await collect.Content.ReadFromJsonAsync<ArtifactMetadata>())!;
        admission.ReadRootsOverride = [@"C:\Windows\Temp"];
        (await client.GetAsync($"{root}/{artifact.ArtifactId:D}")).StatusCode.Should().Be(HttpStatusCode.OK);
        var download = await client.GetAsync($"{root}/{artifact.ArtifactId:D}/download");
        download.StatusCode.Should().Be(HttpStatusCode.OK);
        (await download.Content.ReadAsStringAsync()).Should().Contain("aGVhbHRoeS1nYXRld2F5");

        admission.ReadRootsOverride = ["D:/"];
        (await client.GetAsync($"{root}/{artifact.ArtifactId:D}/download")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData(@"C:\Windows\Temporary\marker.txt")]
    [InlineData(@"C:\Windows\Temp\..\marker.txt")]
    [InlineData(@"C:\Windows\Temp\.env")]
    [InlineData(@"C:\Windows\Temp\secrets\marker.txt")]
    [InlineData(@"D:\Windows\Temp\marker.txt")]
    [InlineData("C:/Windows/Temp/marker.txt")]
    public async Task WindowsPolicyRoots_DoNotBroadenCanonicalRequestOrContainmentRules(string path)
    {
        var agentId = Guid.NewGuid();
        using var app = await BuildAppAsync(agentId, includeReadRoot: true);
        var admission = app.Services.GetRequiredService<TestAdmission>();
        admission.ReadRootsOverride = ["C:/Windows/Temp"];

        var response = await AuthorizedClient(app).GetAsync(
            $"/api/v2/mcp/operator/agents/7/{agentId:D}/files/read?path={Uri.EscapeDataString(path)}");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        admission.Accepted.Should().BeEmpty();
        app.Services.GetRequiredService<TestFileRegistry>().ReadPolicies.Should().BeEmpty();
    }

    [Theory]
    [InlineData("C:/", @"C:\")]
    [InlineData("C:/Windows/Temp", @"C:\Windows\Temp")]
    public async Task WindowsPolicyRoots_CannotDeleteTheAdmittedRoot(string policyRoot, string path)
    {
        var agentId = Guid.NewGuid();
        using var app = await BuildAppAsync(agentId, includeReadRoot: true, includeWriteRoot: true);
        var admission = app.Services.GetRequiredService<TestAdmission>();
        admission.WriteRootsOverride = [policyRoot];

        var response = await AuthorizedClient(app).PostAsJsonAsync(
            $"/api/v2/mcp/operator/agents/7/{agentId:D}/files/delete/preview", new { path });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        admission.Accepted.Should().BeEmpty();
        app.Services.GetRequiredService<TestFileRegistry>().DeletePolicies.Should().BeEmpty();
    }
}
