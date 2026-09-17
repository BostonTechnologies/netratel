namespace NetRatel.Application.ClientAuth;

public interface IAgentTokenService
{
    Task<(string AccessToken, DateTimeOffset ExpiresAtUtc)> GetAccessTokenAsync(CancellationToken ct);
}
