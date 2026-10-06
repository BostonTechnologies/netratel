using NetRatel.Shared.Contracts.RatelDesk;

namespace NetRatel.Web.Services.RatelDesk;

public interface IRatelDeskConnectorApiService
{
    Task<IReadOnlyList<RatelDeskConnectorTenantDto>> GetTenantsAsync(CancellationToken ct);
    Task<RatelDeskConnectorSetupDto> GetSetupAsync(int tenantId, CancellationToken ct);
    Task<RatelDeskConnectorSetupDto> AdoptFlowSourceAsync(int tenantId, AdoptRatelDeskFlowSourceRequest request, CancellationToken ct);
    Task<IReadOnlyList<RatelDeskConnectorDto>> ListAsync(int tenantId, CancellationToken ct);
    Task<RatelDeskConnectorDto> SaveAsync(int tenantId, Guid id, SaveRatelDeskConnectorRequest request, CancellationToken ct);
    Task<RatelDeskConnectorDto> RotateAsync(int tenantId, Guid id, RotateRatelDeskConnectorCredentialRequest request, CancellationToken ct);
    Task<RatelDeskConnectionTestResult> TestAsync(int tenantId, Guid id, CancellationToken ct);
    Task<RatelDeskDryRunResult> DryRunAsync(int tenantId, Guid id, RatelDeskDryRunRequest request, CancellationToken ct);
}

public sealed class RatelDeskConnectorApiException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
