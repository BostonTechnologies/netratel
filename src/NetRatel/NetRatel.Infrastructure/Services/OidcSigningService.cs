using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using NetRatel.Application.Agents;

namespace NetRatel.Infrastructure.Services;

public sealed class OidcSigningService : IDisposable
{
    private const string P256CurveOid = "1.2.840.10045.3.1.7";
    private static readonly byte[] SigningCapabilityProbe = "NetRatel ES256 readiness probe"u8.ToArray();
    private readonly AgentAuthOptions _options;
    private readonly SemaphoreSlim _keyLock = new(1, 1);
    private ECDsaSecurityKey? _cachedKey;
    private int _disposed;

    public OidcSigningService(
        IOptions<AgentAuthOptions> options)
    {
        _options = options.Value;
    }

    public async Task<ECDsaSecurityKey> GetActiveSigningKeyAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_cachedKey is not null)
        {
            return _cachedKey;
        }

        await _keyLock.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_cachedKey is not null)
            {
                return _cachedKey;
            }

            var path = ResolveSigningPrivateKeyPath();
            var pem = await File.ReadAllTextAsync(path, ct);

            var ecdsa = ECDsa.Create();
            try
            {
                ecdsa.ImportFromPem(pem);
                EnsureEs256SigningCapability(ecdsa);

                // This service is request scoped. IdentityModel's default crypto
                // provider cache retains signature providers that reference the
                // underlying ECDsa instance, which this scope disposes. Avoid
                // leaving a cached provider bound to a disposed request key.
                _cachedKey = new ECDsaSecurityKey(ecdsa)
                {
                    KeyId = string.IsNullOrWhiteSpace(_options.SigningKeyId)
                        ? "netratel-agent-es256"
                        : _options.SigningKeyId.Trim(),
                    CryptoProviderFactory = new CryptoProviderFactory
                    {
                        CacheSignatureProviders = false
                    }
                };

                return _cachedKey;
            }
            catch
            {
                ecdsa.Dispose();
                throw;
            }
        }
        finally
        {
            _keyLock.Release();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // Wait for an in-flight key load before disposing the cache. A caller
        // that was queued before disposal rechecks _disposed after acquiring
        // the semaphore and cannot create a replacement key.
        _keyLock.Wait();
        try
        {
            Interlocked.Exchange(ref _cachedKey, null)?.ECDsa.Dispose();
        }
        finally
        {
            _keyLock.Release();
        }
    }

    private static void EnsureEs256SigningCapability(ECDsa ecdsa)
    {
        var parameters = ecdsa.ExportParameters(includePrivateParameters: false);
        if (ecdsa.KeySize != 256 ||
            !string.Equals(parameters.Curve.Oid.Value, P256CurveOid, StringComparison.Ordinal))
        {
            throw new CryptographicException("Agent token signing requires a P-256 private key for ES256.");
        }

        var signature = ecdsa.SignData(SigningCapabilityProbe, HashAlgorithmName.SHA256);
        if (!ecdsa.VerifyData(SigningCapabilityProbe, signature, HashAlgorithmName.SHA256))
        {
            throw new CryptographicException("The configured agent token key cannot perform ES256 signing.");
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
