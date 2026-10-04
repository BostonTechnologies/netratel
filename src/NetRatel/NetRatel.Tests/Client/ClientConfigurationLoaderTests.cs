using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using NetRatel.Client;
using NetRatel.Client.Service.Gateway;
using NetRatel.Shared;
using Xunit;

namespace NetRatel.Tests.Client;

public sealed class ClientConfigurationLoaderTests
{
    private static readonly string[] RetiredGatewayBooleanSettings =
    [
        "TelemetryShadowEnabled", "TelemetryAuthorityEnabled", "CommandAuthorityEnabled", "JobAuthorityEnabled",
        "TerminalAuthorityEnabled", "ControlAuthorityEnabled", "FileAuthorityEnabled", "LogAuthorityEnabled",
        "RemoteSupportAuthorityEnabled", "RemoteSupportV1Enabled", "ControlGatewayEnabled", "FileGatewayEnabled",
        "LogGatewayEnabled", "RemoteSupportGatewayEnabled", "TerminalGatewayEnabled",
        "RemoteSupportV2InventoryEnabled", "RemoteSupportV2MediaEnabled"
    ];

    private static string ClientProjectDirectory =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../NetRatel.Client"));

    [Theory]
    [InlineData("packaged-defaults", false)]
    [InlineData("installed-settings", false)]
    [InlineData("installed-settings", true)]
    [InlineData("deployment-configuration", false)]
    [InlineData("command-line", false)]
    public void ResolveClientOptions_ReportsTheEffectiveApiOriginAndItsSource(string source, bool nestedInstalledSettings)
    {
        var root = Path.Combine(Path.GetTempPath(), $"netratel-api-source-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var packaged = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Client:ApiBaseUrl"] = "https://packaged-defaults.example.invalid/api/"
            }).Build();
            if (source != "packaged-defaults")
            {
                File.WriteAllText(Path.Combine(root, "clientsettings.json"), nestedInstalledSettings
                    ? """{ "Client": { "ApiBaseUrl": "https://installed-settings.example.invalid/api/" } }"""
                    : """{ "apiBaseUrl": "https://installed-settings.example.invalid/api/" }""");
            }
            var deploymentValues = new Dictionary<string, string?>();
            if (source is "deployment-configuration" or "command-line")
                deploymentValues["Client:ApiBaseUrl"] = "https://deployment-configuration.example.invalid/api/";
            var deployment = new ConfigurationBuilder().AddInMemoryCollection(deploymentValues).Build();
            string[] args = source == "command-line" ? ["--api", "https://command-line.example.invalid/api/"] : [];

            var resolution = ClientConfigurationLoader.ResolveClientOptions(packaged, deployment, root, args);

            resolution.ApiBaseUrlSource.Should().Be(source);
            resolution.Options.ApiBaseUrl.Should().Be($"https://{source}.example.invalid");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

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
                    ["Client:ApiBaseUrl"] = "https://service.example.invalid/api/"
                })
                .Build();

            var (options, gatewayOptions) = LoadEffectiveOptions(root, deployment, []);

            options.ApiBaseUrl.Should().Be("https://service.example.invalid");
            options.TenantId.Should().Be(Guid.Parse("11111111-1111-1111-1111-111111111111"));
            options.Environment.Should().Be(ClientEnvironment.Prod);
            options.TerminalBackendPreference.Should().Be("Legacy");
            options.AutoUpdate.Channel.Should().Be("Prerelease");
            gatewayOptions.Endpoint.Should().Be("https://service.example.invalid");
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
                ["--api", "https://command.example.invalid/api"]);

            options.ApiBaseUrl.Should().Be("https://command.example.invalid");
            options.TerminalBackendPreference.Should().Be("Legacy");
            gatewayOptions.Endpoint.Should().Be("https://command.example.invalid");
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
    public void Load_UsesEffectiveApiWhenFlatLegacyGatewayRepeatsItsLegacyApiOrigin()
    {
        var root = Path.Combine(Path.GetTempPath(), $"netratel-client-config-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "clientsettings.json"), """
                {
                  "apiBaseUrl": "https://legacy-api.example.invalid/api/",
                  "Gateway": { "Endpoint": "https://legacy-api.example.invalid" }
                }
                """);
            var deployment = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Client:ApiBaseUrl"] = "https://deployment-api.example.invalid"
                })
                .Build();

            var (options, gatewayOptions) = LoadEffectiveOptions(root, deployment, []);

            options.ApiBaseUrl.Should().Be("https://deployment-api.example.invalid");
            gatewayOptions.Endpoint.Should().Be(options.ApiBaseUrl);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("true")]
    [InlineData("false")]
    public void Load_RetiredGatewaySelectorsAreInertWhileSupportedClientTunablesRemainActive(string? retiredBooleanValue)
    {
        var root = Path.Combine(Path.GetTempPath(), $"netratel-client-config-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var gatewayEntries = new StringBuilder("\"ProtocolVersion\":\"1.0\",\"TelemetryFastIntervalSeconds\":17," +
                "\"PresenceBootstrapTimeoutSeconds\":11,\"PresenceTeardownTimeoutSeconds\":4,\"PresenceStabilityThresholdSeconds\":180");
            if (retiredBooleanValue is not null)
            {
                foreach (var name in RetiredGatewayBooleanSettings)
                {
                    gatewayEntries.Append(",\"").Append(name).Append("\":").Append(retiredBooleanValue);
                }
            }
            gatewayEntries.Append(",\"RequiredPresenceAuthority\":\"legacy-wire-label\"");

            File.WriteAllText(Path.Combine(root, "clientsettings.json"), $$"""
                {
                  "Client": {
                    "ApiBaseUrl": "https://installed-api.example.invalid/api/",
                    "TerminalBackendPreference": "Legacy"
                  },
                  "Gateway": { {{gatewayEntries}} },
                  "Transport": { "Mode": "{{(retiredBooleanValue == "true" ? "removed-runtime" : "AkkaPresence")}}" }
                }
                """);

            var (clientOptions, gatewayOptions) = LoadEffectiveOptions(root, new ConfigurationBuilder().Build(), []);

            clientOptions.ApiBaseUrl.Should().Be("https://installed-api.example.invalid");
            clientOptions.TerminalBackendPreference.Should().Be("Legacy");
            gatewayOptions.Endpoint.Should().Be(clientOptions.ApiBaseUrl);
            gatewayOptions.ProtocolVersion.Should().Be("1.0");
            gatewayOptions.TelemetryFastIntervalSeconds.Should().Be(17);
            gatewayOptions.PresenceBootstrapTimeoutSeconds.Should().Be(11);
            gatewayOptions.PresenceTeardownTimeoutSeconds.Should().Be(4);
            gatewayOptions.PresenceStabilityThresholdSeconds.Should().Be(180);
            typeof(GatewayClientOptions).GetProperties().Select(property => property.Name)
                .Should().BeEquivalentTo("Endpoint", "ProtocolVersion", "TelemetryFastIntervalSeconds",
                    "TelemetrySlowIntervalSeconds", "TelemetryInteractiveIntervalMilliseconds",
                    "TelemetryMinimumIntervalMilliseconds", "TelemetryPolicyMaximumLifetimeSeconds",
                    "PresenceBootstrapTimeoutSeconds", "PresenceTeardownTimeoutSeconds", "PresenceStabilityThresholdSeconds");
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

    [Fact]
    public void Load_CommandLineApiOverridePreservesExplicitSameOriginDeploymentGateway()
    {
        var root = Path.Combine(Path.GetTempPath(), $"netratel-client-config-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var deployment = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Client:ApiBaseUrl"] = "https://origin-a.example.invalid",
                    ["Gateway:Endpoint"] = "https://origin-a.example.invalid"
                })
                .Build();

            var (options, gatewayOptions) = LoadEffectiveOptions(
                root,
                deployment,
                ["--api", "https://origin-b.example.invalid"]);

            options.ApiBaseUrl.Should().Be("https://origin-b.example.invalid");
            gatewayOptions.Endpoint.Should().Be("https://origin-a.example.invalid");
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
    public void Load_UsesEffectiveApiWhenLegacyPackagedGatewayEndpointRepeatsItsPackagedApiOrigin()
    {
        var root = Path.Combine(Path.GetTempPath(), $"netratel-client-config-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "appsettings.json"), """
                {
                  "Client": { "ApiBaseUrl": "https://packaged-api.example.invalid/api/" },
                  "Gateway": { "Endpoint": "https://packaged-api.example.invalid" }
                }
                """);
            var packagedDefaults = ClientConfigurationLoader.BuildPackagedDefaults(root, null);
            var deployment = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Client:ApiBaseUrl"] = "https://deployment-api.example.invalid/api/"
                })
                .Build();
            var effective = ClientConfigurationLoader.BuildEffectiveConfiguration(packagedDefaults, deployment, root, []);
            var client = ClientConfigurationLoader.Load(packagedDefaults, deployment, root, []);
            var gateway = ClientConfigurationLoader.LoadGatewayOptions(
                effective, client.ApiBaseUrl, packagedDefaults, deployment, root, []);

            client.ApiBaseUrl.Should().Be("https://deployment-api.example.invalid");
            gateway.Endpoint.Should().Be(client.ApiBaseUrl);
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
                    ["Client:ApiBaseUrl"] = "https://service.example.invalid/api/",
                    ["Client:TerminalBackendPreference"] = "Service"
                })
                .Build();

            var options = ClientConfigurationLoader.Load(configuration, root, []);

            options.ApiBaseUrl.Should().Be("https://service.example.invalid");
            options.TerminalBackendPreference.Should().Be("Service");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("https://service.example.invalid/tenant")]
    [InlineData("https://service.example.invalid/api/v1")]
    [InlineData("https://service.example.invalid/api/v2")]
    public void Load_RejectsUnrecognizedApiPathBaseInsteadOfSilentlyDroppingIt(string apiBaseUrl)
    {
        var root = Path.Combine(Path.GetTempPath(), $"netratel-client-config-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Client:ApiBaseUrl"] = apiBaseUrl
                })
                .Build();

            FluentActions.Invoking(() => ClientConfigurationLoader.Load(configuration, root, []))
                .Should().Throw<ArgumentException>()
                .WithMessage("*origin, optionally followed by /api*");
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
            ClientConfigurationLoader.LoadGatewayOptions(
                effectiveConfiguration,
                clientOptions.ApiBaseUrl,
                packagedDefaults,
                deploymentOverrides,
                appBaseDir,
                args));
    }
}
