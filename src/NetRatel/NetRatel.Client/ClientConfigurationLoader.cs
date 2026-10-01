using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Configuration;
using NetRatel.Client.Service.Gateway;
using NetRatel.Shared;
using NetRatel.Shared.Client;

namespace NetRatel.Client;

internal static class ClientConfigurationLoader
{
    internal sealed record GatewayOptionsResolution(GatewayClientOptions Options, string Source);

    internal static IConfiguration BuildPackagedDefaults(string appBaseDir, string? environmentName)
    {
        var builder = new ConfigurationBuilder()
            .SetBasePath(appBaseDir)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false);

        if (!string.IsNullOrWhiteSpace(environmentName))
        {
            builder.AddJsonFile($"appsettings.{environmentName}.json", optional: true, reloadOnChange: false);
        }

        return builder.Build();
    }

    internal static IConfiguration BuildDeploymentOverrides()
    {
        return new ConfigurationBuilder()
            .AddEnvironmentVariables(prefix: "NetRatelCLIENT__")
            .AddEnvironmentVariables()
            .Build();
    }

    internal static IConfiguration BuildEffectiveConfiguration(
        IConfiguration? packagedDefaults,
        IConfiguration? deploymentOverrides,
        string appBaseDir,
        IReadOnlyList<string> args)
    {
        var builder = new ConfigurationBuilder().SetBasePath(appBaseDir);
        if (packagedDefaults is not null)
        {
            builder.AddConfiguration(packagedDefaults);
        }

        if (File.Exists(Path.Combine(appBaseDir, "clientsettings.json")))
        {
            builder.AddJsonFile("clientsettings.json", optional: false, reloadOnChange: false);
        }

        if (deploymentOverrides is not null)
        {
            builder.AddConfiguration(deploymentOverrides);
        }

        builder.AddCommandLine(args.ToArray());
        return builder.Build();
    }

    internal static GatewayClientOptions LoadGatewayOptions(
        IConfiguration configuration,
        string apiBaseUrl,
        IConfiguration? packagedDefaults,
        IConfiguration? deploymentOverrides,
        string appBaseDir,
        IReadOnlyList<string> args)
        => ResolveGatewayOptions(configuration, apiBaseUrl, packagedDefaults, deploymentOverrides, appBaseDir, args).Options;

    internal static GatewayOptionsResolution ResolveGatewayOptions(
        IConfiguration configuration,
        string apiBaseUrl,
        IConfiguration? packagedDefaults,
        IConfiguration? deploymentOverrides,
        string appBaseDir,
        IReadOnlyList<string> args)
    {
        var options = new GatewayClientOptions();
        configuration.GetSection("Gateway").Bind(options);
        var endpoint = packagedDefaults?["Gateway:Endpoint"];
        var source = string.IsNullOrWhiteSpace(endpoint) ? "api-base-url" : "packaged-defaults";
        if (!string.IsNullOrWhiteSpace(endpoint) &&
            HasRedundantPairedGatewayDefault(packagedDefaults, ["Client:ApiBaseUrl", "ApiBaseUrl"]))
        {
            endpoint = apiBaseUrl;
            source = "api-base-url";
        }

        var installedSettingsPath = string.IsNullOrWhiteSpace(appBaseDir)
            ? string.Empty
            : Path.Combine(appBaseDir, "clientsettings.json");
        if (!string.IsNullOrWhiteSpace(installedSettingsPath) && File.Exists(installedSettingsPath))
        {
            var installedSettings = new ConfigurationBuilder()
                .AddJsonFile(installedSettingsPath, optional: false, reloadOnChange: false)
                .Build();
            var installedEndpoint = installedSettings["Gateway:Endpoint"];
            if (installedEndpoint is not null)
            {
                var isPairedDefault = HasRedundantPairedGatewayDefault(installedSettings, ["Client:ApiBaseUrl", "ApiBaseUrl"]);
                endpoint = isPairedDefault ? apiBaseUrl : installedEndpoint;
                source = isPairedDefault || string.IsNullOrWhiteSpace(installedEndpoint)
                    ? "api-base-url"
                    : "installed-settings";
            }
        }

        // Deployment and command-line gateway values are explicit choices. Keep them
        // even when they currently match their API value: a higher-precedence API-only
        // override must not erase an intentionally pinned gateway origin.
        if (deploymentOverrides?["Gateway:Endpoint"] is { } deploymentEndpoint)
        {
            endpoint = deploymentEndpoint;
            source = string.IsNullOrWhiteSpace(deploymentEndpoint) ? "api-base-url" : "deployment-configuration";
        }

        var commandLineOverrides = new ConfigurationBuilder()
            .AddCommandLine(args.ToArray())
            .Build();
        if (commandLineOverrides["Gateway:Endpoint"] is { } commandLineEndpoint)
        {
            endpoint = commandLineEndpoint;
            source = string.IsNullOrWhiteSpace(commandLineEndpoint) ? "api-base-url" : "command-line";
        }

        if (string.IsNullOrWhiteSpace(endpoint))
        {
            endpoint = apiBaseUrl;
            source = "api-base-url";
        }

        options.Endpoint = ClientEndpointAddress.NormalizeGatewayBase(endpoint);
        return new GatewayOptionsResolution(options, source);
    }

    private static bool HasRedundantPairedGatewayDefault(
        IConfiguration? configuration,
        IReadOnlyList<string> apiKeys)
    {
        if (configuration is not IConfigurationRoot root)
        {
            return false;
        }

        var providers = root.Providers.ToArray();
        for (var index = providers.Length - 1; index >= 0; index--)
        {
            var provider = providers[index];
            if (!provider.TryGet("Gateway:Endpoint", out var gatewayEndpoint))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(gatewayEndpoint))
            {
                return false;
            }

            string? pairedApiBaseUrl = null;
            foreach (var apiKey in apiKeys)
            {
                if (provider.TryGet(apiKey, out pairedApiBaseUrl))
                {
                    break;
                }
            }

            if (string.IsNullOrWhiteSpace(pairedApiBaseUrl))
            {
                return false;
            }

            try
            {
                var gatewayOrigin = ClientEndpointAddress.NormalizeGatewayBase(gatewayEndpoint);
                var pairedApiOrigin = ClientEndpointAddress.NormalizeApiBase(pairedApiBaseUrl);
                return string.Equals(gatewayOrigin, pairedApiOrigin, StringComparison.OrdinalIgnoreCase);
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        return false;
    }

    internal static ClientOptions Load(
        IConfiguration? configuration,
        string appBaseDir,
        IReadOnlyList<string> args)
        => Load(packagedDefaults: null, deploymentOverrides: configuration, appBaseDir, args);

    internal static ClientOptions Load(
        IConfiguration? packagedDefaults,
        IConfiguration? deploymentOverrides,
        string appBaseDir,
        IReadOnlyList<string> args)
    {
        var options = new ClientOptions();

        // These are intentionally separate configuration layers. The packaged appsettings
        // file is a safe fallback for a fresh install, not an explicit deployment choice.
        // Binding the already-merged root here would let those placeholders overwrite a
        // valid installation's legacy settings.
        packagedDefaults?.GetSection("Client").Bind(options);

        var legacyPath = Path.Combine(appBaseDir, "clientsettings.json");
        if (File.Exists(legacyPath))
        {
            // A configuration provider preserves the distinction between an omitted legacy
            // property and a property whose value is false/zero/null. This matters for old
            // installs whose file only contains the URL or identity settings.
            var legacy = new ConfigurationBuilder()
                .SetBasePath(appBaseDir)
                .AddJsonFile("clientsettings.json", optional: false, reloadOnChange: false)
                .Build();
            IConfiguration legacyConfiguration = legacy.GetSection("Client").Exists()
                ? legacy.GetSection("Client")
                : legacy;
            legacyConfiguration.Bind(options);
        }

        // Environment/service configuration is the explicit deployment contract. Apply it
        // after the installed file so service-installed values win without treating packaged
        // placeholders as an override.
        deploymentOverrides?.GetSection("Client").Bind(options);

        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--tenant" when i + 1 < args.Count && Guid.TryParse(args[i + 1], out var tenant):
                    options.TenantId = tenant;
                    i++;
                    break;
                case "--api" when i + 1 < args.Count:
                    options.ApiBaseUrl = args[i + 1];
                    i++;
                    break;
                case "--env" when i + 1 < args.Count && Enum.TryParse<ClientEnvironment>(args[i + 1], true, out var environment):
                    options.Environment = environment;
                    i++;
                    break;
                case "--enrollment-code" when i + 1 < args.Count:
                    options.EnrollmentCode = args[i + 1];
                    i++;
                    break;
                case "--enroll" when i + 1 < args.Count:
                    options.EnrollmentCode = args[i + 1];
                    i++;
                    break;
                case "--agent-id" when i + 1 < args.Count:
                    options.AgentId = args[i + 1];
                    i++;
                    break;
            }
        }

        options.ApiBaseUrl = ClientEndpointAddress.NormalizeApiBase(options.ApiBaseUrl);
        return options;
    }
}
