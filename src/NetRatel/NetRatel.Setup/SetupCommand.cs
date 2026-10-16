using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NetRatel.AgentClient;
using Termina.Hosting;

namespace NetRatel.Setup;

/// <summary>Explicit setup entrypoint; no protocol or server startup side effects.</summary>
public static class SetupCommand
{
    public const int NonInteractiveExitCode = 2;
    public const string Help = """
NetRatel setup
  setup [--instance WEB_URL] [--target cli|stdio|http-client|http-host] [--mode client|host]
  First screen: Instance Web URL only; HTTPS root and /mcp addresses are equivalent.
  First connect the MCP server to its backend: setup --mode host (same purpose as --target http-host).
  Then connect clients: bare setup or --target http-client saves a client profile. These are server/client sides, not single/multi-tenant modes.
  NetClaw and other third-party clients use their own MCP URL and tenant-scoped HTTP credentials; a local profile is not installed into them.
  Profile, transport, directories and provider settings are available under Advanced.
  setup [--profile NAME] [--config PATH] [--transport cli|stdio|http]
  setup --non-interactive --instance HTTPS_ORIGIN --credential-file PATH [--replace]
  setup --mode host [--instance WEB_URL] [--output-directory PATH]
  setup --mode host --verify --instance WEB_URL  (resume real checks from saved server setup)
  Komodo/custom Compose: run setup-http-mcp.sh from the matching release bundle on the Docker host.
  Deployment options: --deployment-manager release-compose|komodo --compose-file PATH
  setup --mode host --non-interactive --instance WEB_URL [--output-directory PATH] [--replace]
  Noninteractive host setup generates configuration only; apply and live verification remain required.
  Hosting options: --auth-mode local|oidc --oidc-authority HTTPS_URL --required-groups CSV --required-scopes CSV
  Advanced hosting: --instance-name dev|prod --resource HTTPS_MCP_URL --listen-port PORT --allowed-origins CSV --existing-outbound-config PATH --existing-m2m-callers CSV --api-m2m-audience AUDIENCE --api-m2m-authority HTTPS --delegation-principal NAME
  Existing delegation: --delegation-key-file PATH --delegation-key-id ID --delegation-issuer ID --delegation-audience ID
  config validate [--profile NAME] [--config PATH]
  doctor [--profile NAME] [--config PATH]
  profile list | profile select NAME
Interactive setup requires terminal input and output. Exit 2 indicates invalid input or a non-TTY.
Import secrets from protected files or dedicated environment settings, never command arguments.
Normal stdio MCP stdout contains protocol JSON-RPC only; run setup explicitly with a TTY.
""";
    public static bool IsSetupInvocation(string[] args) => args.Length > 0 && (args[0] is "setup" or "doctor" or "profile" || args[0] == "config" && args.Length > 1 && args[1] == "validate" || args[0] is "--help" or "-h");
    public static async Task<int> RunAsync(string[] args, ConfigurationSurface defaultSurface, TextWriter? output = null, TextWriter? error = null, bool? interactive = null, CancellationToken ct = default)
    {
        output ??= Console.Out; error ??= Console.Error;
        try
        {
            if (args.Any(a => a is "--help" or "-h")) { await output.WriteLineAsync(Help); return 0; }
            var options = Parse(args);
            if (options.TryGetValue("--target", out var target))
            {
                options["--transport"] = target switch { "cli" => "cli", "stdio" => "stdio", "http-client" or "http-host" => "http", _ => throw new AgentClientValidationException("Target must be cli, stdio, http-client or http-host.") };
                if (target == "http-host") options["--mode"] = "host";
            }
            var surface = options.TryGetValue("--transport", out var transport) ? transport switch { "cli" => ConfigurationSurface.Cli, "stdio" => ConfigurationSurface.StdioMcp, "http" => ConfigurationSurface.HttpMcpClient, _ => throw new AgentClientValidationException("Transport must be cli, stdio or http.") } : defaultSurface;
            if (args[0] == "setup" && surface == ConfigurationSurface.HttpMcpOutbound) surface = ConfigurationSurface.HttpMcpClient;
            options.TryGetValue("--profile", out var profile); options.TryGetValue("--config", out var path);
            var engine = new NetRatelConfigurationService();
            if (args[0] == "profile") return await ProfilesAsync(args, surface, engine, output);
            if (args[0] is "doctor" or "config")
            {
                var loaded = engine.Load(new(surface, path, profile, CommandOptions: options.TryGetValue("--api-base-url", out var apiOverride) ? AgentClientConfiguration.Empty with { ApiBaseUrl = apiOverride } : null));
                if (args[0] == "config") { await output.WriteLineAsync(JsonSerializer.Serialize(loaded.Diagnostics(), AgentClientConfiguration.JsonOptions)); return loaded.Validate().ExitCode; }
                await using var diagnosis = new SetupSession(surface, loaded.ConfigurationPath, loaded.ProfileName);
                var checks = await diagnosis.DoctorAsync(ct, loaded);
                await output.WriteLineAsync(JsonSerializer.Serialize(checks, AgentClientConfiguration.JsonOptions));
                return checks.Count > 0 && checks.All(c => c.Status == "completed") ? 0 : 2;
            }
            if (args[0] != "setup") throw new AgentClientValidationException("Run setup --help for supported commands.");
            if (options.GetValueOrDefault("--mode", "client") == "host")
                return await RunHostingAsync(options, output, interactive ?? !Console.IsInputRedirected && !Console.IsOutputRedirected, ct);
            if (options.GetValueOrDefault("--mode", "client") != "client") throw new AgentClientValidationException("Mode must be client or host.");
            if (!options.ContainsKey("--non-interactive") && !(interactive ?? !Console.IsInputRedirected && !Console.IsOutputRedirected))
            {
                await error.WriteLineAsync("Interactive setup requires a TTY. Use setup --non-interactive --instance HTTPS_ORIGIN --credential-file PATH, or setup --help."); return NonInteractiveExitCode;
            }
            await using var session = new SetupSession(surface, path, profile);
            if (options.ContainsKey("--non-interactive"))
            {
                if (!options.TryGetValue("--instance", out var origin) && !options.TryGetValue("--api-base-url", out origin)) throw new AgentClientValidationException("--instance is required for noninteractive setup.");
                session.SelectProfile(surface, profile ?? session.ProfileName, path ?? session.Path);
                session.ValidateDestinationStorage();
                await session.DiscoverAsync(origin, ct);
                if (options.TryGetValue("--credential-file", out var secretPath))
                {
                    ConfigurationFileStorage.EnsureSafePath(secretPath);
                    session.UseCredential(ConfigurationFileStorage.ReadText(secretPath, secret: true).TrimEnd('\r', '\n'));
                }
                else session.UseExternalConfiguration();
                session.Review();
                await session.SaveAsync(options.ContainsKey("--replace"), ct);
                var checks = await session.DoctorAsync(ct);
                await output.WriteLineAsync(JsonSerializer.Serialize(new { saved = true, path = session.Path, transport = surface.ToString(), checks, launch = session.LaunchSnippet() }, AgentClientConfiguration.JsonOptions));
                return checks.Count > 0 && checks.All(check => check.Status == "completed") ? 0 : 2;
            }
            return await RunTerminalAsync(session, ct, options);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return 130; }
        catch (AgentClientValidationException ex)
        {
            if (args.Length > 0 && args[0] is "config" or "doctor") await output.WriteLineAsync(JsonSerializer.Serialize(new { schemaVersion = 1, isValid = false, errors = new[] { TerminalText.Safe(ex.Message) } }, AgentClientConfiguration.JsonOptions));
            else await error.WriteLineAsync(TerminalText.Safe(ex.Message));
            return 2;
        }
        catch (Exception) { await error.WriteLineAsync("Setup could not complete. Check protected-file permissions, instance connectivity and authentication configuration."); return 3; }
    }
    public static async Task<int> RunTerminalAsync(SetupSession session, CancellationToken ct = default, IReadOnlyDictionary<string, string>? options = null)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = [], DisableDefaults = true });
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(session);
        var model = CreateViewModel(session, options);
        builder.Services.AddTermina("/setup", termina => termina.RegisterRoute<SetupPage, SetupViewModel>("/setup", sp => new SetupPage(sp.GetRequiredService<Termina.Clipboard.IClipboardService>()), sp => model));
        using var host = builder.Build();
        await host.RunAsync(ct);
        if (!session.Saved) return 130;
        if (model.Step != SetupStep.Finish || model.Checks.Count == 0)
        {
            await Console.Error.WriteLineAsync("Profile saved; read-only verification did not complete. Run doctor explicitly before using this profile.");
            return model.Cancelled ? 130 : 2;
        }
        return model.Checks.All(check => check.Status == "completed") ? 0 : 2;
    }
    public static SetupViewModel CreateViewModel(SetupSession session, IReadOnlyDictionary<string, string>? options = null)
    {
        var model = new SetupViewModel(session);
        if ((options?.GetValueOrDefault("--instance") ?? options?.GetValueOrDefault("--api-base-url")) is { } origin) model.Origin = origin;
        return model;
    }
    private static Dictionary<string, string> Parse(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        var flags = new HashSet<string> { "--non-interactive", "--replace", "--verify" };
        var valued = new HashSet<string> { "--profile", "--config", "--transport", "--target", "--mode", "--instance", "--credential-file", "--public-origin", "--api-base-url", "--output-directory", "--oidc-authority", "--auth-mode", "--required-groups", "--required-scopes", "--instance-name", "--resource", "--listen-port", "--allowed-origins", "--existing-outbound-config", "--existing-m2m-callers", "--api-m2m-audience", "--api-m2m-authority", "--delegation-principal", "--delegation-key-file", "--delegation-key-id", "--delegation-issuer", "--delegation-audience", "--deployment-manager", "--compose-file" };
        for (var i = args[0] == "config" ? 2 : args[0] == "profile" ? args.Length : 1; i < args.Length; i++)
        {
            var key = args[i];
            if (options.ContainsKey(key)) throw new AgentClientValidationException("Duplicate setup option.");
            if (flags.Contains(key)) options[key] = "true";
            else if (valued.Contains(key) && i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)) options[key] = args[++i];
            else throw new AgentClientValidationException("Unknown or incomplete setup option. Run setup --help.");
        }
        return options;
    }
    private static async Task<int> ProfilesAsync(string[] args, ConfigurationSurface surface, NetRatelConfigurationService service, TextWriter output)
    {
        if (args.Length == 2 && args[1] == "list")
        {
            var directory = System.IO.Path.GetDirectoryName(NetRatelConfigurationService.ProfilePath(surface, "default"))!;
            await output.WriteLineAsync(JsonSerializer.Serialize(Directory.Exists(directory) ? Directory.EnumerateFiles(directory, "*.json").Select(System.IO.Path.GetFileNameWithoutExtension).Order().ToArray() : [], AgentClientConfiguration.JsonOptions)); return 0;
        }
        if (args.Length == 3 && args[1] == "select")
        {
            var selected = service.Load(new(surface, ProfileName: args[2]));
            await output.WriteLineAsync(JsonSerializer.Serialize(new { profile = selected.ProfileName, configurationPath = selected.ConfigurationPath, command = surface == ConfigurationSurface.Cli ? $"netratel --config {ShellArgument.Quote(selected.ConfigurationPath!)} auth status" : $"Set {NetRatelConfigurationService.EnvironmentNames(surface, nameof(AgentClientConfiguration.ProfileName))[0]}={selected.ProfileName}" }, AgentClientConfiguration.JsonOptions)); return selected.Validate().ExitCode;
        }
        throw new AgentClientValidationException("Use profile list or profile select NAME; selection is explicit per command or dedicated environment setting.");
    }
    private static async Task<int> RunHostingAsync(Dictionary<string,string> options, TextWriter output, bool interactive, CancellationToken ct)
    {
        if (!options.ContainsKey("--non-interactive"))
        {
            if (!interactive) throw new AgentClientValidationException("Interactive hosting setup requires a TTY. Use setup --mode host --non-interactive --instance WEB_URL for generation only.");
            return await HostingSetupTerminal.RunAsync(options, ct);
        }
        static string Required(Dictionary<string,string> values, string key) => values.TryGetValue(key, out var value) ? value : throw new AgentClientValidationException(key + " is required for hosting setup.");
        var address = InstanceSetupAddress.Parse(options.GetValueOrDefault("--instance") ?? Required(options, "--public-origin"));
        var directory = HttpHostingStorage.Resolve(options.GetValueOrDefault("--output-directory"));
        var saved = NetRatelHttpHostingSetupService.LoadRequest(directory, address.Origin);
        if (options.ContainsKey("--verify")) return await VerifyHostingAsync(options, output, address, directory, saved, ct);
        HttpHostingStorage.ValidateWritable(directory);
        HttpHostingStorage.ValidateHostBoundary(directory);
        var existing = saved is null ? HostingSetupViewModel.DiscoverExistingSettings(options) : null;
        var local = saved?.LocalCredentialMode ?? existing?.LocalCredentialMode;
        if (options.TryGetValue("--auth-mode", out var mode))
            local = mode switch { "local" => true, "oidc" => false, _ => throw new AgentClientValidationException("Hosting auth mode must be local or oidc.") };
        if (local is null)
            throw new AgentClientValidationException("The current MCP client authentication mode is unknown. Specify --auth-mode local or --auth-mode oidc to keep the deployment's intended mode; setup will not choose one implicitly.");
        var capturedOutbound = existing?.MaterializeOutboundConfiguration(directory);
        static string[] Values(string? value) => (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var request = (saved ?? new HttpHostingSetupRequest(directory, address.Origin, address.Origin)) with
        {
            PublicOrigin = address.Origin, OutputDirectory = directory,
            ApiBaseUrl = options.GetValueOrDefault("--api-base-url") ?? saved?.ApiBaseUrl ?? existing?.ApiBaseUrl ?? address.Origin,
            Instance = options.GetValueOrDefault("--instance-name") ?? saved?.Instance ?? existing?.Instance ?? "prod", LocalCredentialMode = local.Value,
            WebApiBackendUrl = saved?.WebApiBackendUrl ?? "http://api:9222/",
            WebMcpBackendUrl = saved?.WebMcpBackendUrl ?? "http://mcp-http:9224/",
            ExistingWebApiBackendUrl = Environment.GetEnvironmentVariable("NETRATEL_SETUP_EXISTING_WEB_API_BACKEND_URL") ?? saved?.ExistingWebApiBackendUrl,
            ExistingWebMcpBackendUrl = Environment.GetEnvironmentVariable("NETRATEL_SETUP_EXISTING_WEB_MCP_BACKEND_URL") ?? saved?.ExistingWebMcpBackendUrl,
            ExistingWebEnvironmentObserved = Environment.GetEnvironmentVariable("NETRATEL_SETUP_WEB_ENVIRONMENT_OBSERVED") == "true" || saved?.ExistingWebEnvironmentObserved == true,
            OidcAuthority = options.GetValueOrDefault("--oidc-authority") ?? saved?.OidcAuthority ?? existing?.OidcAuthority,
            ComposeOutputDirectory = Environment.GetEnvironmentVariable("NETRATEL_SETUP_HOST_OUTPUT_DIRECTORY") ?? saved?.ComposeOutputDirectory,
            RequiredGroups = options.ContainsKey("--required-groups") ? Values(options["--required-groups"]) : saved?.RequiredGroups ?? existing?.RequiredGroups,
            RequiredScopes = options.ContainsKey("--required-scopes") ? Values(options["--required-scopes"]) : saved?.RequiredScopes ?? existing?.RequiredScopes,
            PublicResourceUri = options.GetValueOrDefault("--resource") ?? saved?.PublicResourceUri ?? existing?.PublicResourceUri,
            AllowedOrigins = options.ContainsKey("--allowed-origins") ? Values(options["--allowed-origins"]) : saved?.AllowedOrigins ?? existing?.AllowedOrigins,
            ExistingOutboundConfigurationPath = saved is null ? capturedOutbound ?? options.GetValueOrDefault("--existing-outbound-config") : options.GetValueOrDefault("--existing-outbound-config") ?? saved.ExistingOutboundConfigurationPath,
            DelegationServicePrincipal = options.GetValueOrDefault("--delegation-principal") ?? saved?.DelegationServicePrincipal ?? existing?.DelegationServicePrincipal,
            ExistingDelegationKeyFile = options.GetValueOrDefault("--delegation-key-file") ?? saved?.ExistingDelegationKeyFile ?? existing?.MaterializeDelegationKey(directory),
            DelegationKeyId = options.GetValueOrDefault("--delegation-key-id") ?? saved?.DelegationKeyId ?? existing?.DelegationKeyId,
            DelegationIssuer = options.GetValueOrDefault("--delegation-issuer") ?? saved?.DelegationIssuer ?? existing?.DelegationIssuer,
            DelegationAudience = options.GetValueOrDefault("--delegation-audience") ?? saved?.DelegationAudience ?? existing?.DelegationAudience,
            DelegationLifetimeSeconds = saved?.DelegationLifetimeSeconds ?? existing?.DelegationLifetimeSeconds ?? 90,
            OperatorSurfaceEnabled = saved?.OperatorSurfaceEnabled ?? existing?.OperatorSurfaceEnabled,
            ExistingM2MCallerClientIds = options.ContainsKey("--existing-m2m-callers") ? Values(options["--existing-m2m-callers"]) : saved?.ExistingM2MCallerClientIds,
            ApiServiceAudience = options.GetValueOrDefault("--api-m2m-audience") ?? saved?.ApiServiceAudience ?? "netratel.api",
            ApiServiceAuthority = options.GetValueOrDefault("--api-m2m-authority") ?? saved?.ApiServiceAuthority,
            DeploymentManager = options.GetValueOrDefault("--deployment-manager") ?? saved?.DeploymentManager ?? Environment.GetEnvironmentVariable("NETRATEL_SETUP_DEPLOYMENT_MANAGER") ?? string.Empty,
            ComposeSourcePath = options.GetValueOrDefault("--compose-file") ?? saved?.ComposeSourcePath,
            ListeningPort = options.TryGetValue("--listen-port", out var port) ? int.TryParse(port, out var parsedPort) ? parsedPort : throw new AgentClientValidationException("Listening port must be an integer.") : saved?.ListeningPort ?? existing?.ListeningPort ?? 9224
        };
        var service = new NetRatelHttpHostingSetupService();
        var preview = service.Preview(request);
        var result = service.Apply(request, options.ContainsKey("--replace"));
        var fingerprint = service.PrepareRegistration(request);
        HostingExportReceipt.WriteIfRequested(directory);
        await output.WriteLineAsync(JsonSerializer.Serialize(new { state = "configuration-written-apply-and-live-verification-pending", registrationFingerprint = fingerprint, applyCommand = NetRatelHttpHostingSetupService.ApplyCommand(request), preview, result }, AgentClientConfiguration.JsonOptions)); return 0;
    }

    private static async Task<int> VerifyHostingAsync(Dictionary<string, string> options, TextWriter output,
        InstanceSetupAddress address, string directory, HttpHostingSetupRequest? saved, CancellationToken ct)
    {
        if (saved is null) throw new AgentClientValidationException("No saved server setup was found in this persistent volume. Configure, review and apply the server first, then resume with --verify.");
        if (!saved.LocalCredentialMode)
            throw new AgentClientValidationException("Noninteractive server verification uses a separate HTTP MCP-purpose local credential. This server uses OIDC; omit --non-interactive to verify with its supported external provider account.");
        var allowed = new HashSet<string>(StringComparer.Ordinal) { "--mode", "--target", "--transport", "--non-interactive", "--verify", "--instance", "--public-origin", "--output-directory", "--api-base-url", "--credential-file", "--deployment-manager", "--compose-file" };
        if (options.Keys.Any(key => !allowed.Contains(key)))
            throw new AgentClientValidationException("Verification reuses the saved server identity and authentication settings. Omit configuration overrides or run setup without --verify to review an intentional change.");
        if (options.TryGetValue("--api-base-url", out var api) && !NetRatelConfigurationService.Equivalent(api, saved.ApiBaseUrl))
            throw new AgentClientValidationException("Verification must use the saved API destination. Omit the API override or review a configuration change without --verify.");
        // Verify the serving process, not the one-shot setup container's inherited environment.
        if (Environment.GetEnvironmentVariable("NETRATEL_SETUP_RUNTIME_ENVIRONMENT_SOURCE") is { Length: > 0 } runtimePath)
        {
            using var snapshot = JsonDocument.Parse(ConfigurationFileStorage.ReadText(runtimePath, 1_048_576, secret: true));
            var runtime = new Dictionary<string, Dictionary<string, string?>>(StringComparer.Ordinal);
            foreach (var name in new[] { "mcp-http", "api", "web" })
            {
                if (!snapshot.RootElement.TryGetProperty(name, out var service) || service.ValueKind != JsonValueKind.Object)
                    throw new AgentClientValidationException("effective_runtime_missing: Rerun the host launcher to inspect all recreated services.");
                runtime[name] = service.EnumerateObject().ToDictionary(entry => entry.Name, entry => entry.Value.GetString(), StringComparer.OrdinalIgnoreCase);
            }
            HttpHostingEffectiveDeployment.ValidateRuntimeProposal(directory, (service, key) => runtime.GetValueOrDefault(service)?.GetValueOrDefault(key));
        }
        else if (HttpHostingStorage.UsesSetupVolume(directory))
            throw new AgentClientValidationException("effective_runtime_missing: Rerun setup-http-mcp.sh on the deployment host to inspect the recreated serving process before verification.");
        if (!options.TryGetValue("--credential-file", out var credentialFile) || !Path.IsPathFullyQualified(credentialFile))
            throw new AgentClientValidationException("Provide --credential-file with the absolute path of a protected HTTP MCP-purpose verifier credential. It remains separate from the server identity and is not saved by verification.");
        credentialFile = Path.GetFullPath(credentialFile);
        if (credentialFile.StartsWith(directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new AgentClientValidationException("Keep the verifier credential outside server setup storage, for example in the separate client-data volume or a read-only secret mount. Host keys are not client credentials.");
        // A unique nonexistent path and an empty environment isolate verification
        // from stored profiles and all ambient host/personal credential sources.
        await using var verification = new SetupSession(ConfigurationSurface.HttpMcpClient,
            Path.Combine(directory, ".verification-" + Guid.NewGuid().ToString("N") + ".json"), "host-verification", getEnvironmentVariable: _ => null);
        await verification.DiscoverAsync(address.Origin, ct);
        if (!NetRatel.Shared.Connectivity.McpResourceUri.Equivalent(verification.CanonicalTarget, saved.PublicResourceUri ?? address.McpResource))
            throw new AgentClientValidationException("The applied instance advertises a different MCP resource from the saved server configuration. Review the deployment before sending a verifier credential.");
        verification.UseCredential(ConfigurationFileStorage.ReadText(credentialFile, 16_384, secret: true).TrimEnd('\r', '\n'));
        var checks = await verification.VerifyTransientHttpMcpAsync(ct);
        var completed = checks.Count > 0 && checks.All(check => check.Status == "completed");
        await output.WriteLineAsync(JsonSerializer.Serialize(new
        {
            state = completed ? "end-to-end-verified" : "verification-incomplete",
            instance = address.Origin, target = verification.CanonicalTarget, checks
        }, AgentClientConfiguration.JsonOptions));
        return completed ? 0 : 2;
    }
}
