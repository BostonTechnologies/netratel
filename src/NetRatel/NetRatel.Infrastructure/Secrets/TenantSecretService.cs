using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Secrets;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.Secrets;
using static NetRatel.Infrastructure.Secrets.SecretProviderValidation;

namespace NetRatel.Infrastructure.Secrets;

internal sealed partial class TenantSecretService(OrchestratorDbContext db, IDataProtectionProvider protection,
    IEnumerable<ISecretProvider> adapters, SecretProviderNetworkPolicy network, TimeProvider clock) : ITenantSecretService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly Dictionary<SecretProviderKind, ISecretProvider> _adapters = adapters.ToDictionary(x => x.Kind);

    // A reserved namespace keeps the built-in connection stable across upgrades, backups and replicas.
    internal static Guid LocalConnectionId(int tenantId) => new($"b59b9b79-2e0c-4d0b-950c-{tenantId.ToString("D12", CultureInfo.InvariantCulture)}");

    public async Task<SecretProviderConnection[]> ListConnectionsAsync(int tenantId, bool includeAdministration, CancellationToken ct)
    {
        await RequireTenantAsync(tenantId, ct);
        var records = await db.SecretProviderConnections.AsNoTracking().Where(x => x.TenantId == tenantId && (includeAdministration || x.Enabled))
            .OrderBy(x => x.DisplayName).ThenBy(x => x.Id).ToArrayAsync(ct);
        return [Local(tenantId, !records.Any(x => x.IsDefault)), .. records.Select(x => Project(x, includeAdministration))];
    }

    public async Task<SecretProviderConnection> SaveConnectionAsync(int tenantId, Guid? connectionId, SaveSecretProviderRequest request, CancellationToken ct)
    {
        var name = RequiredText(request.DisplayName, 128, "Connection name");
        var configuration = network.Normalize(request.Kind, request.Configuration ?? throw Invalid("Provider configuration is required."));
        if (!configuration.ManagedIdentity)
            network.RequireProtectedAuthenticationStorage();
        _ = Adapter(request.Kind);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await SecretConsumerQuery.LockTenantAsync(db, tenantId, ct);
        var row = connectionId is { } id ? await GetConnectionRecordAsync(tenantId, id, ct) : null;
        if (row is not null)
        {
            CheckRevision(row.Revision, request.ExpectedRevision);
            if (row.Kind != request.Kind || Deserialize<SecretProviderConfiguration>(row.ConfigurationJson) != configuration)
                throw Invalid("Create a replacement connection to change the provider origin, identity or scope. Rebind its consumers explicitly.");
        }
        else
        {
            if (request.ExpectedRevision is not null)
                throw Invalid("A new connection has no expected revision.");
            row = new()
            {
                Id = Guid.NewGuid(), TenantId = tenantId, Kind = request.Kind, Enabled = true, ScopeRevision = 1,
                DisplayName = name, ConfigurationJson = JsonSerializer.Serialize(configuration, Json)
            };
            db.SecretProviderConnections.Add(row);
        }

        if (row.Revision == 0 || request.ClientSecret is not null || request.RoleId is not null || request.SecretId is not null)
        {
            var authentication = ApplyAuthentication(request, configuration, ReadAuthentication(row));
            row.ProtectedAuthentication = authentication is null ? null : AuthenticationProtector(row).Protect(JsonSerializer.Serialize(authentication, Json));
            row.AuthenticationRevision++;
            row.LastTestJson = null;
        }
        row.Revision++;
        row.DisplayName = name;
        await SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return Project(row, includeAdministration: true);
    }

    public async Task<SecretProviderConnection> SetDefaultAsync(int tenantId, Guid connectionId, SelectDefaultSecretProviderRequest request, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await SecretConsumerQuery.LockTenantAsync(db, tenantId, ct);
        var current = await db.SecretProviderConnections.SingleOrDefaultAsync(x => x.TenantId == tenantId && x.IsDefault, ct);
        var localId = LocalConnectionId(tenantId);
        if ((current?.Id ?? localId) != request.ExpectedDefaultId)
            throw Conflict();
        var selected = connectionId == localId ? null : await GetConnectionRecordAsync(tenantId, connectionId, ct);
        CheckRevision(selected?.Revision ?? 1, request.ExpectedConnectionRevision);
        if (selected is { Enabled: false })
            throw Disabled();
        if (current?.Id != selected?.Id)
        {
            if (current is not null)
            {
                current.IsDefault = false;
                current.Revision++;
                await SaveChangesAsync(ct);
            }
            if (selected is not null)
            {
                selected.IsDefault = true;
                selected.Revision++;
                await SaveChangesAsync(ct);
            }
        }
        await transaction.CommitAsync(ct);
        return selected is null ? Local(tenantId, isDefault: true) : Project(selected, includeAdministration: true);
    }

    public async Task<SecretProviderConnection> SetConnectionEnabledAsync(int tenantId, Guid connectionId, long expectedRevision, bool enabled, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await SecretConsumerQuery.LockTenantAsync(db, tenantId, ct);
        var row = await GetConnectionRecordAsync(tenantId, connectionId, ct);
        CheckRevision(row.Revision, expectedRevision);
        if (row.Enabled != enabled)
        {
            if (!enabled)
                await EnsureConnectionUnusedAsync(row, ct);
            row.Enabled = enabled;
            row.Revision++;
            row.ScopeRevision++;
            row.LastTestJson = null;
            await SaveChangesAsync(ct);
        }
        await transaction.CommitAsync(ct);
        return Project(row, includeAdministration: true);
    }

    public async Task DeleteConnectionAsync(int tenantId, Guid connectionId, long expectedRevision, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await SecretConsumerQuery.LockTenantAsync(db, tenantId, ct);
        var row = await GetConnectionRecordAsync(tenantId, connectionId, ct);
        CheckRevision(row.Revision, expectedRevision);
        await EnsureConnectionUnusedAsync(row, ct);
        db.SecretProviderConnections.Remove(row);
        await SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    public async Task<SecretProviderTestResult> TestConnectionAsync(int tenantId, Guid connectionId, long expectedRevision, CancellationToken ct)
    {
        var connection = await LoadAsync(tenantId, connectionId, ct);
        CheckRevision(connection.Revision, expectedRevision);
        SecretProviderTestResult result;
        try
        {
            var listed = await connection.Adapter.TestMetadataAsync(connection.Context, ct);
            result = new(clock.GetUtcNow(), listed, listed ? null : "MetadataNotObserved");
        }
        catch (SecretProviderException error) { result = new(clock.GetUtcNow(), false, error.Reason.ToString()); }

        if (connection.Adapter.Kind == SecretProviderKind.Local)
            return result;
        var json = JsonSerializer.Serialize(result, Json);
        var saved = await db.SecretProviderConnections.Where(x => x.TenantId == tenantId && x.Id == connectionId && x.Revision == expectedRevision)
            .ExecuteUpdateAsync(set => set.SetProperty(x => x.LastTestJson, json), ct);
        if (saved != 1)
            throw Conflict();
        return result;
    }

    public async Task<SecretBrowsePage> BrowseAsync(int tenantId, Guid connectionId, SecretBrowseQuery query, CancellationToken ct)
    {
        if (query.Limit is < 1 or > MaximumBrowseLimit || query.Cursor?.Length > 8192)
            throw Invalid("Supply a valid page size and continuation.");
        var connection = await LoadAsync(tenantId, connectionId, ct);
        var folder = OptionalText(query.Folder, 1024, "Folder");
        var search = OptionalText(query.Search, 256, "Search");
        var protector = protection.CreateProtector("NetRatel.SecretProvider.BrowseCursor.v1", TenantKey(tenantId), connectionId.ToString(),
            connection.Revision.ToString(CultureInfo.InvariantCulture), folder ?? "", search ?? "", query.Limit.ToString(CultureInfo.InvariantCulture));
        var cursor = query.Cursor is null ? null : ReadCursor(protector, query.Cursor);
        var page = await connection.Adapter.BrowseAsync(connection.Context, new(folder, search, cursor, query.Limit), ct);
        var next = page.NextCursor is null ? null : protector.Protect(JsonSerializer.Serialize(new BrowseCursor(clock.GetUtcNow().AddMinutes(10), page.NextCursor), Json));
        return page with { NextCursor = next };
    }

    public async Task<SecretEntryDetails> GetMetadataAsync(int tenantId, Guid connectionId, string locator, string? exactVersion, CancellationToken ct)
    {
        var connection = await LoadAsync(tenantId, connectionId, ct);
        locator = connection.Adapter.ValidateLocator(locator);
        if (exactVersion is not null)
            connection.Adapter.ValidateVersion(exactVersion);
        var entry = await connection.Adapter.GetMetadataAsync(connection.Context, locator, exactVersion, ct);
        var consumers = await SecretConsumerQuery.FindConsumersAsync(db, tenantId, connectionId, locator, ct);
        return entry with { UsedBy = consumers };
    }

    public async Task<SecretEntryMetadata> WriteAsync(int tenantId, Guid connectionId, WriteSecretEntryRequest request, CancellationToken ct)
    {
        var connection = await LoadAsync(tenantId, connectionId, ct);
        if (request.Locator is { } locator)
            _ = connection.Adapter.ValidateLocator(locator);
        return await connection.Adapter.WriteAsync(connection.Context, request, ct);
    }

    public async Task<SecretDeletionResult> DeleteAsync(int tenantId, Guid connectionId, long expectedConnectionRevision, DeleteSecretEntryRequest request, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await SecretConsumerQuery.LockTenantAsync(db, tenantId, ct);
        var connection = await LoadAsync(tenantId, connectionId, ct);
        CheckRevision(connection.Revision, expectedConnectionRevision);
        var locator = connection.Adapter.ValidateLocator(request.Locator);
        if (await SecretConsumerQuery.HasConsumersAsync(db, tenantId, connectionId, locator, ct))
            throw InUse();
        var deleted = await connection.Adapter.DeleteAsync(connection.Context, request, ct);
        await transaction.CommitAsync(ct);
        return deleted;
    }

    public async Task<RevealedSecret> RevealAsync(int tenantId, SecretReference reference, CancellationToken ct)
    {
        var resolved = (await ResolveForExecutionAsync(tenantId, [reference], ct))[0];
        return new(resolved.ExactReference.Version!, resolved.Value);
    }

    public async Task<IReadOnlyList<ResolvedSecret>> ResolveForExecutionAsync(int tenantId, IReadOnlyList<SecretReference> references, CancellationToken ct)
    {
        if (references.Count is < 1 or > 128)
            throw Invalid("Supply between 1 and 128 secret references.");
        var result = new ResolvedSecret[references.Count];
        foreach (var providerGroup in references.Select((reference, index) => (reference, index)).GroupBy(x => x.reference.ConnectionId))
        {
            var connection = await LoadAsync(tenantId, providerGroup.Key, ct);
            foreach (var binding in providerGroup)
                ValidateReference(connection.Adapter, binding.reference);
            foreach (var entryGroup in providerGroup.GroupBy(x => (x.reference.Locator, x.reference.Version)))
            {
                var bindings = entryGroup.ToArray();
                var values = await connection.Adapter.ResolveAsync(connection.Context, entryGroup.Key.Locator, entryGroup.Key.Version,
                    bindings.Select(x => x.reference.Field).ToArray(), ct);
                for (var i = 0; i < bindings.Length; i++)
                {
                    var (reference, index) = bindings[i];
                    result[index] = new(reference with { VersionMode = SecretVersionMode.Pinned, Version = values.Version },
                        connection.ScopeRevision, values.Values[i]);
                }
            }
        }
        return result;
    }

    private async Task<LoadedConnection> LoadAsync(int tenantId, Guid id, CancellationToken ct)
    {
        if (tenantId <= 0)
            throw Missing();
        if (id == LocalConnectionId(tenantId))
        {
            await RequireTenantAsync(tenantId, ct);
            return new(Adapter(SecretProviderKind.Local), new(tenantId, id, 0, null, () => null), 1, 1);
        }
        var row = await db.SecretProviderConnections.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == id, ct) ?? throw Missing();
        if (!row.Enabled)
            throw Disabled();
        var config = Deserialize<SecretProviderConfiguration>(row.ConfigurationJson);
        return new(Adapter(row.Kind), new(tenantId, id, row.AuthenticationRevision, config, () => ReadAuthentication(row)), row.Revision, row.ScopeRevision);
    }

    private Task<SecretProviderConnectionRecord> GetConnectionRecordAsync(int tenantId, Guid id, CancellationToken ct) =>
        GetEntityAsync(db.SecretProviderConnections, x => x.TenantId == tenantId && x.Id == id, ct);

    private async Task RequireTenantAsync(int tenantId, CancellationToken ct)
    {
        if (tenantId <= 0 || !await db.Tenants.AsNoTracking().AnyAsync(x => x.Id == tenantId, ct))
            throw Missing();
    }

    private async Task EnsureConnectionUnusedAsync(SecretProviderConnectionRecord row, CancellationToken ct)
    {
        if (row.IsDefault)
            throw Invalid("Select another default provider before disabling or deleting this connection.");
        if (await SecretConsumerQuery.HasConsumersAsync(db, row.TenantId, row.Id, locator: null, ct))
            throw InUse();
    }

    private static void ValidateReference(ISecretProvider adapter, SecretReference reference)
    {
        if (reference.SchemaVersion != SecretReference.CurrentSchemaVersion || !Enum.IsDefined(reference.VersionMode) ||
            (reference.VersionMode == SecretVersionMode.Pinned) != (reference.Version is not null))
            throw Invalid("Supply a supported Latest or Pinned secret reference.");
        if (adapter.ValidateLocator(reference.Locator) != reference.Locator)
            throw Invalid("Select the canonical secret locator.");
        if (reference.Version is { } version)
            adapter.ValidateVersion(version);
        if (adapter.Capabilities.StructuredFields)
        {
            if (RequiredText(reference.Field, 256, "Secret field") != reference.Field)
                throw Invalid("Select the exact secret field name.");
        }
        else if (reference.Field is not null)
            throw Invalid("Scalar secrets have no field selector.");
    }

    private SecretProviderConnection Project(SecretProviderConnectionRecord row, bool includeAdministration) => new(
        row.Id, row.Kind, row.DisplayName, row.Revision, row.ScopeRevision, row.Enabled, row.IsDefault,
        includeAdministration ? Deserialize<SecretProviderConfiguration>(row.ConfigurationJson) : null,
        includeAdministration && row.ProtectedAuthentication is not null, Adapter(row.Kind).Capabilities,
        includeAdministration && row.LastTestJson is { } test ? Deserialize<SecretProviderTestResult>(test) : null);

    private SecretProviderConnection Local(int tenantId, bool isDefault) => new(LocalConnectionId(tenantId), SecretProviderKind.Local,
        "Local", 1, 1, true, isDefault, null, false, Adapter(SecretProviderKind.Local).Capabilities, null);

    private ISecretProvider Adapter(SecretProviderKind kind) => _adapters.TryGetValue(kind, out var adapter) ? adapter : throw Unavailable();

    private IDataProtector AuthenticationProtector(SecretProviderConnectionRecord row) => protection.CreateProtector(
        "NetRatel.SecretProvider.Bootstrap.v1", TenantKey(row.TenantId), row.Id.ToString(), row.Kind.ToString());

    private ProviderAuthentication? ReadAuthentication(SecretProviderConnectionRecord row)
    {
        if (row.ProtectedAuthentication is not { } encrypted)
            return null;
        try { return Deserialize<ProviderAuthentication>(AuthenticationProtector(row).Unprotect(encrypted)); }
        catch (CryptographicException)
        {
            throw new SecretProviderException(SecretProviderFailure.KeyUnavailable, "Provider authentication keys are unavailable. Restore the matching API key ring.");
        }
    }

    private static ProviderAuthentication? ApplyAuthentication(SaveSecretProviderRequest request, SecretProviderConfiguration config, ProviderAuthentication? stored)
    {
        if (request.Kind == SecretProviderKind.OpenBao)
        {
            if (request.ClientSecret is not null)
                throw Invalid("OpenBao authentication requires AppRole fields.");
            return new() { RoleId = AuthenticationValue(request.RoleId ?? stored?.RoleId), SecretId = AuthenticationValue(request.SecretId ?? stored?.SecretId) };
        }
        if (request.RoleId is not null || request.SecretId is not null)
            throw Invalid("AppRole fields apply only to OpenBao.");
        if (config.ManagedIdentity)
        {
            if (request.ClientSecret is not null)
                throw Invalid("Managed identity uses the configured Azure identity.");
            return null;
        }
        return new() { ClientSecret = AuthenticationValue(request.ClientSecret ?? stored?.ClientSecret) };
    }

    private static string AuthenticationValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 8192)
            throw Invalid("Supply the required provider authentication fields.");
        return value;
    }

    private string ReadCursor(IDataProtector protector, string encoded)
    {
        try
        {
            var cursor = Deserialize<BrowseCursor>(protector.Unprotect(encoded));
            return cursor.ExpiresAtUtc > clock.GetUtcNow() ? cursor.Cursor : throw Invalid("The page expired. Refresh the provider browser.");
        }
        catch (CryptographicException) { throw Invalid("The page changed. Refresh the provider browser."); }
    }

    private async Task SaveChangesAsync(CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { throw Conflict(); }
    }

    private static async Task<TEntity> GetEntityAsync<TEntity>(DbSet<TEntity> rows,
        System.Linq.Expressions.Expression<Func<TEntity, bool>> predicate, CancellationToken ct) where TEntity : class =>
        await rows.SingleOrDefaultAsync(predicate, ct) ?? throw Missing();

    private static void CheckRevision(long current, long? expected)
    {
        if (expected != current)
            throw Conflict();
    }

    private static T Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Json) ?? throw Unavailable();
    private static SecretProviderException Disabled() => new(SecretProviderFailure.Disabled, "The provider connection is disabled.");
    private static string TenantKey(int tenantId) => tenantId.ToString(CultureInfo.InvariantCulture);

    private sealed record LoadedConnection(ISecretProvider Adapter, SecretProviderContext Context, long Revision, long ScopeRevision);
    private sealed record BrowseCursor(DateTimeOffset ExpiresAtUtc, string Cursor);
}
