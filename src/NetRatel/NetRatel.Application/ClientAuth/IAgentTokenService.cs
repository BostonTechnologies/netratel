namespace NetRatel.Application.ClientAuth;

public interface IAgentTokenService
{
    Task<(string AccessToken, DateTimeOffset ExpiresAtUtc)> GetAccessTokenAsync(CancellationToken ct);

    // A rejection belongs to the token sent by that operation, not a newer token
    // acquired concurrently by another operation.
    bool InvalidateAccessToken(string rejectedAccessToken) => false;
}
