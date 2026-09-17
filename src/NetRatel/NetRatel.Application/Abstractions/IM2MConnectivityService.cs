using NetRatel.Shared.Connectivity;

namespace NetRatel.Application.Abstractions;

public interface IM2MConnectivityService
{
    Task<M2MConnectivitySettingsDto> GetAsync(CancellationToken ct);
    Task SaveAsync(M2MConnectivitySettingsDto dto, CancellationToken ct);
    Task<M2MConnectivityTestResultDto> TestAsync(M2MConnectivityTestRequestDto req, CancellationToken ct);
}
