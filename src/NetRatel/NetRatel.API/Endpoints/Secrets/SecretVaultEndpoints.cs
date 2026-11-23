using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using NetRatel.API.Security;
using NetRatel.API.Security.Authorization;
using NetRatel.Application.Events;
using NetRatel.Application.Secrets;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Shared.Contracts.Secrets;

namespace NetRatel.API.Endpoints;

internal static partial class SecretVaultEndpoints
{
    public static void MapSecretVaultEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/tenants/{tenantId:int}/secret-vault").WithTags("Secret vault")
            .RequireAuthorization().AddEndpointFilter(LocalCookieMutationFilter.InvokeAsync);
        group.MapGet("/permissions", GetPermissionsAsync);
        group.MapGet("/connections", ListConnectionsAsync);
        group.MapPost("/connections", SaveNewConnectionAsync);
        group.MapPut("/connections/{connectionId:guid}", SaveConnectionAsync);
        group.MapPut("/connections/{connectionId:guid}/default", SetDefaultAsync);
        group.MapPost("/connections/{connectionId:guid}/enable", EnableConnectionAsync);
        group.MapPost("/connections/{connectionId:guid}/test", TestConnectionAsync);
        group.MapGet("/connections/{connectionId:guid}/entries", BrowseAsync);
        group.MapGet("/connections/{connectionId:guid}/entry", GetEntryAsync);
        group.MapPut("/connections/{connectionId:guid}/entry", WriteEntryAsync);
        group.MapPost("/reveal", RevealAsync);
        group.MapGet("/profiles", ListProfilesAsync);
        group.MapPost("/profiles", SaveNewProfileAsync);
        group.MapPut("/profiles/{profileId:guid}", SaveProfileAsync);
        group.MapPost("/removals/preview", PreviewRemovalAsync);
        group.MapPost("/removals/confirm", ConfirmRemovalAsync);
    }

    private static async Task<IResult> GetPermissionsAsync(int tenantId, HttpContext http, IEffectiveAccessService access, CancellationToken ct)
    {
        if (!Supported(http, tenantId))
            return Results.Forbid();
        var snapshot = await access.GetSnapshotAsync(http.User, tenantId, ct);
        bool Allowed(string name) => snapshot.IsLegacyOperator || snapshot.IsInstanceAdministrator || snapshot.Permissions.Contains(name);
        return Results.Ok(new SecretVaultPermissions(Allowed(NetRatelPermissions.SecretBrowse), Allowed(NetRatelPermissions.SecretUse),
            IsHuman(http) && Allowed(NetRatelPermissions.SecretReveal), Allowed(NetRatelPermissions.SecretManage),
            Allowed(NetRatelPermissions.SecretProviderAdministration), Allowed(NetRatelPermissions.CredentialProfileManagement), Allowed(NetRatelPermissions.RunAs)));
    }

    private static Task<IResult> ListConnectionsAsync(int tenantId, HttpContext http, IEffectiveAccessService access, ITenantSecretService vault,
        CancellationToken ct) => ExecuteAsync(async () =>
        {
            var admin = await AllowsAsync(http, access, tenantId, NetRatelPermissions.SecretProviderAdministration, ct);
            if (!admin && !await AllowsAsync(http, access, tenantId, NetRatelPermissions.SecretBrowse, ct))
                return Results.Forbid();
            return Results.Ok(await vault.ListConnectionsAsync(tenantId, admin, ct));
        });

    private static Task<IResult> SaveNewConnectionAsync(int tenantId, SaveSecretProviderRequest request, HttpContext http,
        IEffectiveAccessService access, ITenantSecretService vault, IEventRecorder audit, CancellationToken ct) => SaveConnectionCoreAsync(
        tenantId, null, request, http, access, vault, audit, ct);

    private static Task<IResult> SaveConnectionAsync(int tenantId, Guid connectionId, SaveSecretProviderRequest request, HttpContext http,
        IEffectiveAccessService access, ITenantSecretService vault, IEventRecorder audit, CancellationToken ct) => SaveConnectionCoreAsync(
        tenantId, connectionId, request, http, access, vault, audit, ct);

    private static Task<IResult> SaveConnectionCoreAsync(int tenantId, Guid? connectionId, SaveSecretProviderRequest request, HttpContext http,
        IEffectiveAccessService access, ITenantSecretService vault, IEventRecorder audit, CancellationToken ct) => WithPermissionAsync(http, access,
        tenantId, NetRatelPermissions.SecretProviderAdministration, async () =>
        {
            var saved = await vault.SaveConnectionAsync(tenantId, connectionId, request, ct);
            await AuditAsync(audit, http, tenantId, "secret-vault.connection.saved", saved.Id, revision: saved.Revision, ct: ct);
            return Results.Ok(saved);
        }, ct);

    private static Task<IResult> SetDefaultAsync(int tenantId, Guid connectionId, SelectDefaultSecretProviderRequest request, HttpContext http,
        IEffectiveAccessService access, ITenantSecretService vault, IEventRecorder audit, CancellationToken ct) => WithPermissionAsync(http, access,
        tenantId, NetRatelPermissions.SecretProviderAdministration, async () =>
        {
            var saved = await vault.SetDefaultAsync(tenantId, connectionId, request, ct);
            await AuditAsync(audit, http, tenantId, "secret-vault.connection.default", saved.Id, revision: saved.Revision, ct: ct);
            return Results.Ok(saved);
        }, ct);

    private static Task<IResult> EnableConnectionAsync(int tenantId, Guid connectionId, EnableSecretProviderRequest request, HttpContext http,
        IEffectiveAccessService access, ITenantSecretService vault, IEventRecorder audit, CancellationToken ct) => WithPermissionAsync(http, access,
        tenantId, NetRatelPermissions.SecretProviderAdministration, async () =>
        {
            var saved = await vault.SetConnectionEnabledAsync(tenantId, connectionId, request.ExpectedRevision, true, ct);
            await AuditAsync(audit, http, tenantId, "secret-vault.connection.enabled", saved.Id, revision: saved.Revision, ct: ct);
            return Results.Ok(saved);
        }, ct);

    private static Task<IResult> TestConnectionAsync(int tenantId, Guid connectionId, EnableSecretProviderRequest request, HttpContext http,
        IEffectiveAccessService access, ITenantSecretService vault, IEventRecorder audit, CancellationToken ct) => WithPermissionAsync(http, access,
        tenantId, NetRatelPermissions.SecretProviderAdministration, async () =>
        {
            var result = await vault.TestConnectionAsync(tenantId, connectionId, request.ExpectedRevision, ct);
            await AuditAsync(audit, http, tenantId, "secret-vault.connection.tested", connectionId, revision: request.ExpectedRevision,
                result: result.ErrorCode ?? "MetadataListed", ct: ct);
            return Results.Ok(result);
        }, ct);

    private static Task<IResult> BrowseAsync(int tenantId, Guid connectionId, [AsParameters] VaultBrowseParameters query, HttpContext http,
        IEffectiveAccessService access, ITenantSecretService vault, CancellationToken ct) => WithPermissionAsync(http, access,
        tenantId, NetRatelPermissions.SecretBrowse, async () => Results.Ok(await vault.BrowseAsync(tenantId, connectionId,
            new(query.Folder, query.Search, query.Cursor, query.Limit ?? 50), ct)), ct);

    private static Task<IResult> GetEntryAsync(int tenantId, Guid connectionId, [FromQuery] string locator, [FromQuery] string? version,
        HttpContext http, IEffectiveAccessService access, ITenantSecretService vault, CancellationToken ct) => WithPermissionAsync(http, access,
        tenantId, NetRatelPermissions.SecretBrowse, async () => Results.Ok(await vault.GetMetadataAsync(tenantId, connectionId, locator, version, ct)), ct);

    private static Task<IResult> WriteEntryAsync(int tenantId, Guid connectionId, WriteSecretEntryRequest request, HttpContext http,
        IEffectiveAccessService access, ITenantSecretService vault, IEventRecorder audit, CancellationToken ct) => WithPermissionAsync(http, access,
        tenantId, NetRatelPermissions.SecretManage, async () =>
        {
            var saved = await vault.WriteAsync(tenantId, connectionId, request, ct);
            await AuditAsync(audit, http, tenantId, "secret-vault.entry.saved", connectionId, saved.Locator, saved.Version, ct: ct);
            return Results.Ok(saved);
        }, ct);

    private static Task<IResult> RevealAsync(int tenantId, SecretReference request, HttpContext http, IEffectiveAccessService access,
        ITenantSecretService vault, IEventRecorder audit, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";
        return WithPermissionAsync(http, access, tenantId, NetRatelPermissions.SecretReveal, async () =>
        {
            if (!IsHuman(http))
                return Results.Forbid();
            var revealed = await vault.RevealAsync(tenantId, request, ct);
            await AuditAsync(audit, http, tenantId, "secret-vault.entry.revealed", request.ConnectionId, request.Locator, revealed.Version, ct: ct);
            return Results.Ok(revealed);
        }, ct);
    }

    private static Task<IResult> ListProfilesAsync(int tenantId, HttpContext http, IEffectiveAccessService access, ITenantSecretService vault,
        CancellationToken ct) => WithPermissionAsync(http, access, tenantId, NetRatelPermissions.SecretBrowse, async () =>
        {
            if (!await AllowsAsync(http, access, tenantId, NetRatelPermissions.CredentialProfileManagement, ct) &&
                !await AllowsAsync(http, access, tenantId, NetRatelPermissions.RunAs, ct))
                return Results.Forbid();
            return Results.Ok(await vault.ListProfilesAsync(tenantId, ct));
        }, ct);

    private static Task<IResult> SaveNewProfileAsync(int tenantId, SaveCredentialProfileRequest request, HttpContext http,
        IEffectiveAccessService access, ITenantSecretService vault, IEventRecorder audit, CancellationToken ct) => SaveProfileCoreAsync(
        tenantId, null, request, http, access, vault, audit, ct);

    private static Task<IResult> SaveProfileAsync(int tenantId, Guid profileId, SaveCredentialProfileRequest request, HttpContext http,
        IEffectiveAccessService access, ITenantSecretService vault, IEventRecorder audit, CancellationToken ct) => SaveProfileCoreAsync(
        tenantId, profileId, request, http, access, vault, audit, ct);

    private static Task<IResult> SaveProfileCoreAsync(int tenantId, Guid? profileId, SaveCredentialProfileRequest request, HttpContext http,
        IEffectiveAccessService access, ITenantSecretService vault, IEventRecorder audit, CancellationToken ct) => WithPermissionAsync(http, access,
        tenantId, NetRatelPermissions.CredentialProfileManagement, async () =>
        {
            if ((request.PasswordReference is not null || request.UsernameReference is not null) &&
                !await AllowsAsync(http, access, tenantId, NetRatelPermissions.SecretUse, ct))
                return Results.Forbid();
            var saved = await vault.SaveProfileAsync(tenantId, profileId, request, ct);
            await AuditAsync(audit, http, tenantId, "secret-vault.profile.saved", saved.Id, revision: saved.MappingRevision, ct: ct);
            return Results.Ok(saved);
        }, ct);

    private static async Task<IResult> WithPermissionAsync(HttpContext http, IEffectiveAccessService access, int tenantId, string permission,
        Func<Task<IResult>> action, CancellationToken ct) => await AllowsAsync(http, access, tenantId, permission, ct)
        ? await ExecuteAsync(action) : Results.Forbid();

    private static Task<bool> AllowsAsync(HttpContext http, IEffectiveAccessService access, int tenantId, string permission, CancellationToken ct) =>
        Supported(http, tenantId) ? access.AuthorizeAsync(http.User, permission, tenantId, ct) : Task.FromResult(false);

    private static bool Supported(HttpContext http, int tenantId) => tenantId > 0 && MonitoringFlowPermissionAuthorization.IsSupportedAccount(http.User);
    private static bool IsHuman(HttpContext http) => Supported(http, 1) && !http.User.HasClaim(x => x.Type == "netratel_integration_credential_id");
    private static string? Actor(HttpContext http) => http.User.FindFirstValue("netratel_principal_id") ??
        http.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? http.User.FindFirstValue("sub");

    private static Task AuditAsync(IEventRecorder audit, HttpContext http, int tenant, string action, Guid id, string? locator = null,
        string? version = null, long? revision = null, string result = "Succeeded", CancellationToken ct = default) => audit.RecordAsync(new DomainEvent
        {
            EventType = action, Source = "SecretVault", EntityId = id.ToString(), TenantId = tenant.ToString(CultureInfo.InvariantCulture),
            CorrelationId = http.TraceIdentifier, Message = action,
            Payload = new { Actor = Actor(http), CredentialId = http.User.FindFirstValue("netratel_integration_credential_id"),
                TenantId = tenant, ResourceId = id, Locator = locator, Version = version, Revision = revision, Result = result }
        }, ct);

    private static async Task<IResult> ExecuteAsync(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (SecretProviderException error) { return ProviderProblem(error); }
    }

    internal static IResult ProviderProblem(SecretProviderException error) => Results.Json(new SecretVaultError(error.Reason.ToString(), error.Message),
        statusCode: error.Reason switch
        {
            SecretProviderFailure.InvalidRequest => StatusCodes.Status400BadRequest,
            SecretProviderFailure.NotFound or SecretProviderFailure.VersionUnavailable => StatusCodes.Status404NotFound,
            SecretProviderFailure.Conflict or SecretProviderFailure.InUse or SecretProviderFailure.Disabled => StatusCodes.Status409Conflict,
            SecretProviderFailure.Authentication or SecretProviderFailure.Forbidden => StatusCodes.Status422UnprocessableEntity,
            SecretProviderFailure.Throttled => StatusCodes.Status429TooManyRequests,
            _ => StatusCodes.Status503ServiceUnavailable
        });

    private sealed record VaultBrowseParameters(string? Folder, string? Search, string? Cursor, int? Limit);
}
