using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NetRatel.API.Security.M2M;
using NetRatel.AgentClient;
using Xunit;

namespace NetRatel.Tests.API;

[Collection("Endpoint environment")]
public sealed class M2MDeploymentEnvironmentPrecedenceTests
{
    public static bool IsLinux => OperatingSystem.IsLinux();

    [Fact(Skip = "The shipped API container entrypoint requires Linux tools.", SkipUnless = nameof(IsLinux))]
    public async Task Api_shell_entrypoint_preserves_file_backed_underscore_registration_and_canonical_outbound_identity()
    {
        const string canonical = "netratel-mcp-http";
        const string alias = "netratel_mcp_http";
        var directory = Directory.CreateTempSubdirectory("netratel-m2m-shell-").FullName;
        var secretPath = Path.Combine(directory, "service.secret");
        try
        {
            ConfigurationFileStorage.WriteAtomic(secretPath, new string('s', 40) + "\n");
            var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../"));
            var start = new ProcessStartInfo("/bin/sh") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            start.ArgumentList.Add(Path.Combine(repositoryRoot, "docker", "api", "docker-entrypoint.sh"));
            start.ArgumentList.Add("/usr/bin/env"); start.ArgumentList.Add("-0");
            // Never capture the operator's environment or put secret bytes in a process argument.
            start.Environment.Clear();
            start.Environment["M2M__Authority"] = "https://api.example.test";
            start.Environment["M2M__Audience"] = "netratel.api";
            start.Environment["M2M__AllowedCallerClientIds__0"] = canonical;
            start.Environment[$"M2MClients__{alias}__SecretFile"] = secretPath;
            start.Environment[$"M2MClients__{alias}__Secret"] = "";
            start.Environment[$"M2MClients__{alias}__AllowedAudiences__0"] = "netratel.api";
            start.Environment[$"M2MClients__{alias}__AllowedScopes__0"] = "netratel.api";
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await process.WaitForExitAsync(deadline.Token);
            Assert.Equal(0, process.ExitCode); Assert.Empty(await error);
            var forwarded = (await output).Split('\0', StringSplitOptions.RemoveEmptyEntries).Select(entry => entry.Split('=', 2))
                .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.Ordinal);
            Assert.Equal(secretPath, forwarded[$"M2MClients__{alias}__SecretFile"]);
            // Adapt only this isolated child environment using the standard .NET key delimiter.
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(forwarded.Select(pair =>
                new KeyValuePair<string, string?>(pair.Key.Replace("__", ":", StringComparison.Ordinal), pair.Value))).Build();
            var services = new ServiceCollection();
            services.AddOptions<M2MOptions>().Bind(configuration.GetSection("M2M"));
            services.AddNetRatelServiceIdentityApi(configuration);
            using var provider = services.BuildServiceProvider();
            var resolver = provider.GetRequiredService<IM2MDeploymentProfileResolver>();
            var profile = resolver.Resolve(canonical);
            Assert.NotNull(profile); Assert.Equal(canonical, profile.ClientId); Assert.Null(resolver.Resolve(alias));
            Assert.Equal(["netratel.api"], profile.AllowedAudiences); Assert.Equal(["netratel.api"], profile.AllowedScopes);
            var outbound = NetRatelConfigurationService.ResolveSecretReferences(AgentClientConfiguration.Empty with { ApiM2MClientSecretFile = secretPath });
            Assert.True(string.Equals(outbound.ApiM2MClientSecret, profile.Secret, StringComparison.Ordinal), "The API and outbound host must resolve the same protected secret bytes.");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void Command_line_secret_file_remains_authoritative_after_validation_over_process_environment_secret()
    {
        var id = "precedence-" + Guid.NewGuid().ToString("N");
        var environmentKey = $"M2MClients__{id}__Secret";
        var previous = Environment.GetEnvironmentVariable(environmentKey);
        var directory = Directory.CreateTempSubdirectory("netratel-m2m-precedence-").FullName;
        var secretPath = Path.Combine(directory, "service.secret");
        var selectedSecret = new string('f', 40);
        try
        {
            ConfigurationFileStorage.WriteAtomic(secretPath, selectedSecret + "\n");
            Environment.SetEnvironmentVariable(environmentKey, new string('e', 40));
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(Metadata(id))
                .AddEnvironmentVariables()
                .AddCommandLine([$"--M2MClients:{id}:SecretFile", secretPath])
                .Build();
            var services = new ServiceCollection();
            services.AddOptions<M2MOptions>().Bind(configuration.GetSection("M2M"));
            services.AddNetRatelServiceIdentityApi(configuration);
            using var provider = services.BuildServiceProvider();

            // This is the same post-configuration and validator invoked by API startup.
            var validated = provider.GetRequiredService<IOptionsMonitor<M2MDeploymentClientsOptions>>().CurrentValue[id];
            Assert.Equal(secretPath, validated.SecretFile);
            Assert.Equal(selectedSecret, validated.Secret);
            var profile = provider.GetRequiredService<IM2MDeploymentProfileResolver>().Resolve(id);
            Assert.NotNull(profile);
            Assert.Equal(selectedSecret, profile.Secret);
            Assert.NotEqual(Environment.GetEnvironmentVariable(environmentKey), profile.Secret);
            Assert.Equal(["netratel.api"], profile.AllowedAudiences);
            Assert.Equal(["narrow.read"], profile.AllowedScopes);
            Assert.Equal(selectedSecret, new M2MDeploymentProfileResolver(configuration).Resolve(id)!.Secret);
        }
        finally
        {
            Environment.SetEnvironmentVariable(environmentKey, previous);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Otherwise_unrepresented_raw_underscore_alias_keeps_legacy_canonical_metadata()
    {
        var canonical = "legacy-" + Guid.NewGuid().ToString("N");
        var alias = canonical.Replace('-', '_');
        var environmentKey = $"M2MClients__{alias}__Secret";
        var previous = Environment.GetEnvironmentVariable(environmentKey);
        var legacySecret = new string('l', 40);
        try
        {
            Environment.SetEnvironmentVariable(environmentKey, legacySecret);
            // Older explicit configuration callers do not include an environment provider.
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(Metadata(canonical)).Build();
            var resolver = new M2MDeploymentProfileResolver(configuration);

            var profile = resolver.Resolve(canonical);
            Assert.NotNull(profile);
            Assert.Equal(canonical, profile.ClientId);
            Assert.Equal(legacySecret, profile.Secret);
            Assert.Equal(["netratel.api"], profile.AllowedAudiences);
            Assert.Equal(["narrow.read"], profile.AllowedScopes);
            Assert.Null(resolver.Resolve(alias));
            Assert.True(resolver.OwnsIdentity(alias));
        }
        finally { Environment.SetEnvironmentVariable(environmentKey, previous); }
    }

    private static Dictionary<string, string?> Metadata(string id) => new()
    {
        ["M2M:Authority"] = "https://api.example.test",
        ["M2M:Audience"] = "netratel.api",
        ["M2M:AllowedCallerClientIds:0"] = id,
        [$"M2MClients:{id}:AllowedAudiences:0"] = "netratel.api",
        [$"M2MClients:{id}:AllowedScopes:0"] = "narrow.read"
    };
}
