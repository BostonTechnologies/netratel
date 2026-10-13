using NetRatel.Shared.Contracts.Netclaw;

namespace NetRatel.Application.Netclaw;

/// <summary>Tenant-scoped immutable guidance resolution. Caller authorization remains at the API/flow admission boundary.</summary>
public interface IPromptLibraryService
{
    Task<AiPromptSnapshot?> ResolveDefaultAsync(int tenantId, CancellationToken ct = default);
    Task<AiPromptSnapshot> ResolveAsync(int tenantId, AiPromptReference reference, bool allowArchived = false, CancellationToken ct = default);
}

public sealed class AiPromptConflictException(string message) : Exception(message);
