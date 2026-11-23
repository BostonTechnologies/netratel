using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using NetRatel.Application.Events;
using NetRatel.Application.Secrets;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Shared.Contracts.Secrets;

namespace NetRatel.API.Endpoints;

internal static partial class SecretVaultEndpoints
{
    private static async Task<IResult> PreviewRemovalAsync(int tenantId, VaultRemovalRequest request, HttpContext http,
        IEffectiveAccessService access, ITenantSecretService vault, IDataProtectionProvider protection, TimeProvider clock, CancellationToken ct)
    {
        if (ValidateRemoval(request) is not { } permission)
            return InvalidReview();
        if (Actor(http) is not { Length: > 0 } actor)
            return Results.Forbid();
        return await WithPermissionAsync(http, access, tenantId, permission, async () =>
        {
            var (display, mode) = await DescribeRemovalAsync(tenantId, request, vault, ct);
            var expires = clock.GetUtcNow().AddMinutes(5);
            var plan = ReviewProtector(protection, http, actor, tenantId).Protect(JsonSerializer.Serialize(new RemovalReview(request, expires)));
            return Results.Ok(new VaultRemovalPreview(request, plan, expires, display, mode));
        }, ct);
    }

    private static async Task<IResult> ConfirmRemovalAsync(int tenantId, ConfirmVaultRemovalRequest confirmation, HttpContext http,
        IEffectiveAccessService access, ITenantSecretService vault, IDataProtectionProvider protection, TimeProvider clock, IEventRecorder audit, CancellationToken ct)
    {
        if (ValidateRemoval(confirmation.Request) is not { } permission || confirmation.ConfirmationToken is not { Length: > 0 and <= 8192 })
            return InvalidReview();
        if (Actor(http) is not { Length: > 0 } actor)
            return Results.Forbid();
        return await WithPermissionAsync(http, access, tenantId, permission, async () =>
        {
            var request = confirmation.Request;
            if (!MatchReview(ReviewProtector(protection, http, actor, tenantId), confirmation, clock.GetUtcNow()))
                return InvalidReview();
            SecretDeletionResult? result = null;
            switch (request.Kind)
            {
                case VaultRemovalKind.Secret:
                    result = await vault.DeleteAsync(tenantId, request.Id, request.Revision, request.Secret!, ct);
                    break;
                case VaultRemovalKind.DisableConnection:
                    await vault.SetConnectionEnabledAsync(tenantId, request.Id, request.Revision, enabled: false, ct);
                    break;
                case VaultRemovalKind.DeleteConnection:
                    await vault.DeleteConnectionAsync(tenantId, request.Id, request.Revision, ct);
                    break;
                case VaultRemovalKind.CredentialProfile:
                    await vault.DeleteProfileAsync(tenantId, request.Id, request.Revision, ct);
                    break;
            }
            await AuditAsync(audit, http, tenantId, "secret-vault.removal." + request.Kind, request.Id,
                result?.Locator, result?.Version, request.Revision, result?.Pending == true ? "Pending" : "Succeeded", ct);
            return Results.Ok(new VaultRemovalResult(result));
        }, ct);
    }

    private static async Task<(string Display, SecretDeletionMode? Mode)> DescribeRemovalAsync(int tenantId, VaultRemovalRequest request,
        ITenantSecretService vault, CancellationToken ct)
    {
        if (request.Kind == VaultRemovalKind.CredentialProfile)
        {
            var profile = await vault.GetProfileAsync(tenantId, request.Id, ct);
            if (request.Revision != profile.Revision)
                throw ReviewConflict();
            return (profile.DisplayName, null);
        }

        var connections = await vault.ListConnectionsAsync(tenantId, request.Kind != VaultRemovalKind.Secret, ct);
        var connection = connections.SingleOrDefault(x => x.Id == request.Id) ??
            throw new SecretProviderException(SecretProviderFailure.NotFound, "The vault connection was not found.");
        if (request.Revision != connection.Revision)
            throw ReviewConflict();
        if (request.Kind != VaultRemovalKind.Secret)
        {
            if (connection.Kind == SecretProviderKind.Local || connection.IsDefault)
                throw new SecretProviderException(SecretProviderFailure.InvalidRequest, "Keep Local available and select another default before removing a connection.");
            return (connection.DisplayName, null);
        }

        var deletion = request.Secret!;
        var metadata = await vault.GetMetadataAsync(tenantId, connection.Id, deletion.Locator, null, ct);
        if (metadata.UsedBy.Length > 0)
            throw new SecretProviderException(SecretProviderFailure.InUse, "The secret has saved consumers. Review its usage before deleting it.");
        if (deletion.ExpectedVersion != metadata.Entry.Version || deletion.ExpectedRevision != metadata.Entry.Revision)
            throw ReviewConflict();
        return (metadata.Entry.Name, connection.Capabilities.Deletion);
    }

    private static string? ValidateRemoval(VaultRemovalRequest? request)
    {
        if (request is null || request.Id == Guid.Empty || request.Revision < 1 || (request.Kind == VaultRemovalKind.Secret) != (request.Secret is not null))
            return null;
        if (request.Secret is { } secret && (secret.Locator is not { Length: > 0 and <= 1024 } ||
            secret.ExpectedRevision is not { Length: > 0 and <= 256 } || secret.ExpectedVersion is not { Length: > 0 and <= 128 }))
            return null;
        return request.Kind switch
        {
            VaultRemovalKind.Secret => NetRatelPermissions.SecretManage,
            VaultRemovalKind.DisableConnection or VaultRemovalKind.DeleteConnection => NetRatelPermissions.SecretProviderAdministration,
            VaultRemovalKind.CredentialProfile => NetRatelPermissions.CredentialProfileManagement,
            _ => null
        };
    }

    private static IDataProtector ReviewProtector(IDataProtectionProvider protection, HttpContext http, string actor, int tenantId) =>
        protection.CreateProtector("NetRatel.SecretVault.RemovalReview.v1", tenantId.ToString(CultureInfo.InvariantCulture), actor,
            http.User.FindFirstValue("netratel_integration_credential_id") ?? "account");

    private static bool MatchReview(IDataProtector protector, ConfirmVaultRemovalRequest confirmation, DateTimeOffset now)
    {
        try
        {
            return JsonSerializer.Deserialize<RemovalReview>(protector.Unprotect(confirmation.ConfirmationToken)) is { } review &&
                review.Request == confirmation.Request && review.ExpiresAtUtc > now;
        }
        catch (CryptographicException) { return false; }
        catch (JsonException) { return false; }
    }

    private static SecretProviderException ReviewConflict() => new(SecretProviderFailure.Conflict, "The object changed. Refresh it and review the removal again.");
    private static IResult InvalidReview() => Results.BadRequest(new SecretVaultError("InvalidReview", "Preview this exact change and confirm before the review expires."));
    private sealed record RemovalReview(VaultRemovalRequest Request, DateTimeOffset ExpiresAtUtc);
}
