using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Extensions.Configuration;
using NetRatel.Shared;

namespace NetRatel.Client;

internal static class ClientConfigurationLoader
{
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

        return options;
    }
}
