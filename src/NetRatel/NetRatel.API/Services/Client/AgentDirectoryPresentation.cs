using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NetRatel.Infrastructure.Persistence;

namespace NetRatel.API.Services.AgentDirectory;

/// <summary>
/// The persistent Agent directory presentation shared by Client reads and Global Search.
/// Presence may enrich this data, but it never owns the directory.
/// </summary>
public sealed record AgentDirectoryPresentation(
    int TenantId,
    Guid AgentId,
    string DisplayName,
    string? HostName,
    string? OperatingSystem,
    string? Architecture,
    bool IsEnabled,
    string TenantName)
{
    public static AgentDirectoryPresentation Create(
        int tenantId,
        Guid agentId,
        string? name,
        bool isEnabled,
        string? deviceInfoJson,
        string tenantName)
    {
        var device = ParseDeviceInfo(deviceInfoJson);
        var shortId = agentId.ToString("N")[..8];
        return new(
            tenantId,
            agentId,
            FirstNonEmpty(name, device.HostName, $"Agent-{shortId}"),
            device.HostName,
            device.OperatingSystem,
            device.Architecture,
            isEnabled,
            tenantName);
    }

    public bool Matches(string? term) =>
        string.IsNullOrWhiteSpace(term) ||
        Contains(DisplayName, term) ||
        Contains(HostName, term) ||
        Contains(OperatingSystem, term) ||
        Contains(TenantName, term) ||
        AgentId.ToString("D").Contains(term.Trim(), StringComparison.OrdinalIgnoreCase);

    private static DeviceInfo ParseDeviceInfo(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new(null, null, null);
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            return new(
                ReadString(root, "hostName"),
                ReadString(root, "os"),
                ReadString(root, "architecture"));
        }
        catch (JsonException)
        {
            return new(null, null, null);
        }
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static bool Contains(string? value, string term) =>
        !string.IsNullOrWhiteSpace(value) && value.Contains(term.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string FirstNonEmpty(params string?[] values) =>
        values.First(value => !string.IsNullOrWhiteSpace(value))!;

    private sealed record DeviceInfo(string? HostName, string? OperatingSystem, string? Architecture);
}

internal static class AgentDirectorySearch
{
    public static IQueryable<AgentDirectoryRow> Query(
        OrchestratorDbContext db,
        string? term,
        int[]? tenantIds = null)
    {
        var directory = from agent in MatchingAgents(db, term, tenantIds)
                        join tenant in db.Tenants.AsNoTracking()
                            on agent.TenantId equals tenant.Id
                        select new { Agent = agent, TenantName = tenant.Name };

        return directory
            .OrderBy(row => row.TenantName)
            .ThenBy(row => row.Agent.Name)
            .ThenBy(row => row.Agent.Id)
            .Select(row => new AgentDirectoryRow(
                row.Agent.TenantId,
                row.Agent.Id,
                row.Agent.Name,
                row.Agent.IsEnabled,
                row.Agent.DeviceInfoJson,
                row.TenantName));
    }

    public static IQueryable<Agent> MatchingAgents(OrchestratorDbContext db, string? term, int[]? tenantIds = null)
    {
        var agents = db.Agents.AsNoTracking();
        if (tenantIds is not null)
        {
            agents = agents.Where(agent => tenantIds.Contains(agent.TenantId));
        }
        if (string.IsNullOrWhiteSpace(term))
        {
            return agents;
        }

        var like = Like(term);
        var hasAgentId = Guid.TryParse(term, out var agentId);
        var matchingTenantIds = db.Tenants.AsNoTracking()
            .Where(tenant => EF.Functions.ILike(tenant.Name, like))
            .Select(tenant => tenant.Id);
        return agents.Where(agent =>
            (agent.Name != null && EF.Functions.ILike(agent.Name, like)) ||
            (agent.DeviceInfoJson != null && EF.Functions.ILike(agent.DeviceInfoJson, like)) ||
            matchingTenantIds.Contains(agent.TenantId) ||
            (hasAgentId && agent.Id == agentId));
    }

    private static string Like(string term) =>
        $"%{term.Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal)}%";
}

internal sealed record AgentDirectoryRow(
    int TenantId,
    Guid AgentId,
    string? Name,
    bool IsEnabled,
    string? DeviceInfoJson,
    string TenantName);
