using System.Security.Claims;
using NetRatel.Shared.Contracts.RatelDesk;

namespace NetRatel.Application.RatelDesk;

/// <summary>Credential ciphertext is an internal persistence value and never part of a response DTO or graph.</summary>
public sealed record RatelDeskConnectorState(Guid Id, int TenantId, long Revision, long RowVersion,
    string OwnerPrincipalId, RatelDeskConnectorConfiguration Configuration, string? ProtectedCredential,
    long CredentialRevision, RatelDeskConnectorAuthentication? Authentication = null,
    RatelDeskReadinessObservation? Readiness = null);

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
    Task<RatelDeskConnectionTestResult> TestAsync(int tenantId, Guid id, ClaimsPrincipal principal, CancellationToken cancellationToken);
    Task<RatelDeskDryRunResult> DryRunAsync(int tenantId, Guid id, RatelDeskDryRunRequest request,
        ClaimsPrincipal principal, CancellationToken cancellationToken);
}

/// <summary>Authentication reference only; business credentials remain protected in the saved pairing mapping.</summary>
public interface IRatelDeskConnectorBindingStore
{
    Task<RatelDeskConnectorAuthentication> GetAuthenticationAsync(int tenantId, Guid connectorId, CancellationToken ct);
}

public sealed record RatelDeskReadinessObservation(long ConnectorRevision,
    RatelDeskSemanticPeer Peer, RatelDeskVerifiedCapability Capability, DateTimeOffset TargetValidatedAtUtc);
public interface IRatelDeskConnectorReadinessStore
{
    Task<bool> SaveReadinessAsync(RatelDeskConnectorState current, RatelDeskReadinessObservation observation, CancellationToken ct);
    Task<bool> ClearReadinessAsync(RatelDeskConnectorState current, CancellationToken ct);
}
public interface IRatelDeskConnectorReadiness
{
    Task<(bool Available, string Code)> CurrentAsync(RatelDeskConnectorState connector, CancellationToken ct);
}
