using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using NetRatel.Shared;

namespace NetRatel.Client;

internal static class ClientConfigurationLoader
{
    internal static ClientOptions Load(
        IConfiguration? configuration,
        string appBaseDir,
        IReadOnlyList<string> args)
    {
        var options = new ClientOptions();
        var legacyPath = Path.Combine(appBaseDir, "clientsettings.json");
        if (File.Exists(legacyPath))
        {
            var json = File.ReadAllText(legacyPath);
            var legacy = JsonSerializer.Deserialize<ClientOptions>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            if (legacy is not null)
            {
                if (legacy.TenantId != Guid.Empty) options.TenantId = legacy.TenantId;
                if (!string.IsNullOrWhiteSpace(legacy.ApiBaseUrl)) options.ApiBaseUrl = legacy.ApiBaseUrl;
                options.Environment = legacy.Environment;
                options.UseInProcPowerShell = legacy.UseInProcPowerShell;
                if (!string.IsNullOrWhiteSpace(legacy.TerminalBackendPreference)) options.TerminalBackendPreference = legacy.TerminalBackendPreference;
                options.EnableNativeUnixPty = legacy.EnableNativeUnixPty;
                if (legacy.TerminalGracefulExitTimeoutMs > 0) options.TerminalGracefulExitTimeoutMs = legacy.TerminalGracefulExitTimeoutMs;
                if (legacy.TerminalKillTimeoutMs > 0) options.TerminalKillTimeoutMs = legacy.TerminalKillTimeoutMs;
                if (!string.IsNullOrWhiteSpace(legacy.EnrollmentCode)) options.EnrollmentCode = legacy.EnrollmentCode;
                if (!string.IsNullOrWhiteSpace(legacy.AgentId)) options.AgentId = legacy.AgentId;
                if (legacy.AutoUpdate is not null) options.AutoUpdate = legacy.AutoUpdate;
            }
        }

        // Environment and app configuration are the deployment contract. Apply them after
        // the legacy file so service-installed values cannot be shadowed by stale settings.
        configuration?.GetSection("Client").Bind(options);

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
