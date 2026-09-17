using System.Security.Cryptography;
using System.Text;

namespace NetRatel.Infrastructure.Services;

public static class PopSignatureService
{
    public static string ComputeEnrollmentBodyHash(
        string enrollmentCode,
        string publicKey,
        string keyAlgorithm,
        string? deviceInfoJson,
        IReadOnlyList<string>? requestedScopes)
    {
        var scopeFragment = requestedScopes is null ? string.Empty : string.Join(' ', requestedScopes);
        return ComputeBodyHash($"{enrollmentCode.Trim().ToUpperInvariant()}:{publicKey.Trim()}:{keyAlgorithm.Trim().ToLowerInvariant()}:{deviceInfoJson}:{scopeFragment}");
    }

    public static string ComputeTokenBodyHash(Guid agentId, string refreshToken, IReadOnlyList<string>? requestedScopes)
    {
        var scopeFragment = requestedScopes is null ? string.Empty : string.Join(' ', requestedScopes);
        return ComputeBodyHash($"{agentId:N}:{refreshToken.Trim()}:{scopeFragment}");
    }

    public static string ComputeBodyHash(string? body)
    {
        var bytes = Encoding.UTF8.GetBytes(body ?? string.Empty);
        return Convert.ToBase64String(SHA256.HashData(bytes));
    }

    public static byte[] BuildSigningMessage(string method, string path, DateTimeOffset timestampUtc, string nonce, string bodyHash)
    {
        var canonical = $"{method.ToUpperInvariant()}\n{path}\n{timestampUtc.UtcDateTime:O}\n{nonce}\n{bodyHash}";
        return SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
    }

    public static bool VerifySignature(string keyAlgorithm, string publicKeyBase64, string signatureBase64, byte[] message)
    {
        try
        {
            var publicKey = Convert.FromBase64String(publicKeyBase64);
            var signature = Convert.FromBase64String(signatureBase64);
            if (string.Equals(keyAlgorithm, "ecdsa-p256", StringComparison.OrdinalIgnoreCase))
            {
                using var ecdsa = ECDsa.Create();
                ecdsa.ImportSubjectPublicKeyInfo(publicKey, out _);
                return ecdsa.VerifyData(message, signature, HashAlgorithmName.SHA256);
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    public static string Sign(string keyAlgorithm, string privateKeyBase64, byte[] message)
    {
        if (!string.Equals(keyAlgorithm, "ecdsa-p256", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException($"Device key algorithm '{keyAlgorithm}' is not supported.");
        }

        var privateKey = Convert.FromBase64String(privateKeyBase64);
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportPkcs8PrivateKey(privateKey, out _);
        return Convert.ToBase64String(ecdsa.SignData(message, HashAlgorithmName.SHA256));
    }
}
