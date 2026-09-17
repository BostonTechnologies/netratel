using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NetRatel.API.Gateway;
using NetRatel.API.Services;
using NetRatel.API.Services.AgentDirectory;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared;

namespace NetRatel.API.Endpoints.Client;

public static class ClientUpdatesEndpoints
{
    public static IEndpointRouteBuilder MapClientUpdatesEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/v1/client-updates")
            .RequireAuthorization("Operator")
            .WithTags("Client Updates");

        admin.MapGet("/releases", async (OrchestratorDbContext db, [FromQuery] string? runtimeId,
            [FromQuery] int? tenantId, [FromQuery] ClientEnvironment? environment, CancellationToken cancellationToken) =>
        {
            var rows = await db.ClientUpdateReleases.AsNoTracking()
                .Where(x => string.IsNullOrWhiteSpace(runtimeId) || x.RuntimeId == runtimeId)
                .OrderByDescending(x => x.PublishedAtUtc)
                .Select(x => new ClientUpdateReleaseDto(x.Id, null, null, x.RuntimeId, x.Version,
                    $"/api/v1/client-artifacts/{x.RuntimeId}/{x.Version}/raw-download", x.Sha256, x.Enabled,
                    x.PublishedAtUtc, x.PublishedBy ?? string.Empty, x.PublicId, x.Revision, x.SizeBytes, x.Channel))
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            return Results.Ok(rows);
        });

        // The management view has its own bounded page contract.  Keep the legacy
        // release list above intact for existing callers that consume its array shape.
        admin.MapGet("/management/releases", async (
            OrchestratorDbContext db,
            [FromQuery] int? page,
            [FromQuery] int? pageSize,
            [FromQuery] string? search,
            [FromQuery] string? runtimeId,
            [FromQuery] string? version,
            [FromQuery] string? channel,
            [FromQuery] bool? enabled,
            CancellationToken cancellationToken) =>
        {
            if (!TryResolvePage(page, pageSize, out var resolvedPage, out var resolvedPageSize, out var error))
            {
                return Results.ValidationProblem(error!);
            }

            var query = db.ClientUpdateReleases.AsNoTracking().AsQueryable();
            if (!string.IsNullOrWhiteSpace(runtimeId)) query = query.Where(release => release.RuntimeId == runtimeId.Trim());
            if (!string.IsNullOrWhiteSpace(version)) query = query.Where(release => EF.Functions.ILike(release.Version, ToLikePattern(version)));
            if (!string.IsNullOrWhiteSpace(channel)) query = query.Where(release => release.Channel == channel.Trim());
            if (enabled.HasValue) query = query.Where(release => release.Enabled == enabled.Value);
            if (!string.IsNullOrWhiteSpace(search))
            {
                var like = ToLikePattern(search);
                query = query.Where(release =>
                    EF.Functions.ILike(release.RuntimeId, like) ||
                    EF.Functions.ILike(release.Version, like) ||
                    EF.Functions.ILike(release.Channel, like) ||
                    EF.Functions.ILike(release.Sha256, like));
            }

            var total = await query.CountAsync(cancellationToken).ConfigureAwait(false);
            var releases = await query
                .OrderByDescending(release => release.PublishedAtUtc)
                .ThenByDescending(release => release.PublicId)
                .Skip(resolvedPage * resolvedPageSize)
                .Take(resolvedPageSize)
                .Select(release => new ClientUpdateReleaseDto(
                    release.Id, null, null, release.RuntimeId, release.Version,
                    $"/api/v1/client-artifacts/{release.RuntimeId}/{release.Version}/raw-download", release.Sha256,
                    release.Enabled, release.PublishedAtUtc, release.PublishedBy ?? string.Empty, release.PublicId,
                    release.Revision, release.SizeBytes, release.Channel))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            return Results.Ok(new ClientUpdateReleasePageDto(releases, total, resolvedPage, resolvedPageSize));
        });

        admin.MapGet("/attempts", async (OrchestratorDbContext db, [FromQuery] string? clientIdentity,
            [FromQuery] int? releaseId, [FromQuery] string? status, CancellationToken cancellationToken) =>
        {
            var query = db.ClientUpdateAttempts.AsNoTracking().AsQueryable();
            if (Guid.TryParse(clientIdentity, out var agentId)) query = query.Where(x => x.AgentId == agentId);
            if (releaseId.HasValue) query = query.Where(x => x.ReleaseId == releaseId.Value);
            if (Enum.TryParse<ClientUpdateAttemptState>(status, true, out var state)) query = query.Where(x => x.State == state);
            var rows = await (
                from attempt in query.OrderByDescending(x => x.UpdatedAtUtc).Take(500)
                join agent in db.Agents.IgnoreQueryFilters() on attempt.AgentId equals agent.Id into agents
                from agent in agents.DefaultIfEmpty()
                join tenant in db.Tenants on attempt.TenantId equals tenant.Id into tenants
                from tenant in tenants.DefaultIfEmpty()
                select new ClientUpdateAttemptDto(attempt.Id, attempt.AgentId.ToString(), attempt.ReleaseId, attempt.RuntimeId,
                    attempt.TargetVersion, attempt.State.ToString(), attempt.Message, attempt.CreatedAtUtc, attempt.UpdatedAtUtc,
                    attempt.PublicId, attempt.TenantId, attempt.AgentId, attempt.FailureCode, tenant.Name, agent.Name))
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            return Results.Ok(rows);
        });

        admin.MapGet("/history", async (
            OrchestratorDbContext db,
            [FromQuery] int? page,
            [FromQuery] int? pageSize,
            [FromQuery] string? search,
            [FromQuery] string? status,
            [FromQuery] int? releaseId,
            [FromQuery] string? runtimeId,
            [FromQuery] int? tenantId,
            [FromQuery] string? version,
            CancellationToken cancellationToken) =>
        {
            if (!TryResolvePage(page, pageSize, out var resolvedPage, out var resolvedPageSize, out var error))
            {
                return Results.ValidationProblem(error!);
            }
            ClientUpdateAttemptState? parsedStatus = null;
            if (!string.IsNullOrWhiteSpace(status))
            {
                if (!Enum.TryParse<ClientUpdateAttemptState>(status, true, out var parsed))
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["status"] = ["Status is not a recognized update state."] });
                }
                parsedStatus = parsed;
            }

            var query = BuildHistoryQuery(db, search, parsedStatus, releaseId, runtimeId, tenantId, version);
            var total = await query.CountAsync(cancellationToken).ConfigureAwait(false);
            var rows = await query
                .Skip(resolvedPage * resolvedPageSize)
                .Take(resolvedPageSize)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            return Results.Ok(new ClientUpdateHistoryPageDto(
                rows.Select(MapHistoryItem).ToList(),
                total,
                resolvedPage,
                resolvedPageSize));
        });

        admin.MapGet("/management/suspended", async (
            OrchestratorDbContext db,
            [FromQuery] int? page,
            [FromQuery] int? pageSize,
            [FromQuery] string? search,
            [FromQuery] int? tenantId,
            CancellationToken cancellationToken) =>
        {
            if (!TryResolvePage(page, pageSize, out var resolvedPage, out var resolvedPageSize, out var error))
            {
                return Results.ValidationProblem(error!);
            }

            IQueryable<AgentClientUpdateStateRecord> states = db.AgentClientUpdateStates.AsNoTracking()
                .Where(state => state.SuspendedAtUtc != null);
            if (tenantId.HasValue) states = states.Where(state => state.TenantId == tenantId.Value);

            var query = from state in states
                        join agent in db.Agents.IgnoreQueryFilters().AsNoTracking() on state.AgentId equals agent.Id into agents
                        from agent in agents.DefaultIfEmpty()
                        join tenant in db.Tenants.AsNoTracking() on state.TenantId equals tenant.Id into tenants
                        from tenant in tenants.DefaultIfEmpty()
                        select new { State = state, Agent = agent, Tenant = tenant };
            if (!string.IsNullOrWhiteSpace(search))
            {
                var like = ToLikePattern(search);
                query = query.Where(row =>
                    (row.State.SuspensionReason != null && EF.Functions.ILike(row.State.SuspensionReason, like)) ||
                    (row.Agent != null && row.Agent.Name != null && EF.Functions.ILike(row.Agent.Name, like)) ||
                    (row.Agent != null && row.Agent.DeviceInfoJson != null && EF.Functions.ILike(row.Agent.DeviceInfoJson, like)) ||
                    (row.Tenant != null && EF.Functions.ILike(row.Tenant.Name, like)));
            }

            var total = await query.CountAsync(cancellationToken).ConfigureAwait(false);
            var rows = await query
                .OrderByDescending(row => row.State.SuspendedAtUtc)
                .ThenByDescending(row => row.State.AgentId)
                .Skip(resolvedPage * resolvedPageSize)
                .Take(resolvedPageSize)
                .Select(row => new ClientUpdateStateRow(
                    row.State.AgentId, row.State.TenantId, row.State.SuspendedAtUtc, row.State.SuspensionReason,
                    row.State.SuppressedReleaseId, row.State.PolicyRevision, row.State.ResumedAtUtc, row.State.ResumedBy,
                    row.Tenant == null ? null : row.Tenant.Name, row.Agent == null ? null : row.Agent.Name,
                    row.Agent == null ? null : row.Agent.DeviceInfoJson, row.Agent != null && row.Agent.IsEnabled))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            return Results.Ok(new AgentClientUpdateStatePageDto(
                rows.Select(MapState).ToList(), total, resolvedPage, resolvedPageSize));
        });

        admin.MapGet("/attempts/{attemptId:guid}", async (Guid attemptId, OrchestratorDbContext db,
            CancellationToken cancellationToken) =>
        {
            var attempt = await db.ClientUpdateAttempts.AsNoTracking()
                .SingleOrDefaultAsync(x => x.PublicId == attemptId, cancellationToken).ConfigureAwait(false);
            return attempt is null ? Results.NotFound() : Results.Ok(new ClientUpdateAttemptDiagnosticDto(
                attempt.PublicId, attempt.ReleaseId, attempt.TenantId, attempt.AgentId, attempt.FromVersion,
                attempt.TargetVersion, attempt.RuntimeId, attempt.State.ToString(), attempt.FailureCode,
                attempt.Message, attempt.CreatedAtUtc, attempt.UpdatedAtUtc, attempt.GatewayConnectionId,
                attempt.GatewayConnectionEpoch, attempt.ReadmittedAtUtc, attempt.ConfirmedAtUtc, attempt.ConfirmationId));
        });

        admin.MapGet("/agent-states", async (OrchestratorDbContext db, CancellationToken cancellationToken) =>
        {
            var rows = await (
                from state in db.AgentClientUpdateStates.AsNoTracking().OrderByDescending(x => x.SuspendedAtUtc).Take(500)
                join agent in db.Agents.IgnoreQueryFilters() on state.AgentId equals agent.Id into agents
                from agent in agents.DefaultIfEmpty()
                join tenant in db.Tenants on state.TenantId equals tenant.Id into tenants
                from tenant in tenants.DefaultIfEmpty()
                select new
                {
                    State = state,
                    TenantName = tenant.Name,
                    AgentName = agent == null ? null : agent.Name,
                    AgentDeviceInfoJson = agent == null ? null : agent.DeviceInfoJson,
                    AgentIsEnabled = agent != null && agent.IsEnabled
                })
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            var states = rows.Select(row =>
            {
                var presentation = AgentDirectoryPresentation.Create(
                    row.State.TenantId,
                    row.State.AgentId,
                    row.AgentName,
                    row.AgentIsEnabled,
                    row.AgentDeviceInfoJson,
                    row.TenantName ?? $"Tenant {row.State.TenantId}");
                return new AgentClientUpdateStateDto(
                    row.State.AgentId,
                    row.State.TenantId,
                    row.State.SuspendedAtUtc,
                    row.State.SuspensionReason,
                    row.State.SuppressedReleaseId,
                    row.State.PolicyRevision,
                    row.State.ResumedAtUtc,
                    row.State.ResumedBy,
                    row.TenantName,
                    row.AgentName,
                    presentation.DisplayName,
                    presentation.HostName);
            });
            return Results.Ok(states);
        });

        admin.MapPost("/releases/{releaseId:guid}/disable", async (Guid releaseId, HttpContext http,
            ClientUpdateAuthorityService updates, CancellationToken cancellationToken) =>
        {
            await updates.DisableReleaseAsync(releaseId, http.User.Identity?.Name, cancellationToken).ConfigureAwait(false);
            return Results.NoContent();
        });

        // A release is normally created as part of an artifact upload. This additive repair
        // endpoint is deliberately limited to an already immutable stored artifact so an
        // interrupted deployment can be reconciled without replacing its bytes.
        admin.MapPost("/releases/reconcile", async (ClientUpdateReleaseReconcileRequest request, HttpContext http,
            IClientArtifactsService artifacts, IClientUpdatePublisher updates, CancellationToken cancellationToken) =>
        {
            var artifact = await artifacts.GetMetadataAsync(request.RuntimeId, request.Version, cancellationToken)
                .ConfigureAwait(false);
            if (artifact is null)
            {
                return Results.NotFound(new
                {
                    message = "The requested stored client artifact was not found.",
                    request.RuntimeId,
                    request.Version
                });
            }

            var manifestJson = await ClientArtifactManifestValidator.ValidateAsync(artifacts, artifact, cancellationToken)
                .ConfigureAwait(false);
            var release = await updates.PublishArtifactAsync(artifact, manifestJson, http.User.Identity?.Name, cancellationToken)
                .ConfigureAwait(false);
            return Results.Ok(ToDto(release));
        });

        admin.MapPost("/tenants/{tenantId:int}/agents/{agentId:guid}/resume", async (int tenantId, Guid agentId,
            HttpContext http, ClientUpdateAuthorityService updates, CancellationToken cancellationToken) =>
        {
            await updates.ResumeAsync(tenantId, agentId, http.User.Identity?.Name, cancellationToken).ConfigureAwait(false);
            return Results.Accepted();
        });

        admin.MapPost("/recheck/{clientIdentity}", () => Results.Problem(title: "Legacy recheck retired",
            detail: "Akka clients discover releases from the next presence acknowledgement.",
            statusCode: StatusCodes.Status410Gone));

        var agent = app.MapGroup("/api/v2/agent-updates")
            .RequireAuthorization("AgentGatewayAccess").WithTags("Agent Updates");

        agent.MapPost("/releases/{releaseId:guid}/claim", async (Guid releaseId, ClientUpdateClaimRequest request,
            HttpContext http, ClientUpdateAuthorityService updates, CancellationToken cancellationToken) =>
        {
            if (!AgentGatewayIdentityResolver.TryResolve(http.User, out var identity, out var error) || identity is null)
                return Results.Problem(error, statusCode: StatusCodes.Status403Forbidden);
            var result = await updates.ClaimAsync(identity, releaseId, request.RuntimeId, request.CurrentVersion, request.Channel,
                    request.AdmissionNonce, cancellationToken)
                .ConfigureAwait(false);
            return result is null ? Results.Conflict(new { message = "Release is not eligible for this agent." }) : Results.Ok(result);
        });

        agent.MapPost("/attempts/{attemptId:guid}/result", async (Guid attemptId, ClientUpdateResultRequest request,
            HttpContext http, ClientUpdateAuthorityService updates, CancellationToken cancellationToken) =>
        {
            if (!AgentGatewayIdentityResolver.TryResolve(http.User, out var identity, out var error) || identity is null)
                return Results.Problem(error, statusCode: StatusCodes.Status403Forbidden);
            if (!Enum.TryParse<ClientUpdateAttemptState>(request.State, true, out var state) ||
                state is ClientUpdateAttemptState.Accepted or ClientUpdateAttemptState.GatewayReadmitted)
                return Results.BadRequest(new { message = "The reported update state is invalid." });
            await updates.ReportAsync(identity, attemptId, state, request.FailureCode, request.Message, cancellationToken)
                .ConfigureAwait(false);
            return Results.Accepted();
        });

        return app;
    }

    private static ClientUpdateReleaseDto ToDto(ClientUpdateReleaseRecord release) => new(
        release.Id,
        null,
        null,
        release.RuntimeId,
        release.Version,
        $"/api/v1/client-artifacts/{release.RuntimeId}/{release.Version}/raw-download",
        release.Sha256,
        release.Enabled,
        release.PublishedAtUtc,
        release.PublishedBy ?? string.Empty,
        release.PublicId,
        release.Revision,
        release.SizeBytes,
        release.Channel);

    private static IQueryable<ClientUpdateHistoryRow> BuildHistoryQuery(
        OrchestratorDbContext db,
        string? search,
        ClientUpdateAttemptState? status,
        int? releaseId,
        string? runtimeId,
        int? tenantId,
        string? version)
    {
        IQueryable<ClientUpdateAttemptRecord> attempts = db.ClientUpdateAttempts.AsNoTracking();
        if (status.HasValue)
        {
            attempts = attempts.Where(attempt => attempt.State == status.Value);
        }
        if (releaseId.HasValue)
        {
            attempts = attempts.Where(attempt => attempt.ReleaseId == releaseId.Value);
        }
        if (!string.IsNullOrWhiteSpace(runtimeId))
        {
            attempts = attempts.Where(attempt => attempt.RuntimeId == runtimeId.Trim());
        }
        if (tenantId.HasValue)
        {
            attempts = attempts.Where(attempt => attempt.TenantId == tenantId.Value);
        }
        if (!string.IsNullOrWhiteSpace(version))
        {
            attempts = attempts.Where(attempt => EF.Functions.ILike(attempt.TargetVersion, ToLikePattern(version)));
        }

        var query = from attempt in attempts
                    join agent in db.Agents.IgnoreQueryFilters().AsNoTracking() on attempt.AgentId equals agent.Id into agents
                    from agent in agents.DefaultIfEmpty()
                    join tenant in db.Tenants.AsNoTracking() on attempt.TenantId equals tenant.Id into tenants
                    from tenant in tenants.DefaultIfEmpty()
                    select new { Attempt = attempt, Agent = agent, Tenant = tenant };

        if (!string.IsNullOrWhiteSpace(search))
        {
            var like = ToLikePattern(search);
            query = query.Where(row =>
                EF.Functions.ILike(row.Attempt.RuntimeId, like) ||
                EF.Functions.ILike(row.Attempt.TargetVersion, like) ||
                (row.Attempt.FailureCode != null && EF.Functions.ILike(row.Attempt.FailureCode, like)) ||
                (row.Attempt.Message != null && EF.Functions.ILike(row.Attempt.Message, like)) ||
                (row.Agent != null && row.Agent.Name != null && EF.Functions.ILike(row.Agent.Name, like)) ||
                (row.Agent != null && row.Agent.DeviceInfoJson != null && EF.Functions.ILike(row.Agent.DeviceInfoJson, like)) ||
                (row.Tenant != null && EF.Functions.ILike(row.Tenant.Name, like)));
        }

        return query
            .OrderByDescending(row => row.Attempt.UpdatedAtUtc)
            .ThenByDescending(row => row.Attempt.PublicId)
            .Select(row => new ClientUpdateHistoryRow(
            row.Attempt.PublicId,
            row.Attempt.ReleaseId,
            row.Attempt.RuntimeId,
            row.Attempt.TargetVersion,
            row.Attempt.State.ToString(),
            row.Attempt.FailureCode,
            row.Attempt.Message,
            row.Attempt.CreatedAtUtc,
            row.Attempt.UpdatedAtUtc,
            row.Attempt.TenantId,
            row.Attempt.AgentId,
            row.Tenant == null ? null : row.Tenant.Name,
            row.Agent == null ? null : row.Agent.Name,
            row.Agent == null ? null : row.Agent.DeviceInfoJson,
            row.Agent != null && row.Agent.IsEnabled));
    }

    private static ClientUpdateHistoryItemDto MapHistoryItem(ClientUpdateHistoryRow row)
    {
        var presentation = AgentDirectoryPresentation.Create(
            row.TenantId,
            row.AgentId,
            row.AgentName,
            row.AgentIsEnabled,
            row.AgentDeviceInfoJson,
            row.TenantName ?? $"Tenant {row.TenantId}");
        return new ClientUpdateHistoryItemDto(
            row.AttemptId,
            row.ReleaseId,
            row.RuntimeId,
            row.TargetVersion,
            row.Status,
            row.FailureCode,
            row.Message,
            row.CreatedAtUtc,
            row.UpdatedAtUtc,
            row.TenantId,
            row.TenantName,
            row.AgentId,
            presentation.DisplayName,
            presentation.HostName);
    }

    private static AgentClientUpdateStateDto MapState(ClientUpdateStateRow row)
    {
        var presentation = AgentDirectoryPresentation.Create(
            row.TenantId,
            row.AgentId,
            row.AgentName,
            row.AgentIsEnabled,
            row.AgentDeviceInfoJson,
            row.TenantName ?? $"Tenant {row.TenantId}");
        return new AgentClientUpdateStateDto(
            row.AgentId, row.TenantId, row.SuspendedAtUtc, row.SuspensionReason, row.SuppressedReleaseId,
            row.PolicyRevision, row.ResumedAtUtc, row.ResumedBy, row.TenantName, row.AgentName,
            presentation.DisplayName, presentation.HostName);
    }

    private static bool TryResolvePage(
        int? page,
        int? pageSize,
        out int resolvedPage,
        out int resolvedPageSize,
        out Dictionary<string, string[]>? error)
    {
        resolvedPage = page ?? 0;
        resolvedPageSize = pageSize ?? 10;
        error = null;
        if (resolvedPage < 0)
        {
            error = new Dictionary<string, string[]> { ["page"] = ["Page must be zero or greater."] };
            return false;
        }
        if (!ClientUpdateHistoryPageSizes.Contains(resolvedPageSize))
        {
            error = new Dictionary<string, string[]> { ["pageSize"] = ["PageSize must be one of 10, 20, 50, or 100."] };
            return false;
        }
        if (resolvedPage > int.MaxValue / resolvedPageSize)
        {
            error = new Dictionary<string, string[]> { ["page"] = ["Page is too large."] };
            return false;
        }
        return true;
    }

    private static string ToLikePattern(string value) =>
        $"%{value.Trim().Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal)}%";

    private static readonly int[] ClientUpdateHistoryPageSizes = [10, 20, 50, 100];

    private sealed record ClientUpdateHistoryRow(
        Guid AttemptId,
        int ReleaseId,
        string RuntimeId,
        string TargetVersion,
        string Status,
        string? FailureCode,
        string? Message,
        DateTimeOffset CreatedAtUtc,
        DateTimeOffset UpdatedAtUtc,
        int TenantId,
        Guid AgentId,
        string? TenantName,
        string? AgentName,
        string? AgentDeviceInfoJson,
        bool AgentIsEnabled);

    private sealed record ClientUpdateStateRow(
        Guid AgentId,
        int TenantId,
        DateTimeOffset? SuspendedAtUtc,
        string? SuspensionReason,
        Guid? SuppressedReleaseId,
        long PolicyRevision,
        DateTimeOffset? ResumedAtUtc,
        string? ResumedBy,
        string? TenantName,
        string? AgentName,
        string? AgentDeviceInfoJson,
        bool AgentIsEnabled);
}

public sealed record ClientUpdateClaimRequest(string RuntimeId, string CurrentVersion, string Channel, string AdmissionNonce);
public sealed record ClientUpdateResultRequest(string State, string? FailureCode, string? Message);
public sealed record ClientUpdateReleaseReconcileRequest(string RuntimeId, string Version);
public sealed record ClientUpdateReleaseDto(int Id, int? TenantId, ClientEnvironment? Environment, string RuntimeId,
    string Version, string ArtifactUrl, string Sha256, bool Enabled, DateTimeOffset PublishedAt, string PublishedBy,
    Guid ReleaseId, long Revision, long SizeBytes, string Channel);
public sealed record ClientUpdateAttemptDto(int Id, string ClientIdentity, int ReleaseId, string RuntimeId,
    string Version, string Status, string? Message, DateTimeOffset StartedAt, DateTimeOffset UpdatedAt,
    Guid AttemptId, int TenantId, Guid AgentId, string? FailureCode, string? TenantName, string? AgentName);
public sealed record ClientUpdateHistoryItemDto(Guid AttemptId, int ReleaseId, string RuntimeId, string TargetVersion,
    string Status, string? FailureCode, string? Message, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc,
    int TenantId, string? TenantName, Guid AgentId, string ClientDisplayName, string? ClientHostName);
public sealed record ClientUpdateHistoryPageDto(IReadOnlyList<ClientUpdateHistoryItemDto> Items, int Total, int Page, int PageSize);
public sealed record ClientUpdateReleasePageDto(IReadOnlyList<ClientUpdateReleaseDto> Items, int Total, int Page, int PageSize);
public sealed record ClientUpdateAttemptDiagnosticDto(Guid AttemptId, int ReleaseId, int TenantId, Guid AgentId,
    string FromVersion, string TargetVersion, string RuntimeId, string State, string? FailureCode, string? Message,
    DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc, Guid? GatewayConnectionId,
    long? GatewayConnectionEpoch, DateTimeOffset? ReadmittedAtUtc, DateTimeOffset? ConfirmedAtUtc,
    Guid? ConfirmationId);
public sealed record AgentClientUpdateStateDto(Guid AgentId, int TenantId, DateTimeOffset? SuspendedAtUtc,
    string? SuspensionReason, Guid? SuppressedReleaseId, long PolicyRevision, DateTimeOffset? ResumedAtUtc,
    string? ResumedBy, string? TenantName, string? AgentName, string? ClientDisplayName = null,
    string? ClientHostName = null);
public sealed record AgentClientUpdateStatePageDto(IReadOnlyList<AgentClientUpdateStateDto> Items, int Total, int Page, int PageSize);
