using System.CommandLine;
using NetRatel.AgentClient;
using NetRatel.Setup;

internal static partial class NetRatelCli
{
    private static Command BuildSetupCommand(CliRuntime runtime, GlobalOptions globals, string name, string description, params string[] prefix)
    {
        var command = new Command(name, description);
        var options = new Dictionary<string, Option<string?>>();
        command.AddOption(globals.ConfigPath); command.AddOption(globals.ApiBaseUrl); command.AddOption(globals.AppPassword);
        var setupOptions = new[] { "--profile", "--transport", "--mode", "--instance", "--credential-file", "--public-origin", "--output-directory", "--oidc-authority", "--auth-mode", "--required-groups", "--required-scopes", "--instance-name", "--resource", "--listen-port", "--allowed-origins", "--existing-outbound-config", "--existing-m2m-callers", "--delegation-principal", "--delegation-key-file", "--delegation-key-id", "--delegation-issuer", "--delegation-audience" };
        foreach (var key in prefix[0] == "setup" ? setupOptions : new[] { "--profile", "--transport" })
        {
            var option = new Option<string?>(key) { Description = key switch
            {
                "--profile" => "Named profile (letters, digits, dashes or underscores).",
                "--transport" => "Client transport: cli, stdio or http.",
                "--mode" => "Setup mode: client or host (deployment administrators).",
                "--instance" => "Selected instance HTTPS origin.",
                "--credential-file" => "Import an existing credential from a protected file.",
                "--public-origin" => "Public HTTPS hosting origin.",
                "--output-directory" => "Absolute persistent hosting output directory.",
                "--oidc-authority" => "External OIDC authority for hosting ingress.",
                "--auth-mode" => "Hosting ingress authentication: local or oidc.",
                "--required-groups" => "Comma-separated required hosting groups.",
                "--required-scopes" => "Comma-separated required hosting scopes.",
                "--instance-name" => "Hosting environment: dev or prod.",
                "--resource" => "Canonical public HTTPS MCP URL, including any path prefix.",
                "--listen-port" => "HTTP MCP listening port.",
                "--allowed-origins" => "Explicit comma-separated hosting browser origins.",
                "--existing-outbound-config" => "Protected existing outbound authentication JSON.",
                "--existing-m2m-callers" => "Explicit caller IDs to preserve in the API deployment.",
                "--delegation-principal" => "Explicit existing OIDC delegation service principal.",
                "--delegation-key-file" => "Protected existing delegation key file; value is never printed.",
                "--delegation-key-id" => "Existing delegation key ID.",
                "--delegation-issuer" => "Existing delegation issuer.",
                "--delegation-audience" => "Existing delegation audience.",
                _ => "Setup option."
            } }; options[key] = option; command.AddOption(option);
        }
        var noninteractive = new Option<bool>("--non-interactive") { Description = "Use protected files/environment without a terminal." };
        var replace = new Option<bool>("--replace") { Description = "Explicitly replace reviewed existing configuration." };
        if (prefix[0] == "setup") { command.AddOption(noninteractive); command.AddOption(replace); }
        command.SetHandler(async ctx =>
        {
            if (ctx.ParseResult.GetValueForOption(globals.AppPassword) is not null) throw new CliValidationException("Setup and diagnostics do not accept --app-password. Use a protected file or dedicated environment setting.");
            var arguments = new List<string>(prefix);
            foreach (var (key, option) in options)
                if (ctx.ParseResult.GetValueForOption(option) is { } value) { arguments.Add(key); arguments.Add(value); }
            if (ctx.ParseResult.GetValueForOption(globals.ConfigPath) is { } config) { arguments.Add("--config"); arguments.Add(config); }
            if (ctx.ParseResult.GetValueForOption(globals.ApiBaseUrl) is { } api) { arguments.Add("--api-base-url"); arguments.Add(api); }
            if (ctx.ParseResult.GetValueForOption(noninteractive)) arguments.Add("--non-interactive");
            if (ctx.ParseResult.GetValueForOption(replace)) arguments.Add("--replace");
            ctx.ExitCode = await SetupCommand.RunAsync(arguments.ToArray(), ConfigurationSurface.Cli, runtime.Out, runtime.Error, ct: ctx.GetCancellationToken());
        });
        return command;
    }
    private static Command BuildProfileCommand(CliRuntime runtime)
    {
        var command = new Command("profile", "List named profiles or print an explicit profile selector.");
        var list = new Command("list", "List CLI profile names.");
        list.SetHandler(async ctx => ctx.ExitCode = await SetupCommand.RunAsync(["profile", "list"], ConfigurationSurface.Cli, runtime.Out, runtime.Error));
        var select = new Command("select", "Validate a profile and print its explicit command/environment selector.");
        var name = new Argument<string>("name"); select.AddArgument(name);
        select.SetHandler(async ctx => ctx.ExitCode = await SetupCommand.RunAsync(["profile", "select", ctx.ParseResult.GetValueForArgument(name)!], ConfigurationSurface.Cli, runtime.Out, runtime.Error));
        command.AddCommand(list); command.AddCommand(select); return command;
    }
}
