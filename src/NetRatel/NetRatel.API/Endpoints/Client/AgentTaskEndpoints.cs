using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NetRatel.API.Gateway;
using NetRatel.API.Services.AgentDirectory;
using NetRatel.Application.Events;
using NetRatel.Application.Jobs;
using NetRatel.Application.Scripts;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared;
using NetRatel.Shared.Contracts.Execution;
using NetRatel.Shared.Contracts.Tasks;

namespace NetRatel.API.Endpoints;

/// <summary>
/// Current ad-hoc execution API.  Its authority key is the persisted Agent key;
/// no Spacetime client identity or reducer participates in this surface.
/// </summary>
public static class AgentTaskEndpoints
{
    public static IEndpointRouteBuilder MapAgentTaskEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v2/tasks")
            .WithTags("Agent Tasks")
            .RequireAuthorization();

        group.MapGet("", async ([FromQuery] string? requestId, HttpContext http, [FromServices] IEffectiveAccessService access, IJobRunService runs, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(requestId))
                return Results.StatusCode(StatusCodes.Status410Gone);

            var activity = await runs.GetActivityByRequestIdAsync(requestId, ct);
            return activity is null ? Results.NotFound() : !await CanExecuteAsync(http, access, activity.TenantId, ct) ? Results.Forbid() : Results.Ok(new[] { Map(activity, null) });
        });

        group.MapGet("/recent", async (
            [FromQuery] int? limit,
            [FromQuery] int? tenantId,
            [FromQuery] Guid? agentId,
            [FromQuery] string? taskType,
            [FromQuery] string? status,
            HttpContext http,
            [FromServices] IEffectiveAccessService access,
            IJobRunService runs,
            [FromServices] OrchestratorDbContext db,
            CancellationToken ct) =>
        {
            var agents = await db.Agents.AsNoTracking().ToDictionaryAsync(x => x.Id, ct);
            var activities = await runs.ListTaskActivitiesAsync(ct);

            var visible = await FilterAuthorizedAsync(activities, activity => activity.TenantId, http.User, access, ct);
            var payload = visible
                .Where(x => !tenantId.HasValue || x.TenantId == tenantId)
                .Where(x => !agentId.HasValue || x.AgentId == agentId)
                .Where(x => string.IsNullOrWhiteSpace(taskType) || string.Equals(x.TaskType, taskType, StringComparison.OrdinalIgnoreCase))
                .Where(x => string.IsNullOrWhiteSpace(status) || string.Equals(x.Status, status, StringComparison.OrdinalIgnoreCase))
                .Take(Math.Clamp(limit ?? 25, 1, 100))
                .Select(x => Map(x, x.AgentId is { } id && agents.TryGetValue(id, out var agent) ? agent : null))
                .ToList();
            return Results.Ok(payload);
        });

        group.MapGet("/history", async (
            [FromQuery] int? page,
            [FromQuery] int? pageSize,
            [FromQuery] string? search,
            [FromQuery] int? tenantId,
            [FromQuery] Guid? agentId,
            [FromQuery] string? taskType,
            [FromQuery] string? status,
            [FromQuery] string? requestId,
            HttpContext http,
            [FromServices] IEffectiveAccessService access,
            [FromServices] OrchestratorDbContext db,
            CancellationToken ct) =>
        {
            var resolvedPage = page ?? 0;
            var resolvedPageSize = pageSize ?? 10;
            if (resolvedPage < 0)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["page"] = ["Page must be zero or greater."] });
            }
            if (!TaskHistoryPageSizes.Contains(resolvedPageSize))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["pageSize"] = ["PageSize must be one of 10, 20, 50, or 100."] });
            }
            if (resolvedPage > int.MaxValue / resolvedPageSize)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["page"] = ["Page is too large."] });
            }

            var query = BuildHistoryQuery(db, search, tenantId, agentId, taskType, status, requestId);
            var visible = await FilterAuthorizedAsync(await query.ToListAsync(ct), row => row.TenantId, http.User, access, ct);
            var total = visible.Count;
            var items = visible
                .Skip(resolvedPage * resolvedPageSize)
                .Take(resolvedPageSize)
                .ToList();

            return Results.Ok(new TaskHistoryPageDto(
                items.Select(MapHistoryItem).ToList(),
                total,
                resolvedPage,
                resolvedPageSize));
        });

        group.MapGet("/{id:long}", async (ulong id, HttpContext http, [FromServices] IEffectiveAccessService access, IJobRunService runs, [FromServices] OrchestratorDbContext db, CancellationToken ct) =>
        {
            var activity = await runs.GetActivityByIdAsync(id, ct);
            if (activity is null) return Results.NotFound();
            if (!await CanExecuteAsync(http, access, activity.TenantId, ct)) return Results.Forbid();
            var agent = activity.AgentId is { } agentId
                ? await db.Agents.AsNoTracking().SingleOrDefaultAsync(x => x.Id == agentId, ct)
                : null;
            return Results.Ok(Map(activity, agent));
        });

        group.MapPost("", async (
            [FromBody] TaskCreateRequestDto request,
            HttpContext http,
            [FromServices] IEffectiveAccessService access,
            [FromServices] OrchestratorDbContext db,
            IJobRunService runs,
            IScriptService scripts,
            IAgentCommandAuthorityDispatcher commands,
            IEventRecorder events,
            ICorrelationContext correlation,
            CancellationToken ct) =>
        {
            if (request.TenantId is null || request.AgentId is null || request.AgentId == Guid.Empty)
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["target"] = ["TenantId and AgentId are required."] });
            if (!await CanExecuteAsync(http, access, request.TenantId, ct)) return Results.Forbid();
            if (string.IsNullOrWhiteSpace(request.TaskType))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["taskType"] = ["TaskType is required."] });

            var agent = await db.Agents.SingleOrDefaultAsync(x => x.TenantId == request.TenantId && x.Id == request.AgentId, ct);
            if (agent is null) return Results.NotFound(new { code = "agent_not_found" });
            if (!agent.IsEnabled || agent.Status == AgentStatus.Disabled)
                return Results.Conflict(new { code = "agent_not_enabled" });

            var target = new NetRatel.Application.Presence.ClientKey(request.TenantId.Value, request.AgentId.Value);
            if (!commands.IsAvailable(target))
                return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);

            var taskType = NormalizeTaskType(request.TaskType);
            var payload = await BuildPayloadAsync(request, taskType, scripts, ct);
            if (payload.Error is not null) return Results.BadRequest(payload.Error);

            var requestId = string.IsNullOrWhiteSpace(request.RequestId) ? Guid.NewGuid().ToString("N") : request.RequestId.Trim();
            var created = await runs.CreateTaskActivityAsync(new CreateJobTaskActivityCommand(
                requestId, null, null, string.Empty, request.TenantId, taskType, "Pending", null, DateTimeOffset.UtcNow, null, request.AgentId), ct);

            await commands.DispatchAsync(target, requestId, correlation.Current ?? $"netratel-task-{requestId}", taskType, payload.Value!, (int)request.Environment, ct);
            await events.RecordAsync(new DomainEvent
            {
                EventType = NetRatelEventTypes.Task.Submitted,
                Source = "Orchestration",
                CorrelationId = correlation.GetOrCreate(),
                TenantId = request.TenantId.Value.ToString(),
                EntityId = requestId,
                Severity = "Info",
                Message = $"Task {taskType} submitted for agent {agent.Name ?? request.AgentId.Value.ToString()}.",
                Payload = new { requestId, request.TenantId, request.AgentId, taskType, environment = request.Environment.ToString() }
            }, ct);
            return Results.Created($"/api/v2/tasks/{created.Id}", Map(created, agent));
        });

        group.MapGet("/{id:long}/logs", async (ulong id, [FromQuery] long sinceId, [FromQuery] string? stream, HttpContext http, [FromServices] IEffectiveAccessService access, IJobRunService runs, CancellationToken ct) =>
        {
            var activity = await runs.GetActivityByIdAsync(id, ct);
            if (activity is null) return Results.NotFound();
            if (!await CanExecuteAsync(http, access, activity.TenantId, ct)) return Results.Forbid();
            return Results.Ok((await runs.GetLogsByRequestIdAsync(activity.RequestId, ct))
                .Where(x => x.Id > sinceId && (string.IsNullOrWhiteSpace(stream) || stream == "all" || string.Equals(x.Stream, stream, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(x => x.Id)
                .Select(x => new TaskLogDto(x.Id, activity.RequestId, activity.ClientIdentity, x.TimestampUtc, x.Stream, x.Message, x.Sequence)));
        });

        group.MapGet("/logs", async ([FromQuery] string requestId, [FromQuery] long sinceId, [FromQuery] string? stream, HttpContext http, [FromServices] IEffectiveAccessService access, IJobRunService runs, CancellationToken ct) =>
        {
            var activity = await runs.GetActivityByRequestIdAsync(requestId, ct);
            if (activity is null) return Results.NotFound();
            if (!await CanExecuteAsync(http, access, activity.TenantId, ct)) return Results.Forbid();
            return Results.Ok((await runs.GetLogsByRequestIdAsync(requestId, ct))
                .Where(x => x.Id > sinceId && (string.IsNullOrWhiteSpace(stream) || stream == "all" || string.Equals(x.Stream, stream, StringComparison.OrdinalIgnoreCase)))
                .OrderBy(x => x.Id)
                .Select(x => new TaskLogDto(x.Id, requestId, activity.ClientIdentity, x.TimestampUtc, x.Stream, x.Message, x.Sequence)));
        });

        return app;
    }

    private static Task<bool> CanExecuteAsync(HttpContext http, IEffectiveAccessService access, int? tenantId, CancellationToken ct) =>
        access.AuthorizeAsync(http.User, NetRatelPermissions.ScriptExecute, tenantId, ct);

    private static async Task<List<T>> FilterAuthorizedAsync<T>(IEnumerable<T> items, Func<T, int?> tenantId, System.Security.Claims.ClaimsPrincipal principal, IEffectiveAccessService access, CancellationToken ct)
    {
        var visible = new List<T>();
        foreach (var item in items)
            if (await access.AuthorizeAsync(principal, NetRatelPermissions.ScriptExecute, tenantId(item), ct)) visible.Add(item);
        return visible;
    }

    private static async Task<(string? Value, string? Error)> BuildPayloadAsync(TaskCreateRequestDto request, string taskType, IScriptService scripts, CancellationToken ct)
    {
        if (taskType == TaskKinds.ExecShellCommand)
        {
            var command = request.ShellCommand?.Command ?? request.Payload;
            return string.IsNullOrWhiteSpace(command)
                ? (null, "ShellCommand or Payload is required for exec-shell-cmd.")
                : (JsonSerializer.Serialize(request.ShellCommand ?? new ExecShellCommandPayload { Command = command, Preferred = request.Preferred, WorkingDirectory = request.WorkingDirectory, TimeoutSeconds = request.TimeoutSeconds }), null);
        }
        if (taskType == TaskKinds.ExecLibraryScript)
        {
            if (request.ScriptId is not { } scriptId || scriptId <= 0) return (null, "ScriptId is required for exec-library-script.");
            var script = await scripts.GetAsync((ulong)scriptId, ct);
            if (script is null) return (null, $"Script {scriptId} not found.");
            var type = request.ScriptType ?? (Enum.TryParse<ScriptType>(script.ScriptType, true, out var parsed) ? parsed : ScriptType.PowerShell);
            return (JsonSerializer.Serialize(new ExecLibraryScriptPayload { ScriptId = scriptId, ScriptType = type, ScriptContent = script.Content, Preferred = request.Preferred, Parameters = request.Parameters, WorkingDirectory = request.WorkingDirectory, TimeoutSeconds = request.TimeoutSeconds }), null);
        }
        return taskType is TaskKinds.OsInfo or TaskKinds.ProcessesList or TaskKinds.ProcessesTopCpu or TaskKinds.DiskFree
            ? (request.Payload ?? string.Empty, null)
            : (null, $"Unsupported TaskType '{taskType}'.");
    }

    private static string NormalizeTaskType(string value) => value switch
    {
        TaskKinds.Legacy_RunPowerShell or TaskKinds.Legacy_ExecPs or TaskKinds.Legacy_ExecSh => TaskKinds.ExecShellCommand,
        TaskKinds.Legacy_RunLibraryScript => TaskKinds.ExecLibraryScript,
        _ => value
    };

    internal static TaskDto Map(JobTaskActivityInfo activity, Agent? agent)
    {
        var output = ProjectOutput(activity);
        var presentation = agent is null
            ? null
            : AgentDirectoryPresentation.Create(
                agent.TenantId,
                agent.Id,
                agent.Name,
                agent.IsEnabled,
                agent.DeviceInfoJson,
                string.Empty);
        var primaryTarget = presentation?.HostName ?? presentation?.DisplayName;
        var configuredName = presentation is not null &&
                             !string.Equals(primaryTarget, presentation.DisplayName, StringComparison.OrdinalIgnoreCase)
            ? presentation.DisplayName
            : null;

        return new(
            activity.Id > int.MaxValue ? 0 : (int)activity.Id,
            activity.RequestId,
            activity.ClientIdentity,
            activity.TenantId,
            ClientEnvironment.None,
            activity.TaskType,
            activity.Status,
            output.StatusMessage,
            output.ResultJson,
            activity.CreatedAtUtc,
            null,
            activity.CompletedAtUtc,
            output.ExitCode,
            primaryTarget,
            presentation?.HostName,
            configuredName,
            activity.AgentId);
    }

    private static TaskOutputProjection ProjectOutput(JobTaskActivityInfo activity)
    {
        if (!string.IsNullOrWhiteSpace(activity.ResultJson))
        {
            return new(activity.Error, activity.ResultJson, TryGetExitCode(activity.ResultJson));
        }

        if (TryGetLegacyExecutionResult(activity.Error, out var legacyResult))
        {
            return new(SummarizeLegacyResult(legacyResult), legacyResult, TryGetExitCode(legacyResult));
        }

        return new(activity.Error, null, null);
    }

    private static bool TryGetLegacyExecutionResult(string? value, out string resultJson)
    {
        resultJson = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(value);
            var root = document.RootElement;
            if (root.ValueKind is not JsonValueKind.Object ||
                !new[] { "stdout", "stderr", "diagnostics", "exitCode" }.Any(property => root.TryGetProperty(property, out _)))
            {
                return false;
            }

            resultJson = value;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static int? TryGetExitCode(string resultJson)
    {
        try
        {
            using var document = JsonDocument.Parse(resultJson);
            if (!document.RootElement.TryGetProperty("exitCode", out var value))
            {
                return null;
            }

            return value.ValueKind switch
            {
                JsonValueKind.Number when value.TryGetInt32(out var exitCode) => exitCode,
                JsonValueKind.String when int.TryParse(value.GetString(), out var exitCode) => exitCode,
                _ => null
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string SummarizeLegacyResult(string resultJson)
    {
        try
        {
            using var document = JsonDocument.Parse(resultJson);
            var root = document.RootElement;
            foreach (var property in new[] { "stderr", "error", "message", "code" })
            {
                if (!root.TryGetProperty(property, out var value))
                {
                    continue;
                }

                var message = value.ValueKind switch
                {
                    JsonValueKind.String => value.GetString(),
                    JsonValueKind.Array when value.GetArrayLength() > 0 => value[0].GetString(),
                    JsonValueKind.Number => value.GetRawText(),
                    _ => null
                };
                if (!string.IsNullOrWhiteSpace(message))
                {
                    var firstLine = message.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .FirstOrDefault();
                    if (!string.IsNullOrWhiteSpace(firstLine))
                    {
                        return firstLine.Length <= 512 ? firstLine : firstLine[..509] + "...";
                    }
                }
            }
        }
        catch (JsonException)
        {
            return "Historical task result is available.";
        }

        return "Historical task result is available.";
    }

    private sealed record TaskOutputProjection(string? StatusMessage, string? ResultJson, int? ExitCode);

    private static readonly int[] TaskHistoryPageSizes = [10, 20, 50, 100];

    private static IQueryable<TaskHistoryRow> BuildHistoryQuery(
        OrchestratorDbContext db,
        string? search,
        int? tenantId,
        Guid? agentId,
        string? taskType,
        string? status,
        string? requestId)
    {
        IQueryable<JobTaskActivityRecord> activities = db.JobTaskActivities.AsNoTracking();
        if (tenantId.HasValue)
        {
            activities = activities.Where(activity => activity.TenantId == tenantId.Value);
        }
        if (agentId.HasValue)
        {
            activities = activities.Where(activity => activity.AgentId == agentId.Value);
        }
        if (!string.IsNullOrWhiteSpace(taskType))
        {
            activities = activities.Where(activity => activity.TaskType == taskType.Trim());
        }
        if (!string.IsNullOrWhiteSpace(status))
        {
            activities = activities.Where(activity => activity.Status == status.Trim());
        }
        if (!string.IsNullOrWhiteSpace(requestId))
        {
            activities = activities.Where(activity => activity.RequestId == requestId.Trim());
        }

        var query = from activity in activities
                    join agent in db.Agents.AsNoTracking() on activity.AgentId equals (Guid?)agent.Id into agentRows
                    from agent in agentRows.DefaultIfEmpty()
                    select new { Activity = activity, Agent = agent };

        if (!string.IsNullOrWhiteSpace(search))
        {
            var like = $"%{search.Trim()}%";
            query = db.Database.IsNpgsql()
                ? query.Where(row =>
                    EF.Functions.ILike(row.Activity.RequestId, like) || EF.Functions.ILike(row.Activity.TaskType, like) ||
                    EF.Functions.ILike(row.Activity.Status, like) || EF.Functions.ILike(row.Activity.ClientIdentity, like) ||
                    (row.Agent != null && row.Agent.Name != null && EF.Functions.ILike(row.Agent.Name, like)) ||
                    (row.Agent != null && row.Agent.DeviceInfoJson != null && EF.Functions.ILike(row.Agent.DeviceInfoJson, like)))
                : query.Where(row =>
                    EF.Functions.Like(row.Activity.RequestId.ToUpper(), like.ToUpper(), "\\") || EF.Functions.Like(row.Activity.TaskType.ToUpper(), like.ToUpper(), "\\") ||
                    EF.Functions.Like(row.Activity.Status.ToUpper(), like.ToUpper(), "\\") || EF.Functions.Like(row.Activity.ClientIdentity.ToUpper(), like.ToUpper(), "\\") ||
                    (row.Agent != null && row.Agent.Name != null && EF.Functions.Like(row.Agent.Name.ToUpper(), like.ToUpper(), "\\")) ||
                    (row.Agent != null && row.Agent.DeviceInfoJson != null && EF.Functions.Like(row.Agent.DeviceInfoJson.ToUpper(), like.ToUpper(), "\\")));
        }

        return query
            .OrderByDescending(row => row.Activity.CreatedAtUtc)
            .ThenByDescending(row => row.Activity.Id)
            .Select(row => new TaskHistoryRow(
            row.Activity.Id,
            row.Activity.RequestId,
            row.Activity.TenantId,
            row.Activity.TaskType,
            row.Activity.Status,
            row.Activity.Error,
            row.Activity.CreatedAtUtc,
            row.Activity.CompletedAtUtc,
            row.Activity.AgentId,
            row.Agent == null ? null : row.Agent.Name,
            row.Agent == null ? null : row.Agent.DeviceInfoJson,
            row.Agent != null && row.Agent.IsEnabled));
    }

    private static TaskHistoryItemDto MapHistoryItem(TaskHistoryRow row)
    {
        var output = ProjectOutput(new JobTaskActivityInfo(
            (ulong)row.Id,
            row.RequestId,
            null,
            null,
            string.Empty,
            row.TenantId,
            row.TaskType,
            row.Status,
            row.Error,
            row.CreatedAtUtc,
            row.CompletedAtUtc,
            row.AgentId));
        var presentation = row.AgentId is not { } agentId
            ? null
            : AgentDirectoryPresentation.Create(
                row.TenantId ?? 0,
                agentId,
                row.AgentName,
                row.AgentIsEnabled,
                row.AgentDeviceInfoJson,
                string.Empty);
        var primaryTarget = presentation?.HostName ?? presentation?.DisplayName;
        var configuredName = presentation is not null &&
                             !string.Equals(primaryTarget, presentation.DisplayName, StringComparison.OrdinalIgnoreCase)
            ? presentation.DisplayName
            : null;

        return new TaskHistoryItemDto(
            row.Id > int.MaxValue ? 0 : (int)row.Id,
            row.RequestId,
            row.TenantId,
            row.TaskType,
            row.Status,
            output.StatusMessage,
            row.CreatedAtUtc,
            row.CompletedAtUtc,
            primaryTarget,
            presentation?.HostName,
            configuredName,
            row.AgentId);
    }

    private sealed record TaskHistoryRow(
        long Id,
        string RequestId,
        int? TenantId,
        string TaskType,
        string Status,
        string? Error,
        DateTimeOffset CreatedAtUtc,
        DateTimeOffset? CompletedAtUtc,
        Guid? AgentId,
        string? AgentName,
        string? AgentDeviceInfoJson,
        bool AgentIsEnabled);

}
