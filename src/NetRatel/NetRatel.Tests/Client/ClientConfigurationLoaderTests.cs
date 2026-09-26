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
    [Fact]
    public void Load_ServiceConfigurationWinsOverLegacySettings_AndCommandLineWinsLast()
    {
        var root = Path.Combine(Path.GetTempPath(), $"netratel-client-config-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "clientsettings.json"), """
                {
                  "tenantId": "11111111-1111-1111-1111-111111111111",
                  "environment": 1,
                  "apiBaseUrl": "https://legacy.example.invalid",
                  "terminalBackendPreference": "Legacy"
                }
                """);

            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Client:TenantId"] = "22222222-2222-2222-2222-222222222222",
                    ["Client:Environment"] = "Prod",
                    ["Client:ApiBaseUrl"] = "https://service.example.invalid/tenant",
                    ["Client:TerminalBackendPreference"] = "Service"
                })
                .Build();

            var options = ClientConfigurationLoader.Load(
                configuration,
                root,
                ["--api", "https://command.example.invalid/tenant"]);

            options.TenantId.Should().Be(Guid.Parse("22222222-2222-2222-2222-222222222222"));
            options.Environment.Should().Be(ClientEnvironment.Prod);
            options.ApiBaseUrl.Should().Be("https://command.example.invalid/tenant");
            options.TerminalBackendPreference.Should().Be("Service");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
