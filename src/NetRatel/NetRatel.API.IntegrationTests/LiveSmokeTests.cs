using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NetRatel.API.Bootstrap;
using NetRatel.Shared.Contracts.Scripts;

using Xunit;

[Trait("category", "integration")]
[Collection(ApiIntegrationCollection.Name)]
public class LiveSmokeTests
{
    private readonly ApiFactory _factory;

    public LiveSmokeTests(ApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task SystemVersion_IsAvailableAnonymously()
    {
        using var client = _factory.CreateClient();

        var version = await client.GetFromJsonAsync<SystemVersionResponse>("/api/v1/system/version");

        version.Should().NotBeNull();
        version!.ServiceName.Should().Be("NetRatel.API");
        var assemblyVersion = typeof(BootstrapLifecycleService).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];
        version.DisplayVersion.Should().Be($"v{assemblyVersion}");
        version.InformationalVersion.Should().NotBeNullOrWhiteSpace();
        version.AssemblyVersion.Should().NotBeNullOrWhiteSpace();
        version.Environment.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task OldUnversionedApiRouteIsNotRegisteredAndAnonymousRequestRemainsProtected()
    {
        using var client = _factory.CreateClient();

        var oldRoute = await client.GetAsync("/api/system/version");

        oldRoute.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "the API authorization fallback continues to protect unmatched public API paths");

        var localAdministrator = await _factory.CreateLocalAdministratorClientAsync();
        using (localAdministrator)
        {
            var authenticatedOldRoute = await localAdministrator.GetAsync("/api/system/version");
            authenticatedOldRoute.StatusCode.Should().Be(HttpStatusCode.Forbidden,
                "the local instance-administrator identity is not an OIDC Operator claim");
        }

        var routes = _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText)
            .ToArray();
        routes.Should().Contain("/api/v1/system/version");
        routes.Should().NotContain("/api/system/version");
    }

    [Fact]
    public async Task InitializedProductionHostHasHealthyDependenciesAndHealthRoutesRemainProtected()
    {
        using var client = _factory.CreateClient();

        var live = await client.GetAsync("/health/live");
        live.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "health routes require the configured bearer or machine-to-machine identity");
        var ready = await client.GetAsync("/health/ready");
        ready.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var report = await _factory.Services.GetRequiredService<HealthCheckService>().CheckHealthAsync();
        report.Status.Should().Be(HealthStatus.Healthy,
            "the initialized production fixture has its database, actor runtime, and signing key available; " +
            string.Join("; ", report.Entries.Select(entry => $"{entry.Key}: {entry.Value.Status} {entry.Value.Description}")));
    }

    [Fact]
    public async Task MinimalScriptLibraryRoundTrip()
    {
        using var client = await _factory.CreateLocalAdministratorClientAsync();

        var scriptName = $"int-test-{Guid.NewGuid():N}";
        var create = new CreateScriptRequest(scriptName, "/", "test", "echo hi", "bash");
        var createResponse = await client.PostAsJsonAsync("/api/v1/script-library", create);
        createResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var scripts = await client.GetFromJsonAsync<List<ScriptResponse>>("/api/v1/script-library");
        var created = scripts?.FirstOrDefault(s => s.Name == scriptName);
        created.Should().NotBeNull();

        var deleteResponse = await client.DeleteAsync(
            $"/api/v1/script-library/{created!.Id}?expectedSourceRevision={created.SourceRevision}");
        deleteResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    private sealed record SystemVersionResponse(
        string ServiceName,
        string DisplayVersion,
        string InformationalVersion,
        string AssemblyVersion,
        string Environment);
}
