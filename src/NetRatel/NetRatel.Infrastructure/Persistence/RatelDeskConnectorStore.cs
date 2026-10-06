using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NetRatel.Application.RatelDesk;
using NetRatel.Shared.Contracts.RatelDesk;
using Npgsql;

namespace NetRatel.Infrastructure.Persistence;

public sealed class RatelDeskConnectorStore(OrchestratorDbContext db) : IRatelDeskConnectorStore, IRatelDeskConnectorBindingStore, IRatelDeskConnectorReadinessStore
{
    public async Task<RatelDeskConnectorState?> GetAsync(int tenantId, Guid id, CancellationToken cancellationToken)
    {
        var record = await db.RatelDeskConnectors.AsNoTracking().SingleOrDefaultAsync(r => r.TenantId == tenantId && r.Id == id, cancellationToken).ConfigureAwait(false);
        return record is null ? null : Map(record);
    }

    public async Task<IReadOnlyList<RatelDeskConnectorState>> ListAsync(int tenantId, CancellationToken cancellationToken)
    {
        var records = await db.RatelDeskConnectors.AsNoTracking().Where(r => r.TenantId == tenantId).OrderBy(r => r.Id)
            .Take(RatelDeskConnectorLimits.MaximumConnectorsPerTenant + 1).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (records.Count > RatelDeskConnectorLimits.MaximumConnectorsPerTenant)
            throw new InvalidOperationException("connector-capacity-exhausted");
        return records.Select(Map).ToArray();
    }

    public async Task<bool> SaveAsync(RatelDeskConnectorState state, long expectedRowVersion, CancellationToken cancellationToken)
    {
        if (state.TenantId <= 0 || state.Id == Guid.Empty || state.RowVersion != checked(expectedRowVersion + 1) || state.Revision <= 0)
            throw new ArgumentException("invalid-connector-state");
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false) : null;
        // Serialize different-ID admission before its lookup/count/insert; row CAS alone cannot bound a tenant aggregate.
        if (expectedRowVersion == 0 && db.Database.IsNpgsql())
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({733460001}, {state.TenantId})", cancellationToken).ConfigureAwait(false);
        var record = await db.RatelDeskConnectors.SingleOrDefaultAsync(r => r.TenantId == state.TenantId && r.Id == state.Id, cancellationToken).ConfigureAwait(false);
        var configurationJson = JsonSerializer.Serialize(state.Configuration);
        var authentication = state.Authentication ?? new(RatelDeskAuthenticationMode.ManualApiBearer, null);
        var authenticationJson = state.Authentication is null ? null : JsonSerializer.Serialize(authentication);
        if (record is null && expectedRowVersion != 0 || record is not null &&
            (record.RowVersion != expectedRowVersion || record.OwnerPrincipalId != state.OwnerPrincipalId || state.Revision < record.Revision || state.CredentialRevision < record.CredentialRevision))
            return false;
        if (record is not null && (state.Revision > record.Revision + 1 ||
            state.Revision == record.Revision && configurationJson != JsonSerializer.Serialize(
                JsonSerializer.Deserialize<RatelDeskConnectorConfiguration>(record.ConfigurationJson) ?? throw new InvalidOperationException("invalid-connector-record")) ||
            state.Revision == record.Revision && JsonSerializer.Serialize(authentication) != JsonSerializer.Serialize(
                record.AuthenticationJson is null ? new RatelDeskConnectorAuthentication(RatelDeskAuthenticationMode.ManualApiBearer, null) :
                JsonSerializer.Deserialize<RatelDeskConnectorAuthentication>(record.AuthenticationJson) ?? throw new InvalidOperationException("invalid-connector-authentication")) ||
            state.CredentialRevision == record.CredentialRevision && state.ProtectedCredential != record.ProtectedCredential)) return false;
        if (record is null)
        {
            if (await db.RatelDeskConnectors.CountAsync(r => r.TenantId == state.TenantId, cancellationToken).ConfigureAwait(false) >= RatelDeskConnectorLimits.MaximumConnectorsPerTenant)
                throw new InvalidOperationException("connector-capacity-exhausted");
            record = new() { TenantId = state.TenantId, Id = state.Id, OwnerPrincipalId = state.OwnerPrincipalId };
            db.RatelDeskConnectors.Add(record);
        }
        record.ConfigurationJson = configurationJson;
        record.Revision = state.Revision; record.RowVersion = state.RowVersion;
        record.ProtectedCredential = state.ProtectedCredential; record.CredentialRevision = state.CredentialRevision;
        record.AuthenticationJson = authenticationJson;
        record.ReadinessJson = state.Readiness is null ? null : JsonSerializer.Serialize(state.Readiness);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            if (transaction is not null) await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateConcurrencyException) { db.Entry(record).State = EntityState.Detached; return false; }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "PK_RatelDeskConnectors" })
        { db.Entry(record).State = EntityState.Detached; return false; }
    }

    public async Task<RatelDeskConnectorAuthentication> GetAuthenticationAsync(int tenantId, Guid connectorId, CancellationToken ct)
    {
        var current = await GetAsync(tenantId, connectorId, ct).ConfigureAwait(false) ?? throw new UnauthorizedAccessException("connector-not-found");
        return current.Authentication ?? new(RatelDeskAuthenticationMode.ManualApiBearer, null);
    }

    public Task<bool> SaveReadinessAsync(RatelDeskConnectorState current, RatelDeskReadinessObservation observation, CancellationToken ct)
    {
        if (observation.ConnectorRevision != current.Revision || observation.Peer.ConnectorId != current.Id ||
            observation.Peer.LocalTenantId != current.TenantId) throw new ArgumentException("invalid-connector-readiness");
        return SaveAsync(current with { RowVersion = checked(current.RowVersion + 1), Readiness = observation }, current.RowVersion, ct);
    }

    public Task<bool> ClearReadinessAsync(RatelDeskConnectorState current, CancellationToken ct) =>
        SaveAsync(current with { RowVersion = checked(current.RowVersion + 1), Readiness = null }, current.RowVersion, ct);

    private static RatelDeskConnectorState Map(RatelDeskConnectorRecord r) => new(r.Id, r.TenantId, r.Revision, r.RowVersion,
        r.OwnerPrincipalId, JsonSerializer.Deserialize<RatelDeskConnectorConfiguration>(r.ConfigurationJson) ?? throw new InvalidOperationException("invalid-connector-record"),
        r.ProtectedCredential, r.CredentialRevision,
        r.AuthenticationJson is null ? null :
            JsonSerializer.Deserialize<RatelDeskConnectorAuthentication>(r.AuthenticationJson) ?? throw new InvalidOperationException("invalid-connector-authentication"),
        r.ReadinessJson is null ? null : JsonSerializer.Deserialize<RatelDeskReadinessObservation>(r.ReadinessJson) ?? throw new InvalidOperationException("invalid-connector-readiness"));
}
