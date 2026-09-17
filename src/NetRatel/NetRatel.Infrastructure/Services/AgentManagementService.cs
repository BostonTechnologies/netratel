using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using NetRatel.Application.Agents;
using NetRatel.Application.Events;
using NetRatel.Infrastructure.Persistence;

namespace NetRatel.Infrastructure.Services;

public sealed class AgentManagementService : IAgentManagementService
{
    private readonly OrchestratorDbContext _db;
    private readonly ILogger<AgentManagementService> _logger;

    public AgentManagementService(OrchestratorDbContext db, ILogger<AgentManagementService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<AgentListResponse> ListAsync(int tenantId, AgentListQuery query, CancellationToken ct)
    {
        var page = Math.Max(query.Page, 1);
        var pageSize = Math.Clamp(query.PageSize, 1, 200);

        var q = _db.Agents.Where(a => a.TenantId == tenantId);
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            q = q.Where(a =>
                a.Id.ToString().Contains(term) ||
                (a.Name != null && a.Name.Contains(term)));
        }

        if (query.IsEnabled.HasValue)
        {
            q = q.Where(a => a.IsEnabled == query.IsEnabled.Value);
        }

        var total = await q.CountAsync(ct);
        var items = await q
            .OrderByDescending(a => a.CreatedAtUtc)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(a => new AgentSummaryDto(
                a.TenantId,
                a.Id,
                a.Name,
                a.IsEnabled,
                a.CreatedAtUtc,
                a.LastSeenUtc,
                a.LastTokenIssuedAtUtc))
            .ToListAsync(ct);

        return new AgentListResponse(items, page, pageSize, total);
    }

    public async Task<AgentDetailDto?> GetAsync(int tenantId, Guid agentId, CancellationToken ct)
    {
        return await _db.Agents
            .Where(a => a.TenantId == tenantId && a.Id == agentId)
            .Select(a => new AgentDetailDto(
                a.TenantId,
                a.Id,
                a.Name,
                a.IsEnabled,
                a.DisabledReason,
                a.CreatedAtUtc,
                a.CreatedBy,
                a.LastSeenUtc,
                a.LastTokenIssuedAtUtc,
                a.RevokedAtUtc,
                a.DeletedAtUtc))
            .FirstOrDefaultAsync(ct);
    }

    public async Task DisableAsync(int tenantId, Guid agentId, string reason, string actor, CancellationToken ct)
    {
        var agent = await _db.Agents.FirstOrDefaultAsync(a => a.TenantId == tenantId && a.Id == agentId, ct);
        if (agent is null)
        {
            throw new AgentAuthException(404, "Agent not found.", "agent_not_found");
        }

        agent.IsEnabled = false;
        agent.Status = AgentStatus.Disabled;
        agent.DisabledReason = reason;
        agent.RevokedAtUtc = DateTimeOffset.UtcNow;
        _db.OutboxMessages.Add(new OutboxMessage
        {
            Id = Guid.NewGuid(),
            OccurredUtc = DateTimeOffset.UtcNow,
            Type = NetRatelEventTypes.Agent.WentOffline,
            PayloadJson = JsonSerializer.Serialize(new AgentLifecyclePayload(agent.Id, tenantId, Actor: actor, Reason: reason)),
            Source = "Agent",
            CorrelationId = $"netratel-agent-{agent.Id:N}",
            TenantId = tenantId.ToString(),
            EntityId = agent.Id.ToString(),
            Severity = "Warning",
            Message = $"Agent {agent.Id} disabled.",
            Status = OutboxStatuses.Pending,
            Attempts = 0,
            NextAttemptUtc = DateTimeOffset.UtcNow
        });
        _db.OutboxMessages.Add(new OutboxMessage
        {
            Id = Guid.NewGuid(),
            OccurredUtc = DateTimeOffset.UtcNow,
            Type = NetRatelEventTypes.AgentToken.Revoked,
            PayloadJson = JsonSerializer.Serialize(new AgentLifecyclePayload(agent.Id, tenantId, Actor: actor, Reason: reason)),
            Source = "Agent",
            CorrelationId = $"netratel-agent-{agent.Id:N}",
            TenantId = tenantId.ToString(),
            EntityId = agent.Id.ToString(),
            Severity = "Warning",
            Message = $"Agent token revoked for {agent.Id}.",
            Status = OutboxStatuses.Pending,
            Attempts = 0,
            NextAttemptUtc = DateTimeOffset.UtcNow
        });

        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Agent disabled. tenantId={TenantId}, agentId={AgentId}, actor={Actor}", tenantId, agentId, actor);
    }

    public async Task EnableAsync(int tenantId, Guid agentId, string actor, CancellationToken ct)
    {
        var agent = await _db.Agents.FirstOrDefaultAsync(a => a.TenantId == tenantId && a.Id == agentId, ct);
        if (agent is null)
        {
            throw new AgentAuthException(404, "Agent not found.", "agent_not_found");
        }

        agent.IsEnabled = true;
        agent.Status = AgentStatus.Active;
        agent.DisabledReason = null;
        agent.RevokedAtUtc = null;
        _db.OutboxMessages.Add(new OutboxMessage
        {
            Id = Guid.NewGuid(),
            OccurredUtc = DateTimeOffset.UtcNow,
            Type = NetRatelEventTypes.Agent.Connected,
            PayloadJson = JsonSerializer.Serialize(new AgentLifecyclePayload(agent.Id, tenantId, Actor: actor, Reason: "enabled")),
            Source = "Agent",
            CorrelationId = $"netratel-agent-{agent.Id:N}",
            TenantId = tenantId.ToString(),
            EntityId = agent.Id.ToString(),
            Severity = "Info",
            Message = $"Agent {agent.Id} enabled.",
            Status = OutboxStatuses.Pending,
            Attempts = 0,
            NextAttemptUtc = DateTimeOffset.UtcNow
        });
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Agent enabled. tenantId={TenantId}, agentId={AgentId}, actor={Actor}", tenantId, agentId, actor);
    }

    public async Task DeleteAsync(int tenantId, Guid agentId, string reason, string actor, CancellationToken ct)
    {
        var agent = await _db.Agents.FirstOrDefaultAsync(a => a.TenantId == tenantId && a.Id == agentId, ct);
        if (agent is null)
            throw new AgentAuthException(404, "Agent not found.", "agent_not_found");

        var now = DateTimeOffset.UtcNow;
        agent.IsEnabled = false;
        agent.Status = AgentStatus.Disabled;
        agent.DisabledReason = "decommissioned";
        agent.RevokedAtUtc = now;
        agent.DeletedAtUtc = now;
        agent.DeletedBy = actor;
        foreach (var credential in _db.AgentCredentials.Where(candidate => candidate.AgentId == agentId && candidate.RevokedAtUtc == null))
            credential.RevokedAtUtc = now;
        foreach (var token in _db.AgentRefreshTokens.Where(candidate => candidate.AgentId == agentId && candidate.RevokedAtUtc == null))
            token.RevokedAtUtc = now;

        _db.OutboxMessages.Add(new OutboxMessage
        {
            Id = Guid.NewGuid(),
            OccurredUtc = now,
            Type = NetRatelEventTypes.Agent.WentOffline,
            PayloadJson = JsonSerializer.Serialize(new AgentLifecyclePayload(agent.Id, tenantId, Actor: actor, Reason: "decommissioned")),
            Source = "Agent",
            CorrelationId = $"netratel-agent-{agent.Id:N}",
            TenantId = tenantId.ToString(),
            EntityId = agent.Id.ToString(),
            Severity = "Warning",
            Message = $"Agent {agent.Id} decommissioned.",
            Status = OutboxStatuses.Pending,
            Attempts = 0,
            NextAttemptUtc = now
        });
        _db.OutboxMessages.Add(new OutboxMessage
        {
            Id = Guid.NewGuid(),
            OccurredUtc = now,
            Type = NetRatelEventTypes.AgentToken.Revoked,
            PayloadJson = JsonSerializer.Serialize(new AgentLifecyclePayload(agent.Id, tenantId, Actor: actor, Reason: "decommissioned")),
            Source = "Agent",
            CorrelationId = $"netratel-agent-{agent.Id:N}",
            TenantId = tenantId.ToString(),
            EntityId = agent.Id.ToString(),
            Severity = "Warning",
            Message = $"Agent credentials revoked for decommissioned agent {agent.Id}.",
            Status = OutboxStatuses.Pending,
            Attempts = 0,
            NextAttemptUtc = now
        });
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Agent decommissioned. tenantId={TenantId}, agentId={AgentId}, actor={Actor}", tenantId, agentId, actor);
    }
}
