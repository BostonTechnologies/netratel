using System.Security.Claims;
using System.Security.Cryptography;
using NetRatel.Application.RatelDesk;
using NetRatel.Shared.Contracts.RatelDesk;

namespace NetRatel.Infrastructure.RatelDesk;

public sealed class RatelDeskConnectorService(IRatelDeskConnectorStore store, IRatelDeskConnectorAuthorization authorization,
    IRatelDeskCredentialProtector protector, IRatelDeskOriginPolicy origins, IRatelDeskConnectionTester tester) : IRatelDeskConnectorService
{
    public async Task<IReadOnlyList<RatelDeskConnectorDto>> ListAsync(int tenantId, ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        await RequireManageAsync(principal, tenantId, cancellationToken).ConfigureAwait(false);
        return (await store.ListAsync(tenantId, cancellationToken).ConfigureAwait(false)).Select(ToDto).ToArray();
    }
    public async Task<RatelDeskConnectorDto?> GetAsync(int tenantId, Guid id, ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        await RequireManageAsync(principal, tenantId, cancellationToken).ConfigureAwait(false);
        var state = await store.GetAsync(tenantId, id, cancellationToken).ConfigureAwait(false);
        return state is null ? null : ToDto(state);
    }
    public async Task<RatelDeskConnectorDto> SaveAsync(int tenantId, Guid id, SaveRatelDeskConnectorRequest request, ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        await RequireManageAsync(principal, tenantId, cancellationToken).ConfigureAwait(false);
        Validate(request.Configuration);
        if (id == Guid.Empty || request.ExpectedRevision < 0) throw new ArgumentException("invalid-connector-id-or-revision");
        var current = await store.GetAsync(tenantId, id, cancellationToken).ConfigureAwait(false);
        if ((current?.Revision ?? 0) != request.ExpectedRevision) throw new InvalidOperationException("connector-conflict");
        var owner = current?.OwnerPrincipalId ?? principal.FindFirst("netratel_principal_id")!.Value;
        if (!await authorization.CanExecuteAsync(owner, null, tenantId, cancellationToken).ConfigureAwait(false)) throw new UnauthorizedAccessException();
        var configuration = request.Configuration with { Origin = new Uri(request.Configuration.Origin).GetLeftPart(UriPartial.Authority), CategoryIds = request.Configuration.CategoryIds.Order().ToArray() };
        var state = new RatelDeskConnectorState(id, tenantId, checked(request.ExpectedRevision + 1), checked((current?.RowVersion ?? 0) + 1), owner,
            configuration, current?.ProtectedCredential, current?.CredentialRevision ?? 0);
        if (!await store.SaveAsync(state, current?.RowVersion ?? 0, cancellationToken).ConfigureAwait(false)) throw new InvalidOperationException("connector-conflict");
        return ToDto(state);
    }
    public async Task<RatelDeskConnectorDto> RotateAsync(int tenantId, Guid id, RotateRatelDeskConnectorCredentialRequest request, ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        await RequireManageAsync(principal, tenantId, cancellationToken).ConfigureAwait(false);
        if (!ValidCredential(request.Credential)) throw new ArgumentException("api-purpose-rateldesk-credential-required");
        var current = await store.GetAsync(tenantId, id, cancellationToken).ConfigureAwait(false) ?? throw new KeyNotFoundException();
        if (current.CredentialRevision != request.ExpectedCredentialRevision) throw new InvalidOperationException("connector-conflict");
        var state = current with { RowVersion = checked(current.RowVersion + 1), CredentialRevision = checked(current.CredentialRevision + 1),
            ProtectedCredential = protector.Protect(tenantId, id, request.Credential) };
        if (!await store.SaveAsync(state, current.RowVersion, cancellationToken).ConfigureAwait(false)) throw new InvalidOperationException("connector-conflict");
        return ToDto(state);
    }
    public async Task<RatelDeskConnectionTestResult> TestAsync(int tenantId, Guid id, ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        await RequireManageAsync(principal, tenantId, cancellationToken).ConfigureAwait(false);
        var state = await store.GetAsync(tenantId, id, cancellationToken).ConfigureAwait(false) ?? throw new KeyNotFoundException();
        if (state.ProtectedCredential is null) return new(RatelDeskConnectionTestStatus.Unavailable, "credential-required");
        if (!await authorization.CanExecuteAsync(state.OwnerPrincipalId, null, tenantId, cancellationToken).ConfigureAwait(false)) return new(RatelDeskConnectionTestStatus.Unavailable, "connector-owner-denied");
        try { return await tester.TestAsync(tenantId, id, state.Configuration, protector.Unprotect(tenantId, id, state.ProtectedCredential), cancellationToken).ConfigureAwait(false); }
        catch (CryptographicException) { return new(RatelDeskConnectionTestStatus.Unavailable, "credential-unavailable"); }
    }
    public async Task<RatelDeskDryRunResult> DryRunAsync(int tenantId, Guid id, RatelDeskDryRunRequest request, ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        await RequireManageAsync(principal, tenantId, cancellationToken).ConfigureAwait(false);
        var state = await store.GetAsync(tenantId, id, cancellationToken).ConfigureAwait(false) ?? throw new KeyNotFoundException();
        var payload = RatelDeskPayload.Create(state.Configuration, request.Title, request.Description, request.Priority);
        return new(payload, RatelDeskPayload.Fingerprint(payload));
    }

    private async Task RequireManageAsync(ClaimsPrincipal principal, int tenantId, CancellationToken cancellationToken)
    { if (tenantId <= 0 || !await authorization.CanManageAsync(principal, tenantId, cancellationToken).ConfigureAwait(false)) throw new UnauthorizedAccessException(); }
    private void Validate(RatelDeskConnectorConfiguration c)
    {
        static bool Id(string? value) => value is { Length: > 0 and <= 128 } && !value.Any(char.IsControl) && value.Trim() == value;
        if (c is null || c.Name is not { Length: > 0 and <= 128 } || c.Name.Any(char.IsControl) || !origins.TryValidate(c.Origin, out _) ||
            !Id(c.OrganizationId) || !Id(c.CustomerId) || c.AssignedToId is not null && !Id(c.AssignedToId) ||
            c.CategoryIds is null || c.CategoryIds.Count > RatelDeskConnectorLimits.MaximumCategories || c.CategoryIds.Any(id => id == Guid.Empty) || c.CategoryIds.Distinct().Count() != c.CategoryIds.Count ||
            c.Priorities is null || new[] { c.Priorities.Information, c.Priorities.Warning, c.Priorities.Error, c.Priorities.Critical }.Any(p => p is < 0 or > 3))
            throw new ArgumentException("invalid-connector-configuration");
    }
    public static bool ValidCredential(string? credential) => credential is { Length: >= 40 and <= 512 } && credential.StartsWith("rdk_", StringComparison.Ordinal) && credential.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');
    public static RatelDeskConnectorDto ToDto(RatelDeskConnectorState state) => new(state.Id, state.TenantId, state.Revision,
        state.Configuration, state.ProtectedCredential is not null, state.CredentialRevision, false, RatelDeskConnectorLimits.ReceiverUnavailableCode);
}
