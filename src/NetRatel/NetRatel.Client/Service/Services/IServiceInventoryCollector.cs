using NetRatel.Shared.Contracts.Services;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NetRatel.Client.Service.Services;

/// <summary>Read-only OS discovery. Implementations never start, stop, or configure services.</summary>
public interface IServiceInventoryCollector
{
    Task<ServiceCollectionResult> CollectInventoryAsync(CancellationToken cancellationToken);
    Task<ServiceCollectionResult> CollectWatchAsync(IReadOnlyList<string> names, ulong policyRevision, CancellationToken cancellationToken);
}
