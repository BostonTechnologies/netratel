namespace NetRatel.Application.ClientAuth;

public interface IAgentTokenService
{
    Task<(string AccessToken, DateTimeOffset ExpiresAtUtc)> GetAccessTokenAsync(CancellationToken ct);

    // ExpiresAtUtc above is the conservative cache/refresh deadline. JWT exp
    // bounds the authority acknowledged by the gateway; reading it locally
    // does not replace server signature/identity/lifetime validation. Opaque
    // token providers retain their existing lifetime bound.
    DateTimeOffset GetAuthorityExpiryUtc(string accessToken, DateTimeOffset cacheExpiresAtUtc) => cacheExpiresAtUtc;

    // A rejection belongs to the token sent by that operation, not a newer token
    // acquired concurrently by another operation.
    bool InvalidateAccessToken(string rejectedAccessToken) => false;
}
