using System.Text;
using System.Text.Json;
using NetRatel.Application.Agents;

namespace NetRatel.Infrastructure.Services;

internal static class AgentAuthenticationRequestValidation
{
    public static void ValidateEnrollment(AgentEnrollRequest request)
    {
        CheckLength(request.EnrollmentCode, 128, "enrollmentCode");
        CheckLength(request.PublicKey, 2048, "publicKey");
        CheckLength(request.KeyAlgorithm, 32, "keyAlgorithm");
        CheckLength(request.MtlsThumbprint, 128, "mtlsThumbprint");
        CheckProof(request.ProofSignature, request.ProofNonce);
        CheckScopes(request.RequestedScopes);
        CheckDeviceInfo(request.DeviceInfoJson);
    }

    public static void ValidateToken(AgentTokenRequest request)
    {
        CheckLength(request.RefreshToken, 512, "refreshToken");
        CheckLength(request.ProofJwt, 8192, "proofJwt");
        CheckLength(request.ClientCertificateThumbprint, 128, "clientCertificateThumbprint");
        CheckProof(request.ProofSignature, request.ProofNonce);
        CheckScopes(request.RequestedScopes);
    }

    private static void CheckProof(string? signature, string? nonce)
    {
        CheckLength(signature, 1024, "proofSignature");
        CheckLength(nonce, 256, "proofNonce");
    }

    private static void CheckScopes(IReadOnlyList<string>? scopes)
    {
        if (scopes is null)
            return;
        if (scopes.Count > 32 || scopes.Any(scope => scope is null || scope.Length > 128))
            throw new AgentAuthException(400, "Requested scopes exceed the supported limits.", "agent_scopes_invalid");
    }

    private static void CheckDeviceInfo(string? deviceInfoJson)
    {
        if (deviceInfoJson is null)
            return;

        try
        {
            if (Encoding.UTF8.GetByteCount(deviceInfoJson) <= 8192)
            {
                if (string.IsNullOrWhiteSpace(deviceInfoJson))
                    return;
                using var document = JsonDocument.Parse(deviceInfoJson, new JsonDocumentOptions { MaxDepth = 8 });
                if (document.RootElement.ValueKind == JsonValueKind.Object &&
                    document.RootElement.EnumerateObject().All(property => property.Name.Length <= 128 && property.Value.GetRawText().Length <= 2048))
                    return;
            }
        }
        catch (JsonException)
        {
            // The response does not echo device-provided metadata.
        }

        throw new AgentAuthException(400, "Device metadata exceeds the supported JSON limits.", "agent_device_info_invalid");
    }

    private static void CheckLength(string? value, int maximum, string field)
    {
        if (value?.Length > maximum)
            throw new AgentAuthException(400, $"Invalid agent authentication field length: {field}.", "agent_auth_field_too_long");
    }
}
