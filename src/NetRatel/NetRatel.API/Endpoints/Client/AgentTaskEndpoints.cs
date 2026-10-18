using System.Security.Cryptography;
using System.Text;
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
using NetRatel.Shared.Security;

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

        group.MapGet("", async ([FromQuery] string? requestId, HttpContext http, [FromServices] IEffectiveAccessService access, IJobRunService runs, [FromServices] OrchestratorDbContext db, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(requestId))
                return Results.StatusCode(StatusCodes.Status410Gone);

            var activity = await runs.GetActivityByRequestIdAsync(requestId, ct);
            if (activity is null) return Results.NotFound();
            if (!await CanExecuteAsync(http, access, activity.TenantId, ct)) return Results.Forbid();
            var agent = await ResolveAgentAsync(db, activity, ct);
            var tenantName = await ResolveTenantNameAsync(db, activity.TenantId, ct);
            return Results.Ok(new[] { Map(activity, agent, tenantName) });
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
            var activities = await runs.ListTaskActivitiesAsync(ct);

            var visible = await FilterAuthorizedAsync(activities, activity => activity.TenantId, http.User, access, ct);
            var selected = visible
                .Where(x => !tenantId.HasValue || x.TenantId == tenantId)
                .Where(x => !agentId.HasValue || x.AgentId == agentId)
                .Where(x => string.IsNullOrWhiteSpace(taskType) || string.Equals(x.TaskType, taskType, StringComparison.OrdinalIgnoreCase))
                .Where(x => string.IsNullOrWhiteSpace(status) || string.Equals(x.Status, status, StringComparison.OrdinalIgnoreCase))
                .Take(Math.Clamp(limit ?? 25, 1, 100))
                .ToList();
            var tenants = selected.Where(x => x.TenantId.HasValue).Select(x => x.TenantId!.Value).Distinct().ToArray();
            var agentIds = selected.Where(x => x.AgentId.HasValue).Select(x => x.AgentId!.Value).Distinct().ToArray();
            var agents = await db.Agents.AsNoTracking()
                .Where(x => tenants.Contains(x.TenantId) && agentIds.Contains(x.Id))
                .ToDictionaryAsync(x => (x.TenantId, x.Id), ct);
            var tenantNames = await db.Tenants.AsNoTracking().Where(x => tenants.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Name, ct);
            return Results.Ok(selected.Select(x => Map(x,
                x.TenantId is { } tenant && x.AgentId is { } id && agents.TryGetValue((tenant, id), out var agent) ? agent : null,
                x.TenantId is { } tenantIdValue && tenantNames.TryGetValue(tenantIdValue, out var name) ? name : null)).ToList());
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
            IJobRunService runs,
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

            // Establish the visible activity tenants before resolving directory metadata.
            var activityTenants = await db.JobTaskActivities.AsNoTracking().Select(x => x.TenantId).Distinct().ToListAsync(ct);
            var authorizedTenants = await FilterAuthorizedAsync(activityTenants, id => id, http.User, access, ct);
            var query = BuildHistoryQuery(db, authorizedTenants.ToArray(), search, tenantId, agentId, taskType, status, requestId);
            var total = await query.CountAsync(ct);
            var items = await query.Skip(resolvedPage * resolvedPageSize).Take(resolvedPageSize).ToListAsync(ct);

            var projected = new List<TaskHistoryItemDto>();
            foreach (var item in items)
            {
                var safe = await runs.GetActivityByIdAsync((ulong)item.Id, ct);
                if (safe is not null) projected.Add(MapHistoryItem(item with { Error = safe.Error, CommandLabel = safe.CommandLabel }));
            }
            return Results.Ok(new TaskHistoryPageDto(
                projected,
                total,
                resolvedPage,
                resolvedPageSize));
        });

        group.MapGet("/{id:long}", async (ulong id, HttpContext http, [FromServices] IEffectiveAccessService access, IJobRunService runs, [FromServices] OrchestratorDbContext db, CancellationToken ct) =>
        {
            var activity = await runs.GetActivityByIdAsync(id, ct);
            if (activity is null) return Results.NotFound();
            if (!await CanExecuteAsync(http, access, activity.TenantId, ct)) return Results.Forbid();
            var agent = await ResolveAgentAsync(db, activity, ct);
            return Results.Ok(Map(activity, agent, await ResolveTenantNameAsync(db, activity.TenantId, ct)));
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
            if (!Enum.IsDefined(request.Environment))
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["environment"] = ["Environment must be None, Dev, Prod, or Both."] });

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
            // A caller-selected key must not reveal or re-dispatch an existing execution.
            if (await runs.GetActivityByRequestIdAsync(requestId, ct) is not null)
                return Results.Conflict(new { code = "request_id_in_use" });
            var presentation = CreatePresentation(agent);
            var metadata = BuildExecutionMetadata(taskType, payload.Value!, payload.ScriptName);
            var created = await runs.CreateTaskActivityAsync(new CreateJobTaskActivityCommand(
                requestId, null, null, string.Empty, request.TenantId, taskType, "Pending", null, DateTimeOffset.UtcNow, null, request.AgentId,
                Environment: request.Environment,
                TargetDisplayName: presentation.HostName ?? presentation.DisplayName,
                TargetHostName: presentation.HostName,
                TargetName: ConfiguredName(presentation),
                ScriptName: metadata.ScriptName,
                ScriptType: metadata.ScriptType,
                CommandLabel: metadata.CommandLabel,
                ScriptContentHash: metadata.ScriptContentHash,
                OutputPolicy: payload.OutputPolicy), ct);

            if (created.TenantId != request.TenantId || created.AgentId != request.AgentId)
                return Results.Conflict(new { code = "request_id_in_use" });

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
            return Results.Created($"/api/v2/tasks/{created.Id}", Map(created, agent, await ResolveTenantNameAsync(db, created.TenantId, ct)));
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

    internal static async Task<PreparedTaskPayload> BuildPayloadAsync(TaskCreateRequestDto request, string taskType, IScriptService scripts, CancellationToken ct)
    {
        if (request.TaskType is TaskKinds.Legacy_RunPowerShell or TaskKinds.Legacy_ExecPs)
        {
            if (request.ShellCommand?.EnvironmentReferences is { Count: > 0 })
                return (null, "EnvironmentReferences require exec-shell-cmd; legacy PowerShell aliases cannot carry that client-local requirement.");
            var script = request.ShellCommand?.Command ?? request.Payload;
            if (request.TaskType == TaskKinds.Legacy_ExecPs && request.ShellCommand is null)
            {
                try
                {
                    using var document = JsonDocument.Parse(script ?? string.Empty);
                    script = document.RootElement.ValueKind == JsonValueKind.Object &&
                             document.RootElement.TryGetProperty("script", out var value) && value.ValueKind == JsonValueKind.String
                        ? value.GetString()
                        : null;
                }
                catch (JsonException) { script = null; }
            }
            var outputPolicy = TaskOutputPolicy.Create([]);
            return string.IsNullOrWhiteSpace(script)
                ? (null, "A PowerShell script is required for the legacy task kind.")
                : new PreparedTaskPayload(JsonSerializer.Serialize(new ExecLibraryScriptPayload
                {
                    ScriptType = ScriptType.PowerShell,
                    ScriptContent = script,
                    Preferred = request.ShellCommand?.Preferred ?? request.Preferred,
                    Parameters = request.Parameters,
                    WorkingDirectory = request.ShellCommand?.WorkingDirectory ?? request.WorkingDirectory,
                    TimeoutSeconds = request.ShellCommand?.TimeoutSeconds ?? request.TimeoutSeconds,
                    OutputPolicy = outputPolicy
                }), null, OutputPolicy: outputPolicy);
        }
        if (taskType == TaskKinds.ExecShellCommand)
        {
            var command = request.ShellCommand?.Command ?? request.Payload;
            if (string.IsNullOrWhiteSpace(command)) return (null, "ShellCommand or Payload is required for exec-shell-cmd.");
            var outputPolicy = TaskOutputPolicy.Create([], environmentReferences: request.ShellCommand?.EnvironmentReferences);
            return new PreparedTaskPayload(JsonSerializer.Serialize(new ExecShellCommandPayload
            {
                Command = command, Preferred = request.ShellCommand?.Preferred ?? request.Preferred,
                WorkingDirectory = request.ShellCommand?.WorkingDirectory ?? request.WorkingDirectory,
                TimeoutSeconds = request.ShellCommand?.TimeoutSeconds ?? request.TimeoutSeconds,
                EnvironmentReferences = request.ShellCommand?.EnvironmentReferences,
                OutputPolicy = outputPolicy
            }), null, OutputPolicy: outputPolicy);
        }
        if (taskType == TaskKinds.ExecLibraryScript)
        {
            if (request.ScriptId is not { } scriptId || scriptId <= 0) return (null, "ScriptId is required for exec-library-script.");
            var script = await scripts.GetAsync((ulong)scriptId, ct);
            if (script is null) return (null, $"Script {scriptId} not found.");
            var type = request.ScriptType ?? (Enum.TryParse<ScriptType>(script.ScriptType, true, out var parsed) ? parsed : ScriptType.PowerShell);
            var parameters = await scripts.GetParamsAsync((ulong)scriptId, ct);
            var current = await scripts.GetAsync((ulong)scriptId, ct);
            if (current?.SourceRevision != script.SourceRevision) return (null, "The script changed while preparing execution; reload and retry.");
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(script.Content))).ToLowerInvariant();
            var outputPolicy = TaskOutputPolicy.Create(parameters.Select(p => (p.Name, p.Type)), scriptId, script.SourceRevision, hash);
            return new PreparedTaskPayload(JsonSerializer.Serialize(new ExecLibraryScriptPayload
            {
                ScriptId = scriptId, ScriptType = type, ScriptContent = script.Content, Preferred = request.Preferred,
                Parameters = request.Parameters, WorkingDirectory = request.WorkingDirectory, TimeoutSeconds = request.TimeoutSeconds,
                OutputPolicy = outputPolicy
            }), null, script.Name, outputPolicy);
        }
        return taskType is TaskKinds.OsInfo or TaskKinds.ProcessesList or TaskKinds.ProcessesTopCpu or TaskKinds.DiskFree
            ? (request.Payload ?? string.Empty, null)
            : (null, $"Unsupported TaskType '{taskType}'.");
    }

    internal static string NormalizeTaskType(string value) => value switch
    {
        TaskKinds.Legacy_RunPowerShell or TaskKinds.Legacy_ExecPs => TaskKinds.ExecLibraryScript,
        TaskKinds.Legacy_ExecSh => TaskKinds.ExecShellCommand,
        TaskKinds.Legacy_RunLibraryScript => TaskKinds.ExecLibraryScript,
        _ => value
    };

    private static Task<Agent?> ResolveAgentAsync(OrchestratorDbContext db, JobTaskActivityInfo activity, CancellationToken ct) =>
        activity.TenantId is { } tenantId && activity.AgentId is { } agentId
            ? db.Agents.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == agentId, ct)
            : Task.FromResult<Agent?>(null);

    private static Task<string?> ResolveTenantNameAsync(OrchestratorDbContext db, int? tenantId, CancellationToken ct) =>
        tenantId is { } id
            ? db.Tenants.AsNoTracking().Where(x => x.Id == id).Select(x => (string?)x.Name).SingleOrDefaultAsync(ct)
            : Task.FromResult<string?>(null);

    private static AgentDirectoryPresentation CreatePresentation(Agent agent) =>
        AgentDirectoryPresentation.Create(agent.TenantId, agent.Id, agent.Name, agent.IsEnabled,
            agent.DeviceInfoJson, string.Empty, agent.Tags, agent.FriendlyName);

    private static string? ConfiguredName(AgentDirectoryPresentation presentation) =>
        !string.Equals(presentation.HostName ?? presentation.DisplayName, presentation.DisplayName, StringComparison.OrdinalIgnoreCase)
            ? presentation.DisplayName : null;

    internal static TaskDto Map(JobTaskActivityInfo activity, Agent? agent, string? tenantName = null)
    {
        var output = ProjectOutput(activity);
        // Directory metadata must belong to the activity's authorized target.
        var presentation = agent is not null && agent.TenantId == activity.TenantId && agent.Id == activity.AgentId
            ? CreatePresentation(agent) : null;
        var hasTargetSnapshot = activity.TargetDisplayName is not null;
        return new(
            activity.Id > int.MaxValue ? 0 : (int)activity.Id,
            activity.RequestId,
            activity.ClientIdentity,
            activity.TenantId,
            activity.Environment,
            activity.TaskType,
            activity.Status,
            output.StatusMessage,
            output.ResultJson,
            activity.CreatedAtUtc,
            activity.StartedAtUtc,
            activity.CompletedAtUtc,
            activity.ExitCode ?? output.ExitCode,
            hasTargetSnapshot ? activity.TargetDisplayName : presentation?.HostName ?? presentation?.DisplayName ?? LegacyTargetLabel(activity.AgentId, activity.ClientIdentity),
            hasTargetSnapshot ? activity.TargetHostName : presentation?.HostName,
            hasTargetSnapshot ? activity.TargetName : presentation is null ? null : ConfiguredName(presentation),
            activity.AgentId,
            activity.ScriptName,
            activity.ScriptType,
            activity.CommandLabel,
            tenantName);
    }

    private static string? LegacyTargetLabel(Guid? agentId, string? clientIdentity) =>
        agentId is null && !string.IsNullOrWhiteSpace(clientIdentity) ? $"Historical target: {clientIdentity.Trim()}" : null;

    internal static ExecutionMetadata BuildExecutionMetadata(string taskType, string payload, string? scriptName)
    {
        if (taskType == TaskKinds.ExecLibraryScript)
        {
            var invocation = JsonSerializer.Deserialize<ExecLibraryScriptPayload>(payload)!;
            return new(scriptName ?? "PowerShell script", invocation.ScriptType.ToString(), null,
                invocation.ScriptContent is null ? null : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(invocation.ScriptContent))).ToLowerInvariant());
        }
        if (taskType == TaskKinds.ExecShellCommand)
        {
            var invocation = JsonSerializer.Deserialize<ExecShellCommandPayload>(payload)!;
            var command = OperatorOutputRedactor.Redact(invocation.Command).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
            return new(null, null, command?.Length > 160 ? command[..157] + "..." : command, null);
        }
        return new(null, null, null, null);
    }

    internal sealed record PreparedTaskPayload(string? Value, string? Error, string? ScriptName = null, TaskOutputPolicy? OutputPolicy = null)
    {
        public static implicit operator PreparedTaskPayload((string? Value, string? Error) result) => new(result.Value, result.Error);
    }

    internal sealed record ExecutionMetadata(string? ScriptName, string? ScriptType, string? CommandLabel, string? ScriptContentHash);

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
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("exitCode", out var value))
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
        int?[] authorizedTenants,
        string? search,
        int? tenantId,
        Guid? agentId,
        string? taskType,
        string? status,
        string? requestId)
    {
        IQueryable<JobTaskActivityRecord> activities = db.JobTaskActivities.AsNoTracking()
            .Where(activity => authorizedTenants.Contains(activity.TenantId));
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
                    join agent in db.Agents.AsNoTracking()
                        on new { activity.TenantId, activity.AgentId } equals new { TenantId = (int?)agent.TenantId, AgentId = (Guid?)agent.Id } into agentRows
                    from agent in agentRows.DefaultIfEmpty()
                    join tenant in db.Tenants.AsNoTracking() on activity.TenantId equals (int?)tenant.Id into tenantRows
                    from tenant in tenantRows.DefaultIfEmpty()
                    select new { Activity = activity, Agent = agent, TenantName = tenant == null ? null : tenant.Name };

        if (!string.IsNullOrWhiteSpace(search))
        {
            var like = $"%{search.Trim()}%";
            query = db.Database.IsNpgsql()
                ? query.Where(row =>
                    EF.Functions.ILike(row.Activity.RequestId, like) || EF.Functions.ILike(row.Activity.TaskType, like) ||
                    EF.Functions.ILike(row.Activity.Status, like) || EF.Functions.ILike(row.Activity.ClientIdentity, like) ||
                    (row.Agent != null && row.Agent.Name != null && EF.Functions.ILike(row.Agent.Name, like)) ||
                    (row.Agent != null && row.Agent.DeviceInfoJson != null && EF.Functions.ILike(row.Agent.DeviceInfoJson, like)) ||
                    (row.Activity.TargetDisplayName != null && EF.Functions.ILike(row.Activity.TargetDisplayName, like)) ||
                    (row.Activity.ScriptName != null && EF.Functions.ILike(row.Activity.ScriptName, like)) ||
                    (row.Activity.CommandLabel != null && EF.Functions.ILike(row.Activity.CommandLabel, like)))
                : query.Where(row =>
                    EF.Functions.Like(row.Activity.RequestId.ToUpper(), like.ToUpper(), "\\") || EF.Functions.Like(row.Activity.TaskType.ToUpper(), like.ToUpper(), "\\") ||
                    EF.Functions.Like(row.Activity.Status.ToUpper(), like.ToUpper(), "\\") || EF.Functions.Like(row.Activity.ClientIdentity.ToUpper(), like.ToUpper(), "\\") ||
                    (row.Agent != null && row.Agent.Name != null && EF.Functions.Like(row.Agent.Name.ToUpper(), like.ToUpper(), "\\")) ||
                    (row.Agent != null && row.Agent.DeviceInfoJson != null && EF.Functions.Like(row.Agent.DeviceInfoJson.ToUpper(), like.ToUpper(), "\\")) ||
                    (row.Activity.TargetDisplayName != null && EF.Functions.Like(row.Activity.TargetDisplayName.ToUpper(), like.ToUpper(), "\\")) ||
                    (row.Activity.ScriptName != null && EF.Functions.Like(row.Activity.ScriptName.ToUpper(), like.ToUpper(), "\\")) ||
                    (row.Activity.CommandLabel != null && EF.Functions.Like(row.Activity.CommandLabel.ToUpper(), like.ToUpper(), "\\")));
        }

        return query
            .OrderByDescending(row => row.Activity.CreatedAtUtc)
            .ThenByDescending(row => row.Activity.Id)
            .Select(row => new TaskHistoryRow(
            row.Activity.Id,
            row.Activity.RequestId,
            row.Activity.ClientIdentity,
            row.Activity.TenantId,
            row.Activity.TaskType,
            row.Activity.Status,
            row.Activity.OutputProtectionJson == null && (row.Activity.TaskType == TaskKinds.ExecLibraryScript || row.Activity.TaskType == TaskKinds.Legacy_RunLibraryScript)
                ? row.Activity.Error == null ? null : "[REDACTED]" : row.Activity.Error,
            row.Activity.CreatedAtUtc,
            row.Activity.CompletedAtUtc,
            row.Activity.AgentId,
            row.Agent == null ? null : row.Agent.Name,
            row.Agent == null ? null : row.Agent.FriendlyName,
            row.Agent == null ? null : row.Agent.DeviceInfoJson,
            row.Agent != null && row.Agent.IsEnabled,
            row.Agent != null,
            row.Activity.Environment,
            row.Activity.StartedAtUtc,
            row.Activity.ExitCode,
            row.Activity.TargetDisplayName,
            row.Activity.TargetHostName,
            row.Activity.TargetName,
            row.Activity.ScriptName,
            row.Activity.ScriptType,
            row.Activity.OutputProtectionJson == null && (row.Activity.TaskType == TaskKinds.ExecLibraryScript || row.Activity.TaskType == TaskKinds.Legacy_RunLibraryScript)
                ? row.Activity.CommandLabel == null ? null : "[REDACTED]" : row.Activity.CommandLabel,
            row.TenantName));
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
        var presentation = row.AgentId is not { } agentId || !row.AgentExists
            ? null
            : AgentDirectoryPresentation.Create(
                row.TenantId ?? 0,
                agentId,
                row.AgentName,
                row.AgentIsEnabled,
                row.AgentDeviceInfoJson,
                string.Empty,
                friendlyName: row.AgentFriendlyName);
        var primaryTarget = presentation?.HostName ?? presentation?.DisplayName;
        var configuredName = presentation is not null &&
                             !string.Equals(primaryTarget, presentation.DisplayName, StringComparison.OrdinalIgnoreCase)
            ? presentation.DisplayName
            : null;

        var hasTargetSnapshot = row.TargetDisplayName is not null;
        return new TaskHistoryItemDto(
            row.Id > int.MaxValue ? 0 : (int)row.Id,
            row.RequestId,
            row.TenantId,
            row.TaskType,
            row.Status,
            output.StatusMessage,
            row.CreatedAtUtc,
            row.CompletedAtUtc,
            hasTargetSnapshot ? row.TargetDisplayName : primaryTarget ?? LegacyTargetLabel(row.AgentId, row.ClientIdentity),
            hasTargetSnapshot ? row.TargetHostName : presentation?.HostName,
            hasTargetSnapshot ? row.TargetName : configuredName,
            row.AgentId,
            row.Environment,
            row.StartedAtUtc,
            row.ExitCode,
            row.ScriptName,
            row.ScriptType,
            row.CommandLabel,
            row.TenantName);
    }

    private sealed record TaskHistoryRow(
        long Id,
        string RequestId,
        string ClientIdentity,
        int? TenantId,
        string TaskType,
        string Status,
        string? Error,
        DateTimeOffset CreatedAtUtc,
        DateTimeOffset? CompletedAtUtc,
        Guid? AgentId,
        string? AgentName,
        string? AgentFriendlyName,
        string? AgentDeviceInfoJson,
        bool AgentIsEnabled,
        bool AgentExists,
        ClientEnvironment? Environment,
        DateTimeOffset? StartedAtUtc,
        int? ExitCode,
        string? TargetDisplayName,
        string? TargetHostName,
        string? TargetName,
        string? ScriptName,
        string? ScriptType,
        string? CommandLabel,
        string? TenantName);

}
