using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using NetRatel.Application.Agents;

namespace NetRatel.Infrastructure.Services;

public sealed class OidcSigningService
{
    private readonly AgentAuthOptions _options;
    private readonly SemaphoreSlim _keyLock = new(1, 1);
    private ECDsaSecurityKey? _cachedKey;

    public OidcSigningService(
        IOptions<AgentAuthOptions> options)
    {
        _options = options.Value;
    }

    public async Task<ECDsaSecurityKey> GetActiveSigningKeyAsync(CancellationToken ct)
    {
        if (_cachedKey is not null)
        {
            return _cachedKey;
        }

        await _keyLock.WaitAsync(ct);
        try
        {
            if (_cachedKey is not null)
            {
                return _cachedKey;
            }

            var path = ResolveSigningPrivateKeyPath();
            var pem = await File.ReadAllTextAsync(path, ct);

            var ecdsa = ECDsa.Create();
            ecdsa.ImportFromPem(pem);

            _cachedKey = new ECDsaSecurityKey(ecdsa)
            {
                KeyId = string.IsNullOrWhiteSpace(_options.SigningKeyId)
                    ? "netratel-agent-es256"
                    : _options.SigningKeyId.Trim()
            };

            return _cachedKey;
        }
        finally
        {
            _keyLock.Release();
        }
    }

    public async Task<JsonWebKeySetDto> GetJwksAsync(CancellationToken ct)
    {
        var key = await GetActiveSigningKeyAsync(ct);
        var parameters = key.ECDsa.ExportParameters(false);

        var x = Base64UrlEncode(parameters.Q.X ?? throw new InvalidOperationException("Signing key X coordinate missing."));
        var y = Base64UrlEncode(parameters.Q.Y ?? throw new InvalidOperationException("Signing key Y coordinate missing."));

        return new JsonWebKeySetDto(
            [
                new JsonWebKeyDto(
                    Kty: "EC",
                    Kid: key.KeyId ?? string.Empty,
                    Use: "sig",
                    Crv: "P-256",
                    X: x,
                    Y: y,
                    Alg: "ES256")
            ]);
    }

    private string ResolveSigningPrivateKeyPath()
    {
        // 1. Local dev override (user-secrets / config)
        if (!string.IsNullOrWhiteSpace(_options.PrivateKeyPath))
        {
            if (!File.Exists(_options.PrivateKeyPath))
                throw new FileNotFoundException("Signing key not found.", _options.PrivateKeyPath);

            return _options.PrivateKeyPath;
        }

        // 2. Container active path, followed by the temporary compatibility path.
        const string containerPath = "/app/storage/keys/netratel-agent-es256-private.pem";
        if (File.Exists(containerPath))
            return containerPath;

        const string legacyContainerPath = "/app/storage/keys/spacetime-es256-private.pem";
        if (File.Exists(legacyContainerPath))
            return legacyContainerPath;

        throw new FileNotFoundException(
            "Signing key not found. Configure AgentAuth:PrivateKeyPath for local dev.");
    }

    private static string Base64UrlEncode(byte[] value)
    {
        return Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
