using System.Security.Claims;
using NetRatel.Application.RatelDesk;
using NetRatel.Shared.Contracts.RatelDesk;
namespace NetRatel.Infrastructure.RatelDesk;
public sealed class RatelDeskConnectorService(IRatelDeskConnectorStore store, IRatelDeskConnectorAuthorization authorization,
    RatelDeskConnectorReceiver receiver) : IRatelDeskConnectorService
{
    public async Task<IReadOnlyList<RatelDeskConnectorDto>> ListAsync(int tenantId, ClaimsPrincipal actor, CancellationToken ct)
    {
        await RequireAsync(actor, tenantId, ct); var result = new List<RatelDeskConnectorDto>();
        foreach (var state in await store.ListAsync(tenantId, ct)) result.Add(await CurrentDtoAsync(state, ct)); return result;
    }
    public async Task<RatelDeskConnectorDto?> GetAsync(int tenantId, Guid id, ClaimsPrincipal actor, CancellationToken ct)
    { await RequireAsync(actor, tenantId, ct); var state = await store.GetAsync(tenantId, id, ct); return state is null ? null : await CurrentDtoAsync(state, ct); }
    public async Task<RatelDeskConnectorDto> SaveAsync(int tenantId, Guid id, SaveRatelDeskConnectorRequest request, ClaimsPrincipal actor, CancellationToken ct)
    {
        await RequireAsync(actor, tenantId, ct);
        var current = await store.GetAsync(tenantId, id, ct) ?? throw new ArgumentException("create-the-connection-with-pairing");
        if (current.Authentication?.Mode != RatelDeskAuthenticationMode.PairedSystem || request.ExpectedRevision != current.Revision || request.Configuration.OrganizationId != current.Configuration.OrganizationId || request.Configuration.CustomerId != current.Configuration.CustomerId || request.Configuration.Origin != current.Configuration.Origin)
            throw new ArgumentException("edit-the-tenant-mapping-on-the-pairing-account-page");
        var c = request.Configuration;
        if (c.Name is not { Length: > 0 and <= 128 } || c.Name.Any(char.IsControl) || c.CategoryIds.Count > RatelDeskConnectorLimits.MaximumCategories || c.CategoryIds.Any(x => x == Guid.Empty) || new[] { c.Priorities.Information, c.Priorities.Warning, c.Priorities.Error, c.Priorities.Critical }.Any(x => x is < 0 or > 3)) throw new ArgumentException("invalid-connector-configuration");
        if (!await authorization.CanExecuteAsync(current.OwnerPrincipalId, null, tenantId, ct)) throw new UnauthorizedAccessException();
        var saved = current with { Revision = current.Revision + 1, RowVersion = current.RowVersion + 1, Configuration = c, Readiness = null };
        if (!await store.SaveAsync(saved, current.RowVersion, ct)) throw new InvalidOperationException("connector-conflict"); return await CurrentDtoAsync(saved, ct);
    }
    public async Task<RatelDeskConnectionTestResult> TestAsync(int tenant, Guid id, ClaimsPrincipal actor, CancellationToken ct)
    { await RequireAsync(actor, tenant, ct); return await receiver.TestAsync(await store.GetAsync(tenant, id, ct) ?? throw new KeyNotFoundException(), ct); }
    public async Task<RatelDeskDryRunResult> DryRunAsync(int tenant, Guid id, RatelDeskDryRunRequest request, ClaimsPrincipal actor, CancellationToken ct)
    { await RequireAsync(actor, tenant, ct); var state = await store.GetAsync(tenant, id, ct) ?? throw new KeyNotFoundException(); var body = RatelDeskPayload.Create(state.Configuration, request.Title, request.Description, request.Priority); return new(body, RatelDeskPayload.Fingerprint(body)); }
    private async Task RequireAsync(ClaimsPrincipal actor, int tenant, CancellationToken ct) { if (tenant <= 0 || !await authorization.CanManageAsync(actor, tenant, ct)) throw new UnauthorizedAccessException(); }
    public static RatelDeskConnectorDto ToDto(RatelDeskConnectorState state) => new(state.Id, state.TenantId, state.Revision, state.Configuration, state.Authentication?.Mode == RatelDeskAuthenticationMode.PairedSystem, state.CredentialRevision, false, RatelDeskConnectorLimits.ReceiverUnavailableCode, RatelDeskConnectorReceiver.ToDto(state.Authentication), state.Readiness?.TargetValidatedAtUtc);
    private async Task<RatelDeskConnectorDto> CurrentDtoAsync(RatelDeskConnectorState state, CancellationToken ct)
    { var current = await receiver.CurrentAsync(state, ct); return ToDto(state) with { AutomaticDeliveryAvailable = current.Available, AvailabilityCode = current.Code }; }
}
