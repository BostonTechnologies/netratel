using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NetRatel.Client;
using NetRatel.Infrastructure.Identity.Branding;
using Xunit;

namespace NetRatel.Tests.Client;

[CollectionDefinition("Endpoint environment", DisableParallelization = true)]
public sealed class EndpointEnvironmentCollection;

[Collection("Endpoint environment")]
public sealed class EndpointEnvironmentOptionsTests
{
    [Fact]
    public void Docker_environment_binds_both_api_and_native_client_addresses()
    {
        var values = new Dictionary<string, string>
        {
            ["Branding__SiteUrl"] = "https://web.example.test",
            ["Branding__GatewayUrl"] = "https://gateway.example.test",
            ["NetRatelCLIENT__Client__ApiBaseUrl"] = "https://web.example.test/api/",
            ["NetRatelCLIENT__Gateway__Endpoint"] = "https://gateway.example.test/"
        };
        var previous = values.Keys.ToDictionary(key => key, Environment.GetEnvironmentVariable);
        try
        {
            foreach (var (key, value) in values) Environment.SetEnvironmentVariable(key, value);
            var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();
            using var services = new ServiceCollection().AddClientInstallationOptions(configuration).BuildServiceProvider();
            var branding = services.GetRequiredService<IOptionsMonitor<DeploymentBrandingOptions>>().CurrentValue;
            branding.SiteUrl.Should().Be("https://web.example.test");
            branding.GatewayUrl.Should().Be("https://gateway.example.test");

            var deployment = ClientConfigurationLoader.BuildDeploymentOverrides();
            var packaged = new ConfigurationBuilder().Build();
            var client = ClientConfigurationLoader.ResolveClientOptions(packaged, deployment, Path.GetTempPath(), []);
            var effective = ClientConfigurationLoader.BuildEffectiveConfiguration(packaged, deployment, Path.GetTempPath(), []);
            var gateway = ClientConfigurationLoader.ResolveGatewayOptions(effective, client.Options.ApiBaseUrl, packaged, deployment, Path.GetTempPath(), []);
            client.ConfiguredOptions.Value.ApiBaseUrl.Should().Be("https://web.example.test");
            gateway.ConfiguredOptions.Value.Endpoint.Should().Be("https://gateway.example.test");
            client.ApiBaseUrlSource.Should().Be("deployment-configuration");
            gateway.Source.Should().Be("deployment-configuration");

            var overridden = ClientConfigurationLoader.ResolveClientOptions(packaged, deployment, Path.GetTempPath(), ["--api", "https://one-shot.example.test"]);
            overridden.Options.ApiBaseUrl.Should().Be("https://one-shot.example.test");
            ClientConfigurationLoader.ResolveGatewayOptions(effective, overridden.Options.ApiBaseUrl, packaged, deployment, Path.GetTempPath(), [])
                .Options.Endpoint.Should().Be("https://gateway.example.test");
        }
        finally
        {
            foreach (var (key, value) in previous) Environment.SetEnvironmentVariable(key, value);
        }
    }
}
