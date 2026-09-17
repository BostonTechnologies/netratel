using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NetRatel.API.Security.M2M;
using NetRatel.Application.Agents;
using NetRatel.Infrastructure.Services;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class M2MJwtBearerOptionsConfiguratorTests
{
    [Fact]
    public async Task M2m_scheme_uses_the_active_api_signing_key_without_runtime_discovery()
    {
        var keyPath = Path.Combine(Path.GetTempPath(), $"netratel-m2m-{Guid.NewGuid():N}.pem");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await File.WriteAllTextAsync(keyPath, key.ExportPkcs8PrivateKeyPem());

        try
        {
            var signing = new OidcSigningService(Options.Create(new AgentAuthOptions
            {
                PrivateKeyPath = keyPath,
                SigningKeyId = "m2m-test-key"
            }));
            var services = new ServiceCollection();
            services.AddOptions();
            services.AddSingleton<IOptions<M2MOptions>>(Options.Create(new M2MOptions
            {
                Authority = "https://netratel.example/",
                Audience = "orchestrator.api"
            }));
            services.AddSingleton(signing);
            services.AddAuthentication()
                .AddJwtBearer("M2M", _ => { });
            services.AddSingleton<IConfigureOptions<JwtBearerOptions>, M2MJwtBearerOptionsConfigurator>();
            await using var provider = services.BuildServiceProvider();
            var options = provider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>().Get("M2M");

            options.Configuration.Should().NotBeNull();
            options.Configuration!.Issuer.Should().Be("https://netratel.example");
            options.Configuration.SigningKeys.Should().ContainSingle().Which.KeyId.Should().Be("m2m-test-key");
            options.TokenValidationParameters.ValidIssuers.Should().BeEquivalentTo("https://netratel.example/", "https://netratel.example");
            options.TokenValidationParameters.ValidAudience.Should().Be("orchestrator.api");

            var staticConfiguration = await options.ConfigurationManager!.GetConfigurationAsync(CancellationToken.None);
            staticConfiguration.Issuer.Should().Be("https://netratel.example");
            staticConfiguration.SigningKeys.Should().ContainSingle().Which.KeyId.Should().Be("m2m-test-key");
        }
        finally
        {
            File.Delete(keyPath);
        }
    }

    [Fact]
    public void Only_the_m2m_scheme_is_configured()
    {
        var configurator = new M2MJwtBearerOptionsConfigurator(
            Options.Create(new M2MOptions
            {
                Authority = "https://netratel.example/",
                Audience = "orchestrator.api"
            }),
            new OidcSigningService(Options.Create(new AgentAuthOptions
            {
                PrivateKeyPath = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.pem")
            })));
        var otherScheme = new JwtBearerOptions();

        configurator.Configure("Other", otherScheme);

        otherScheme.Configuration.Should().BeNull();
    }
}
