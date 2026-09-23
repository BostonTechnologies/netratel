using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using NetRatel.Shared.Contracts.Scripts;

using Xunit;

[Trait("category", "integration")]
[Collection(ApiIntegrationCollection.Name)]
public class LiveSmokeTests
{
    private static readonly IConfiguration EnvConfig = new ConfigurationBuilder().AddEnvironmentVariables().Build();
    private readonly ApiFactory _factory;

    public LiveSmokeTests(ApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task SystemVersion_IsAvailableAnonymously()
    {
        SkipIfApiPrerequisitesAreMissing();

        using var client = _factory.CreateClient();

        var version = await client.GetFromJsonAsync<SystemVersionResponse>("/api/v1/system/version");

        version.Should().NotBeNull();
        version!.ServiceName.Should().Be("NetRatel.API");
        version.DisplayVersion.Should().Be("v0.1.0-rc.5");
        version.InformationalVersion.Should().NotBeNullOrWhiteSpace();
        version.AssemblyVersion.Should().NotBeNullOrWhiteSpace();
        version.Environment.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task OldUnversionedApiRoute_NoLongerMatches()
    {
        SkipIfApiPrerequisitesAreMissing();

        using var client = _factory.CreateClient();

        var oldRoute = await client.GetAsync("/api/system/version");

        oldRoute.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public void HasRequiredEnv()
    {
        SkipIfApiPrerequisitesAreMissing();
    }

    [Fact]
    public async Task Health_and_MinimalRoundTrip()
    {
        SkipIfApiPrerequisitesAreMissing();

        using var client = _factory.CreateClient();

        var health = await client.GetAsync("/health");
        health.EnsureSuccessStatusCode();

        var scriptName = $"int-test-{Guid.NewGuid():N}";
        var create = new CreateScriptRequest(scriptName, "/", "test", "echo hi", "bash");
        var createResponse = await client.PostAsJsonAsync("/api/v1/script-library", create);
        createResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var scripts = await client.GetFromJsonAsync<List<ScriptResponse>>("/api/v1/script-library");
        var created = scripts?.FirstOrDefault(s => s.Name == scriptName);
        created.Should().NotBeNull();

        var deleteResponse = await client.DeleteAsync($"/api/v1/script-library/{created!.Id}");
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    private static void SkipIfApiPrerequisitesAreMissing()
    {
        var postgresConnection = EnvConfig.GetConnectionString("DefaultConnection")
            ?? EnvConfig["ConnectionStrings__DefaultConnection"];
        var privateKeyPath = EnvConfig["AgentAuth:PrivateKeyPath"] ?? EnvConfig["AGENTAUTH__PRIVATEKEYPATH"];
        Assert.SkipWhen(
            string.IsNullOrWhiteSpace(postgresConnection) || string.IsNullOrWhiteSpace(privateKeyPath),
            "ConnectionStrings:DefaultConnection and AgentAuth:PrivateKeyPath must be set to run API integration tests.");
    }

    private sealed record SystemVersionResponse(
        string ServiceName,
        string DisplayVersion,
        string InformationalVersion,
        string AssemblyVersion,
        string Environment);
}
