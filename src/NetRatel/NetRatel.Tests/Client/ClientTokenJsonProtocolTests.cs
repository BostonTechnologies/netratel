using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using NetRatel.Client.Service.Auth;
using NetRatel.Shared.Security;
using Xunit;

namespace NetRatel.Tests.Client;

public sealed class ClientTokenJsonProtocolTests
{
    // Captured from actual requests at ad9eee91d9e0f152b4e2e98359b287fbc51c9af8,
    // before replacing either serializer. HTTP uses D GUIDs/raw refresh text;
    // signed v1 payloads use N GUIDs/trimmed refresh text, preserving null/order.
    [Theory]
    [MemberData(nameof(PreChangeRequestVectors))]
    public void Generated_requests_match_prechange_utf8_and_signed_protocol_vectors(
        Guid agentId, string refreshToken, string[]? scopes, int? version, Guid? exchangeId,
        string expectedJson, string expectedPopHash, string expectedSigningMessage)
    {
        var json = version is null
            ? JsonSerializer.Serialize(new ClientAgentTokenService.LegacyTokenRequest(agentId, refreshToken, scopes),
                ClientAgentTokenJsonContext.Default.LegacyTokenRequest)
            : JsonSerializer.Serialize(new ClientAgentTokenService.RefreshExchangeTokenRequest(agentId, refreshToken, scopes, version.Value, exchangeId!.Value),
                ClientAgentTokenJsonContext.Default.RefreshExchangeTokenRequest);
        Encoding.UTF8.GetBytes(json).Should().Equal(Encoding.UTF8.GetBytes(expectedJson));
        var hash = PopSignatureService.ComputeTokenBodyHash(agentId, refreshToken, scopes, version, exchangeId);
        hash.Should().Be(expectedPopHash);
        Convert.ToBase64String(PopSignatureService.BuildSigningMessage("POST", "/api/v1/agents/token",
            new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero), "fixed-test-nonce", hash)).Should().Be(expectedSigningMessage);
    }

    public static IEnumerable<object?[]> PreChangeRequestVectors()
    {
        yield return [Guid.Parse("01234567-89ab-cdef-0123-456789abcdef"), " a:<&\"\u00e9\n/token \u2028> ", new[] { "netratel:connect" }, null, null,
            "{\"agentId\":\"01234567-89ab-cdef-0123-456789abcdef\",\"refreshToken\":\" a:\\u003C\\u0026\\u0022\\u00E9\\n/token \\u2028\\u003E \",\"requestedScopes\":[\"netratel:connect\"]}",
            "4P6FBvsvbeqoKgGRmpqaDwdctVW1Dkx0LORd3N5z7Ho=", "TWeN1BNS1OfgfA65eLfpspkPj3ue9ds+frfGnnlr+tU="];
        yield return [Guid.Parse("01234567-89ab-cdef-0123-456789abcdef"), " a:<&\"\u00e9\n/token \u2028> ", new[] { "netratel:connect" }, 1, Guid.Parse("fedcba98-7654-3210-fedc-ba9876543210"),
            "{\"agentId\":\"01234567-89ab-cdef-0123-456789abcdef\",\"refreshToken\":\" a:\\u003C\\u0026\\u0022\\u00E9\\n/token \\u2028\\u003E \",\"requestedScopes\":[\"netratel:connect\"],\"exchangeVersion\":1,\"exchangeId\":\"fedcba98-7654-3210-fedc-ba9876543210\"}",
            "k+shgPwsESF9R+RbaCWgGim2sR1zqYCmf+/sAsILPCQ=", "DvKWwHN4qprtcGSMBhQcbjl+IzD4mh6RJYOJSBJhu3Y="];
        yield return [Guid.Parse("00000000-0000-0000-0000-000000000000"), " token ", null, null, null,
            "{\"agentId\":\"00000000-0000-0000-0000-000000000000\",\"refreshToken\":\" token \",\"requestedScopes\":null}",
            "/+k1AqUYS3yt3znE4LPImRj0Kyu+6UeT/Qldv/Bdr6I=", "HLcaf6+7XRm9KKvt5YvQELrQKp2oMnK1/YePG7iKfGM="];
        yield return [Guid.Parse("01234567-89ab-cdef-0123-456789abcdef"), " a:<&\"\u00e9\n/token \u2028> ", new[] { "scope:z", "scope:<\u00e9>&", "netratel:connect" }, 1, Guid.Parse("fedcba98-7654-3210-fedc-ba9876543210"),
            "{\"agentId\":\"01234567-89ab-cdef-0123-456789abcdef\",\"refreshToken\":\" a:\\u003C\\u0026\\u0022\\u00E9\\n/token \\u2028\\u003E \",\"requestedScopes\":[\"scope:z\",\"scope:\\u003C\\u00E9\\u003E\\u0026\",\"netratel:connect\"],\"exchangeVersion\":1,\"exchangeId\":\"fedcba98-7654-3210-fedc-ba9876543210\"}",
            "PegbnyoKmSAPuqrYTC3FcY+0tltgxCR8q7wNdd8bfYQ=", "igFY+wjWpj1hmfAO6qAqqAeHrKIdyfAzQE3PQfnBYUc="];
    }

    [Theory]
    [MemberData(nameof(PreChangePartialExchangeVectors))]
    public void Signed_protocol_preserves_nullable_version_exchange_and_null_versus_empty_scopes(
        int? version, Guid? exchangeId, string[]? scopes, string expectedHash, string expectedSigningMessage)
    {
        var hash = PopSignatureService.ComputeTokenBodyHash(Guid.Parse("01234567-89ab-cdef-0123-456789abcdef"),
            " a:<&\"é\n/token \u2028> ", scopes, version, exchangeId);
        hash.Should().Be(expectedHash);
        Convert.ToBase64String(PopSignatureService.BuildSigningMessage("POST", "/api/v1/agents/token",
            new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero), "fixed-test-nonce", hash)).Should().Be(expectedSigningMessage);
    }

    public static IEnumerable<object?[]> PreChangePartialExchangeVectors()
    {
        yield return [1, null, null, "sSceHJ31cn9RHMyEkaYpqI1nqglN1yuLZqg6Bc2YFGU=", "U7+ZcXN63W1pVTJDB59Hfp7FzkXQoEUekjUt1dx5So4="];
        yield return [null, Guid.Parse("fedcba98-7654-3210-fedc-ba9876543210"), Array.Empty<string>(), "N8HKaZrxUhgHbycDt7D2Sek4st7ik0N7dWMvVIqLnvI=", "Uqqtd78DZbFE/CZA9N/TY7212bqw9d0+GrtmDP8OUH4="];
    }
}
