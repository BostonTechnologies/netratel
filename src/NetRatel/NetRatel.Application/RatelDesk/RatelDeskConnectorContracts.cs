using System.Security.Claims;
using NetRatel.Shared.Contracts.RatelDesk;

namespace NetRatel.Application.RatelDesk;

/// <summary>Credential ciphertext is an internal persistence value and never part of a response DTO or graph.</summary>
public sealed record RatelDeskConnectorState(Guid Id, int TenantId, long Revision, long RowVersion,
    string OwnerPrincipalId, RatelDeskConnectorConfiguration Configuration, string? ProtectedCredential,
    long CredentialRevision);

public interface IRatelDeskConnectorStore
{
    Task<RatelDeskConnectorState?> GetAsync(int tenantId, Guid id, CancellationToken cancellationToken);
    Task<IReadOnlyList<RatelDeskConnectorState>> ListAsync(int tenantId, CancellationToken cancellationToken);
    Task<bool> SaveAsync(RatelDeskConnectorState state, long expectedRowVersion, CancellationToken cancellationToken);
}

public interface IRatelDeskCredentialProtector
{
    string Protect(int tenantId, Guid connectorId, string credential);
    string Unprotect(int tenantId, Guid connectorId, string ciphertext);
}

public interface IRatelDeskOriginPolicy
{
    bool TryValidate(string origin, out Uri? normalizedOrigin);
}

/// <summary>Uses only actual read-only ticketing lookup routes. This interface has no capability guess or incident write.</summary>
public interface IRatelDeskConnectionTester
{
    Task<RatelDeskConnectionTestResult> TestAsync(int tenantId, Guid connectorId, RatelDeskConnectorConfiguration configuration,
        string credential, CancellationToken cancellationToken);
}

/// <summary>The normal create contract alone does not support safe replay. Automatic dispatch never calls this transport.</summary>
public interface IRatelDeskIncidentTransport
{
    Task<RatelDeskDeliveryResult> CreateAsync(int tenantId, Guid connectorId, string origin, string credential, RatelDeskCreateIncidentDto payload,
        CancellationToken cancellationToken);
}

public interface IRatelDeskConnectorAuthorization
{
    Task<bool> CanManageAsync(ClaimsPrincipal principal, int tenantId, CancellationToken cancellationToken);
    Task<bool> CanExecuteAsync(string principalId, string? integrationCredentialId, int tenantId, CancellationToken cancellationToken);
}

public interface IRatelDeskConnectorService
{
    Task<IReadOnlyList<RatelDeskConnectorDto>> ListAsync(int tenantId, ClaimsPrincipal principal, CancellationToken cancellationToken);
    Task<RatelDeskConnectorDto?> GetAsync(int tenantId, Guid id, ClaimsPrincipal principal, CancellationToken cancellationToken);
    Task<RatelDeskConnectorDto> SaveAsync(int tenantId, Guid id, SaveRatelDeskConnectorRequest request,
        ClaimsPrincipal principal, CancellationToken cancellationToken);
    Task<RatelDeskConnectorDto> RotateAsync(int tenantId, Guid id, RotateRatelDeskConnectorCredentialRequest request,
        ClaimsPrincipal principal, CancellationToken cancellationToken);
    Task<RatelDeskConnectionTestResult> TestAsync(int tenantId, Guid id, ClaimsPrincipal principal, CancellationToken cancellationToken);
    Task<RatelDeskDryRunResult> DryRunAsync(int tenantId, Guid id, RatelDeskDryRunRequest request,
        ClaimsPrincipal principal, CancellationToken cancellationToken);
}
