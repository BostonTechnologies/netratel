using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.Secrets;
using static NetRatel.Infrastructure.Secrets.SecretProviderValidation;

namespace NetRatel.Infrastructure.Secrets;

internal sealed partial class TenantSecretService
{
    public async Task<CredentialProfile[]> ListProfilesAsync(int tenantId, CancellationToken ct)
    {
        await RequireTenantAsync(tenantId, ct);
        var profiles = await db.CredentialProfiles.AsNoTracking().Where(x => x.TenantId == tenantId)
            .OrderBy(x => x.DisplayName).ThenBy(x => x.Id).ToArrayAsync(ct);
        return profiles.Select(Project).ToArray();
    }

    public async Task<CredentialProfile> GetProfileAsync(int tenantId, Guid profileId, CancellationToken ct) =>
        Project(await db.CredentialProfiles.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == profileId, ct) ?? throw Missing());

    public async Task<CredentialProfile> SaveProfileAsync(int tenantId, Guid? profileId, SaveCredentialProfileRequest request, CancellationToken ct)
    {
        var name = RequiredText(request.DisplayName, 128, "Credential name");
        var mapping = ValidateMapping(request);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await SecretConsumerQuery.LockTenantAsync(db, tenantId, ct);
        var row = profileId is { } id ? await GetProfileRecordAsync(tenantId, id, ct) : null;
        if (row is not null)
            CheckRevision(row.Revision, request.ExpectedRevision);
        if (mapping.UsernameReference is { VersionMode: SecretVersionMode.LatestAtExecution } username && mapping.Scope.AccountId is null)
        {
            var binding = await BindReferenceAsync(tenantId, username, ct);
            var version = binding.ExactVersion ?? throw Invalid("Select an exact version of the username reference.");
            mapping = mapping with { UsernameReference = binding.Reference with { VersionMode = SecretVersionMode.Pinned, Version = version } };
        }
        var serialized = JsonSerializer.Serialize(mapping, Json);
        if (row is null)
        {
            if (request.ExpectedRevision is not null)
                throw Invalid("A new credential has no expected revision.");
            row = new() { Id = Guid.NewGuid(), TenantId = tenantId, DisplayName = name, MappingJson = serialized };
            db.CredentialProfiles.Add(row);
        }
        if (row.MappingRevision == 0 || JsonSerializer.Serialize(Deserialize<ProfileMapping>(row.MappingJson), Json) != serialized)
        {
            var targetIds = mapping.Scope.AgentIds;
            var targets = await db.Agents.AsNoTracking().CountAsync(x => x.TenantId == tenantId && targetIds.Contains(x.Id), ct);
            if (targets != targetIds.Length)
                throw Invalid("Each execution target must be a current client in this tenant.");
            await ValidateProfileReferenceAsync(tenantId, mapping.UsernameReference, ct);
            await ValidateProfileReferenceAsync(tenantId, mapping.PasswordReference, ct);
            row.MappingJson = serialized;
            row.MappingRevision++;
            row.UsernameConnectionId = mapping.UsernameReference?.ConnectionId;
            row.UsernameLocator = mapping.UsernameReference?.Locator;
            row.PasswordConnectionId = mapping.PasswordReference?.ConnectionId;
            row.PasswordLocator = mapping.PasswordReference?.Locator;
        }
        row.DisplayName = name;
        row.Revision++;
        await SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Project(row);
    }

    public async Task DeleteProfileAsync(int tenantId, Guid profileId, long expectedRevision, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await SecretConsumerQuery.LockTenantAsync(db, tenantId, ct);
        var row = await GetProfileRecordAsync(tenantId, profileId, ct);
        CheckRevision(row.Revision, expectedRevision);
        db.CredentialProfiles.Remove(row);
        await SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    private async Task ValidateProfileReferenceAsync(int tenantId, SecretReference? reference, CancellationToken ct)
    {
        if (reference is null)
            return;
        var connection = await LoadAsync(tenantId, reference.ConnectionId, ct);
        ValidateReference(connection.Adapter, reference);
        var metadata = await connection.Adapter.GetMetadataAsync(connection.Context, reference.Locator, reference.Version, ct);
        if (reference.Field is not null && !metadata.Fields.Any(f => string.Equals(f.Name, reference.Field, StringComparison.Ordinal)))
            throw Invalid("Select an existing scalar field for this credential.");
    }

    private static ProfileMapping ValidateMapping(SaveCredentialProfileRequest request)
    {
        var username = OptionalText(request.PublicUsername, 256, "Public username");
        var domain = OptionalText(request.Domain, 255, "Windows domain");
        if ((username is null) == (request.UsernameReference is null))
            throw Invalid("Choose one username source.");
        if (request.Mode is not (ExecutionIdentityMode.WindowsPassword or ExecutionIdentityMode.UnixPrivilegedLocal))
            throw Invalid("Select a saved-credential identity mode.");

        if (request.Mode == ExecutionIdentityMode.WindowsPassword && request.PasswordReference is null)
            throw Invalid("Windows password mode requires a password reference.");
        if (request.Mode == ExecutionIdentityMode.UnixPrivilegedLocal && (domain is not null || request.PasswordReference is not null))
            throw Invalid("Unix privileged-local profiles use a local account. Remove the domain and password reference.");

        if (request.UsernameReference is { } user && request.PasswordReference is { } password &&
            user.ConnectionId == password.ConnectionId && user.Locator == password.Locator &&
            user.Field == password.Field)
            throw Invalid("Choose separate fields for the username and password.");

        var scope = request.Scope ?? throw Invalid("An execution scope is required.");
        if (scope.AgentIds is not { Length: > 0 and <= CredentialExecutionScope.MaximumClients } || scope.AgentIds.Contains(Guid.Empty))
            throw Invalid($"Select between 1 and {CredentialExecutionScope.MaximumClients} clients in this tenant.");
        if (!(scope.Jobs || scope.Terminals))
            throw Invalid("Select Jobs, Terminals, or both.");
        var accountId = OptionalText(scope.AccountId, 184, "Approved account ID");
        var windows = request.Mode == ExecutionIdentityMode.WindowsPassword;
        if (accountId is not null && (windows ? !ObservedExecutionIdentity.IsWindowsAccountId(accountId) : !ObservedExecutionIdentity.IsUnixAccountId(accountId)))
            throw Invalid(windows ? "Supply the approved Windows account SID." : "Supply the approved Unix numeric UID.");
        return new(request.Mode, username, request.UsernameReference, domain, request.PasswordReference,
            scope with { AccountId = accountId, AgentIds = scope.AgentIds.Distinct().Order().ToArray() });
    }

    private Task<CredentialProfileRecord> GetProfileRecordAsync(int tenantId, Guid profileId, CancellationToken ct) =>
        GetEntityAsync(db.CredentialProfiles, x => x.TenantId == tenantId && x.Id == profileId, ct);

    private static CredentialProfile Project(CredentialProfileRecord row)
    {
        var mapping = Deserialize<ProfileMapping>(row.MappingJson);
        return new(row.Id, row.DisplayName, row.Revision, row.MappingRevision, mapping.Mode, mapping.PublicUsername,
            mapping.UsernameReference, mapping.Domain, mapping.PasswordReference, mapping.Scope);
    }

    private sealed record ProfileMapping(ExecutionIdentityMode Mode, string? PublicUsername, SecretReference? UsernameReference,
        string? Domain, SecretReference? PasswordReference, CredentialExecutionScope Scope);
}
