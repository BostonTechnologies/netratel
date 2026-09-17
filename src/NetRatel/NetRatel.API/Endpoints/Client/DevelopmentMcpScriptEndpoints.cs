using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NetRatel.API.Endpoints.Client;
using NetRatel.API.Gateway;
using NetRatel.Application.Events;
using NetRatel.Application.Jobs;
using NetRatel.Application.Operations;
using NetRatel.Application.Presence;
using NetRatel.Application.Scripts;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.Execution;
using NetRatel.Shared.Contracts.Tasks;

namespace NetRatel.API.Endpoints;

/// <summary>
/// Development-only marker-script adapter. The ordinary script library is
/// global, so this surface creates only server-generated harmless scripts and
/// persists target ownership before allowing later reads or lifecycle changes.
/// </summary>
public static partial class DevelopmentMcpScriptEndpoints
{
    internal const string StandardExecutionMode = "standard";
    internal const string CancellationProbeExecutionMode = "cancellation_probe";
    private const int MaxScripts = 100;
    private static readonly Regex MarkerPattern = MarkerRegex();

    public static IEndpointRouteBuilder MapDevelopmentMcpScriptEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v2/development/mcp/agents/{tenantId:int}/{agentId:guid}/scripts")
            .WithTags("Development MCP Scripts")
            .RequireAuthorization("Operator");

        group.MapGet("", ListAsync);
        group.MapGet("/{scriptId:long}", GetAsync);
        group.MapGet("/{scriptId:long}/params", ParamsAsync);
        group.MapPost("", CreateAsync);
        group.MapPut("/{scriptId:long}", UpdateAsync);
        group.MapPost("/{scriptId:long}/parse-manifest", ParseManifestAsync);
        group.MapPost("/{scriptId:long}/runs", RunAsync);
        group.MapDelete("/{scriptId:long}", DeleteAsync);
        return app;
    }

    private static async Task<IResult> ListAsync(
        int tenantId,
        Guid agentId,
        HttpContext http,
        [FromServices] OrchestratorDbContext db,
        ICorrelationContext correlation,
        IDevelopmentOperatorTargetAuthority targets,
        CancellationToken cancellationToken)
    {
        if (await RequireAcceptedAsync(http, tenantId, agentId, correlation, targets, cancellationToken).ConfigureAwait(false) is { } rejection)
        {
            return rejection;
        }

        var scripts = await (
            from owned in db.DevelopmentMcpScripts.AsNoTracking()
            join script in db.Scripts.AsNoTracking() on owned.ScriptId equals script.Id
            where owned.TenantId == tenantId && owned.AgentId == agentId && owned.DeletedAtUtc == null
            orderby owned.CreatedAtUtc descending
            select new DevelopmentMcpScriptDto(
                checked((ulong)script.Id),
                script.Name,
                script.FolderPath,
                script.Description,
                script.Content,
                script.ScriptType,
                owned.Marker,
                owned.Shell,
                owned.ExecutionMode,
                script.CreatedAtUtc,
                script.UpdatedAtUtc))
            .Take(MaxScripts)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return Results.Ok(scripts);
    }

    private static async Task<IResult> GetAsync(
        int tenantId,
        Guid agentId,
        long scriptId,
        HttpContext http,
        [FromServices] OrchestratorDbContext db,
        ICorrelationContext correlation,
        IDevelopmentOperatorTargetAuthority targets,
        CancellationToken cancellationToken)
    {
        var ownership = await RequireOwnershipAsync(tenantId, agentId, scriptId, http, db, correlation, targets, cancellationToken).ConfigureAwait(false);
        if (ownership.Error is not null) return ownership.Error;

        var script = await db.Scripts.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == scriptId, cancellationToken).ConfigureAwait(false);
        return script is null ? Results.NotFound(new { code = "marker_script_not_found" }) : Results.Ok(ToDto(ownership.Record!, script));
    }

    private static async Task<IResult> ParamsAsync(
        int tenantId,
        Guid agentId,
        long scriptId,
        HttpContext http,
        [FromServices] OrchestratorDbContext db,
        ICorrelationContext correlation,
        IDevelopmentOperatorTargetAuthority targets,
        CancellationToken cancellationToken)
    {
        var ownership = await RequireOwnershipAsync(tenantId, agentId, scriptId, http, db, correlation, targets, cancellationToken).ConfigureAwait(false);
        if (ownership.Error is not null) return ownership.Error;

        var parameters = await db.ScriptParameters.AsNoTracking()
            .Where(candidate => candidate.ScriptId == scriptId)
            .OrderBy(candidate => candidate.Id)
            .Select(candidate => new DevelopmentMcpScriptParameterDto(candidate.Id, candidate.Name, candidate.Type, candidate.Required, candidate.Default, candidate.Description, candidate.OptionsJson))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return Results.Ok(parameters);
    }

    private static async Task<IResult> CreateAsync(
        int tenantId,
        Guid agentId,
        CreateDevelopmentMcpMarkerScriptRequest request,
        HttpContext http,
        [FromServices] OrchestratorDbContext db,
        IScriptService scripts,
        IEventRecorder events,
        ICorrelationContext correlation,
        IDevelopmentOperatorTargetAuthority targets,
        CancellationToken cancellationToken)
    {
        if (!TryNormalizeCreate(request, out var marker, out var name, out var description, out var shell, out var executionMode, out var validationError))
        {
            return Results.BadRequest(new { code = "marker_script_invalid", detail = validationError });
        }

        if (await RequireAcceptedAsync(http, tenantId, agentId, correlation, targets, cancellationToken).ConfigureAwait(false) is { } rejection)
        {
            return rejection;
        }

        var grant = await targets.GetActiveGrantAsync(tenantId, agentId, cancellationToken).ConfigureAwait(false);
        if (grant is null)
        {
            return TargetRejected("target_authorization_changed");
        }

        var created = await scripts.CreateAsync(new CreateScriptCommand(
            name,
            Folder(tenantId, agentId),
            description,
            Content(marker, shell, executionMode),
            ScriptType(shell),
            Manifest(marker)), cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var record = new DevelopmentMcpScriptRecord
        {
            Id = Guid.NewGuid(),
            ScriptId = checked((long)created.Id),
            TenantId = tenantId,
            AgentId = agentId,
            TargetGrantId = grant.GrantId,
            Marker = marker,
            Shell = shell,
            ExecutionMode = executionMode,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };
        db.DevelopmentMcpScripts.Add(record);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await events.RecordAsync(new DomainEvent
        {
            EventType = NetRatelEventTypes.Script.Created,
            Source = "DevelopmentMcp",
            CorrelationId = correlation.GetOrCreate(),
            TenantId = tenantId.ToString(),
            EntityId = created.Id.ToString(),
            Severity = "Info",
            Message = $"Development marker script {created.Id} created.",
            Payload = new { scriptId = created.Id, tenantId, agentId, ownershipId = record.Id }
        }, cancellationToken).ConfigureAwait(false);
        return Results.Created($"/api/v2/development/mcp/agents/{tenantId}/{agentId:D}/scripts/{created.Id}", ToDto(record, created));
    }

    private static async Task<IResult> UpdateAsync(
        int tenantId,
        Guid agentId,
        long scriptId,
        UpdateDevelopmentMcpMarkerScriptRequest request,
        HttpContext http,
        [FromServices] OrchestratorDbContext db,
        IScriptService scripts,
        IEventRecorder events,
        ICorrelationContext correlation,
        IDevelopmentOperatorTargetAuthority targets,
        CancellationToken cancellationToken)
    {
        if (!TryNormalizeUpdate(request, out var marker, out var name, out var description, out var validationError))
        {
            return Results.BadRequest(new { code = "marker_script_invalid", detail = validationError });
        }

        var ownership = await RequireOwnershipAsync(tenantId, agentId, scriptId, http, db, correlation, targets, cancellationToken).ConfigureAwait(false);
        if (ownership.Error is not null) return ownership.Error;

        var updated = await scripts.UpdateAsync(new UpdateScriptCommand(
            checked((ulong)scriptId),
            name,
            Folder(tenantId, agentId),
            description,
            marker is null ? null : Content(marker, ownership.Record!.Shell, ownership.Record.ExecutionMode),
            ScriptType(ownership.Record!.Shell),
            marker is null ? null : Manifest(marker)), cancellationToken).ConfigureAwait(false);
        if (updated is null) return Results.NotFound(new { code = "marker_script_not_found" });

        if (marker is not null) ownership.Record!.Marker = marker;
        ownership.Record!.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await events.RecordAsync(new DomainEvent
        {
            EventType = NetRatelEventTypes.Script.Updated,
            Source = "DevelopmentMcp",
            CorrelationId = correlation.GetOrCreate(),
            TenantId = tenantId.ToString(),
            EntityId = updated.Id.ToString(),
            Severity = "Info",
            Message = $"Development marker script {updated.Id} updated.",
            Payload = new { scriptId = updated.Id, tenantId, agentId, ownershipId = ownership.Record.Id }
        }, cancellationToken).ConfigureAwait(false);
        return Results.Ok(ToDto(ownership.Record!, updated));
    }

    private static async Task<IResult> ParseManifestAsync(
        int tenantId,
        Guid agentId,
        long scriptId,
        HttpContext http,
        [FromServices] OrchestratorDbContext db,
        IScriptService scripts,
        ICorrelationContext correlation,
        IDevelopmentOperatorTargetAuthority targets,
        CancellationToken cancellationToken)
    {
        var ownership = await RequireOwnershipAsync(tenantId, agentId, scriptId, http, db, correlation, targets, cancellationToken).ConfigureAwait(false);
        if (ownership.Error is not null) return ownership.Error;
        var script = await scripts.ParseManifestAsync(checked((ulong)scriptId), Manifest(ownership.Record!.Marker), cancellationToken).ConfigureAwait(false);
        return script is null ? Results.NotFound(new { code = "marker_script_not_found" }) : Results.NoContent();
    }

    private static async Task<IResult> DeleteAsync(
        int tenantId,
        Guid agentId,
        long scriptId,
        HttpContext http,
        [FromServices] OrchestratorDbContext db,
        IScriptService scripts,
        IEventRecorder events,
        ICorrelationContext correlation,
        IDevelopmentOperatorTargetAuthority targets,
        CancellationToken cancellationToken)
    {
        var ownership = await RequireOwnershipAsync(tenantId, agentId, scriptId, http, db, correlation, targets, cancellationToken).ConfigureAwait(false);
        if (ownership.Error is not null) return ownership.Error;
        if (await scripts.DeleteAsync(checked((ulong)scriptId), cancellationToken).ConfigureAwait(false) is null)
        {
            return Results.NotFound(new { code = "marker_script_not_found" });
        }

        ownership.Record!.DeletedAtUtc = DateTimeOffset.UtcNow;
        ownership.Record.UpdatedAtUtc = ownership.Record.DeletedAtUtc.Value;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await events.RecordAsync(new DomainEvent
        {
            EventType = NetRatelEventTypes.Script.Deleted,
            Source = "DevelopmentMcp",
            CorrelationId = correlation.GetOrCreate(),
            TenantId = tenantId.ToString(),
            EntityId = scriptId.ToString(),
            Severity = "Info",
            Message = $"Development marker script {scriptId} deleted.",
            Payload = new { scriptId, tenantId, agentId, ownership.Record.Id }
        }, cancellationToken).ConfigureAwait(false);
        return Results.NoContent();
    }

    /// <summary>
    /// Dispatches one target-owned, server-generated marker script. This is
    /// intentionally separate from the general task endpoint: it never accepts
    /// shell content, parameters, a working directory, a timeout, or a caller
    /// supplied request identifier.
    /// </summary>
    private static async Task<IResult> RunAsync(
        int tenantId,
        Guid agentId,
        long scriptId,
        HttpContext http,
        [FromServices] OrchestratorDbContext db,
        IScriptService scripts,
        IJobRunService runs,
        IAgentCommandAuthorityDispatcher commands,
        IEventRecorder events,
        ICorrelationContext correlation,
        IDevelopmentOperatorTargetAuthority targets,
        CancellationToken cancellationToken)
    {
        var ownership = await RequireOwnershipAsync(tenantId, agentId, scriptId, http, db, correlation, targets, cancellationToken).ConfigureAwait(false);
        if (ownership.Error is not null) return ownership.Error;

        if (await DevelopmentOperatorTargetGate.RequireAcceptedAsync(
                http,
                tenantId,
                agentId,
                DevelopmentOperatorOperation.TaskCreate,
                correlation,
                targets,
                cancellationToken).ConfigureAwait(false) is { } taskRejection)
        {
            return taskRejection;
        }

        var agent = await db.Agents.SingleOrDefaultAsync(candidate => candidate.TenantId == tenantId && candidate.Id == agentId, cancellationToken).ConfigureAwait(false);
        if (agent is null) return Results.NotFound(new { code = "agent_not_found" });
        if (!agent.IsEnabled || agent.Status == AgentStatus.Disabled) return Results.Conflict(new { code = "agent_not_enabled" });

        var target = new ClientKey(tenantId, agentId);
        if (!commands.IsAvailable(target)) return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);

        var script = await scripts.GetAsync(checked((ulong)scriptId), cancellationToken).ConfigureAwait(false);
        if (script is null) return Results.NotFound(new { code = "marker_script_not_found" });
        if (!string.Equals(script.Content, Content(ownership.Record!.Marker, ownership.Record.Shell), StringComparison.Ordinal) ||
            !string.Equals(script.ScriptType, ScriptType(ownership.Record.Shell), StringComparison.OrdinalIgnoreCase))
        {
            return Results.Conflict(new { code = "marker_script_integrity_invalid" });
        }

        var scriptType = ownership.Record!.Shell == "sh"
            ? NetRatel.Shared.Contracts.Execution.ScriptType.Bash
            : NetRatel.Shared.Contracts.Execution.ScriptType.PowerShell;
        var payload = JsonSerializer.Serialize(new ExecLibraryScriptPayload
        {
            ScriptId = checked((int)scriptId),
            ScriptType = scriptType,
            ScriptContent = script.Content
        });
        var requestId = Guid.NewGuid().ToString("N");
        var created = await runs.CreateTaskActivityAsync(new CreateJobTaskActivityCommand(
            requestId,
            null,
            null,
            string.Empty,
            tenantId,
            TaskKinds.ExecLibraryScript,
            "Pending",
            null,
            DateTimeOffset.UtcNow,
            null,
            agentId), cancellationToken).ConfigureAwait(false);

        try
        {
            await commands.DispatchAsync(target, requestId, correlation.GetOrCreate(), TaskKinds.ExecLibraryScript, payload, environment: 0, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await runs.UpdateTaskActivityStatusAsync(new UpdateJobTaskActivityStatusCommand(requestId, "Failed", "dispatch_failed", DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
            throw;
        }

        await events.RecordAsync(new DomainEvent
        {
            EventType = NetRatelEventTypes.Task.Submitted,
            Source = "DevelopmentMcp",
            CorrelationId = correlation.GetOrCreate(),
            TenantId = tenantId.ToString(),
            EntityId = requestId,
            Severity = "Info",
            Message = $"Development marker script {scriptId} submitted for agent {agentId:D}.",
            Payload = new { requestId, scriptId, tenantId, agentId, ownershipId = ownership.Record.Id }
        }, cancellationToken).ConfigureAwait(false);
        return Results.Accepted(
            $"/api/v2/tasks/{created.Id}",
            new DevelopmentMcpMarkerScriptRunDto(created.Id, requestId, checked((ulong)scriptId), tenantId, agentId, "Pending"));
    }

    private static async Task<(DevelopmentMcpScriptRecord? Record, IResult? Error)> RequireOwnershipAsync(
        int tenantId,
        Guid agentId,
        long scriptId,
        HttpContext http,
        OrchestratorDbContext db,
        ICorrelationContext correlation,
        IDevelopmentOperatorTargetAuthority targets,
        CancellationToken cancellationToken)
    {
        if (await RequireAcceptedAsync(http, tenantId, agentId, correlation, targets, cancellationToken).ConfigureAwait(false) is { } rejection)
        {
            return (null, rejection);
        }

        var record = await db.DevelopmentMcpScripts.SingleOrDefaultAsync(candidate =>
            candidate.ScriptId == scriptId && candidate.TenantId == tenantId && candidate.AgentId == agentId && candidate.DeletedAtUtc == null,
            cancellationToken).ConfigureAwait(false);
        return record is null ? (null, Results.NotFound(new { code = "marker_script_not_found" })) : (record, null);
    }

    private static async Task<IResult?> RequireAcceptedAsync(
        HttpContext http,
        int tenantId,
        Guid agentId,
        ICorrelationContext correlation,
        IDevelopmentOperatorTargetAuthority targets,
        CancellationToken cancellationToken)
        => await DevelopmentOperatorTargetGate.RequireAcceptedAsync(http, tenantId, agentId, DevelopmentOperatorOperation.ScriptMutation, correlation, targets, cancellationToken).ConfigureAwait(false);

    private static bool TryNormalizeCreate(CreateDevelopmentMcpMarkerScriptRequest? request, out string marker, out string name, out string description, out string shell, out string executionMode, out string error)
    {
        marker = NormalizeMarker(request?.Marker);
        name = NormalizeText(request?.Name, 120) ?? marker;
        description = NormalizeText(request?.Description, 512) ?? $"Development MCP marker script: {marker}";
        shell = NormalizeShell(request?.Shell);
        executionMode = NormalizeExecutionMode(request?.ExecutionMode);
        error = string.Empty;
        if (marker.Length == 0) error = "marker must be 1-128 ASCII letters, digits, underscore, or hyphen.";
        else if (shell.Length == 0) error = "shell must be sh or powershell.";
        else if (executionMode.Length == 0) error = "executionMode must be standard or cancellation_probe.";
        return error.Length == 0;
    }

    private static bool TryNormalizeUpdate(UpdateDevelopmentMcpMarkerScriptRequest? request, out string? marker, out string? name, out string? description, out string error)
    {
        marker = request?.Marker is null ? null : NormalizeMarker(request.Marker);
        name = request?.Name is null ? null : NormalizeText(request.Name, 120);
        description = request?.Description is null ? null : NormalizeText(request.Description, 512);
        error = string.Empty;
        if (request is null || (marker is null && name is null && description is null)) error = "at least one of marker, name, or description is required.";
        else if (request.Marker is not null && marker!.Length == 0) error = "marker must be 1-128 ASCII letters, digits, underscore, or hyphen.";
        else if (request.Name is not null && string.IsNullOrWhiteSpace(name)) error = "name must contain 1-120 non-whitespace characters.";
        else if (request.Description is not null && string.IsNullOrWhiteSpace(description)) error = "description must contain 1-512 non-whitespace characters.";
        return error.Length == 0;
    }

    private static string NormalizeMarker(string? value)
    {
        var marker = value?.Trim() ?? string.Empty;
        return marker.Length <= 128 && MarkerPattern.IsMatch(marker) ? marker : string.Empty;
    }

    private static string? NormalizeText(string? value, int maximum)
    {
        if (value is null)
        {
            return null;
        }

        var normalized = value.Trim();
        return normalized.Length > 0 && normalized.Length <= maximum ? normalized : null;
    }

    private static string NormalizeShell(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "sh" => "sh",
        "powershell" => "powershell",
        _ => string.Empty
    };

    private static string NormalizeExecutionMode(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or StandardExecutionMode => StandardExecutionMode,
        CancellationProbeExecutionMode => CancellationProbeExecutionMode,
        _ => string.Empty
    };

    private static string Folder(int tenantId, Guid agentId) => $"/mcp-dev/{tenantId}/{agentId:N}/";
    private static string ScriptType(string shell) => shell == "sh" ? "bash" : "powershell";
    private static string Manifest(string marker) => $$"""
        {"marker":"{{marker}}","parameters":[]}
        """;

    internal static string Content(string marker, string shell, string executionMode = StandardExecutionMode) => shell == "sh"
        ? $$"""
            #| NetRatel-MANIFEST
            #| {{Manifest(marker)}}
            #| END
            #!/usr/bin/env sh
            {{DelayCommand(shell, executionMode)}}
            printf '%s\n' 'NETRATEL_MCP_QA_MARKER={{marker}}'
            hostname
            id -un
            date -u +%Y-%m-%dT%H:%M:%SZ
            uname -sr
            """
        : $$"""
            #| NetRatel-MANIFEST
            #| {{Manifest(marker)}}
            #| END
            {{DelayCommand(shell, executionMode)}}
            Write-Output 'NETRATEL_MCP_QA_MARKER={{marker}}'
            $env:COMPUTERNAME
            [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
            Get-Date -AsUTC -Format o
            $PSVersionTable.PSVersion.ToString()
            """;

    private static string DelayCommand(string shell, string executionMode) => executionMode == CancellationProbeExecutionMode
        ? shell == "sh" ? "sleep 15" : "Start-Sleep -Seconds 15"
        : string.Empty;

    private static DevelopmentMcpScriptDto ToDto(DevelopmentMcpScriptRecord ownership, ScriptDefinition script) => new(
        checked((ulong)script.Id), script.Name, script.FolderPath, script.Description, script.Content, script.ScriptType,
        ownership.Marker, ownership.Shell, ownership.ExecutionMode, script.CreatedAtUtc, script.UpdatedAtUtc);

    private static DevelopmentMcpScriptDto ToDto(DevelopmentMcpScriptRecord ownership, ScriptInfo script) => new(
        script.Id, script.Name, script.FolderPath, script.Description, script.Content, script.ScriptType,
        ownership.Marker, ownership.Shell, ownership.ExecutionMode, script.CreatedAtUtc, script.UpdatedAtUtc);

    private static IResult TargetRejected(string code) => Results.Problem(
        statusCode: StatusCodes.Status403Forbidden,
        title: "Development target is not eligible for this operation.",
        extensions: new Dictionary<string, object?> { ["code"] = code });

    [GeneratedRegex("^[A-Za-z0-9_-]{1,128}$", RegexOptions.CultureInvariant)]
    private static partial Regex MarkerRegex();
}

public sealed record CreateDevelopmentMcpMarkerScriptRequest(string Marker, string? Name, string? Description, string Shell, string? ExecutionMode = null);
public sealed record UpdateDevelopmentMcpMarkerScriptRequest(string? Marker, string? Name, string? Description);
public sealed record DevelopmentMcpScriptDto(ulong Id, string Name, string FolderPath, string Description, string Content, string ScriptType, string Marker, string Shell, string ExecutionMode, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);
public sealed record DevelopmentMcpScriptParameterDto(long Id, string Name, string Type, bool Required, string? Default, string? Description, string? OptionsJson);
public sealed record DevelopmentMcpMarkerScriptRunDto(ulong TaskId, string RequestId, ulong ScriptId, int TenantId, Guid AgentId, string Status);
