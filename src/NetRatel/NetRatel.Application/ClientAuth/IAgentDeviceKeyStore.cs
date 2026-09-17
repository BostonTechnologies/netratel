namespace NetRatel.Application.ClientAuth;

public sealed record AgentDeviceKeyMaterial(string PublicKey, string PrivateKey, string Algorithm);

public interface IAgentDeviceKeyStore
{
    Task<AgentDeviceKeyMaterial> GetOrCreateAsync(CancellationToken ct);
    Task<AgentDeviceKeyMaterial?> LoadAsync(CancellationToken ct);
}
