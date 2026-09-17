namespace NetRatel.Application.ClientAuth;

public interface IAgentCredentialStore
{
    Task SaveAsync(string agentId, string refreshToken);
    Task<(string AgentId, string RefreshToken)?> LoadAsync();
    Task ClearRefreshCredentialsAsync();
    Task ResetInstallationIdentityAsync();

    [Obsolete("Use ClearRefreshCredentialsAsync so the stable device identity is preserved.")]
    Task ClearAsync();
}
