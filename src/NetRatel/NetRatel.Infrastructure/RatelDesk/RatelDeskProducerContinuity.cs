using NetRatel.Application.RatelDesk;
using NetRatel.Infrastructure.SystemPairing;
namespace NetRatel.Infrastructure.RatelDesk;
public interface IRatelDeskProducerContinuity { Task RequireCurrentAsync(Guid expectedFlowSource, CancellationToken ct); }
public interface IRatelDeskInstallationIdentityReader { Task<InstallationIdentityRecord> GetAsync(CancellationToken ct); }
public sealed class RatelDeskInstallationIdentityReader(InstallationIdentityStore installation) : IRatelDeskInstallationIdentityReader
{ public Task<InstallationIdentityRecord> GetAsync(CancellationToken ct) => installation.GetAsync(ct); }
public sealed class RatelDeskProducerContinuity(IFlowSourceIdentityResolver flow, IRatelDeskInstallationIdentityReader installation) : IRatelDeskProducerContinuity
{
    public async Task RequireCurrentAsync(Guid expectedFlowSource, CancellationToken ct)
    {
        var before = await installation.GetAsync(ct);
        if (expectedFlowSource == Guid.Empty || await flow.EnsureAsync(ct) != expectedFlowSource) throw new UnauthorizedAccessException("flow-source-identity-drift");
        var current = await installation.GetAsync(ct);
        if (current.InstanceId != before.InstanceId || current.SourceInstanceId != expectedFlowSource) throw new UnauthorizedAccessException("installation-producer-identity-drift");
    }
}
