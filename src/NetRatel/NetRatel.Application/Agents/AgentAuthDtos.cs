using System.Text.Json.Serialization;

namespace NetRatel.Application.Agents;

public sealed record AgentEnrollRequest(
    string EnrollmentCode,
    string? PublicKey = null,
    string? KeyAlgorithm = null,
    string? DeviceInfoJson = null,
    IReadOnlyList<string>? RequestedScopes = null,
    string? MtlsThumbprint = null
)
{
    public string? ProofSignature { get; init; }
    public string? ProofNonce { get; init; }
    public DateTimeOffset? ProofTimestampUtc { get; init; }
}

public sealed record AgentEnrollResponse(
    Guid AgentId,
    string RefreshToken,
    int ExpiresInDays
);

public sealed record AgentTokenRequest(
    Guid AgentId,
    string RefreshToken,
    string? ProofJwt = null,
    string? ProofSignature = null,
    string? ProofNonce = null,
    DateTimeOffset? ProofTimestampUtc = null,
    IReadOnlyList<string>? RequestedScopes = null,
    string? ClientCertificateThumbprint = null
);

public sealed record AgentTokenResponse(
    string AccessToken,
    int ExpiresIn,
    string? RefreshToken = null
);

public sealed record OpenIdConfigurationDto(
    [property: JsonPropertyName("issuer")] string Issuer,
    [property: JsonPropertyName("token_endpoint")] string TokenEndpoint,
    [property: JsonPropertyName("jwks_uri")] string JwksUri,
    [property: JsonPropertyName("id_token_signing_alg_values_supported")] IReadOnlyList<string> IdTokenSigningAlgValuesSupported
);

public sealed record JsonWebKeyDto(
    string Kty,
    string Kid,
    string Use,
    string Crv,
    string X,
    string Y,
    string Alg
);

public sealed record JsonWebKeySetDto(
    IReadOnlyList<JsonWebKeyDto> Keys
);
