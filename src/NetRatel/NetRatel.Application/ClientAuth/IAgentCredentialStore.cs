namespace NetRatel.Application.ClientAuth;

public interface IAgentCredentialStore
{
    Task SaveAsync(string agentId, string refreshToken);
    Task<(string AgentId, string RefreshToken)?> LoadAsync();
    Task SaveAsync(string agentId, string refreshToken, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return SaveAsync(agentId, refreshToken);
    }
    Task<(string AgentId, string RefreshToken)?> LoadAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return LoadAsync();
    }
    Task ClearRefreshCredentialsAsync();
    Task ResetInstallationIdentityAsync();

    [Obsolete("Use ClearRefreshCredentialsAsync so the stable device identity is preserved.")]
    Task ClearAsync();
}
