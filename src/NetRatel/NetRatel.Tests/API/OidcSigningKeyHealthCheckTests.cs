using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using NetRatel.API.Gateway;
using NetRatel.Application.Agents;
using NetRatel.Infrastructure.Services;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class OidcSigningKeyHealthCheckTests
{
    [Fact]
    public async Task Missing_key_is_unhealthy()
    {
        using var keyFile = new TemporaryPemFile();

        var result = await CheckAsync(keyFile.Path);

        result.Status.Should().Be(HealthStatus.Unhealthy);
    }

    [Fact]
    public async Task Malformed_key_is_unhealthy()
    {
        using var keyFile = new TemporaryPemFile("not a PEM key");

        var result = await CheckAsync(keyFile.Path);

        result.Status.Should().Be(HealthStatus.Unhealthy);
    }

    [Fact]
    public async Task Public_only_P256_key_is_unhealthy()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var keyFile = new TemporaryPemFile(key.ExportSubjectPublicKeyInfoPem());

        var result = await CheckAsync(keyFile.Path);

        result.Status.Should().Be(HealthStatus.Unhealthy);
    }

    [Fact]
    public async Task Private_key_on_the_wrong_curve_is_unhealthy()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        using var keyFile = new TemporaryPemFile(key.ExportPkcs8PrivateKeyPem());

        var result = await CheckAsync(keyFile.Path);

        result.Status.Should().Be(HealthStatus.Unhealthy);
    }

    [Fact]
    public async Task P256_private_key_is_proven_usable_for_signing()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var keyFile = new TemporaryPemFile(key.ExportPkcs8PrivateKeyPem());

        OidcSigningService? resolvedSigningService = null;
        using var provider = CreateProvider(keyFile.Path, service => resolvedSigningService = service);
        var check = new OidcSigningKeyHealthCheck(provider.GetRequiredService<IServiceScopeFactory>());

        var result = await check.CheckHealthAsync(Context(check));

        result.Status.Should().Be(HealthStatus.Healthy);
        resolvedSigningService.Should().NotBeNull();

        var useDisposedService = () => resolvedSigningService!.GetActiveSigningKeyAsync(CancellationToken.None);
        await useDisposedService.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public async Task Request_scoped_key_signing_remains_valid_after_scope_disposal()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKeyPem = key.ExportSubjectPublicKeyInfoPem();
        using var keyFile = new TemporaryPemFile(key.ExportPkcs8PrivateKeyPem());
        using var provider = CreateProvider(keyFile.Path);
        var handler = new JwtSecurityTokenHandler();
        handler.MapInboundClaims = false;
        var issuedTokens = new List<string>();
        var signingKeys = new List<ECDsaSecurityKey>();

        for (var scopeIndex = 0; scopeIndex < 2; scopeIndex++)
        {
            using var scope = provider.CreateScope();
            var signingService = scope.ServiceProvider.GetRequiredService<OidcSigningService>();
            var signingKey = await signingService.GetActiveSigningKeyAsync(CancellationToken.None);
            signingKey.CryptoProviderFactory.CacheSignatureProviders.Should().BeFalse();
            signingKeys.Add(signingKey);
            issuedTokens.Add(CreateToken(handler, signingKey, $"request-{scopeIndex}"));
        }

        signingKeys.Should().HaveCount(2);
        signingKeys[0].Should().NotBeSameAs(signingKeys[1]);

        using var validationKey = ECDsa.Create();
        validationKey.ImportFromPem(publicKeyPem);
        var validationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = "https://netratel.example",
            ValidateAudience = true,
            ValidAudience = "orchestrator.api",
            ValidateLifetime = true,
            ClockSkew = TimeSpan.Zero,
            IssuerSigningKey = new ECDsaSecurityKey(validationKey)
        };

        for (var tokenIndex = 0; tokenIndex < issuedTokens.Count; tokenIndex++)
        {
            var principal = handler.ValidateToken(issuedTokens[tokenIndex], validationParameters, out _);
            principal.FindFirstValue(JwtRegisteredClaimNames.Sub).Should().Be($"request-{tokenIndex}");
        }
    }

    [Fact]
    public async Task Concurrent_request_scoped_signers_can_sign_without_cross_request_key_use()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKeyPem = key.ExportSubjectPublicKeyInfoPem();
        using var keyFile = new TemporaryPemFile(key.ExportPkcs8PrivateKeyPem());
        using var provider = CreateProvider(keyFile.Path);

        var issuedTokens = await Task.WhenAll(Enumerable.Range(0, 8).Select(async requestIndex =>
        {
            using var scope = provider.CreateScope();
            var signingService = scope.ServiceProvider.GetRequiredService<OidcSigningService>();
            var signingKey = await signingService.GetActiveSigningKeyAsync(CancellationToken.None);
            var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
            return CreateToken(handler, signingKey, $"concurrent-{requestIndex}");
        }));

        using var validationKey = ECDsa.Create();
        validationKey.ImportFromPem(publicKeyPem);
        var validationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = "https://netratel.example",
            ValidateAudience = true,
            ValidAudience = "orchestrator.api",
            ValidateLifetime = true,
            ClockSkew = TimeSpan.Zero,
            IssuerSigningKey = new ECDsaSecurityKey(validationKey)
        };
        var validator = new JwtSecurityTokenHandler { MapInboundClaims = false };

        for (var tokenIndex = 0; tokenIndex < issuedTokens.Length; tokenIndex++)
        {
            var principal = validator.ValidateToken(issuedTokens[tokenIndex], validationParameters, out _);
            principal.FindFirstValue(JwtRegisteredClaimNames.Sub).Should().Be($"concurrent-{tokenIndex}");
        }
    }

    [Fact]
    public async Task Caller_cancellation_is_propagated()
    {
        using var keyFile = new TemporaryPemFile();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var provider = CreateProvider(keyFile.Path);
        var check = new OidcSigningKeyHealthCheck(provider.GetRequiredService<IServiceScopeFactory>());

        var act = () => check.CheckHealthAsync(Context(check), cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private static async Task<HealthCheckResult> CheckAsync(string path)
    {
        var fileExisted = File.Exists(path);
        var originalContents = fileExisted ? await File.ReadAllTextAsync(path) : null;
        using var provider = CreateProvider(path);
        var check = new OidcSigningKeyHealthCheck(provider.GetRequiredService<IServiceScopeFactory>());
        var result = await check.CheckHealthAsync(Context(check));

        File.Exists(path).Should().Be(fileExisted);
        if (originalContents is not null)
        {
            (await File.ReadAllTextAsync(path)).Should().Be(originalContents);
        }

        if (result.Status == HealthStatus.Unhealthy)
        {
            result.Exception.Should().BeNull();
            var exposed = $"{result.Description}\n{string.Join('\n', result.Data.Select(item => $"{item.Key}:{item.Value}"))}";
            exposed.Should().NotContain(path);
            if (!string.IsNullOrEmpty(originalContents))
            {
                exposed.Should().NotContain(originalContents);
            }
        }

        return result;
    }

    private static ServiceProvider CreateProvider(string path, Action<OidcSigningService>? onCreated = null)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ =>
        {
            var signingService = new OidcSigningService(Options.Create(new AgentAuthOptions { PrivateKeyPath = path }));
            onCreated?.Invoke(signingService);
            return signingService;
        });
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        });
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

    private static HealthCheckContext Context(IHealthCheck check) => new()
    {
        Registration = new HealthCheckRegistration("agent-auth-signing", check, HealthStatus.Unhealthy, [])
    };

    private sealed class TemporaryPemFile : IDisposable
    {
        public TemporaryPemFile(string? contents = null)
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"netratel-signing-{Guid.NewGuid():N}.pem");
            if (contents is not null)
            {
                File.WriteAllText(Path, contents);
            }
        }

        public string Path { get; }

        public void Dispose()
        {
            if (File.Exists(Path))
            {
                File.Delete(Path);
            }
        }
    }
}
