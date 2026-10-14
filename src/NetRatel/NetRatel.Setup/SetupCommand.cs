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
  setup [--profile NAME] [--config PATH] [--transport cli|stdio|http] [--mode client|host]
  setup --non-interactive --instance HTTPS_ORIGIN --credential-file PATH [--replace]
  setup --mode host --non-interactive --public-origin HTTPS_ORIGIN --api-base-url HTTPS_ORIGIN --output-directory PATH [--replace]
  Hosting options: --auth-mode local|oidc --oidc-authority HTTPS_URL --required-groups CSV --required-scopes CSV
  Advanced hosting: --instance-name dev|prod --resource HTTPS_MCP_URL --listen-port PORT --allowed-origins CSV --existing-outbound-config PATH --existing-m2m-callers CSV --delegation-principal NAME
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
                session.SelectProfile(surface, profile ?? "default", path);
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
            return await RunTerminalAsync(session, ct);
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
    public static async Task<int> RunTerminalAsync(SetupSession session, CancellationToken ct = default)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = [], DisableDefaults = true });
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(session);
        var model = new SetupViewModel(session);
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
    private static Dictionary<string, string> Parse(string[] args)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        var flags = new HashSet<string> { "--non-interactive", "--replace" };
        var valued = new HashSet<string> { "--profile", "--config", "--transport", "--mode", "--instance", "--credential-file", "--public-origin", "--api-base-url", "--output-directory", "--oidc-authority", "--auth-mode", "--required-groups", "--required-scopes", "--instance-name", "--resource", "--listen-port", "--allowed-origins", "--existing-outbound-config", "--existing-m2m-callers", "--delegation-principal", "--delegation-key-file", "--delegation-key-id", "--delegation-issuer", "--delegation-audience" };
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
            if (!interactive) throw new AgentClientValidationException("Interactive hosting setup requires a TTY. Use setup --mode host --non-interactive with --public-origin, --api-base-url and --output-directory.");
            return await HostingSetupTerminal.RunAsync(ct);
        }
        static string Required(Dictionary<string,string> values, string key) => values.TryGetValue(key, out var value) ? value : throw new AgentClientValidationException(key + " is required for hosting setup.");
        var mode = options.GetValueOrDefault("--auth-mode", "local");
        if (mode is not "local" and not "oidc") throw new AgentClientValidationException("Hosting auth mode must be local or oidc.");
        static string[] Values(string? value) => (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var request = new HttpHostingSetupRequest(Required(options, "--output-directory"), Required(options, "--public-origin"), Required(options, "--api-base-url"), options.GetValueOrDefault("--instance-name", "prod"), mode == "local")
        {
            OidcAuthority = options.GetValueOrDefault("--oidc-authority"), ComposeOutputDirectory = Environment.GetEnvironmentVariable("NETRATEL_SETUP_HOST_OUTPUT_DIRECTORY"),
            RequiredGroups = Values(options.GetValueOrDefault("--required-groups")), RequiredScopes = Values(options.GetValueOrDefault("--required-scopes")),
            PublicResourceUri = options.GetValueOrDefault("--resource"), AllowedOrigins = options.ContainsKey("--allowed-origins") ? Values(options["--allowed-origins"]) : null,
            ExistingOutboundConfigurationPath = options.GetValueOrDefault("--existing-outbound-config"), DelegationServicePrincipal = options.GetValueOrDefault("--delegation-principal"), ExistingDelegationKeyFile = options.GetValueOrDefault("--delegation-key-file"), DelegationKeyId = options.GetValueOrDefault("--delegation-key-id"), DelegationIssuer = options.GetValueOrDefault("--delegation-issuer"), DelegationAudience = options.GetValueOrDefault("--delegation-audience"), ExistingM2MCallerClientIds = options.ContainsKey("--existing-m2m-callers") ? Values(options["--existing-m2m-callers"]) : null,
            ListeningPort = options.TryGetValue("--listen-port", out var port) ? int.TryParse(port, out var parsedPort) ? parsedPort : throw new AgentClientValidationException("Listening port must be an integer.") : 9224
        };
        var service = new NetRatelHttpHostingSetupService();
        var preview = service.Preview(request);
        var result = service.Apply(request, options.ContainsKey("--replace"));
        await output.WriteLineAsync(JsonSerializer.Serialize(new { preview, result }, AgentClientConfiguration.JsonOptions)); return 0;
    }
}
