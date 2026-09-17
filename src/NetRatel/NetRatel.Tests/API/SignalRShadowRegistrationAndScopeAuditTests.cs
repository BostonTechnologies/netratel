using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NetRatel.API.Gateway;
using NetRatel.API.Realtime.Shadow;
using NetRatel.Akka.Configuration;
using Xunit;

namespace NetRatel.Tests.API;

[Collection(NetRatel.Tests.Akka.NetRatelAkkaTelemetryCollection.Name)]
public sealed class SignalRShadowRegistrationAndScopeAuditTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "../../../../../../"));

    [Fact]
    public void ProductionAndDevelopmentConfiguration_DefaultBothSignalRFlagsOff()
    {
        foreach (var relativePath in new[]
                 {
                     "src/NetRatel/NetRatel.API/appsettings.json",
                     "src/NetRatel/NetRatel.API/appsettings.Development.json"
                 })
        {
            using var document = JsonDocument.Parse(Read(relativePath));
            var section = document.RootElement.GetProperty("NetRatelAkkaMigration");
            section.GetProperty("SignalRShadowEnabled").GetBoolean().Should().BeFalse();
            section.GetProperty("SignalRShadowLocalCanaryEnabled").GetBoolean().Should().BeFalse();
        }
    }

    [Fact]
    public void Registration_IsDefaultOffAndValidatorRequiresExplicitNestedCanaryFlag()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        services.AddNetRatelAkkaMigration(configuration);
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IShadowFanoutSink>().Should().BeOfType<NullShadowFanoutSink>();
        services.Should().NotContain(descriptor => descriptor.ServiceType == typeof(SignalRShadowFanoutBridge));
        provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations
            .Should().Contain(registration => registration.Name == "akka-signalr-shadow");

        var validator = new NetRatelAkkaMigrationOptionsValidator();
        validator.Validate(null, new()
        {
            Enabled = true,
            SignalRShadowEnabled = true,
            SignalRShadowLocalCanaryEnabled = true
        }).Succeeded.Should().BeTrue();
        validator.Validate(null, new()
        {
            Enabled = true,
            SignalRShadowEnabled = false,
            SignalRShadowLocalCanaryEnabled = true
        }).FailureMessage.Should().Contain("requires SignalRShadowEnabled");
        validator.Validate(null, new()
        {
            Enabled = true,
            SignalRShadowEnabled = true,
            SignalRShadowLocalCanaryEnabled = false
        }).FailureMessage.Should().Contain("requires SignalRShadowLocalCanaryEnabled");
    }

    [Fact]
    public async Task LocalCanary_RegistersBridgeHubEndpointAndHealthOnlyWhenExplicitlyEnabled()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development
        });
        builder.Configuration.AddInMemoryCollection(EnabledConfiguration());
        builder.Services.AddAuthorization();
        builder.Services.AddNetRatelAkkaMigration(builder.Configuration, builder.Environment);

        await using var app = builder.Build();
        app.MapSignalRShadowEndpoints();

        app.Services.GetRequiredService<IShadowFanoutSink>()
            .Should().BeOfType<SignalRShadowFanoutBridge>();
        app.Services.GetRequiredService<IShadowFanoutSnapshotSource>()
            .Should().BeSameAs(app.Services.GetRequiredService<IShadowFanoutSink>());
        app.Services.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations
            .Should().Contain(registration => registration.Name == "akka-signalr-shadow");
        Routes(app).Should().Contain(SignalRShadowEndpointRegistrationExtensions.HubPath);
    }

    [Fact]
    public async Task EnabledFlags_InProductionStillDoNotMapTheLocalCanaryEndpoint()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Production
        });
        builder.Configuration.AddInMemoryCollection(EnabledConfiguration());
        builder.Services.AddAuthorization();
        builder.Services.AddNetRatelAkkaMigration(builder.Configuration, builder.Environment);

        await using var app = builder.Build();
        app.MapSignalRShadowEndpoints();

        app.Services.GetRequiredService<IShadowFanoutSink>()
            .Should().BeOfType<NullShadowFanoutSink>();
        Routes(app).Should().NotContain(SignalRShadowEndpointRegistrationExtensions.HubPath);
    }

    [Fact]
    public void ForbiddenSurfacesAndProductionUiRemainOutsideTheShadowFanout()
    {
        var webSources = Directory.EnumerateFiles(
                Path.Combine(RepoRoot, "src", "NetRatel", "NetRatel.Web"),
                "*",
                SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".cs", StringComparison.Ordinal) ||
                           path.EndsWith(".razor", StringComparison.Ordinal))
            .Select(File.ReadAllText)
            .ToArray();
        webSources.Should().OnlyContain(source =>
            !source.Contains("AkkaShadowHub", StringComparison.Ordinal) &&
            !source.Contains("/hubs/akka-shadow", StringComparison.Ordinal) &&
            !source.Contains("SignalRShadow", StringComparison.Ordinal));

        Directory.Exists(Path.Combine(RepoRoot, "src/NetRatel/NetRatel.Shared/module_bindings"))
            .Should().BeFalse();
        File.Exists(Path.Combine(RepoRoot, "NetRatel.Server/StdbModule.csproj"))
            .Should().BeFalse();
        Read("src/NetRatel/NetRatel.AgentGateway.Contracts/Protos/agent_gateway.proto").Should().NotContain("SignalRShadow");
        Read("src/NetRatel/NetRatel.Akka/NetRatel.Akka.csproj").Should().Contain("Akka.Cluster.Hosting");
        Read("src/NetRatel/NetRatel.Shared/NetRatel.Shared.csproj").Should().NotContain("SpacetimeDB");
    }

    private static Dictionary<string, string?> EnabledConfiguration() => new()
    {
        ["NetRatelAkkaMigration:Enabled"] = "true",
        ["NetRatelAkkaMigration:SignalRShadowEnabled"] = "true",
        ["NetRatelAkkaMigration:SignalRShadowLocalCanaryEnabled"] = "true"
    };

    private static IEnumerable<string?> Routes(WebApplication app) =>
        ((IEndpointRouteBuilder)app).DataSources
        .SelectMany(static dataSource => dataSource.Endpoints)
        .OfType<RouteEndpoint>()
        .Select(static endpoint => endpoint.RoutePattern.RawText);

    private static string Read(string relativePath) =>
        File.ReadAllText(Path.Combine(RepoRoot, relativePath));
}
