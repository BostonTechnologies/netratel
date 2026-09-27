using System;
using System.Collections.Generic;
using System.IO;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using NetRatel.Client;
using NetRatel.Client.Service.Gateway;
using NetRatel.Shared;
using Xunit;

namespace NetRatel.Tests.Client;

public sealed class ClientConfigurationLoaderTests
{
    private static string ClientProjectDirectory =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../NetRatel.Client"));

    [Fact]
    public void Load_PackagedDefaultsAndLegacySettings_PreserveInstalledValuesWithoutOverrides()
    {
        var root = Path.Combine(Path.GetTempPath(), $"netratel-client-config-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "clientsettings.json"), """
                {
                  "tenantId": "11111111-1111-1111-1111-111111111111",
                  "environment": "Prod",
                  "apiBaseUrl": "https://legacy.example.invalid",
                  "terminalBackendPreference": "Legacy",
                  "useInProcPowerShell": true,
                  "enableNativeUnixPty": false,
                  "autoUpdate": { "channel": "Prerelease" }
                }
                """);

            var (options, gatewayOptions) = LoadEffectiveOptions(root, new ConfigurationBuilder().Build(), []);

            options.TenantId.Should().Be(Guid.Parse("11111111-1111-1111-1111-111111111111"));
            options.Environment.Should().Be(ClientEnvironment.Prod);
            options.ApiBaseUrl.Should().Be("https://legacy.example.invalid");
            options.TerminalBackendPreference.Should().Be("Legacy");
            options.UseInProcPowerShell.Should().BeTrue();
            options.EnableNativeUnixPty.Should().BeFalse();
            options.AutoUpdate.Channel.Should().Be("Prerelease");
            gatewayOptions.Endpoint.Should().Be("https://legacy.example.invalid");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Load_ExplicitServiceApiOverrideWinsWithoutResettingInstalledOptions()
    {
        var root = Path.Combine(Path.GetTempPath(), $"netratel-client-config-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "clientsettings.json"), """
                {
                  "tenantId": "11111111-1111-1111-1111-111111111111",
                  "environment": "Prod",
                  "apiBaseUrl": "https://legacy.example.invalid",
                  "terminalBackendPreference": "Legacy",
                  "autoUpdate": { "channel": "Prerelease" }
                }
                """);

            var deployment = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Client:ApiBaseUrl"] = "https://service.example.invalid/tenant"
                })
                .Build();

            var (options, gatewayOptions) = LoadEffectiveOptions(root, deployment, []);

            options.ApiBaseUrl.Should().Be("https://service.example.invalid/tenant");
            options.TenantId.Should().Be(Guid.Parse("11111111-1111-1111-1111-111111111111"));
            options.Environment.Should().Be(ClientEnvironment.Prod);
            options.TerminalBackendPreference.Should().Be("Legacy");
            options.AutoUpdate.Channel.Should().Be("Prerelease");
            gatewayOptions.Endpoint.Should().Be("https://service.example.invalid/tenant");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Load_CommandLineApiSelectionWinsOverLegacyAndServiceConfiguration()
    {
        var root = Path.Combine(Path.GetTempPath(), $"netratel-client-config-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "clientsettings.json"), """
                {
                  "apiBaseUrl": "https://legacy.example.invalid",
                  "terminalBackendPreference": "Legacy"
                }
                """);

            var deployment = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Client:ApiBaseUrl"] = "https://service.example.invalid"
                })
                .Build();

            var (options, gatewayOptions) = LoadEffectiveOptions(
                root,
                deployment,
                ["--api", "https://command.example.invalid/tenant"]);

            options.ApiBaseUrl.Should().Be("https://command.example.invalid/tenant");
            options.TerminalBackendPreference.Should().Be("Legacy");
            gatewayOptions.Endpoint.Should().Be("https://command.example.invalid/tenant");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Load_LegacyGatewayEndpointRemainsAnExplicitOverride()
    {
        var root = Path.Combine(Path.GetTempPath(), $"netratel-client-config-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "clientsettings.json"), """
                {
                  "apiBaseUrl": "https://legacy-api.example.invalid",
                  "Gateway": { "Endpoint": "https://legacy-gateway.example.invalid" }
                }
                """);

            var (options, gatewayOptions) = LoadEffectiveOptions(root, new ConfigurationBuilder().Build(), []);

            options.ApiBaseUrl.Should().Be("https://legacy-api.example.invalid");
            gatewayOptions.Endpoint.Should().Be("https://legacy-gateway.example.invalid");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Load_DeploymentAndCommandLineGatewayEndpointsOverrideLowerLayers()
    {
        var root = Path.Combine(Path.GetTempPath(), $"netratel-client-config-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "clientsettings.json"), """
                {
                  "apiBaseUrl": "https://legacy-api.example.invalid",
                  "Gateway": { "Endpoint": "https://legacy-gateway.example.invalid" }
                }
                """);

            var deployment = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Client:ApiBaseUrl"] = "https://service-api.example.invalid",
                    ["Gateway:Endpoint"] = "https://service-gateway.example.invalid"
                })
                .Build();

            var (deploymentOptions, deploymentGateway) = LoadEffectiveOptions(root, deployment, []);
            deploymentGateway.Endpoint.Should().Be("https://service-gateway.example.invalid");

            var (commandOptions, commandGateway) = LoadEffectiveOptions(
                root,
                deployment,
                ["--api", "https://command-api.example.invalid", "--Gateway:Endpoint=https://command-gateway.example.invalid"]);

            commandOptions.ApiBaseUrl.Should().Be("https://command-api.example.invalid");
            deploymentOptions.ApiBaseUrl.Should().Be("https://service-api.example.invalid");
            commandGateway.Endpoint.Should().Be("https://command-gateway.example.invalid");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Load_AbsentOrEmptyGatewayEndpointUsesEffectiveApiBaseUrl(string? gatewayEndpoint)
    {
        var root = Path.Combine(Path.GetTempPath(), $"netratel-client-config-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "clientsettings.json"), """
                { "apiBaseUrl": "https://legacy-api.example.invalid" }
                """);

            var values = new Dictionary<string, string?>
            {
                ["Client:ApiBaseUrl"] = "https://deployment-api.example.invalid"
            };
            if (gatewayEndpoint is not null)
            {
                values["Gateway:Endpoint"] = gatewayEndpoint;
            }

            var deployment = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
            var (options, gatewayOptions) = LoadEffectiveOptions(root, deployment, []);

            options.ApiBaseUrl.Should().Be("https://deployment-api.example.invalid");
            gatewayOptions.Endpoint.Should().Be(options.ApiBaseUrl);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Load_FreshServiceWithoutLegacyFile_UsesBrandingDerivedApiAcrossProcessRestarts()
    {
        var root = Path.Combine(Path.GetTempPath(), $"netratel-client-config-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var deployment = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Client:ApiBaseUrl"] = "https://branding.example.invalid"
                })
                .Build();

            var firstProcess = ClientConfigurationLoader.Load(
                ClientConfigurationLoader.BuildPackagedDefaults(ClientProjectDirectory, null),
                deployment,
                root,
                []);
            var restartedProcess = ClientConfigurationLoader.Load(
                ClientConfigurationLoader.BuildPackagedDefaults(ClientProjectDirectory, null),
                deployment,
                root,
                []);

            File.Exists(Path.Combine(root, "clientsettings.json")).Should().BeFalse();
            firstProcess.ApiBaseUrl.Should().Be("https://branding.example.invalid");
            restartedProcess.ApiBaseUrl.Should().Be(firstProcess.ApiBaseUrl);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Load_LegacyOptionsRemainAvailableThroughCompatibilityOverload()
    {
        var root = Path.Combine(Path.GetTempPath(), $"netratel-client-config-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "clientsettings.json"), """
                { "apiBaseUrl": "https://legacy.example.invalid", "terminalBackendPreference": "Legacy" }
                """);

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Client:ApiBaseUrl"] = "https://service.example.invalid/tenant",
                    ["Client:TerminalBackendPreference"] = "Service"
                })
                .Build();

            var options = ClientConfigurationLoader.Load(configuration, root, []);

            options.ApiBaseUrl.Should().Be("https://service.example.invalid/tenant");
            options.TerminalBackendPreference.Should().Be("Service");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static (ClientOptions Client, GatewayClientOptions Gateway) LoadEffectiveOptions(
        string appBaseDir,
        IConfiguration deploymentOverrides,
        IReadOnlyList<string> args)
    {
        var packagedDefaults = ClientConfigurationLoader.BuildPackagedDefaults(ClientProjectDirectory, null);
        var effectiveConfiguration = ClientConfigurationLoader.BuildEffectiveConfiguration(
            packagedDefaults,
            deploymentOverrides,
            appBaseDir,
            args);
        var clientOptions = ClientConfigurationLoader.Load(
            packagedDefaults,
            deploymentOverrides,
            appBaseDir,
            args);
        return (
            clientOptions,
            ClientConfigurationLoader.LoadGatewayOptions(effectiveConfiguration, clientOptions.ApiBaseUrl));
    }
}
