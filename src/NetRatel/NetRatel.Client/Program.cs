using Humanizer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Win32;
using NetRatel.Application.ClientAuth;
using NetRatel.Client;
using NetRatel.Client.Service;
using NetRatel.Client.Service.Auth;
using NetRatel.Client.Service.Gateway;
using NetRatel.Client.Service.Logging;
using NetRatel.Client.Service.RemoteDesktop;
using NetRatel.Client.Service.RemoteSupport;
using NetRatel.Client.Service.Tasks;
using NetRatel.Client.Service.Terminal;
using NetRatel.Client.Service.Updates;
using NetRatel.Client.Services;
using NetRatel.Infrastructure.Auth;
using NetRatel.Shared;
using NetRatel.Shared.Service.ClientEnvironment;
using NetRatel.Shared.Utils;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.ServiceProcess;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Security.Authentication;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

// Use a thread-safe queue to handle input commands
var input_queue = new ConcurrentQueue<(string Command, string Args)>();

async Task RunClientAsync()
{
    // *** MUST BE FIRST LINES IN Main() ***
    var runtimeBaseDir = AppContext.BaseDirectory ?? Environment.CurrentDirectory;
    var appBaseDir = ResolveExecutableDirectory(runtimeBaseDir);
    var cliArgs = Environment.GetCommandLineArgs().Skip(1).ToArray();

    // Keep this probe before logging, configuration, and service initialization so packaged
    // Clients can be validated on every supported platform without credentials, network access,
    // or side effects.
    if (cliArgs.Any(a => string.Equals(a, "--version", StringComparison.OrdinalIgnoreCase)))
    {
        Console.WriteLine(GetAgentVersion());
        return;
    }

    LogManager.Initialize(appBaseDir, ResolveLogDirectory(appBaseDir, cliArgs), ResolveLogFilePrefix(cliArgs));
    NetRatel.Shared.Service.Logging.LogManager.Initialize(LogManager.LogFilePath);
    LogStartupBlock(appBaseDir, cliArgs);
    using var applicationStopping = new CancellationTokenSource();

    if (cliArgs.Any(a => string.Equals(a, "--terminal-pty-self-test", StringComparison.OrdinalIgnoreCase)) ||
        cliArgs.Any(a => string.Equals(a, "--terminal-pty-self-test-native", StringComparison.OrdinalIgnoreCase)) ||
        cliArgs.Any(a => string.Equals(a, "--terminal-pty-self-test-python", StringComparison.OrdinalIgnoreCase)))
    {
        var forceNative = cliArgs.Any(a => string.Equals(a, "--terminal-pty-self-test-native", StringComparison.OrdinalIgnoreCase));
        var forcePython = cliArgs.Any(a => string.Equals(a, "--terminal-pty-self-test-python", StringComparison.OrdinalIgnoreCase));
        Environment.ExitCode = await TerminalPtySelfTest.RunAsync(forceNative, forcePython, CancellationToken.None).ConfigureAwait(false);
        return;
    }

    if (OperatingSystem.IsWindows() && IsWindowsServiceMode(cliArgs))
    {
        WindowsRemoteSupportFirewall.EnsureRulesForCurrentProcess();
    }

    if (OperatingSystem.IsWindows() && cliArgs.Any(a => string.Equals(a, "--remote-support-firewall-fix", StringComparison.OrdinalIgnoreCase)))
    {
        WindowsRemoteSupportFirewall.EnsureRulesForCurrentProcess();
    }

    if (OperatingSystem.IsWindows() && cliArgs.Any(a => string.Equals(a, "--remote-support-enable-software-sas", StringComparison.OrdinalIgnoreCase)))
    {
#pragma warning disable CA1416
        var result = RemoteSupportSasPolicyRemediator.EnableForServices(
            cliArgs.Any(a => string.Equals(a, "--confirm", StringComparison.OrdinalIgnoreCase)));
#pragma warning restore CA1416
        Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true
        }));
        Environment.ExitCode = result.Success ? 0 : 2;
        return;
    }

    if (OperatingSystem.IsWindows() && cliArgs.Any(a => string.Equals(a, "--remote-support-helper-fix", StringComparison.OrdinalIgnoreCase)))
    {
#pragma warning disable CA1416
        var result = RemoteSupportInteractiveHelperRepairService.RunManualRepair();
#pragma warning restore CA1416
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(result, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)
        {
            WriteIndented = true
        }));
        if (!cliArgs.Any(a => string.Equals(a, "--remote-desktop-diagnostics", StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }
    }

    if (OperatingSystem.IsWindows() && cliArgs.Any(a => string.Equals(a, "--remote-desktop-diagnostics", StringComparison.OrdinalIgnoreCase)))
    {
        RunRemoteDesktopDiagnostics(appBaseDir, cliArgs);
        return;
    }

    if (OperatingSystem.IsWindows() && cliArgs.Any(a => string.Equals(a, "--remote-support-session-inventory", StringComparison.OrdinalIgnoreCase)))
    {
        RunRemoteSupportSessionInventoryDiagnostics(cliArgs);
        return;
    }

    if (OperatingSystem.IsWindows() && cliArgs.Any(a => string.Equals(a, "--remote-support-console-helper", StringComparison.OrdinalIgnoreCase)))
    {
        await RemoteSupportConsoleHelper.RunAsync(
            cliArgs.Any(a => string.Equals(a, "--self-test", StringComparison.OrdinalIgnoreCase)),
            cliArgs.Any(a => string.Equals(a, "--capture-self-test", StringComparison.OrdinalIgnoreCase)),
            cliArgs.Any(a => string.Equals(a, "--capture-backend-matrix", StringComparison.OrdinalIgnoreCase)),
            cliArgs.Any(a => string.Equals(a, "--session-launch-self-test", StringComparison.OrdinalIgnoreCase)),
            cliArgs.Any(a => string.Equals(a, "--input-self-test", StringComparison.OrdinalIgnoreCase)),
            cliArgs.Any(a => string.Equals(a, "--move-mouse-probe", StringComparison.OrdinalIgnoreCase)),
            cliArgs.Any(a => string.Equals(a, "--input-visual-feedback-self-test", StringComparison.OrdinalIgnoreCase)),
            GetArgValue(cliArgs, "--type-probe"),
            cliArgs.Any(a => string.Equals(a, "--sas-self-test", StringComparison.OrdinalIgnoreCase)),
            cliArgs.Any(a => string.Equals(a, "--send", StringComparison.OrdinalIgnoreCase)),
            CancellationToken.None).ConfigureAwait(false);
        return;
    }

    if (OperatingSystem.IsWindows() && cliArgs.Any(a => string.Equals(a, "--remote-desktop-user-helper", StringComparison.OrdinalIgnoreCase)))
    {
        await RemoteDesktopInteractiveHelper.RunResidentAsync(CancellationToken.None).ConfigureAwait(false);
        return;
    }

    var remoteDesktopHelperPipe = GetArgValue(cliArgs, "--remote-desktop-helper");
    if (!string.IsNullOrWhiteSpace(remoteDesktopHelperPipe))
    {
        await RemoteDesktopInteractiveHelper.RunAsync(remoteDesktopHelperPipe, CancellationToken.None).ConfigureAwait(false);
        return;
    }

    // Force the application base path SMA uses internally
    AppContext.SetData("APP_CONTEXT_BASE_DIRECTORY", runtimeBaseDir);
    AppDomain.CurrentDomain.SetData("APP_CONTEXT_BASE_DIRECTORY", runtimeBaseDir);

    // Keep PowerShell's mutable profile outside a read-only packaged application
    // directory when a deployment provides a dedicated state location.
    var home = Environment.GetEnvironmentVariable("NETRATEL_POWERSHELL_HOME");
    if (string.IsNullOrWhiteSpace(home))
    {
        home = Path.Combine(appBaseDir, "_psprofile");
    }
    var userConfigDir = Path.Combine(home, "Documents", "PowerShell");
    Directory.CreateDirectory(userConfigDir);

    // Ensure both config files exist
    var systemCfg = Path.Combine(appBaseDir, "powershell.config.json"); // keep it next to your exe
    if (!File.Exists(systemCfg)) File.WriteAllText(systemCfg, "{}");
    var userCfg = Path.Combine(userConfigDir, "powershell.config.json");
    if (!File.Exists(userCfg)) File.WriteAllText(userCfg, "{}");

    // Set process env vars PowerShell actually reads
    Environment.SetEnvironmentVariable("HOME", home, EnvironmentVariableTarget.Process);
    Environment.SetEnvironmentVariable("USERPROFILE", home, EnvironmentVariableTarget.Process);

    // IMPORTANT: also set env var (not only AppContext) so SMA can pick it up early
    Environment.SetEnvironmentVariable("POWERSHELL_CONFIG_PATH", userCfg, EnvironmentVariableTarget.Process);

    // Optional (harmless): PSHOME = app base (non-null)
    Environment.SetEnvironmentVariable("PSHOME", appBaseDir, EnvironmentVariableTarget.Process);

    // Prepend built-ins for module discovery (nice-to-have)
    var ridRoot = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "win" : "unix";
    var builtIns = Path.Combine(runtimeBaseDir, "runtimes", ridRoot, "lib", "net9.0", "Modules");
    if (!Directory.Exists(builtIns))
    {
        builtIns = Path.Combine(appBaseDir, "runtimes", ridRoot, "lib", "net9.0", "Modules");
    }
    if (Directory.Exists(builtIns))
    {
        var sep = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ";" : ":";
        var existing = Environment.GetEnvironmentVariable("PSModulePath") ?? string.Empty;
        if (!existing.Split(new[] { sep }, StringSplitOptions.RemoveEmptyEntries)
            .Any(p => string.Equals(p, builtIns, StringComparison.OrdinalIgnoreCase)))
        {
            var newValue = string.IsNullOrEmpty(existing) ? builtIns : (builtIns + sep + existing);
            Environment.SetEnvironmentVariable("PSModulePath", newValue, EnvironmentVariableTarget.Process);
        }
    }

    /// PS config crap ends

    AppDomain.CurrentDomain.UnhandledException += (s, e) =>
    {
        LogManager.WriteLog($"[FATAL] Unhandled: {e.ExceptionObject}");
    };

    Console.CancelKeyPress += (s, e) =>
    {
        LogManager.WriteLog("Ctrl+C received, shutting down.");
        e.Cancel = true;
        applicationStopping.Cancel();
    };

    var enrollOnly = cliArgs.Any(a => string.Equals(a, "--enroll", StringComparison.OrdinalIgnoreCase));
    var resetAgentIdentity = cliArgs.Any(a => string.Equals(a, "--reset-agent-identity", StringComparison.OrdinalIgnoreCase));
    var serviceMode = cliArgs.Any(a => string.Equals(a, "--service", StringComparison.OrdinalIgnoreCase));
    var authCheckOnly = cliArgs.Any(a => string.Equals(a, "--auth-check", StringComparison.OrdinalIgnoreCase));
    var dotnetEnvironment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
        ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");

    // Keep packaged defaults separate from explicit deployment configuration. The client
    // loader uses this distinction to preserve a supported installation's legacy file.
    var packagedDefaults = ClientConfigurationLoader.BuildPackagedDefaults(appBaseDir, dotnetEnvironment);
    var deploymentOverrides = ClientConfigurationLoader.BuildDeploymentOverrides();
    IConfiguration configuration = new ConfigurationBuilder()
        .AddConfiguration(packagedDefaults)
        .AddConfiguration(deploymentOverrides)
        .AddCommandLine(cliArgs)
        .Build();
    var cfg = ClientConfigurationLoader.Load(packagedDefaults, deploymentOverrides, appBaseDir, cliArgs);
    cfg.ApiBaseUrl = NormalizeApiBaseUrl(cfg.ApiBaseUrl);
    var transportMode = configuration["Transport:Mode"] ?? "AkkaPresence";
    var gatewayOptions = LoadGatewayOptions(configuration, cfg.ApiBaseUrl);
    GlobalContext.version = GetAgentVersion();

    LogManager.WriteLog($"[Client] RuntimeBaseDir={runtimeBaseDir}");
    LogManager.WriteLog($"[Client] AppBaseDir={appBaseDir}");
    LogManager.WriteLog($"[Client] LogFile={LogManager.LogFilePath}");
    LogManager.WriteLog($"[Client] Tenant={cfg.TenantId}, Env={cfg.Environment}, API={cfg.ApiBaseUrl}");
    LogManager.WriteLog($"[Client] TransportMode={transportMode}");
    LogManager.WriteLog($"Application {GlobalContext.version} starting.");

    var services = new ServiceCollection();
    services.AddSingleton(cfg);
    services.AddSingleton<IAgentCredentialStore, AgentCredentialStore>();
    services.AddSingleton<IAgentDeviceKeyStore>(sp => (IAgentDeviceKeyStore)sp.GetRequiredService<IAgentCredentialStore>());
    services.AddHttpClient("AgentAuthApi", http =>
    {
        http.BaseAddress = new Uri(cfg.ApiBaseUrl.TrimEnd('/'));
        http.Timeout = TimeSpan.FromSeconds(30);
    });
    services.AddScoped<IAgentEnrollmentService>(sp =>
        new AgentEnrollmentService(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient("AgentAuthApi"),
            sp.GetRequiredService<IAgentDeviceKeyStore>()));
    services.AddScoped<IAgentTokenService>(sp =>
        new ClientAgentTokenService(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient("AgentAuthApi"),
            sp.GetRequiredService<IAgentCredentialStore>(),
            sp.GetRequiredService<IAgentDeviceKeyStore>()));

    using var rootProvider = services.BuildServiceProvider();
    var credentialStore = rootProvider.GetRequiredService<IAgentCredentialStore>();
    if (resetAgentIdentity)
    {
        if (!cliArgs.Any(a => string.Equals(a, "--confirm", StringComparison.OrdinalIgnoreCase)))
        {
            Console.Error.WriteLine("Refusing to reset the Agent installation identity without --confirm.");
            Environment.ExitCode = 2;
            return;
        }

        await credentialStore.ResetInstallationIdentityAsync().ConfigureAwait(false);
        LogManager.WriteLog("[Auth] Agent installation identity explicitly reset by local operator request.");
        Console.WriteLine("Agent installation identity reset. The next enrollment creates a new Agent identity.");
        Environment.ExitCode = 0;
        return;
    }

    var enrollmentService = rootProvider.GetRequiredService<IAgentEnrollmentService>();
    var tokenService = (ClientAgentTokenService)rootProvider.GetRequiredService<IAgentTokenService>();
    var injectedEnrollmentBootstrap = new InjectedEnrollmentBootstrap();
    var enrollmentCliCommand = new EnrollmentCliCommand();
    var creds = await credentialStore.LoadAsync();
    if (creds is null)
    {
        try
        {
            creds = await injectedEnrollmentBootstrap.TryEnrollAsync(cfg, enrollmentService, credentialStore, CancellationToken.None);
            if (creds is not null)
            {
                LogManager.WriteLog($"[Auth] Auto-enrollment from netratel.enroll.json succeeded. AgentId={creds.Value.AgentId}");
            }
        }
        catch (AgentClientAuthException ex)
        {
            LogManager.WriteLog($"[Auth] Injected enrollment failed ({ex.Message}). Falling back to manual enrollment code entry.");
        }
    }

    var cliResult = await enrollmentCliCommand.TryExecuteAsync(
        enrollOnly,
        cfg.EnrollmentCode,
        creds,
        enrollmentService,
        credentialStore,
        Console.Out,
        Console.Error,
        CancellationToken.None);
    if (cliResult.Handled)
    {
        Environment.ExitCode = cliResult.ExitCode;
        return;
    }

    var apiGuard = await ValidateApiBaseUrlAsync(cfg.ApiBaseUrl, CancellationToken.None);
    if (!apiGuard.IsValid)
    {
        LogManager.WriteLog($"[Auth] {apiGuard.Message}");
        Environment.ExitCode = 14;
        return;
    }

    if (authCheckOnly)
    {
        var authCheckExitCode = await RunAuthCheckAsync(
            cfg,
            appBaseDir,
            creds,
            credentialStore,
            enrollmentService,
            tokenService,
            injectedEnrollmentBootstrap,
            CancellationToken.None);
        Environment.ExitCode = authCheckExitCode;
        return;
    }

    if (creds is null)
    {
        var enrollmentCode = cfg.EnrollmentCode;
        if (EnrollmentStartupPolicy.ShouldPromptForEnrollment(serviceMode, enrollmentCode))
        {
            Console.Write("Enter enrollment code: ");
            enrollmentCode = Console.ReadLine();
        }

        if (string.IsNullOrWhiteSpace(enrollmentCode))
        {
            var exitCode = EnrollmentStartupPolicy.GetMissingEnrollmentExitCode(serviceMode);
            LogManager.WriteLog(serviceMode
                ? "[Auth] Enrollment is required before starting the service. Run NetRatel.Client --enroll <code> --api <url>. Exiting."
                : "[Auth] Enrollment code is required for first run. Exiting.");
            Environment.ExitCode = exitCode;
            return;
        }

        try
        {
            creds = await enrollmentService.EnrollAsync(enrollmentCode, CancellationToken.None);
            await credentialStore.SaveAsync(creds.Value.AgentId, creds.Value.RefreshToken);
            creds = await credentialStore.LoadAsync();
            if (creds is null)
            {
                LogManager.WriteLog("[Auth] Enrollment credentials were not persisted. Exiting.");
                Environment.ExitCode = 11;
                return;
            }

            LogManager.WriteLog($"[Auth] Enrollment succeeded. AgentId={creds.Value.AgentId}");
        }
        catch (AgentClientAuthException ex)
        {
            LogManager.WriteLog($"[Auth] Enrollment failed: {ex.Message}");
            Environment.ExitCode = 11;
            return;
        }
        catch (Exception ex)
        {
            LogManager.WriteLog($"[Auth] Enrollment persistence failed: {ex.Message}");
            Environment.ExitCode = 11;
            return;
        }
    }

    string initialToken;
    try
    {
        var tokenResult = await DisabledAgentTokenRetry.GetAccessTokenAsync(
            tokenService, message => LogManager.WriteLog($"[Auth] {message}"), applicationStopping.Token);
        initialToken = tokenResult.AccessToken;
        LogManager.WriteLog($"[Auth] Access token acquired (expires {tokenResult.ExpiresAtUtc:u}; {AccessTokenAuditMetadata.Describe(initialToken)}).");
    }
    catch (OperationCanceledException) when (applicationStopping.IsCancellationRequested)
    {
        return;
    }
    catch (AgentClientAuthException ex)
    {
        if (ex.ShouldClearCredentials)
        {
            await credentialStore.ClearRefreshCredentialsAsync();
        }
        LogManager.WriteLog($"[Auth] Failed to acquire access token: {ex.Message}");
        Environment.ExitCode = 12;
        return;
    }

    var tokenStore = new InMemoryAuthTokenStore();
    await tokenStore.SaveAsync(initialToken);
    var tokenTenantId = TryGetTenantId(initialToken);
    LogManager.WriteLog($"[Auth] Token tenant_id={(tokenTenantId.HasValue ? tokenTenantId.Value.ToString() : "missing")}");

    if (string.Equals(transportMode, "AkkaPresence", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(transportMode, "AkkaPresenceCanary", StringComparison.OrdinalIgnoreCase))
    {
        if (!tokenTenantId.HasValue)
        {
            LogManager.WriteLog("[Gateway] The agent token does not contain tenant_id. Exiting.");
            Environment.ExitCode = 13;
            return;
        }

        if (!Guid.TryParse(creds.Value.AgentId, out var agentId) || agentId == Guid.Empty)
        {
            LogManager.WriteLog("[Gateway] The enrolled agent ID is invalid. Exiting.");
            Environment.ExitCode = 13;
            return;
        }

        LogManager.WriteLog($"[Gateway] Starting authenticated Akka presence. Endpoint={gatewayOptions.Endpoint}, requiredAuthority={gatewayOptions.RequiredPresenceAuthority}");
        var telemetryPublisher = new AgentGatewayTelemetryShadowPublisher(
            gatewayOptions,
            GetAgentVersion(),
            message => LogManager.WriteLog($"[Gateway] {message}"));
        var controlGateway = new AgentControlGatewayClient(
            gatewayOptions,
            message => LogManager.WriteLog($"[Gateway] {message}"));
        var fileGateway = new AgentFileGatewayClient(
            gatewayOptions,
            new FileSystemService(),
            message => LogManager.WriteLog($"[Gateway] {message}"));
        var logGateway = new AgentLogGatewayClient(gatewayOptions);
        var remoteSupportGateway = new AgentRemoteSupportGatewayClient(
            gatewayOptions,
            message => LogManager.WriteLog($"[Gateway] {message}"));
        var terminalShells = ShellInventoryDetector.NormalizeKeywords(ShellInventoryDetector.Detect());
        using var terminalGateway = new AgentTerminalGatewayClient(
            gatewayOptions,
            BuildTerminalHostOptions(cfg),
            terminalShells,
            message => LogManager.WriteLog($"[Gateway] {message}"));
        var commandGateway = new AgentCommandGatewayClient(
            gatewayOptions,
            cfg.UseInProcPowerShell,
            message => LogManager.WriteLog($"[Gateway] {message}"));
        var jobGateway = new AgentJobGatewayClient(
            gatewayOptions,
            cfg.UseInProcPowerShell,
            message => LogManager.WriteLog($"[Gateway] {message}"));
        await using var updateCoordinator = new AkkaClientAutoUpdateCoordinator(
            cfg,
            GetAgentVersion(),
            tokenService,
            rootProvider.GetRequiredService<IHttpClientFactory>().CreateClient("AgentAuthApi"));
        var gatewayExtensionSupervisor = new GatewayPresenceExtensionSupervisor(
            message => LogManager.WriteLog($"[Gateway] {message}"));
        var gatewayClient = new AgentGatewayPresenceClient(
            gatewayOptions,
            tokenService,
            tokenTenantId.Value,
            agentId,
            GetAgentVersion(),
            terminalShells,
            message => LogManager.WriteLog($"[Gateway] {message}"),
            (session, accessToken, stoppingToken) => gatewayExtensionSupervisor.RunForPresenceSessionAsync(
                session,
                accessToken,
                stoppingToken,
                [
                    new GatewayPresenceExtension("telemetry", telemetryPublisher.RunForPresenceSessionAsync),
                    new GatewayPresenceExtension("control", controlGateway.RunForPresenceSessionAsync),
                    new GatewayPresenceExtension("file", fileGateway.RunForPresenceSessionAsync),
                    new GatewayPresenceExtension("log", logGateway.RunForPresenceSessionAsync),
                    new GatewayPresenceExtension("remote-support", remoteSupportGateway.RunForPresenceSessionAsync),
                    new GatewayPresenceExtension("terminal", terminalGateway.RunForPresenceSessionAsync),
                    new GatewayPresenceExtension("command", commandGateway.RunForPresenceSessionAsync),
                    new GatewayPresenceExtension("job", jobGateway.RunForPresenceSessionAsync)
                ]),
            updateCoordinator);
        await gatewayClient.RunAsync(applicationStopping.Token).ConfigureAwait(false);
        return;
    }

    LogManager.WriteLog($"[Client] Unsupported Transport:Mode '{transportMode}'. Supported modes are AkkaPresence and the legacy AkkaPresenceCanary.");
    Environment.ExitCode = 13;
    return;
}

static GatewayClientOptions LoadGatewayOptions(IConfiguration configuration, string apiBaseUrl)
{
    var options = new GatewayClientOptions();
    configuration.GetSection("Gateway").Bind(options);
    options.Endpoint = string.IsNullOrWhiteSpace(options.Endpoint)
        ? apiBaseUrl
        : options.Endpoint;
    return options;
}

static TerminalHostOptions BuildTerminalHostOptions(ClientOptions cfg)
{
    var preference = Enum.TryParse<TerminalBackendPreference>(cfg.TerminalBackendPreference, true, out var parsed)
        ? parsed
        : TerminalBackendPreference.Auto;

    return new TerminalHostOptions
    {
        BackendPreference = preference,
        EnableNativeUnixPty = cfg.EnableNativeUnixPty,
        GracefulExitTimeoutMs = cfg.TerminalGracefulExitTimeoutMs > 0 ? cfg.TerminalGracefulExitTimeoutMs : 1500,
        KillTimeoutMs = cfg.TerminalKillTimeoutMs > 0 ? cfg.TerminalKillTimeoutMs : 2000
    };
}

static void RunRemoteSupportSessionInventoryDiagnostics(IReadOnlyCollection<string> args)
{
    var agentVersion = GetAgentVersion();
    if (!OperatingSystem.IsWindows())
    {
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
        {
            mode = "remote-support-session-inventory",
            agentVersion,
            inventoryCapability = false,
            explicitTargetingCapability = false,
            reason = "unsupported_os",
            sessionsCount = 0,
            sessions = Array.Empty<object>(),
            helpersCount = 0,
            helpers = Array.Empty<object>(),
            upsertAttempted = false,
            upsertAck = (bool?)null,
            upsertError = "Standalone diagnostics are local-only. The running service reports remote-support state through the authenticated Akka gateway; this command does not attempt a network write."
        }, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)
        {
            WriteIndented = true
        }));
        return;
    }

#pragma warning disable CA1416
    var sessions = RemoteSupportWindowsSessionInventory.CaptureLocalForDiagnostics();
#pragma warning restore CA1416
    var helpers = sessions
        .Where(x => x.HelperConnected)
        .Select(x => new
        {
            provider = x.Provider,
            sessionId = x.WindowsSessionId,
            pid = x.HelperPid,
            version = x.HelperVersion,
            versionMatchesService = x.HelperVersionMatches
        })
        .ToArray();
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
    {
        mode = "remote-support-session-inventory",
        agentVersion,
        inventoryCapability = true,
        explicitTargetingCapability = true,
        sessionsCount = sessions.Count,
        sessions = sessions.Select(x => new
        {
            sessionId = x.WindowsSessionId,
            kind = x.SessionType,
            state = x.State,
            domain = x.Domain,
            username = x.Username,
            userSidHash = x.UserSidHash,
            displayLabel = x.DisplayLabel,
            isActiveConsole = x.IsConsoleSession,
            isLockedOrWinlogon = x.IsLocked || x.IsWinlogon,
            canAssist = x.IsAssistable,
            providerHint = x.Provider,
            helperConnected = x.HelperConnected,
            helperVersion = x.HelperVersion,
            helperVersionMatchesService = x.HelperVersionMatches
        }),
        helpersCount = helpers.Length,
        helpers,
        refreshRequestId = args.Any(a => string.Equals(a, "--refresh", StringComparison.OrdinalIgnoreCase))
            ? Guid.NewGuid().ToString("n")
            : null,
        inventorySequence = (ulong?)null,
        upsertAttempted = false,
        upsertAck = (bool?)null,
        upsertError = "Standalone diagnostics are local WTS enumeration only. The running service reports remote-support state through the authenticated Akka gateway; this command does not attempt a network write."
    }, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    }));
}

static async Task<int> RunAuthCheckAsync(
    ClientOptions cfg,
    string appBaseDir,
    (string AgentId, string RefreshToken)? existingCredentials,
    IAgentCredentialStore credentialStore,
    IAgentEnrollmentService enrollmentService,
    ClientAgentTokenService tokenService,
    IInjectedEnrollmentBootstrap injectedEnrollmentBootstrap,
    CancellationToken ct)
{
    LogManager.WriteLog("[AuthCheck] Starting auth readiness check.");
    LogManager.WriteLog($"[AuthCheck] appBaseDir={appBaseDir}");
    LogManager.WriteLog($"[AuthCheck] apiBaseUrl={cfg.ApiBaseUrl}");

    var creds = existingCredentials ?? await credentialStore.LoadAsync();
    if (creds is null)
    {
        try
        {
            creds = await injectedEnrollmentBootstrap.TryEnrollAsync(cfg, enrollmentService, credentialStore, ct);
            if (creds is not null)
            {
                LogManager.WriteLog($"[AuthCheck] Auto-enrollment from netratel.enroll.json succeeded. agentId={creds.Value.AgentId}");
            }
        }
        catch (AgentClientAuthException ex)
        {
            LogManager.WriteLog($"[AuthCheck] Injected enrollment failed: {ex.Message}");
            return 11;
        }
    }

    if (creds is null && !string.IsNullOrWhiteSpace(cfg.EnrollmentCode))
    {
        try
        {
            creds = await enrollmentService.EnrollAsync(cfg.EnrollmentCode, ct);
            await credentialStore.SaveAsync(creds.Value.AgentId, creds.Value.RefreshToken);
            creds = await credentialStore.LoadAsync();
            if (creds is null)
            {
                LogManager.WriteLog("[AuthCheck] Enrollment credentials were not persisted.");
                return 11;
            }

            LogManager.WriteLog($"[AuthCheck] Enrollment succeeded. agentId={creds.Value.AgentId}");
        }
        catch (AgentClientAuthException ex)
        {
            LogManager.WriteLog($"[AuthCheck] Enrollment failed: {ex.Message}");
            return 11;
        }
        catch (Exception ex)
        {
            LogManager.WriteLog($"[AuthCheck] Enrollment persistence failed: {ex.Message}");
            return 11;
        }
    }

    if (creds is null)
    {
        LogManager.WriteLog("[AuthCheck] Need enrollment: no stored credentials and no enrollment code/netratel.enroll.json available.");
        return 10;
    }

    LogManager.WriteLog($"[AuthCheck] Using agentId={creds.Value.AgentId}");

    string accessToken;
    DateTimeOffset expiresAtUtc;
    try
    {
        var token = await tokenService.GetAccessTokenAsync(ct);
        accessToken = token.AccessToken;
        expiresAtUtc = token.ExpiresAtUtc;
        LogManager.WriteLog($"[AuthCheck] Access token acquired. expiresAtUtc={expiresAtUtc:u}; {AccessTokenAuditMetadata.Describe(accessToken)}");
    }
    catch (AgentClientAuthException ex)
    {
        if (IsAgentDisabled(ex))
        {
            LogManager.WriteLog("[AuthCheck] Agent disabled by administrator.");
            return 3;
        }

        if (ex.ShouldClearCredentials)
        {
            await credentialStore.ClearRefreshCredentialsAsync();
        }

        LogManager.WriteLog($"[AuthCheck] Token request failed: {ex.Message}");
        return 12;
    }

    using var pingClient = new HttpClient
    {
        BaseAddress = new Uri(cfg.ApiBaseUrl),
        Timeout = TimeSpan.FromSeconds(20)
    };
    pingClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

    try
    {
        using var pingResponse = await pingClient.GetAsync("/api/v1/agent-auth/ping", ct);
        var pingBody = await pingResponse.Content.ReadAsStringAsync(ct);
        if (!pingResponse.IsSuccessStatusCode)
        {
            LogManager.WriteLog($"[AuthCheck] Agent ping failed. status={(int)pingResponse.StatusCode}, body={TrimForLog(pingBody)}");
            return 13;
        }

        LogManager.WriteLog($"[AuthCheck] Agent ping succeeded. status={(int)pingResponse.StatusCode}, body={TrimForLog(pingBody)}");
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
    {
        LogManager.WriteLog($"[AuthCheck] Agent ping failed: {ex.Message}");
        return 13;
    }

    LogManager.WriteLog("[AuthCheck] PASS");
    return 0;
}

static async Task<(bool IsValid, string Message)> ValidateApiBaseUrlAsync(string apiBaseUrl, CancellationToken ct)
{
    if (string.IsNullOrWhiteSpace(apiBaseUrl))
    {
        return (false, "ApiBaseUrl is empty. Set Client:ApiBaseUrl to the NetRatel.API base URL.");
    }

    if (!Uri.TryCreate(apiBaseUrl, UriKind.Absolute, out var _))
    {
        return (false, $"ApiBaseUrl is invalid: {apiBaseUrl}");
    }

    using var client = new HttpClient
    {
        BaseAddress = new Uri(apiBaseUrl),
        Timeout = TimeSpan.FromSeconds(10)
    };

    try
    {
        using var pingProbe = await client.GetAsync("/api/v1/agent-auth/ping", ct);
        if (pingProbe.StatusCode == HttpStatusCode.Unauthorized || pingProbe.StatusCode == HttpStatusCode.Forbidden)
        {
            return (true, "ApiBaseUrl validation passed.");
        }

        var probeContentType = pingProbe.Content.Headers.ContentType?.MediaType ?? string.Empty;
        if (probeContentType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase))
        {
            return (false, "ApiBaseUrl appears to be NetRatel.Web. Set Client:ApiBaseUrl to NetRatel.API base URL (for example https://netratel-dev-api...).");
        }
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
    {
        return (false, $"ApiBaseUrl is unreachable ({ex.Message}). Ensure Client:ApiBaseUrl points to NetRatel.API.");
    }

    try
    {
        using var rootProbe = await client.GetAsync("/", ct);
        var rootContentType = rootProbe.Content.Headers.ContentType?.MediaType ?? string.Empty;
        if (rootProbe.IsSuccessStatusCode && rootContentType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase))
        {
            return (false, "ApiBaseUrl appears to be NetRatel.Web. Set Client:ApiBaseUrl to NetRatel.API base URL (for example https://netratel-dev-api...).");
        }
    }
    catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
    {
        LogManager.WriteLog($"[Auth] Optional API root probe failed after the auth probe succeeded: {exception.Message}");
    }

    return (true, "ApiBaseUrl validation passed.");
}

static string? GetArgValue(string[] args, string key)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (string.Equals(args[i], key, StringComparison.OrdinalIgnoreCase))
        {
            return args[i + 1];
        }
    }

    return null;
}

static string NormalizeApiBaseUrl(string value)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return value;
    }

    return value.Trim().TrimEnd('/');
}

static string ResolveExecutableDirectory(string fallback)
{
    var processPath = Environment.ProcessPath;
    if (!string.IsNullOrWhiteSpace(processPath))
    {
        var dir = Path.GetDirectoryName(processPath);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            return dir;
        }
    }

    return fallback;
}

static void LogStartupBlock(string appBaseDir, IReadOnlyCollection<string> args)
{
    var mode = ResolveProcessMode(args);
    var sessionId = OperatingSystem.IsWindows() ? Process.GetCurrentProcess().SessionId.ToString() : "n/a";
    var user = "unknown";
    if (OperatingSystem.IsWindows())
    {
        try
        {
            user = WindowsIdentity.GetCurrent().Name;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            LogManager.WriteLog($"[RemoteSupport] Unable to read helper diagnostic state: {exception.Message}");
            user = $"{Environment.UserDomainName}\\{Environment.UserName}";
        }
    }
    else
    {
        user = Environment.UserName;
    }

    LogManager.WriteLog($"[Startup] version={GetAgentVersion()} mode={mode} pid={Environment.ProcessId} sessionId={sessionId}");
    LogManager.WriteLog($"[Startup] executable={Environment.ProcessPath ?? "unknown"}");
    LogManager.WriteLog($"[Startup] commandLine={GetSafeCommandLine(args)}");
    LogManager.WriteLog($"[Startup] appBaseDir={appBaseDir}");
    LogManager.WriteLog($"[Startup] logPath={LogManager.LogFilePath}");
    LogManager.WriteLog($"[Startup] user={user}");
}

static string GetSafeCommandLine(IReadOnlyCollection<string> args)
{
    var safeArgs = new List<string>(args.Count);
    var redactNext = false;

    foreach (var arg in args)
    {
        if (redactNext)
        {
            safeArgs.Add("<redacted>");
            redactNext = false;
            continue;
        }

        if (arg.StartsWith("--enroll=", StringComparison.OrdinalIgnoreCase) ||
            arg.StartsWith("--enrollment-code=", StringComparison.OrdinalIgnoreCase))
        {
            safeArgs.Add(arg[..(arg.IndexOf('=') + 1)] + "<redacted>");
            continue;
        }

        safeArgs.Add(arg);
        redactNext = string.Equals(arg, "--enroll", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(arg, "--enrollment-code", StringComparison.OrdinalIgnoreCase);
    }

    return string.Join(' ', safeArgs);
}

static string ResolveProcessMode(IReadOnlyCollection<string> args)
{
    if (args.Any(a => string.Equals(a, "--remote-desktop-user-helper", StringComparison.OrdinalIgnoreCase)))
    {
        return "remote-desktop-helper";
    }

    if (args.Any(a => string.Equals(a, "--remote-desktop-diagnostics", StringComparison.OrdinalIgnoreCase)))
    {
        return "remote-desktop-diagnostics";
    }

    if (args.Any(a => string.Equals(a, "--remote-support-helper-fix", StringComparison.OrdinalIgnoreCase)))
    {
        return "remote-support-helper-fix";
    }

    if (args.Any(a => string.Equals(a, "--remote-support-console-helper", StringComparison.OrdinalIgnoreCase)))
    {
        return "remote-support-console-helper";
    }

    if (args.Any(a => string.Equals(a, "--remote-desktop-helper", StringComparison.OrdinalIgnoreCase)))
    {
        return "remote-desktop-legacy-helper";
    }

    if (args.Any(a => string.Equals(a, "--service", StringComparison.OrdinalIgnoreCase)) ||
        (OperatingSystem.IsWindows() && !Environment.UserInteractive))
    {
        return "service";
    }

    return "interactive";
}

static bool IsUtilityMode(IReadOnlyCollection<string> args)
{
#pragma warning disable CA1416
    return args.Any(a => NetRatelWindowsServiceHost.UtilityArgs.Contains(a, StringComparer.OrdinalIgnoreCase));
#pragma warning restore CA1416
}

static bool IsWindowsServiceMode(IReadOnlyCollection<string> args) =>
    !IsUtilityMode(args) &&
    (args.Any(a => string.Equals(a, "--service", StringComparison.OrdinalIgnoreCase)) ||
     (OperatingSystem.IsWindows() && !Environment.UserInteractive));

[SupportedOSPlatform("windows")]
static void RunRemoteDesktopDiagnostics(string appBaseDir, IReadOnlyCollection<string> args)
{
    static void Write(string key, object? value) => Console.WriteLine($"{key}: {value}");
    static string Bool(bool value) => value ? "yes" : "no";

    Console.WriteLine("NetRatel Remote Desktop Diagnostics");
    Write("processPath", Environment.ProcessPath ?? "unknown");
    Write("appBaseDir", appBaseDir);
    Write("mode", args.Contains("--service", StringComparer.OrdinalIgnoreCase) ? "service" : "utility");
    Write("sessionId", Process.GetCurrentProcess().SessionId);
    Write("userInteractive", Bool(Environment.UserInteractive));
    Write("user", $"{Environment.UserDomainName}\\{Environment.UserName}");
    Write("elevated", Bool(IsProcessElevated()));
    if (args.Contains("--handover-self-test", StringComparer.OrdinalIgnoreCase))
    {
        Write("handoverSelfTest.no_user.desiredProvider", RemoteSupportProviderKinds.ConsoleSecureDesktopHelper);
        Write("handoverSelfTest.no_user.reason", "windows_logon_desktop_detected");
        Write("handoverSelfTest.locked.desiredProvider", RemoteSupportProviderKinds.ConsoleSecureDesktopHelper);
        Write("handoverSelfTest.locked.reason", "interactive_to_console_after_lock");
        Write("handoverSelfTest.logged_in_matching_helper.desiredProvider", RemoteSupportProviderKinds.InteractiveUserHelper);
        Write("handoverSelfTest.logged_in_matching_helper.reason", "console_to_interactive_after_login");
        Write("handoverSelfTest.logged_in_missing_helper.state", "handover_waiting_for_target_provider");
        Write("handoverSelfTest.logged_in_non_console_helper.rejected", "helper_connected_non_console_session");
        Write("handoverSelfTest.helper_disconnect.trigger", "provider_pipe_disconnected");
        Write("handoverSelfTest.peer_failure_after_logoff.trigger", "peer_failed_desktop_changed");
        Write("handoverSelfTest.capture_failure_after_desktop_change.trigger", "provider_capture_failed_desktop_changed");
    }

    Write("logFolder", LogManager.LogFolderPath);
    Write("logFile", LogManager.LogFilePath);
    var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
    var serviceLogPath = Path.Combine(programData, "NetRatel", "Client", "logs", "service");
    var legacyServiceLogPath = Path.Combine(programData, "NetRatel", "logs");
    Write("serviceLogPath", serviceLogPath);
    Write("serviceLogPathExists", Bool(Directory.Exists(serviceLogPath)));
    Write("legacyServiceLogPath", legacyServiceLogPath);
    Write("legacyServiceLogPathExists", Bool(Directory.Exists(legacyServiceLogPath)));
    var latestServiceLog = GetLatestLogFile(serviceLogPath, "netratel-client-service-");
    var latestLegacyServiceLog = GetLatestLogFile(legacyServiceLogPath, "netratel-client-service-");
    Write("latestServiceLogFile", latestServiceLog ?? "<missing>");
    Write("latestLegacyServiceLogFile", latestLegacyServiceLog ?? "<missing>");
    var effectiveServiceLog = latestServiceLog ?? latestLegacyServiceLog;
    Write("helperLogPath", RemoteDesktopUserHelperTask.GetHelperLogDirectory());
    Write("helperLogWritable", Bool(IsDirectoryWritable(RemoteDesktopUserHelperTask.GetHelperLogDirectory(), out var helperLogError)));
    if (!string.IsNullOrWhiteSpace(helperLogError))
    {
        Write("helperLogWritableError", helperLogError);
    }

    Write("pipeName", RemoteDesktopUserHelperConstants.PipeName);
    Write("pipePath", RemoteDesktopUserHelperConstants.FullPipePath);
    Write("pipeExists", Bool(DoesPipeExist(RemoteDesktopUserHelperConstants.PipeName, out var pipeError)));
    if (!string.IsNullOrWhiteSpace(pipeError))
    {
        Write("pipeCheckError", pipeError);
    }

    Write("consoleProviderPipeName", RemoteSupportConsoleProviderConstants.PipeName);
    Write("consoleProviderPipePath", RemoteSupportConsoleProviderConstants.FullPipePath);
    Write("consoleProviderHelperLogPath", RemoteSupportConsoleHelper.GetLogDirectory());
    var latestConsoleProviderLog = GetLatestLogFile(RemoteSupportConsoleHelper.GetLogDirectory(), "netratel-client-remote-support-console-helper-");
    var latestConsoleProviderException = GetLatestLogFile(RemoteSupportConsoleHelper.GetLogDirectory(), "console-provider-exception-");
    Write("consoleProviderHelperLatestLogFile", latestConsoleProviderLog ?? "<missing>");
    Write("consoleProviderHelperLatestExceptionFile", latestConsoleProviderException ?? "<missing>");
    Write("consoleProviderHelperLatestExceptionLastWriteUtc", latestConsoleProviderException is null ? "<missing>" : File.GetLastWriteTimeUtc(latestConsoleProviderException).ToString("O"));
    Write("consoleProviderHelperLatestException", latestConsoleProviderException is null ? "<missing>" : ReadTailText(latestConsoleProviderException, 20));
    var consolePipeExists = DoesPipeExist(RemoteSupportConsoleProviderConstants.PipeName, out var consolePipeError);
    Write("consoleProviderPipeExists", Bool(consolePipeExists));
    if (!string.IsNullOrWhiteSpace(consolePipeError))
    {
        Write("consoleProviderPipeCheckError", consolePipeError);
    }
    var readOnlyDiagnostics = !args.Contains("--service", StringComparer.OrdinalIgnoreCase);
    if (readOnlyDiagnostics)
    {
        Write("diagnostics_state_write_skipped", "read_only_diagnostics");
    }
    else
    {
        RemoteSupportDiagnosticState.UpdateConsoleProvider(state =>
        {
            state.ConsoleProviderPipeName = RemoteSupportConsoleProviderConstants.PipeName;
            state.ConsoleProviderPipePath = RemoteSupportConsoleProviderConstants.FullPipePath;
            state.ConsoleProviderPipeExists = consolePipeExists;
            state.ConsoleProviderPipeSecurity ??= RemoteSupportConsoleProviderConstants.SecurityDescription;
        });
    }

    Write("remoteDesktopStatePath", RemoteDesktopDiagnosticState.StatePath);
    Write("remoteDesktopStateExists", Bool(File.Exists(RemoteDesktopDiagnosticState.StatePath)));
    if (File.Exists(RemoteDesktopDiagnosticState.StatePath))
    {
        try
        {
            using var stateDoc = JsonDocument.Parse(File.ReadAllText(RemoteDesktopDiagnosticState.StatePath));
            var root = stateDoc.RootElement;
            Write("remoteDesktopStateUpdatedUtc", GetJsonString(root, "updatedUtc"));
            Write("remoteDesktopHelperConnected", GetJsonString(root, "helperConnected"));
            Write("remoteDesktopHelperPid", GetJsonString(root, "helperPid"));
            Write("remoteDesktopHelperSessionId", GetJsonString(root, "helperSessionId"));
            Write("remoteDesktopHelperVersion", GetJsonString(root, "helperVersion"));
            Write("remoteDesktopServiceVersion", GetAgentVersion());
            var helperVersion = GetJsonString(root, "helperVersion");
            Write("remoteDesktopHelperVersionMatchesService", Bool(VersionBaseMatches(helperVersion, GetAgentVersion())));
            Write("remoteDesktopLatestStreamId", GetJsonString(root, "latestStreamId"));
            Write("remoteDesktopLatestStage", GetJsonString(root, "latestStage"));
            Write("remoteDesktopLatestFrameSequence", GetJsonString(root, "latestFrameSequence"));
            Write("remoteDesktopLatestFrameBytes", GetJsonString(root, "latestFrameBytes"));
            Write("remoteDesktopLatestFrameSize", $"{GetJsonString(root, "latestFrameWidth")}x{GetJsonString(root, "latestFrameHeight")}");
            Write("remoteDesktopLatestError", GetJsonString(root, "latestError"));
        }
        catch (Exception ex)
        {
            Write("remoteDesktopStateReadError", ex.Message);
        }
    }

    var helperConnected = false;
    string? helperVersionForDecision = null;
    int? helperSessionForDecision = null;
    if (File.Exists(RemoteDesktopDiagnosticState.StatePath))
    {
        try
        {
            using var stateDoc = JsonDocument.Parse(File.ReadAllText(RemoteDesktopDiagnosticState.StatePath));
            var root = stateDoc.RootElement;
            helperConnected = string.Equals(GetJsonString(root, "helperConnected"), "true", StringComparison.OrdinalIgnoreCase);
            helperVersionForDecision = GetJsonString(root, "helperVersion");
            if (int.TryParse(GetJsonString(root, "helperSessionId"), out var parsedHelperSession))
            {
                helperSessionForDecision = parsedHelperSession;
            }
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            LogManager.WriteLog($"[RemoteSupport] Unable to read helper diagnostic state: {exception.Message}");
        }
    }

    var diagnosticHelper = helperConnected
        ? new ConnectedUserHelper(
            helperSessionForDecision ?? 0,
            0,
            helperVersionForDecision ?? string.Empty,
            string.Empty,
            DateTimeOffset.UtcNow,
            new StreamWriter(Stream.Null))
        : null;
    var providerDecision = RemoteSupportProviderDiagnostics.Evaluate(
        diagnosticHelper,
        GetAgentVersion(),
        helperConnected && VersionBaseMatches(helperVersionForDecision, GetAgentVersion()));
    if (!readOnlyDiagnostics)
    {
        RemoteSupportDiagnosticState.Write(providerDecision);
    }
    Write("remoteSupportActiveConsoleSessionId", providerDecision.ActiveConsoleSessionId?.ToString() ?? "<unknown>");
    Write("remoteSupportDesktopState", providerDecision.DesktopState);
    Write("remoteSupportInputDesktopName", providerDecision.InputDesktopName ?? "<unknown>");
    Write("remoteSupportCaptureAvailable", Bool(providerDecision.CaptureAvailable));
    Write("remoteSupportInputAvailable", Bool(providerDecision.InputAvailable));
    Write("remoteSupportSelectedProvider", providerDecision.Provider);
    Write("remoteSupportSupportLevel", providerDecision.SupportLevel);
    Write("remoteSupportMediaSupported", Bool(providerDecision.MediaSupported));
    Write("remoteSupportHelperMatchesActiveConsole", Bool(providerDecision.HelperMatchesActiveConsole));
    Write("remoteSupportRdpOrNonConsoleHelperDetected", Bool(providerDecision.RdpOrNonConsoleHelperDetected));
    Write("remoteSupportSelectedProviderReason", providerDecision.SelectedProviderReason ?? "<unknown>");
    Write("remoteSupportStatusCode", providerDecision.StatusCode);
    Write("remoteSupportStatusMessage", providerDecision.Message);
    Write("remoteSupportDiagnosticError", providerDecision.DiagnosticError ?? "<none>");
    Write("remoteSupportStatePath", RemoteSupportDiagnosticState.StatePath);
    Write("remoteSupportStateExists", Bool(File.Exists(RemoteSupportDiagnosticState.StatePath)));
    if (File.Exists(RemoteSupportDiagnosticState.StatePath))
    {
        try
        {
            using var stateDoc = JsonDocument.Parse(File.ReadAllText(RemoteSupportDiagnosticState.StatePath));
            var root = stateDoc.RootElement;
            Write("remoteSupportStateUpdatedUtc", GetJsonString(root, "updatedUtc"));
            Write("remoteSupportStateProvider", GetJsonString(root, "provider"));
            Write("remoteSupportStateDesktopState", GetJsonString(root, "desktopState"));
            Write("remoteSupportStateSupportLevel", GetJsonString(root, "supportLevel"));
            Write("remoteSupportStateStatusCode", GetJsonString(root, "statusCode"));
            Write("remoteSupportStateMediaSupported", GetJsonString(root, "mediaSupported"));
            Write("helperMatchesActiveConsole", GetJsonString(root, "helperMatchesActiveConsole"));
            Write("rdpOrNonConsoleHelperDetected", GetJsonString(root, "rdpOrNonConsoleHelperDetected"));
            Write("selectedProviderReason", GetJsonString(root, "selectedProviderReason"));
            Write("consoleProviderPipeName", GetJsonString(root, "consoleProviderPipeName"));
            Write("consoleProviderPipePath", GetJsonString(root, "consoleProviderPipePath"));
            Write("consoleProviderPipeExists", GetJsonString(root, "consoleProviderPipeExists"));
            Write("consoleProviderPipeHostStarted", GetJsonString(root, "consoleProviderPipeHostStarted"));
            Write("consoleProviderPipeHostReady", GetJsonString(root, "consoleProviderPipeHostReady"));
            Write("consoleProviderPipeSecurity", GetJsonString(root, "consoleProviderPipeSecurity"));
            Write("consoleProviderHelperLaunchAttempted", GetJsonString(root, "consoleProviderHelperLaunchAttempted"));
            Write("consoleProviderHelperLaunchCommand", GetJsonString(root, "consoleProviderHelperLaunchCommand"));
            Write("consoleProviderHelperLaunchExitCode", GetJsonString(root, "consoleProviderHelperLaunchExitCode"));
            Write("consoleProviderHelperLaunchError", GetJsonString(root, "consoleProviderHelperLaunchError"));
            Write("consoleProviderHelperLastExitCode", GetJsonString(root, "consoleProviderHelperLaunchExitCode"));
            Write("consoleProviderLauncherBackend", GetJsonString(root, "consoleProviderLauncherBackend"));
            Write("consoleProviderDuplicatedTokenSucceeded", GetJsonString(root, "consoleProviderDuplicatedTokenSucceeded"));
            Write("consoleProviderSetTokenSessionIdSucceeded", GetJsonString(root, "consoleProviderSetTokenSessionIdSucceeded"));
            Write("consoleProviderTokenSessionId", GetJsonString(root, "consoleProviderTokenSessionId"));
            Write("consoleProviderCreateEnvironmentBlockSucceeded", GetJsonString(root, "consoleProviderCreateEnvironmentBlockSucceeded"));
            Write("consoleProviderCreateProcessAsUserSucceeded", GetJsonString(root, "consoleProviderCreateProcessAsUserSucceeded"));
            Write("consoleProviderLaunchedProcessSessionId", GetJsonString(root, "consoleProviderLaunchedProcessSessionId"));
            Write("consoleProviderLaunchedDesktop", GetJsonString(root, "consoleProviderLaunchedDesktop"));
            Write("consoleProviderLaunchWin32Error", GetJsonString(root, "consoleProviderLaunchWin32Error"));
            Write("consoleProviderLaunchHresult", GetJsonString(root, "consoleProviderLaunchHresult"));
            Write("consoleProviderLaunchException", GetJsonString(root, "consoleProviderLaunchException"));
            Write("consoleProviderLaunchAttemptsJson", GetJsonString(root, "consoleProviderLaunchAttemptsJson"));
            Write("consoleProviderHelperPid", GetJsonString(root, "consoleProviderHelperPid"));
            Write("consoleProviderHelperSessionId", GetJsonString(root, "consoleProviderHelperSessionId"));
            Write("consoleProviderHelperActiveConsoleSessionId", GetJsonString(root, "consoleProviderHelperActiveConsoleSessionId"));
            Write("consoleProviderHelperLaunchedInTargetSession", GetJsonString(root, "consoleProviderHelperLaunchedInTargetSession"));
            Write("consoleProviderHelperWindowStation", GetJsonString(root, "consoleProviderHelperWindowStation"));
            Write("consoleProviderHelperDesktop", GetJsonString(root, "consoleProviderHelperDesktop"));
            Write("consoleProviderHelperConnected", GetJsonString(root, "consoleProviderHelperConnected"));
            Write("consoleProviderHelperVersion", GetJsonString(root, "consoleProviderHelperVersion"));
            Write("consoleProviderVersionMatchesService", GetJsonString(root, "consoleProviderVersionMatchesService"));
            Write("consoleProviderHelloReceived", GetJsonString(root, "consoleProviderHelloReceived"));
            Write("consoleProviderRawFirstMessageBytes", GetJsonString(root, "consoleProviderRawFirstMessageBytes"));
            Write("consoleProviderRawFirstMessagePreview", GetJsonString(root, "consoleProviderRawFirstMessagePreview"));
            Write("consoleProviderHelloParseError", GetJsonString(root, "consoleProviderHelloParseError"));
            Write("consoleProviderLastStage", GetJsonString(root, "consoleProviderLastStage"));
            Write("consoleProviderLastError", GetJsonString(root, "consoleProviderLastError"));
            Write("consoleProviderLastOfferForwarded", GetJsonString(root, "consoleProviderLastOfferForwarded"));
            Write("consoleProviderLastIceForwarded", GetJsonString(root, "consoleProviderLastIceForwarded"));
            Write("consoleProviderLastCloseForwarded", GetJsonString(root, "consoleProviderLastCloseForwarded"));
            Write("consoleProviderLastAnswerReceived", GetJsonString(root, "consoleProviderLastAnswerReceived"));
            Write("captureProviderName", GetJsonString(root, "captureProviderName"));
            Write("captureProviderBackendName", GetJsonString(root, "captureProviderBackendName"));
            Write("captureProviderSelected", GetJsonString(root, "captureProviderSelected"));
            Write("captureProviderInitStarted", GetJsonString(root, "captureProviderInitStarted"));
            Write("captureProviderInitSucceeded", GetJsonString(root, "captureProviderInitSucceeded"));
            Write("captureProviderInitFailed", GetJsonString(root, "captureProviderInitFailed"));
            Write("captureProviderInitError", GetJsonString(root, "captureProviderInitError"));
            Write("captureProviderDesktopName", GetJsonString(root, "captureProviderDesktopName"));
            Write("captureProviderSessionId", GetJsonString(root, "captureProviderSessionId"));
            Write("captureProviderActiveConsoleSessionId", GetJsonString(root, "captureProviderActiveConsoleSessionId"));
            Write("captureProviderThreadDesktopBefore", GetJsonString(root, "captureProviderThreadDesktopBefore"));
            Write("captureProviderThreadDesktopAfter", GetJsonString(root, "captureProviderThreadDesktopAfter"));
            Write("captureProviderSetThreadDesktopSucceeded", GetJsonString(root, "captureProviderSetThreadDesktopSucceeded"));
            Write("captureProviderLastFrameAttemptUtc", GetJsonString(root, "captureProviderLastFrameAttemptUtc"));
            Write("captureProviderLastFrameError", GetJsonString(root, "captureProviderLastFrameError"));
            Write("captureProviderConsecutiveFailures", GetJsonString(root, "captureProviderConsecutiveFailures"));
            Write("captureProviderLastSuccessfulFrameUtc", GetJsonString(root, "captureProviderLastSuccessfulFrameUtc"));
            Write("captureProviderLastWin32Error", GetJsonString(root, "captureProviderLastWin32Error"));
            Write("captureProviderBestBackend", GetJsonString(root, "captureProviderBestBackend"));
            Write("captureProviderFailedBackends", GetJsonString(root, "captureProviderFailedBackends"));
            Write("captureProviderMatrixJson", GetJsonString(root, "captureProviderMatrixJson"));
            Write("remoteSupportTransportSupported", GetJsonString(root, "remoteSupportTransportSupported"));
            Write("remoteSupportSessionId", GetJsonString(root, "remoteSupportSessionId"));
            Write("remoteSupportStateIsCurrentSession", GetJsonString(root, "remoteSupportStateIsCurrentSession"));
            Write("remoteSupportPeerConnected", GetJsonString(root, "remoteSupportPeerConnected"));
            Write("remoteSupportPeerConnectedAt", GetJsonString(root, "remoteSupportPeerConnectedAt"));
            Write("remoteSupportPeerLastState", GetJsonString(root, "remoteSupportPeerLastState"));
            Write("remoteSupportDataChannelOpen", GetJsonString(root, "remoteSupportDataChannelOpen"));
            Write("remoteSupportDataChannelOpenAt", GetJsonString(root, "remoteSupportDataChannelOpenAt"));
            Write("remoteSupportCaptureSupported", GetJsonString(root, "remoteSupportCaptureSupported"));
            Write("remoteSupportFirstFrameDelivered", GetJsonString(root, "remoteSupportFirstFrameDelivered"));
            Write("remoteSupportInputSupported", GetJsonString(root, "remoteSupportInputSupported"));
            Write("remoteSupportRenderedFrames", GetJsonString(root, "remoteSupportRenderedFrames"));
            Write("remoteSupportStateUpdatedUtc", GetJsonString(root, "remoteSupportStateUpdatedUtc"));
            Write("remoteSupportStateSource", GetJsonString(root, "remoteSupportStateSource"));
            Write("remoteSupportIsLiveSession", GetJsonString(root, "remoteSupportIsLiveSession"));
            Write("remoteSupportIsLastClosedSession", GetJsonString(root, "remoteSupportIsLastClosedSession"));
            Write("remoteSupportClosedAt", GetJsonString(root, "remoteSupportClosedAt"));
            Write("remoteSupportCloseReason", GetJsonString(root, "remoteSupportCloseReason"));
            Write("remoteSupportFirstFrameDeliveredAt", GetJsonString(root, "remoteSupportFirstFrameDeliveredAt"));
            Write("remoteSupportLastFrameRenderedAt", GetJsonString(root, "remoteSupportLastFrameRenderedAt"));
            Write("activeInputProvider", GetJsonString(root, "activeInputProvider"));
            Write("inputProviderName", GetJsonString(root, "inputProviderName"));
            Write("inputProviderSessionId", GetJsonString(root, "inputProviderSessionId"));
            Write("inputProviderActiveConsoleSessionId", GetJsonString(root, "inputProviderActiveConsoleSessionId"));
            Write("inputProviderMatchesTargetSession", GetJsonString(root, "inputProviderMatchesTargetSession"));
            Write("inputProviderDesktopName", GetJsonString(root, "inputProviderDesktopName"));
            Write("inputProviderReady", GetJsonString(root, "inputProviderReady"));
            Write("inputProviderLastInputReceivedAt", GetJsonString(root, "inputProviderLastInputReceivedAt"));
            Write("inputProviderLastInputInjectedAt", GetJsonString(root, "inputProviderLastInputInjectedAt"));
            Write("inputProviderLastInputError", GetJsonString(root, "inputProviderLastInputError"));
            Write("inputProviderInjectedMouseCount", GetJsonString(root, "inputProviderInjectedMouseCount"));
            Write("inputProviderInjectedKeyCount", GetJsonString(root, "inputProviderInjectedKeyCount"));
            Write("inputProviderRejectedCount", GetJsonString(root, "inputProviderRejectedCount"));
            Write("inputProviderReceivedMouseCount", GetJsonString(root, "inputProviderReceivedMouseCount"));
            Write("inputProviderReceivedKeyCount", GetJsonString(root, "inputProviderReceivedKeyCount"));
            Write("mouseMoveReceivedCount", GetJsonString(root, "mouseMoveReceivedCount"));
            Write("mouseMoveSentCount", GetJsonString(root, "mouseMoveSentCount"));
            Write("mouseMoveCoalescedCount", GetJsonString(root, "mouseMoveCoalescedCount"));
            Write("mouseClickSentCount", GetJsonString(root, "mouseClickSentCount"));
            Write("inputProviderLastInputEventId", GetJsonString(root, "inputProviderLastInputEventId"));
            Write("inputProviderLastInputKind", GetJsonString(root, "inputProviderLastInputKind"));
            Write("inputProviderLastKeyCategory", GetJsonString(root, "inputProviderLastKeyCategory"));
            Write("inputProviderLastBrowserSentAt", GetJsonString(root, "inputProviderLastBrowserSentAt"));
            Write("inputProviderLastDataChannelSentAt", GetJsonString(root, "inputProviderLastDataChannelSentAt"));
            Write("inputProviderLastReceivedAt", GetJsonString(root, "inputProviderLastReceivedAt"));
            Write("inputProviderLastSendInputAttemptedAt", GetJsonString(root, "inputProviderLastSendInputAttemptedAt"));
            Write("inputProviderLastSendInputResultCount", GetJsonString(root, "inputProviderLastSendInputResultCount"));
            Write("inputProviderLastSendInputWin32Error", GetJsonString(root, "inputProviderLastSendInputWin32Error"));
            Write("captureFrameSequence", GetJsonString(root, "captureFrameSequence"));
            Write("captureFrameHash", GetJsonString(root, "captureFrameHash"));
            Write("captureFrameChanged", GetJsonString(root, "captureFrameChanged"));
            Write("captureSameFrameCount", GetJsonString(root, "captureSameFrameCount"));
            Write("captureLastChangedFrameUtc", GetJsonString(root, "captureLastChangedFrameUtc"));
            Write("captureLastInputEventId", GetJsonString(root, "captureLastInputEventId"));
            Write("captureLastInputInjectedUtc", GetJsonString(root, "captureLastInputInjectedUtc"));
            Write("captureFirstFrameAfterInputUtc", GetJsonString(root, "captureFirstFrameAfterInputUtc"));
            Write("captureFirstChangedFrameAfterInputUtc", GetJsonString(root, "captureFirstChangedFrameAfterInputUtc"));
            Write("captureInputToChangedFrameMs", GetJsonString(root, "captureInputToChangedFrameMs"));
            Write("captureInputToRenderedFrameMs", GetJsonString(root, "captureInputToRenderedFrameMs"));
            Write("encoderFrameSequence", GetJsonString(root, "encoderFrameSequence"));
            Write("encoderKeyframeRequestedAt", GetJsonString(root, "encoderKeyframeRequestedAt"));
            Write("encoderKeyframeStatus", GetJsonString(root, "encoderKeyframeStatus"));
            Write("encoderLastFrameSentAt", GetJsonString(root, "encoderLastFrameSentAt"));
            Write("webLastFrameRenderedAt", GetJsonString(root, "webLastFrameRenderedAt"));
            Write("inputVisualFeedbackStatus", GetJsonString(root, "inputVisualFeedbackStatus"));
            Write("remoteSupportSasSupported", GetJsonString(root, "remoteSupportSasSupported"));
            Write("remoteSupportSasPolicyAllowsServices", GetJsonString(root, "remoteSupportSasPolicyAllowsServices"));
            Write("remoteSupportSasApiAvailable", GetJsonString(root, "remoteSupportSasApiAvailable"));
            Write("remoteSupportSasProvider", GetJsonString(root, "remoteSupportSasProvider"));
            Write("remoteSupportSasTargetSessionId", GetJsonString(root, "remoteSupportSasTargetSessionId"));
            Write("remoteSupportSasLastAttemptAt", GetJsonString(root, "remoteSupportSasLastAttemptAt"));
            Write("remoteSupportSasLastResult", GetJsonString(root, "remoteSupportSasLastResult"));
            Write("remoteSupportSasLastError", GetJsonString(root, "remoteSupportSasLastError"));
            Write("remoteSupportSasLastWin32Error", GetJsonString(root, "remoteSupportSasLastWin32Error"));
            Write("remoteSupportSasLastHresult", GetJsonString(root, "remoteSupportSasLastHresult"));
            Write("softwareSasGenerationRawValue", GetJsonString(root, "softwareSasGenerationRawValue"));
            Write("softwareSasGenerationInterpretedValue", GetJsonString(root, "softwareSasGenerationInterpretedValue"));
            Write("softwareSasAllowsServices", GetJsonString(root, "softwareSasAllowsServices"));
            Write("softwareSasAllowsEaseOfAccess", GetJsonString(root, "softwareSasAllowsEaseOfAccess"));
            Write("softwareSasPolicySource", GetJsonString(root, "softwareSasPolicySource"));
            Write("remoteSupportOsCaption", GetJsonString(root, "remoteSupportOsCaption"));
            Write("remoteSupportOsVersion", GetJsonString(root, "remoteSupportOsVersion"));
            Write("remoteSupportOsBuild", GetJsonString(root, "remoteSupportOsBuild"));
            Write("remoteSupportOsIsServer", GetJsonString(root, "remoteSupportOsIsServer"));
            Write("interactiveHelperRepairSupported", GetJsonString(root, "interactiveHelperRepairSupported"));
            Write("interactiveHelperRepairInProgress", GetJsonString(root, "interactiveHelperRepairInProgress"));
            Write("interactiveHelperRepairLastAttemptAt", GetJsonString(root, "interactiveHelperRepairLastAttemptAt"));
            Write("interactiveHelperRepairLastResult", GetJsonString(root, "interactiveHelperRepairLastResult"));
            Write("interactiveHelperRepairLastError", GetJsonString(root, "interactiveHelperRepairLastError"));
            Write("interactiveHelperLauncherTarget", GetJsonString(root, "interactiveHelperLauncherTarget"));
            Write("interactiveHelperLauncherTargetVersion", GetJsonString(root, "interactiveHelperLauncherTargetVersion"));
            Write("interactiveHelperConnectedVersion", GetJsonString(root, "interactiveHelperConnectedVersion"));
            Write("interactiveHelperServiceVersion", GetJsonString(root, "interactiveHelperServiceVersion"));
            Write("interactiveHelperVersionMismatchAgeSeconds", GetJsonString(root, "interactiveHelperVersionMismatchAgeSeconds"));
            Write("interactiveHelperStalePid", GetJsonString(root, "interactiveHelperStalePid"));
            Write("interactiveHelperStaleSessionId", GetJsonString(root, "interactiveHelperStaleSessionId"));
            Write("interactiveHelperTerminateAttempted", GetJsonString(root, "interactiveHelperTerminateAttempted"));
            Write("interactiveHelperRelaunchAttempted", GetJsonString(root, "interactiveHelperRelaunchAttempted"));
            Write("interactiveHelperRelaunchResult", GetJsonString(root, "interactiveHelperRelaunchResult"));
            Write("remoteSupportInventoryPublisherStarted", GetJsonString(root, "remoteSupportInventoryPublisherStarted"));
            Write("remoteSupportInventoryLastEnumerationStartedAt", GetJsonString(root, "remoteSupportInventoryLastEnumerationStartedAt"));
            Write("remoteSupportInventoryLastEnumerationCompletedAt", GetJsonString(root, "remoteSupportInventoryLastEnumerationCompletedAt"));
            Write("remoteSupportInventoryLastPublishStartedAt", GetJsonString(root, "remoteSupportInventoryLastPublishStartedAt"));
            Write("remoteSupportInventoryLastPublishCompletedAt", GetJsonString(root, "remoteSupportInventoryLastPublishCompletedAt"));
            Write("remoteSupportInventoryLastRefreshObservedAt", GetJsonString(root, "remoteSupportInventoryLastRefreshObservedAt"));
            Write("remoteSupportInventoryLastRefreshAckSentAt", GetJsonString(root, "remoteSupportInventoryLastRefreshAckSentAt"));
            Write("remoteSupportInventoryLastRefreshRequestId", GetJsonString(root, "remoteSupportInventoryLastRefreshRequestId"));
            Write("remoteSupportInventoryLastSource", GetJsonString(root, "remoteSupportInventoryLastSource"));
            Write("remoteSupportInventoryLastSequence", GetJsonString(root, "remoteSupportInventoryLastSequence"));
            Write("remoteSupportInventoryLastSessionCount", GetJsonString(root, "remoteSupportInventoryLastSessionCount"));
            Write("remoteSupportInventoryPendingRefreshCount", GetJsonString(root, "remoteSupportInventoryPendingRefreshCount"));
            Write("remoteSupportInventoryLastError", GetJsonString(root, "remoteSupportInventoryLastError"));
            Write("telemetrySinkSeenAt", GetJsonString(root, "telemetrySinkSeenAt"));
            Write("telemetryLastPublishAt", GetJsonString(root, "telemetryLastPublishAt"));
            Write("telemetryLastError", GetJsonString(root, "telemetryLastError"));
            Write("handoverSupported", GetJsonString(root, "handoverSupported"));
            Write("handoverInProgress", GetJsonString(root, "handoverInProgress"));
            Write("handoverGeneration", GetJsonString(root, "handoverGeneration"));
            Write("handoverPreviousProvider", GetJsonString(root, "handoverPreviousProvider"));
            Write("handoverTargetProvider", GetJsonString(root, "handoverTargetProvider"));
            Write("handoverReason", GetJsonString(root, "handoverReason"));
            Write("handoverDetectedAt", GetJsonString(root, "handoverDetectedAt"));
            Write("handoverStartedAt", GetJsonString(root, "handoverStartedAt"));
            Write("handoverTargetProviderReadyAt", GetJsonString(root, "handoverTargetProviderReadyAt"));
            Write("handoverOfferSentAt", GetJsonString(root, "handoverOfferSentAt"));
            Write("handoverAnswerReceivedAt", GetJsonString(root, "handoverAnswerReceivedAt"));
            Write("handoverPeerConnectedAt", GetJsonString(root, "handoverPeerConnectedAt"));
            Write("handoverCompletedAt", GetJsonString(root, "handoverCompletedAt"));
            Write("handoverFailedAt", GetJsonString(root, "handoverFailedAt"));
            Write("handoverFailureReason", GetJsonString(root, "handoverFailureReason"));
            Write("handoverLastTrigger", GetJsonString(root, "handoverLastTrigger"));
            Write("handoverAttemptCount", GetJsonString(root, "handoverAttemptCount"));
            Write("lastDesktopStateBeforeHandover", GetJsonString(root, "lastDesktopStateBeforeHandover"));
            Write("lastDesktopStateAfterHandover", GetJsonString(root, "lastDesktopStateAfterHandover"));
            Write("lastProviderBeforeHandover", GetJsonString(root, "lastProviderBeforeHandover"));
            Write("lastProviderAfterHandover", GetJsonString(root, "lastProviderAfterHandover"));
            Write("activeProvider", GetJsonString(root, "activeProvider"));
            Write("desiredProvider", GetJsonString(root, "desiredProvider"));
            Write("currentProviderGeneration", GetJsonString(root, "currentProviderGeneration"));
            Write("activeHelperSessionId", GetJsonString(root, "activeHelperSessionId"));
        }
        catch (Exception ex)
        {
            Write("remoteSupportStateReadError", ex.Message);
        }
    }

    Write("helperLauncherPath", RemoteDesktopUserHelperTask.GetLauncherPath());
    Write("helperLauncherExists", Bool(File.Exists(RemoteDesktopUserHelperTask.GetLauncherPath())));
    var helperLauncherTarget = TryGetVbsLauncherTarget(RemoteDesktopUserHelperTask.GetLauncherPath());
    Write("helperLauncherTarget", helperLauncherTarget ?? "<unknown>");
    Write("helperLauncherTargetVersion", !string.IsNullOrWhiteSpace(helperLauncherTarget)
        ? TryGetFileVersionFromCommandLine(helperLauncherTarget) ?? "<unknown>"
        : "<unknown>");
    var credentialPath = AgentCredentialStore.DefaultPath();
    Write("agentCredentialPath", credentialPath);
    Write("agentCredentialExists", Bool(File.Exists(credentialPath)));
    if (File.Exists(credentialPath))
    {
        try
        {
            var credentialInfo = new FileInfo(credentialPath);
            Write("agentCredentialLastWriteUtc", credentialInfo.LastWriteTimeUtc.ToString("O"));
            Write("agentCredentialBytes", credentialInfo.Length);
        }
        catch (Exception ex)
        {
            Write("agentCredentialInfoError", ex.Message);
        }
    }

    try
    {
        using var runKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", writable: false);
        Write("hklmRunValue", runKey?.GetValue(RemoteDesktopUserHelperConstants.RunValueName) ?? "<missing>");
    }
    catch (Exception ex)
    {
        Write("hklmRunValueError", ex.Message);
    }

    Write("remoteSupportFirewallStatePath", WindowsRemoteSupportFirewall.StatePath);
    Write("remoteSupportFirewallStateExists", Bool(File.Exists(WindowsRemoteSupportFirewall.StatePath)));
    if (File.Exists(WindowsRemoteSupportFirewall.StatePath))
    {
        try
        {
            using var firewallStateDoc = JsonDocument.Parse(File.ReadAllText(WindowsRemoteSupportFirewall.StatePath));
            var root = firewallStateDoc.RootElement;
            Write("remoteSupportFirewallStateUpdatedUtc", GetJsonString(root, "updatedUtc"));
            Write("remoteSupportFirewallStateRan", GetJsonString(root, "ran"));
            Write("remoteSupportFirewallStateProcessId", GetJsonString(root, "processId"));
            Write("remoteSupportFirewallStateProcessPath", GetJsonString(root, "processPath"));
            Write("remoteSupportFirewallStateServiceMode", GetJsonString(root, "isServiceMode"));
            Write("remoteSupportFirewallStateUdpRulePresent", GetJsonString(root, "udpRulePresent"));
            Write("remoteSupportFirewallStateTcpRulePresent", GetJsonString(root, "tcpRulePresent"));
            Write("remoteSupportFirewallStateError", GetJsonString(root, "error"));

            if (root.TryGetProperty("udp", out var udpAttempt))
            {
                Write("remoteSupportFirewallStateUdpNetshAddExit", GetJsonString(udpAttempt, "netshAddExitCode"));
                Write("remoteSupportFirewallStateUdpPowerShellExit", GetJsonString(udpAttempt, "powerShellExitCode"));
                Write("remoteSupportFirewallStateUdpAfterPowerShell", GetJsonString(udpAttempt, "rulePresentAfterPowerShell"));
            }

            if (root.TryGetProperty("tcp", out var tcpAttempt))
            {
                Write("remoteSupportFirewallStateTcpNetshAddExit", GetJsonString(tcpAttempt, "netshAddExitCode"));
                Write("remoteSupportFirewallStateTcpPowerShellExit", GetJsonString(tcpAttempt, "powerShellExitCode"));
                Write("remoteSupportFirewallStateTcpAfterPowerShell", GetJsonString(tcpAttempt, "rulePresentAfterPowerShell"));
            }
        }
        catch (Exception ex)
        {
            Write("remoteSupportFirewallStateReadError", ex.Message);
        }
    }

    var firewallUdp = TryGetFirewallRule(WindowsRemoteSupportFirewall.RuleNameUdp, out var firewallUdpError);
    Write("remoteSupportFirewallUdpRule", firewallUdp ?? "<missing>");
    if (!string.IsNullOrWhiteSpace(firewallUdpError))
    {
        Write("remoteSupportFirewallUdpRuleError", firewallUdpError);
    }

    var firewallTcp = TryGetFirewallRule(WindowsRemoteSupportFirewall.RuleNameTcp, out var firewallTcpError);
    Write("remoteSupportFirewallTcpRule", firewallTcp ?? "<missing>");
    if (!string.IsNullOrWhiteSpace(firewallTcpError))
    {
        Write("remoteSupportFirewallTcpRuleError", firewallTcpError);
    }

    try
    {
        using var controller = new ServiceController("NetRatel.Client");
        Write("serviceStatus", controller.Status);
        Write("serviceCanStop", Bool(controller.CanStop));
    }
    catch (Exception ex)
    {
        Write("serviceStatusError", ex.Message);
    }

    var serviceConfig = TryGetServiceConfig("NetRatel.Client", out var serviceConfigError);
    if (!string.IsNullOrWhiteSpace(serviceConfigError))
    {
        Write("serviceConfigError", serviceConfigError);
    }
    else if (!string.IsNullOrWhiteSpace(serviceConfig))
    {
        var binaryPath = TryExtractServiceBinaryPath(serviceConfig);
        Write("serviceBinaryPath", binaryPath ?? "<unknown>");
        if (!string.IsNullOrWhiteSpace(binaryPath))
        {
            Write("serviceVersion", TryGetFileVersionFromCommandLine(binaryPath) ?? "<unknown>");
        }
    }

    if (!string.IsNullOrWhiteSpace(effectiveServiceLog))
    {
        Write("diagnosticServiceLogFile", effectiveServiceLog);
        Write("pipeHostStartedFromLogs", Bool(ServiceLogContains(effectiveServiceLog, "Helper pipe listener task started")));
        Write("pipeHostReadyFromLogs", Bool(ServiceLogContains(effectiveServiceLog, "Helper pipe listener ready")));
        var pipeHostLines = ReadFilteredTailLines(effectiveServiceLog, line =>
            line.Contains("Helper pipe", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("User helper pipe host", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("helper_not_connected", StringComparison.OrdinalIgnoreCase),
            12);
        Console.WriteLine("pipeHostLogTail:");
        foreach (var line in pipeHostLines)
        {
            Console.WriteLine("  " + line);
        }

        Write("consoleProviderPipeHostStartedFromLogs", Bool(ServiceLogContains(effectiveServiceLog, "[RemoteSupportConsoleProvider] Pipe host startup")));
        Write("consoleProviderPipeHostReadyFromLogs", Bool(ServiceLogContains(effectiveServiceLog, "[RemoteSupportConsoleProvider] Pipe listener ready")));
        Write("consoleProviderHelperLaunchAttemptedFromLogs", Bool(ServiceLogContains(effectiveServiceLog, "Started provider process")));
        var consoleProviderLines = ReadFilteredTailLines(effectiveServiceLog, line =>
            line.Contains("RemoteSupportConsoleProvider", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("Console provider", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("console_provider", StringComparison.OrdinalIgnoreCase),
            20);
        Console.WriteLine("consoleProviderLogTail:");
        foreach (var line in consoleProviderLines)
        {
            Console.WriteLine("  " + line);
        }
    }

    Console.WriteLine("netratelClientProcesses:");
    foreach (var process in Process.GetProcessesByName("NetRatel.Client").OrderBy(p => p.Id))
    {
        using (process)
        {
            try
            {
                Write("  process", $"pid={process.Id} sessionId={process.SessionId} path={TryGetProcessPath(process)}");
            }
            catch (Exception ex)
            {
                Write("  process", $"pid={process.Id} error={ex.Message}");
            }
        }
    }
}

[SupportedOSPlatform("windows")]
static string? TryGetFirewallRule(string ruleName, out string? error)
{
    try
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "netsh.exe",
            Arguments = $"advfirewall firewall show rule name=\"{ruleName}\"",
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        using var process = Process.Start(startInfo);
        if (process is null)
        {
            error = "Unable to start netsh.exe.";
            return null;
        }

        var output = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(5000))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                LogManager.WriteLog($"[Diagnostics] Unable to terminate timed-out netsh.exe: {exception.Message}");
            }

            error = "netsh.exe timed out.";
            return null;
        }

        if (process.ExitCode != 0)
        {
            error = TrimForLog(standardError + " " + output);
            return null;
        }

        error = null;
        var programLine = output
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(x => x.StartsWith("Program:", StringComparison.OrdinalIgnoreCase));
        return string.IsNullOrWhiteSpace(programLine)
            ? "present"
            : programLine;
    }
    catch (Exception ex)
    {
        error = ex.Message;
        return null;
    }
}

static string GetJsonString(JsonElement root, string propertyName)
{
    if (!root.TryGetProperty(propertyName, out var value) || value.ValueKind == JsonValueKind.Null)
    {
        return "<null>";
    }

    return value.ValueKind == JsonValueKind.String
        ? value.GetString() ?? string.Empty
        : value.ToString();
}

static bool VersionBaseMatches(string? left, string? right)
{
    static string Normalize(string? version)
    {
        if (string.IsNullOrWhiteSpace(version) || string.Equals(version, "<null>", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        var trimmed = version.Trim();
        var plusIndex = trimmed.IndexOf('+', StringComparison.Ordinal);
        return plusIndex > 0 ? trimmed[..plusIndex] : trimmed;
    }

    var normalizedLeft = Normalize(left);
    return !string.IsNullOrWhiteSpace(normalizedLeft) &&
        string.Equals(normalizedLeft, Normalize(right), StringComparison.OrdinalIgnoreCase);
}

[SupportedOSPlatform("windows")]
static string? TryGetServiceConfig(string serviceName, out string? error)
{
    try
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "sc.exe",
            Arguments = $"qc {serviceName}",
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        using var process = Process.Start(startInfo);
        if (process is null)
        {
            error = "Unable to start sc.exe.";
            return null;
        }

        var output = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(5000))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                LogManager.WriteLog($"[Diagnostics] Unable to terminate timed-out sc.exe: {exception.Message}");
            }

            error = "sc.exe timed out.";
            return null;
        }

        if (process.ExitCode != 0)
        {
            error = TrimForLog(standardError + " " + output);
            return null;
        }

        error = null;
        return output;
    }
    catch (Exception ex)
    {
        error = ex.Message;
        return null;
    }
}

static string? TryExtractServiceBinaryPath(string serviceConfig)
{
    foreach (var rawLine in serviceConfig.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
    {
        var marker = "BINARY_PATH_NAME";
        var markerIndex = rawLine.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            continue;
        }

        var colonIndex = rawLine.IndexOf(':', markerIndex);
        if (colonIndex >= 0 && colonIndex + 1 < rawLine.Length)
        {
            return rawLine[(colonIndex + 1)..].Trim();
        }
    }

    return null;
}

static string? TryGetFileVersionFromCommandLine(string commandLine)
{
    try
    {
        var exePath = ExtractExecutablePath(commandLine);
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
        {
            return null;
        }

        var version = FileVersionInfo.GetVersionInfo(exePath);
        return !string.IsNullOrWhiteSpace(version.ProductVersion)
            ? version.ProductVersion
            : version.FileVersion;
    }
    catch
    {
        return null;
    }
}

static string? TryGetVbsLauncherTarget(string launcherPath)
{
    try
    {
        if (!File.Exists(launcherPath))
        {
            return null;
        }

        foreach (var line in File.ReadLines(launcherPath))
        {
            var exeIndex = line.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (exeIndex < 0)
            {
                continue;
            }

            var beforeExe = line[..(exeIndex + 4)];
            var start = beforeExe.LastIndexOf("\"\"\"", StringComparison.Ordinal);
            if (start >= 0)
            {
                return beforeExe[(start + 3)..];
            }

            start = beforeExe.LastIndexOf('"');
            return start >= 0 ? beforeExe[(start + 1)..] : ExtractExecutablePath(beforeExe);
        }
    }
    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
    {
        LogManager.WriteLog($"[Diagnostics] Unable to inspect service configuration: {exception.Message}");
    }

    return null;
}

static string? ExtractExecutablePath(string commandLine)
{
    var trimmed = commandLine.Trim();
    if (trimmed.Length == 0)
    {
        return null;
    }

    if (trimmed[0] == '"')
    {
        var endQuote = trimmed.IndexOf('"', 1);
        return endQuote > 1 ? trimmed[1..endQuote] : null;
    }

    var exeIndex = trimmed.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
    if (exeIndex >= 0)
    {
        return trimmed[..(exeIndex + 4)];
    }

    var firstSpace = trimmed.IndexOf(' ');
    return firstSpace > 0 ? trimmed[..firstSpace] : trimmed;
}

static string? GetLatestLogFile(string directory, string? requiredPrefix = null)
{
    try
    {
        if (!Directory.Exists(directory))
        {
            return null;
        }

        return Directory.GetFiles(directory, "*.log")
            .Where(path => string.IsNullOrWhiteSpace(requiredPrefix) ||
                           Path.GetFileName(path).StartsWith(requiredPrefix, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }
    catch
    {
        return null;
    }
}

static bool ServiceLogContains(string logFile, string value)
{
    try
    {
        return File.ReadLines(logFile).Any(line => line.Contains(value, StringComparison.OrdinalIgnoreCase));
    }
    catch
    {
        return false;
    }
}

static IReadOnlyList<string> ReadFilteredTailLines(string logFile, Func<string, bool> predicate, int maxLines)
{
    try
    {
        var lines = new Queue<string>(maxLines);
        foreach (var line in File.ReadLines(logFile))
        {
            if (!predicate(line))
            {
                continue;
            }

            if (lines.Count == maxLines)
            {
                lines.Dequeue();
            }

            lines.Enqueue(line);
        }

        return lines.ToList();
    }
    catch (Exception ex)
    {
        return [$"<unable to read service log: {ex.Message}>"];
    }
}

static string ReadTailText(string logFile, int maxLines)
{
    try
    {
        return string.Join(" | ", File.ReadLines(logFile).TakeLast(maxLines));
    }
    catch (Exception ex)
    {
        return $"<unable to read exception log: {ex.Message}>";
    }
}

[SupportedOSPlatform("windows")]
static bool IsProcessElevated()
{
    using var identity = WindowsIdentity.GetCurrent();
    var principal = new WindowsPrincipal(identity);
    return principal.IsInRole(WindowsBuiltInRole.Administrator);
}

static bool IsDirectoryWritable(string directory, out string? error)
{
    try
    {
        Directory.CreateDirectory(directory);
        var probe = Path.Combine(directory, $".write-test-{Guid.NewGuid():N}.tmp");
        File.WriteAllText(probe, "ok");
        File.Delete(probe);
        error = null;
        return true;
    }
    catch (Exception ex)
    {
        error = ex.Message;
        return false;
    }
}

static bool DoesPipeExist(string pipeName, out string? error)
{
    try
    {
        var pipes = Directory.GetFiles(@"\\.\pipe\");
        error = null;
        return pipes.Any(p => p.EndsWith("\\" + pipeName, StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(Path.GetFileName(p), pipeName, StringComparison.OrdinalIgnoreCase));
    }
    catch (Exception ex)
    {
        error = ex.Message;
        return false;
    }
}

static string TryGetProcessPath(Process process)
{
    try
    {
        return process.MainModule?.FileName ?? "unknown";
    }
    catch
    {
        return "unavailable";
    }
}

static string ResolveLogDirectory(string appBaseDir, IReadOnlyCollection<string> args)
{
    var configured = Environment.GetEnvironmentVariable("NetRatel_CLIENT_LOG_DIR")
        ?? Environment.GetEnvironmentVariable("NetRatelCLIENT__Client__LogDirectory")
        ?? Environment.GetEnvironmentVariable("NetRatelCLIENT__LogDirectory");
    if (!string.IsNullOrWhiteSpace(configured))
    {
        return configured;
    }

    var container = string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase)
        || File.Exists("/.dockerenv");
    if (container)
    {
        return Path.Combine(appBaseDir, "logs");
    }

    if (OperatingSystem.IsWindows() &&
        args.Any(a => string.Equals(a, "--remote-desktop-user-helper", StringComparison.OrdinalIgnoreCase)))
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "NetRatel",
            "Client",
            "logs",
            "remote-desktop-helper");
    }

    if (OperatingSystem.IsWindows() &&
        args.Any(a => string.Equals(a, "--remote-support-console-helper", StringComparison.OrdinalIgnoreCase)))
    {
        return RemoteSupportConsoleHelper.GetLogDirectory();
    }

    var serviceMode = IsWindowsServiceMode(args);
    if (!serviceMode)
    {
        if (OperatingSystem.IsWindows())
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrWhiteSpace(localAppData))
            {
                return Path.Combine(localAppData, "NetRatel", "Client", "logs");
            }
        }

        return Path.Combine(appBaseDir, "logs");
    }

    if (OperatingSystem.IsWindows())
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "NetRatel",
            "Client",
            "logs",
            "service");
    }

    return "/var/lib/netratel/logs";
}

static string ResolveLogFilePrefix(IReadOnlyCollection<string> args)
{
    var version = GetAgentVersion();
    var mode = ResolveProcessMode(args);
    return mode switch
    {
        "service" => $"netratel-client-service-{version}-{Environment.ProcessId}",
        "remote-desktop-helper" => $"netratel-client-remote-desktop-helper-{version}-{Environment.ProcessId}",
        "remote-desktop-diagnostics" => $"netratel-client-remote-desktop-diagnostics-{version}-{Environment.ProcessId}",
        "remote-support-console-helper" => $"netratel-client-remote-support-console-helper-{version}-{Environment.ProcessId}",
        _ => $"netratel-client-{mode}-{version}-{Environment.ProcessId}"
    };
}

static string TrimForLog(string value)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return string.Empty;
    }

    const int max = 300;
    var compact = value.Replace(Environment.NewLine, " ").Trim();
    return compact.Length <= max ? compact : compact.Substring(0, max) + "...";
}

static bool IsAgentDisabled(AgentClientAuthException ex)
{
    if (string.Equals(ex.Code, "agent_disabled", StringComparison.OrdinalIgnoreCase))
    {
        return true;
    }

    return ex.StatusCode == 403 && ex.Message.Contains("disabled", StringComparison.OrdinalIgnoreCase);
}

static int? TryGetTenantId(string token)
{
    if (string.IsNullOrWhiteSpace(token))
    {
        return null;
    }

    try
    {
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        var rawTenantId = jwt.Claims.FirstOrDefault(c => c.Type == "tenant_id")?.Value;
        return int.TryParse(rawTenantId, out var tenantId) ? tenantId : null;
    }
    catch
    {
        return null;
    }
}

static string GetAgentVersion()
{
    var assembly = Assembly.GetEntryAssembly() ?? typeof(GlobalContext).Assembly;
    var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
    if (!string.IsNullOrWhiteSpace(informational))
    {
        return informational;
    }

    return assembly.GetName().Version?.ToString() ?? "Unknown";
}

var startupArgs = Environment.GetCommandLineArgs().Skip(1).ToArray();
if (OperatingSystem.IsWindows() && NetRatelWindowsServiceHost.ShouldRunAsService(startupArgs))
{
    NetRatelWindowsServiceHost.Run(() => RunClientAsync());
}
else
{
    RunClientAsync().GetAwaiter().GetResult();
}

[SupportedOSPlatform("windows")]
public sealed class NetRatelWindowsServiceHost : ServiceBase
{
    public static readonly string[] UtilityArgs =
    [
        "--enroll",
        "--auth-check",
        "--help",
        "-h",
        "/?",
        "--version",
        "--remote-desktop-user-helper",
        "--remote-desktop-diagnostics",
        "--remote-support-console-helper",
        "--remote-support-helper-fix",
        "--sas-self-test"
    ];

    private readonly Func<Task> _runAsync;
    private Task? _runTask;

    private NetRatelWindowsServiceHost(Func<Task> runAsync)
    {
        _runAsync = runAsync;
        ServiceName = "NetRatel.Client";
        CanStop = true;
        CanShutdown = true;
    }

    public static bool ShouldRunAsService(IReadOnlyCollection<string> args)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        if (args.Any(a => UtilityArgs.Contains(a, StringComparer.OrdinalIgnoreCase)))
        {
            return false;
        }

        return args.Any(a => string.Equals(a, "--service", StringComparison.OrdinalIgnoreCase))
            || !Environment.UserInteractive;
    }

    public static void Run(Func<Task> runAsync)
    {
        ServiceBase.Run(new NetRatelWindowsServiceHost(runAsync));
    }

    protected override void OnStart(string[] args)
    {
        LogManager.WriteLog("[Service] Windows service start requested.");
        _runTask = Task.Run(async () =>
        {
            try
            {
                await _runAsync().ConfigureAwait(false);
                var exitCode = Environment.ExitCode == 0 ? 1 : Environment.ExitCode;
                LogManager.WriteLog($"[Service] Client worker returned; stopping service process. exitCode={exitCode}");
                Environment.ExitCode = exitCode;
                Environment.Exit(exitCode);
            }
            catch (Exception ex)
            {
                LogManager.WriteLog($"[Service] Client service terminated unexpectedly: {ex}");
                try
                {
                    Stop();
                }
                catch
                {
                    Environment.ExitCode = 1;
                    Environment.Exit(1);
                }
            }
        });
    }

    protected override void OnStop()
    {
        LogManager.WriteLog("[Service] Windows service stop requested.");
        Environment.ExitCode = 0;
        Environment.Exit(0);
    }

    protected override void OnShutdown()
    {
        LogManager.WriteLog("[Service] Windows shutdown requested.");
        Environment.ExitCode = 0;
        Environment.Exit(0);
    }
}

public static class GlobalContext
{
    public static string version = "Unknown";
    public static string? ClientName = null;
    public static string? HostName = null;
    public static string? IpAddress = null;
}
