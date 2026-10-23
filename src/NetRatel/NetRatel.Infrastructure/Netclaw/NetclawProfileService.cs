using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Netclaw;
using NetRatel.Infrastructure.Identity;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.Netclaw;

namespace NetRatel.Infrastructure.Netclaw;

public sealed class NetclawAuthority(IEffectiveAccessService access, NetRatelIdentityDbContext identity)
{
    public async Task<bool> CanInvokeAsync(int tenantId, string actor, string? credential, bool automated, CancellationToken ct)
    {
        if (tenantId <= 0 || string.IsNullOrWhiteSpace(actor) ||
            !await identity.ApplicationPrincipals.AsNoTracking().AnyAsync(x => x.Id == actor, ct) ||
            await identity.Users.AsNoTracking().AnyAsync(x => x.PrincipalId == actor && !x.IsEnabled, ct)) return false;
        var claims = new List<Claim> { new("netratel_principal_id", actor) };
        if (credential is not null) claims.Add(new("netratel_integration_credential_id", credential));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "NetclawSavedAuthority"));
        return await access.AuthorizeAsync(principal, automated ? NetRatelPermissions.FlowExecute : NetRatelPermissions.ClientManagement, tenantId, ct) &&
            await access.AuthorizeAsync(principal, NetRatelPermissions.TelemetryRead, tenantId, ct) &&
            await access.AuthorizeAsync(principal, NetRatelPermissions.SecretUse, tenantId, ct) &&
            (!automated || await access.AuthorizeAsync(principal, NetRatelPermissions.MonitoringRead, tenantId, ct));
    }

    public async Task<bool> CanReadAlertAsync(int tenantId, string actor, string? credential, CancellationToken ct)
    {
        var claims = new List<Claim> { new("netratel_principal_id", actor) };
        if (credential is not null) claims.Add(new("netratel_integration_credential_id", credential));
        return await access.AuthorizeAsync(new(new ClaimsIdentity(claims, "NetclawSavedAuthority")), NetRatelPermissions.MonitoringRead, tenantId, ct);
    }
}

public sealed class NetclawProfileService(OrchestratorDbContext db, IDataProtectionProvider protection,
    INetclawPairingService pairing, IAiAssistantChatClientFactory clients, TimeProvider clock)
{
    public async Task<NetclawConnectionDto> GetAsync(int tenantId, CancellationToken ct = default)
    {
        var row = await db.Set<NetclawConnectionRecord>().AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId, ct);
        return row is null ? new(Guid.Empty, tenantId, 0, false, null, false, null, null, null) : Dto(row);
    }

    public async Task<NetclawRuntimeProfile?> ResolveAsync(int tenantId, CancellationToken ct = default)
    {
        var row = await db.Set<NetclawConnectionRecord>().AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Enabled, ct);
        if (row is null || string.IsNullOrWhiteSpace(row.ProtectedDeviceToken)) return null;
        try
        {
            return RuntimeProfile(row);
        }
        catch (CryptographicException) { return null; }
    }

    public async Task<NetclawTestResult> PairAsync(int tenantId, NetclawPairRequest request, string actor, CancellationToken ct)
    {
        if (tenantId <= 0 || !await db.Tenants.AsNoTracking().AnyAsync(x => x.Id == tenantId, ct))
            throw new KeyNotFoundException("Tenant not found.");
        var allowPrivate = NetclawEndpointNormalizer.IsPrivateHttpLiteral(request.Address);
        var endpoint = NetclawEndpointNormalizer.Normalize(request.Address, "Address", allowPrivate)
            ?? throw new ArgumentException("Enter the Netclaw address.");
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(821, {tenantId})", ct);
        var row = await db.Set<NetclawConnectionRecord>().SingleOrDefaultAsync(x => x.TenantId == tenantId, ct);
        if ((row?.Revision ?? 0) != request.ExpectedRevision)
            throw new InvestigationConflictException("The Netclaw connection changed. Reload before pairing; the code has not been sent.");
        var token = await pairing.ExchangeCodeAsync(new(new Uri(endpoint), allowPrivate), request.PairingCode, ct);
        if (row is null) { row = new() { TenantId = tenantId }; db.Add(row); }
        row.Endpoint = endpoint; row.AllowPrivateHttp = allowPrivate; row.Enabled = true;
        row.ProfileFingerprint = Fingerprint(tenantId, endpoint); row.OwnerPrincipalId = actor;
        row.ProtectedDeviceToken = Protector(row).Protect(token); row.Revision++;
        row.LastTestSucceeded = null; row.LastTestedAtUtc = null; row.LastTestCode = null;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        // Preserve the exchanged device token before testing. A failed probe never consumes another pairing code.
        return await TestAsync(tenantId, row.Revision, ct);
    }

    public async Task<NetclawConnectionDto> SetEnabledAsync(int tenantId, NetclawEnabledRequest request, CancellationToken ct)
    {
        var row = await db.Set<NetclawConnectionRecord>().SingleOrDefaultAsync(x => x.TenantId == tenantId, ct)
            ?? throw new KeyNotFoundException("Pair a Netclaw daemon first.");
        if (row.Revision != request.ExpectedRevision) throw new InvestigationConflictException("The connection changed. Reload its current state.");
        row.Enabled = request.Enabled; row.Revision++;
        row.LastTestSucceeded = null; row.LastTestedAtUtc = null; row.LastTestCode = null;
        await db.SaveChangesAsync(ct);
        return Dto(row);
    }

    public async Task<NetclawTestResult> TestAsync(int tenantId, long? expectedRevision = null, CancellationToken ct = default)
    {
        var saved = await db.Set<NetclawConnectionRecord>().AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId, ct)
            ?? throw new InvestigationConflictException("Pair a Netclaw daemon for this tenant before testing its connection.");
        if (expectedRevision.HasValue && saved.Revision != expectedRevision)
            throw new InvestigationConflictException("The connection changed during verification.");
        if (!saved.Enabled)
            throw new InvestigationConflictException("Enable this tenant's saved Netclaw connection before testing it.");
        if (string.IsNullOrWhiteSpace(saved.ProtectedDeviceToken))
            throw new InvestigationConflictException("This tenant has no saved Netclaw device credential. Pair its daemon before testing the connection.");
        var success = false; var code = "authenticated-session-unavailable";
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await using var client = clients.Create(RuntimeProfile(saved));
            var result = await client.ConnectAsync(null, _ => Task.CompletedTask, deadline.Token);
            success = !string.IsNullOrWhiteSpace(result.SessionId); code = success ? "authenticated-signalr-verified" : "session-not-confirmed";
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { success = false; code = "session-test-timeout"; }
        catch (Exception exception) when (!ct.IsCancellationRequested) { success = false; code = NetclawConnectionFailure.Classify(exception); }
        ct.ThrowIfCancellationRequested();
        db.ChangeTracker.Clear();
        var row = await db.Set<NetclawConnectionRecord>().SingleAsync(x => x.TenantId == tenantId, ct);
        if (row.Revision != saved.Revision) throw new InvestigationConflictException("The connection changed during verification. Test the current profile.");
        row.LastTestSucceeded = success; row.LastTestedAtUtc = clock.GetUtcNow(); row.LastTestCode = code;
        await db.SaveChangesAsync(ct);
        return new(success, code, Dto(row));
    }

    private NetclawRuntimeProfile RuntimeProfile(NetclawConnectionRecord row) => new(row.TenantId, row.Id, row.Revision, row.Endpoint,
        Protector(row).Unprotect(row.ProtectedDeviceToken), row.AllowPrivateHttp, row.ProfileFingerprint);

    private IDataProtector Protector(NetclawConnectionRecord row) => protection.CreateProtector("NetRatel.Netclaw.DeviceToken.v1",
        row.TenantId.ToString(CultureInfo.InvariantCulture), row.Id.ToString("N"), row.ProfileFingerprint);
    public static string Fingerprint(int tenantId, string endpoint) => Convert.ToHexStringLower(SHA256.HashData(
        Encoding.UTF8.GetBytes($"netclaw|{tenantId.ToString(CultureInfo.InvariantCulture)}|dev|{endpoint}")));
    private static NetclawConnectionDto Dto(NetclawConnectionRecord row) => new(row.Id, row.TenantId, row.Revision, row.Enabled,
        NetclawEndpointNormalizer.TryGetDaemonAddress(row.Endpoint), !string.IsNullOrEmpty(row.ProtectedDeviceToken),
        row.LastTestedAtUtc, row.LastTestSucceeded, row.LastTestCode);
}
