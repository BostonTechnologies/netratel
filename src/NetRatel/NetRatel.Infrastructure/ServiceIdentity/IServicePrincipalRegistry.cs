using System.Security.Claims;
using NetRatel.Shared.ServiceIdentity;

namespace NetRatel.Infrastructure.ServiceIdentity;

public sealed record CreatedServiceClient(ServicePrincipalRegistration Principal, string ClientSecret, long CredentialRevision)
{
    public DateTimeOffset CredentialExpiresAtUtc { get; init; }
}
public sealed record AuthenticatedServiceClient(ServicePrincipalRegistration Principal, ServicePrincipalSecret Credential);
public interface IServicePrincipalRegistry
{
    Task<CreatedServiceClient> CreateAsync(ServiceClientCreateRequest request, string actorId, bool pending = false, CancellationToken ct = default);
    Task<CreatedServiceClient> CreatePendingAsync(ServiceClientCreateRequest request, string actorId, CancellationToken ct = default);
    Task<ServicePrincipalRegistration?> ResolvePrincipalAsync(ClaimsPrincipal principal, string? requiredScope = null, CancellationToken ct = default);
    Task<AuthenticatedServiceClient?> AuthenticateClientAsync(string clientId, string secret, CancellationToken ct = default);
    Task<bool> CanIssueScopesAsync(AuthenticatedServiceClient client, string[] scopes, CancellationToken ct = default);
    Task ActivateAsync(Guid id, CancellationToken ct = default);
    Task SetStatusAsync(Guid id, string status, CancellationToken ct = default);
    Task RevokeAsync(Guid id, CancellationToken ct = default);
    Task<CreatedServiceClient> CreateSuccessorAsync(Guid id, long expectedRevision, CancellationToken ct = default);
    Task ActivateSuccessorAsync(Guid id, long revision, CancellationToken ct = default);
    Task RetirePredecessorAsync(Guid id, long revision, DateTimeOffset retireAt, CancellationToken ct = default);
    Task<CreatedServiceClient> RotateAsync(Guid id, long expectedRevision, CancellationToken ct = default);
    Task<IReadOnlyList<ServiceClientMetadata>> ListAsync(CancellationToken ct = default);
    string[] PermittedScopes(ServicePrincipalRegistration principal, ServicePrincipalSecret credential);
}

public sealed class ServiceClientConflictException(string message) : Exception(message);
