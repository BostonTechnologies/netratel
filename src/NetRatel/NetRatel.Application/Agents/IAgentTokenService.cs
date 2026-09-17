namespace NetRatel.Application.Agents;

public interface IAgentTokenService
{
    Task<AgentTokenResponse> ExchangeRefreshTokenAsync(AgentTokenRequest request, CancellationToken ct);
    Task<string> CreateServiceTokenAsync(string tenantId, CancellationToken ct);
    Task DisableAgentAsync(Guid agentId, CancellationToken ct);
    Task EnableAgentAsync(Guid agentId, CancellationToken ct);
    Task<OpenIdConfigurationDto> GetOpenIdConfigurationAsync(CancellationToken ct);
    Task<JsonWebKeySetDto> GetJwksAsync(CancellationToken ct);
}
