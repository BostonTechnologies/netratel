using System;
using System.Collections.Generic;
using System.IO;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using NetRatel.Client;
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

            var options = ClientConfigurationLoader.Load(
                ClientConfigurationLoader.BuildPackagedDefaults(ClientProjectDirectory, null),
                new ConfigurationBuilder().Build(),
                root,
                []);

            options.TenantId.Should().Be(Guid.Parse("11111111-1111-1111-1111-111111111111"));
            options.Environment.Should().Be(ClientEnvironment.Prod);
            options.ApiBaseUrl.Should().Be("https://legacy.example.invalid");
            options.TerminalBackendPreference.Should().Be("Legacy");
            options.UseInProcPowerShell.Should().BeTrue();
            options.EnableNativeUnixPty.Should().BeFalse();
            options.AutoUpdate.Channel.Should().Be("Prerelease");
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

            var options = ClientConfigurationLoader.Load(
                ClientConfigurationLoader.BuildPackagedDefaults(ClientProjectDirectory, null),
                deployment,
                root,
                []);

            options.ApiBaseUrl.Should().Be("https://service.example.invalid/tenant");
            options.TenantId.Should().Be(Guid.Parse("11111111-1111-1111-1111-111111111111"));
            options.Environment.Should().Be(ClientEnvironment.Prod);
            options.TerminalBackendPreference.Should().Be("Legacy");
            options.AutoUpdate.Channel.Should().Be("Prerelease");
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

            var options = ClientConfigurationLoader.Load(
                ClientConfigurationLoader.BuildPackagedDefaults(ClientProjectDirectory, null),
                deployment,
                root,
                ["--api", "https://command.example.invalid/tenant"]);

            options.ApiBaseUrl.Should().Be("https://command.example.invalid/tenant");
            options.TerminalBackendPreference.Should().Be("Legacy");
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
}
