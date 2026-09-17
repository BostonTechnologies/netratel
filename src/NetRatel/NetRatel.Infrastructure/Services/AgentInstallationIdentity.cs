using System.Security.Cryptography;
using NetRatel.Application.Agents;

namespace NetRatel.Infrastructure.Services;

public static class AgentInstallationIdentity
{
    public static AgentInstallationKey Parse(string? publicKey, string? keyAlgorithm)
    {
        if (string.IsNullOrWhiteSpace(publicKey))
        {
            throw new AgentAuthException(400, "A device public key is required for canonical enrollment.", "agent_key_required");
        }

        var algorithm = string.IsNullOrWhiteSpace(keyAlgorithm)
            ? "ecdsa-p256"
            : keyAlgorithm.Trim().ToLowerInvariant();
        if (!string.Equals(algorithm, "ecdsa-p256", StringComparison.Ordinal))
        {
            throw new AgentAuthException(400, "The device key algorithm is not supported.", "agent_key_algorithm_unsupported");
        }

        try
        {
            var encoded = Convert.FromBase64String(publicKey.Trim());
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(encoded, out var bytesRead);
            if (bytesRead != encoded.Length)
            {
                throw new CryptographicException("The public key contains trailing data.");
            }

            var canonicalBytes = ecdsa.ExportSubjectPublicKeyInfo();
            return new AgentInstallationKey(
                Convert.ToBase64String(canonicalBytes),
                Convert.ToHexString(SHA256.HashData(canonicalBytes)).ToLowerInvariant(),
                algorithm);
        }
        catch (Exception exception) when (exception is FormatException or CryptographicException)
        {
            throw new AgentAuthException(400, "The device public key is invalid.", "agent_key_invalid");
        }
    }
}

public sealed record AgentInstallationKey(string PublicKey, string Fingerprint, string Algorithm);
