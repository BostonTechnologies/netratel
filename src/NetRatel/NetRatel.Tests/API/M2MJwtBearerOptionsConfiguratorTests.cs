using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using NetRatel.API.Gateway;
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
        var privateKeyPem = key.ExportPkcs8PrivateKeyPem();
        var publicKeyPem = key.ExportSubjectPublicKeyInfoPem();
        await File.WriteAllTextAsync(keyPath, privateKeyPem);

        try
        {
            var services = new ServiceCollection();
            services.AddOptions();
            services.AddLogging();
            services.AddScoped(_ => new OidcSigningService(Options.Create(new AgentAuthOptions
            {
                PrivateKeyPath = keyPath,
                SigningKeyId = "m2m-test-key"
            })));
            services.AddSingleton<IOptions<M2MOptions>>(Options.Create(new M2MOptions
            {
                Authority = "https://netratel.example/",
                Audience = "orchestrator.api"
            }));
            services.AddAuthentication()
                .AddJwtBearer("M2M", _ => { });
            services.AddSingleton<IConfigureOptions<JwtBearerOptions>, M2MJwtBearerOptionsConfigurator>();
            ECDsaSecurityKey? retainedKey = null;
            await using (var provider = services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateScopes = true,
                ValidateOnBuild = true
            }))
            {
                var options = provider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>().Get("M2M");
                var configuration = options.Configuration ?? throw new InvalidOperationException("M2M configuration was not created.");

                configuration.Issuer.Should().Be("https://netratel.example");
                configuration.SigningKeys.Should().ContainSingle().Which.KeyId.Should().Be("m2m-test-key");
                options.TokenValidationParameters.ValidIssuers.Should().BeEquivalentTo("https://netratel.example/", "https://netratel.example");
                options.TokenValidationParameters.ValidAudience.Should().Be("orchestrator.api");

                var staticConfiguration = await options.ConfigurationManager!.GetConfigurationAsync(CancellationToken.None);
                staticConfiguration.Issuer.Should().Be("https://netratel.example");
                staticConfiguration.SigningKeys.Should().ContainSingle().Which.KeyId.Should().Be("m2m-test-key");

                var activeSigningKey = (ECDsaSecurityKey)configuration.SigningKeys.Should().ContainSingle().Which;
                retainedKey = activeSigningKey;
                var healthCheck = new OidcSigningKeyHealthCheck(provider.GetRequiredService<IServiceScopeFactory>());
                var healthContext = new HealthCheckContext
                {
                    Registration = new HealthCheckRegistration("agent-auth-signing", healthCheck, HealthStatus.Unhealthy, [])
                };
                (await healthCheck.CheckHealthAsync(healthContext)).Status.Should().Be(HealthStatus.Healthy);
                configuration.SigningKeys.Should().ContainSingle().Which.Should().BeSameAs(activeSigningKey);

                using var validationKey = ECDsa.Create();
                validationKey.ImportFromPem(publicKeyPem);
                var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
                var token = CreateToken(handler, activeSigningKey, "host-retained");
                var validationParameters = options.TokenValidationParameters.Clone();
                validationParameters.IssuerSigningKey = new ECDsaSecurityKey(validationKey);
                var principal = handler.ValidateToken(token, validationParameters, out _);
                principal.FindFirstValue(JwtRegisteredClaimNames.Sub).Should().Be("host-retained");
            }

            Action useHostDisposedKey = () => retainedKey!.ECDsa.ExportParameters(includePrivateParameters: false);
            useHostDisposedKey.Should().Throw<ObjectDisposedException>();
        }
        finally
        {
            File.Delete(keyPath);
        }
    }

    [Fact]
    public void Only_the_m2m_scheme_is_configured()
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => new OidcSigningService(Options.Create(new AgentAuthOptions
        {
            PrivateKeyPath = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.pem")
        })));
        services.AddSingleton<IOptions<M2MOptions>>(Options.Create(new M2MOptions
        {
            Authority = "https://netratel.example/",
            Audience = "orchestrator.api"
        }));
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        });
        var configurator = new M2MJwtBearerOptionsConfigurator(
            Options.Create(new M2MOptions
            {
                Authority = "https://netratel.example/",
                Audience = "orchestrator.api"
            }),
            provider.GetRequiredService<IServiceScopeFactory>());
        var otherScheme = new JwtBearerOptions();

        configurator.Configure("Other", otherScheme);

        otherScheme.Configuration.Should().BeNull();
        configurator.Dispose();
    }

    private static string CreateToken(JwtSecurityTokenHandler handler, ECDsaSecurityKey signingKey, string subject)
    {
        var now = DateTime.UtcNow;
        var token = handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "https://netratel.example",
            Audience = "orchestrator.api",
            Subject = new ClaimsIdentity([new Claim(JwtRegisteredClaimNames.Sub, subject)]),
            NotBefore = now.AddSeconds(-1),
            Expires = now.AddMinutes(5),
            SigningCredentials = new SigningCredentials(signingKey, SecurityAlgorithms.EcdsaSha256)
        });

        return handler.WriteToken(token);
    }
}
