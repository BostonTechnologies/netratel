using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NetRatel.API.Security.Authorization;
using NetRatel.API.Services.AgentDirectory;
using NetRatel.API.Services.Secrets;
using NetRatel.Application.Events;
using NetRatel.Application.Secrets;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts;

namespace NetRatel.API.Endpoints.Client;

public static class ClientMetadataEndpoints
{
    public static IEndpointRouteBuilder MapClientMetadataEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v2/agents/{tenantId:int}/{agentId:guid}/metadata")
            .WithTags("Client Metadata");
        group.MapGet("", ReadAsync).RequireAuthorization(ClientMetadataAuthorization.ReadPolicy)
            .WithName("ClientMetadata_Read").Produces<ClientMetadataDto>();
        group.MapPut("", UpdateAsync).RequireAuthorization(ClientMetadataAuthorization.WritePolicy)
            .WithName("ClientMetadata_Update").Produces<ClientMetadataDto>()
            .ProducesProblem(StatusCodes.Status400BadRequest);
        return app;
    }

    internal static async Task<IResult> ReadAsync(int tenantId, Guid agentId, HttpContext http,
        IEffectiveAccessService access, OrchestratorDbContext db, CancellationToken cancellationToken)
    {
        if (!await access.AuthorizeAsync(http.User, NetRatelPermissions.TelemetryRead, tenantId, cancellationToken).ConfigureAwait(false))
            return Results.Forbid();
        var agent = await db.Agents.AsNoTracking()
            .SingleOrDefaultAsync(item => item.TenantId == tenantId && item.Id == agentId, cancellationToken).ConfigureAwait(false);
        return agent is null || agent.DeletedAtUtc is not null ? Results.NotFound() : Results.Ok(Map(agent));
    }

    internal static async Task<IResult> UpdateAsync(int tenantId, Guid agentId, UpdateClientMetadataRequest request,
        HttpContext http, IEffectiveAccessService access, OrchestratorDbContext db, TimeProvider clock,
        CancellationToken cancellationToken)
    {
        if (!await access.AuthorizeAsync(http.User, NetRatelPermissions.ClientManagement, tenantId, cancellationToken).ConfigureAwait(false))
            return Results.Forbid();
        if (!ClientMetadataValidation.TryNormalize(request, out var normalized, out var error))
            return Results.Problem(error, statusCode: StatusCodes.Status400BadRequest, title: "Invalid client labels");

        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false) : null;
        var agent = await AgentLifecycleLock.LockAsync(db, tenantId, agentId, cancellationToken).ConfigureAwait(false);
        if (agent is null || agent.DeletedAtUtc is not null) return Results.NotFound();
        var tags = normalized.Tags!.ToArray();
        var changeDefaults = normalized.ExecutionDefaults is { } defaults &&
            (agent.DefaultJobCredentialProfileId != defaults.JobProfileId || agent.DefaultTerminalCredentialProfileId != defaults.TerminalProfileId);
        if (changeDefaults)
        {
            var accounts = http.RequestServices.GetRequiredService<ExecutionAccountService>();
            if (!await accounts.IsAllowedAsync(http.User, tenantId, cancellationToken)) return Results.Forbid();
            try
            {
                var selection = normalized.ExecutionDefaults!;
                if (selection.JobProfileId is { } job && job != agent.DefaultJobCredentialProfileId)
                    await accounts.GetEligibleAsync(agent, job, false, cancellationToken);
                if (selection.TerminalProfileId is { } terminal && terminal != agent.DefaultTerminalCredentialProfileId)
                    await accounts.GetEligibleAsync(agent, terminal, true, cancellationToken);
                agent.DefaultJobCredentialProfileId = selection.JobProfileId;
                agent.DefaultTerminalCredentialProfileId = selection.TerminalProfileId;
            }
            catch (ExecutionAccountException failure) { return Results.Problem(failure.Message, statusCode: failure.StatusCode); }
            catch (SecretProviderException failure) { return Results.Problem(failure.Message, statusCode: StatusCodes.Status409Conflict); }
        }
        if (!changeDefaults && agent.FriendlyName == normalized.FriendlyName && agent.Tags.SequenceEqual(tags, StringComparer.Ordinal))
            return Results.Ok(Map(agent));

        agent.FriendlyName = normalized.FriendlyName;
        agent.Tags = tags;
        var now = clock.GetUtcNow();
        var actor = http.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? http.User.Identity?.Name ?? "unknown";
        db.OutboxMessages.Add(new OutboxMessage
        {
            Id = Guid.NewGuid(),
            OccurredUtc = now,
            Type = NetRatelEventTypes.Agent.MetadataUpdated,
            PayloadJson = JsonSerializer.Serialize(new { agentId, tenantId, actor, hasFriendlyName = agent.FriendlyName is not null, tagCount = tags.Length, executionDefaultsChanged = changeDefaults }),
            Source = "Agent",
            CorrelationId = $"netratel-agent-{agentId:N}",
            TenantId = tenantId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            EntityId = agentId.ToString("D"),
            Severity = "Info",
            Message = $"Client settings updated for agent {agentId}.",
            Status = OutboxStatuses.Pending,
            Attempts = 0,
            NextAttemptUtc = now
        });
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return Results.Ok(Map(agent));
    }

    private static ClientMetadataDto Map(Agent agent)
    {
        var directory = AgentDirectoryPresentation.Create(agent.TenantId, agent.Id, agent.Name,
            agent.IsEnabled, agent.DeviceInfoJson, string.Empty);
        return new(agent.TenantId, agent.Id, directory.HostName, agent.FriendlyName, agent.Tags) { ExecutionDefaults = agent.GetExecutionDefaults() };
    }
}
