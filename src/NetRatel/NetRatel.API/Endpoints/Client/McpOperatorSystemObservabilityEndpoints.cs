using System.Security.Cryptography;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using NetRatel.Infrastructure.Persistence;
using NetRatel.API.Endpoints.Search;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using NetRatel.Akka.Configuration;
using NetRatel.API.Middleware;
using NetRatel.API.Ops;
using NetRatel.API.Security.M2M;
using NetRatel.Application.Operations;
using NetRatel.Application.Telemetry;

namespace NetRatel.API.Endpoints.Client;

/// <summary>Delegated, policy-admitted access to current platform telemetry and redacted API logs.</summary>
public static class McpOperatorSystemObservabilityEndpoints
{
    private const int MaximumSnapshots = 1000;
    private static readonly string Digest = Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes("netratel-mcp-system-observability-v1")));

    public static IEndpointRouteBuilder MapMcpOperatorSystemObservabilityEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v2/mcp/operator")
            .WithTags("MCP Operator System Observability").RequireAuthorization("M2MOnly");
        group.MapGet("/telemetry/overview", TelemetryAsync);
        group.MapGet("/logs", LogsAsync);
        group.MapGet("/search/{kind}", SearchDirectoryAsync);
        return app;
    }

    private static async Task<IResult> TelemetryAsync(HttpContext http, IHostEnvironment environment,
        IOptions<M2MOptions> m2m, IMcpOperatorAuthorization authorization,
        [FromServices] NetRatelAkkaMigrationOptions options, IServiceProvider services,
        CancellationToken cancellationToken)
    {
        var admission = await AdmitAsync("netratel_telemetry", "overview", http, environment, m2m,
            authorization, cancellationToken).ConfigureAwait(false);
        if (admission.FailureCode is { } code) return Failure(code, admission.Context ?? admission.Fallback);
        var context = admission.Context!;
        if (!options.IsTelemetryAuthorityActive || services.GetService<IClientTelemetryRouter>() is not { } telemetry)
            return Failure("telemetry_unavailable", context);
        try
        {
            await authorization.RecordAcceptedAsync(context.Decision, context.ServicePrincipal, cancellationToken).ConfigureAwait(false);
            var model = await telemetry.GetReadModelAsync(cancellationToken).ConfigureAwait(false);
            var snapshots = model.Snapshots.OrderBy(snapshot => snapshot.Client.TenantId)
                .ThenBy(snapshot => snapshot.Client.AgentId).Take(MaximumSnapshots)
                .Select(snapshot => new AgentTelemetrySnapshotResponse(snapshot.Client.TenantId, snapshot.Client.AgentId,
                    snapshot.ObservedAtUtc, snapshot.ReceivedAtUtc, snapshot.Cpu, snapshot.Memory, snapshot.Disks,
                    snapshot.Networks, snapshot.TransportHealth, snapshot.Source, snapshot.IsAuthoritative)).ToArray();
            return Results.Ok(new { snapshots, totalCount = model.Snapshots.Count, limit = MaximumSnapshots,
                truncated = model.Snapshots.Count > MaximumSnapshots, model.GeneratedAtUtc });
        }
        catch (McpOperatorAdmissionRejectedException rejection) { return Failure(rejection.FailureCode, context); }
    }

    private static async Task<IResult> LogsAsync(HttpContext http, IHostEnvironment environment,
        IOptions<M2MOptions> m2m, IMcpOperatorAuthorization authorization,
        [FromServices] AiAgentOpsLogBuffer logs, [FromQuery] long? since, [FromQuery] string? level,
        [FromQuery] string? contains, [FromQuery] string? correlationId, [FromQuery] int? limit,
        CancellationToken cancellationToken)
    {
        var admission = await AdmitAsync("netratel_logs", "search", http, environment, m2m,
            authorization, cancellationToken).ConfigureAwait(false);
        if (admission.FailureCode is { } code) return Failure(code, admission.Context ?? admission.Fallback);
        var context = admission.Context!;
        if (since is < 0 || limit is < 1 or > 500 || level is { Length: > 32 } ||
            contains is { Length: > 512 } || correlationId is { Length: > 128 })
            return Failure("validation_error", context);
        try
        {
            await authorization.RecordAcceptedAsync(context.Decision, context.ServicePrincipal, cancellationToken).ConfigureAwait(false);
            return Results.Ok(logs.Query(since, level, contains, correlationId, limit));
        }
        catch (McpOperatorAdmissionRejectedException rejection) { return Failure(rejection.FailureCode, context); }
    }

    private static async Task<IResult> SearchDirectoryAsync(string kind, HttpContext http,
        IHostEnvironment environment, IOptions<M2MOptions> m2m,
        IMcpOperatorAuthorization authorization, [FromServices] OrchestratorDbContext db,
        [FromQuery] string? q, CancellationToken cancellationToken)
    {
        if (kind is not ("scripts" or "jobs" or "requests" or "tasks"))
            return Results.BadRequest(new { code = "unsupported_operation", allowedOperations = new[] { "scripts", "jobs", "requests", "tasks" } });
        var admission = await AdmitAsync("netratel_search", kind, http, environment, m2m,
            authorization, cancellationToken).ConfigureAwait(false);
        if (admission.FailureCode is { } code) return Failure(code, admission.Context ?? admission.Fallback);
        var context = admission.Context!;
        if (q is { Length: > 512 }) return Failure("validation_error", context);
        var term = string.IsNullOrWhiteSpace(q) ? null : q.Trim();
        const int limit = 25;
        McpOperatorDirectoryItem[] rows;
        try
        {
            await authorization.RecordAcceptedAsync(context.Decision, context.ServicePrincipal, cancellationToken).ConfigureAwait(false);
            switch (kind)
            {
                case "scripts":
                    var query = db.Scripts.AsNoTracking();
                    if (term is not null)
                    {
                        var like = "%" + term.Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%";
                        var hasId = long.TryParse(term, out var id);
                        query = query.Where(script => EF.Functions.ILike(script.Name, like) ||
                            EF.Functions.ILike(script.FolderPath, like) || EF.Functions.ILike(script.Description, like) ||
                            EF.Functions.ILike(script.ScriptType, like) || (hasId && script.Id == id));
                    }
                    var scripts = await query.OrderBy(script => script.Name).ThenBy(script => script.Id)
                        .Take(limit + 1).Select(script => new { script.Id, script.Name, script.FolderPath })
                        .ToArrayAsync(cancellationToken).ConfigureAwait(false);
                    rows = scripts.Select(script => new McpOperatorDirectoryItem(kind, script.Id.ToString(CultureInfo.InvariantCulture),
                        script.Name, script.FolderPath, null, null, null)).ToArray();
                    break;
                case "jobs":
                    var jobs = await GlobalSearchEndpoints.BuildJobQuery(db, term).Take(limit + 1)
                        .ToArrayAsync(cancellationToken).ConfigureAwait(false);
                    rows = jobs.Select(job => new McpOperatorDirectoryItem(kind, job.Id.ToString(CultureInfo.InvariantCulture),
                        job.Name, job.FolderPath, job.TenantId, job.AgentId, null)).ToArray();
                    break;
                case "requests":
                    var requests = await GlobalSearchEndpoints.BuildRequestQuery(db, term).Take(limit + 1)
                        .ToArrayAsync(cancellationToken).ConfigureAwait(false);
                    rows = requests.Select(request => new McpOperatorDirectoryItem(kind, request.Id.ToString(CultureInfo.InvariantCulture),
                        request.SourceSystem, null, request.TargetTenantId, request.TargetAgentId, request.Status)).ToArray();
                    break;
                default:
                    var tasks = await GlobalSearchEndpoints.BuildTaskQuery(db, term).Take(limit + 1)
                        .ToArrayAsync(cancellationToken).ConfigureAwait(false);
                    rows = tasks.Select(task => new McpOperatorDirectoryItem(kind, task.Id.ToString(CultureInfo.InvariantCulture),
                        task.TaskType, null, task.TenantId, task.AgentId, task.Status)).ToArray();
                    break;
            }
            return Results.Ok(new { items = rows.Take(limit).ToArray(), limit, hasMore = rows.Length > limit });
        }
        catch (McpOperatorAdmissionRejectedException rejection) { return Failure(rejection.FailureCode, context); }
    }

    private static Task<McpOperatorControlPlaneAdmissionResult> AdmitAsync(string tool, string operation,
        HttpContext http, IHostEnvironment environment, IOptions<M2MOptions> m2m,
        IMcpOperatorAuthorization authorization, CancellationToken cancellationToken)
    {
        if (http.TryGetMcpOperatorDelegation(out var delegation) && delegation is not null &&
            !string.Equals(delegation.Resource, $"{m2m.Value.Authority.TrimEnd('/')}/mcp", StringComparison.Ordinal))
            return Task.FromResult(new McpOperatorControlPlaneAdmissionResult(null, "delegated_identity_invalid",
                McpOperatorControlPlaneAdmission.MinimalContext(tool, operation, http.TraceIdentifier)));
        return McpOperatorControlPlaneAdmission.TryAuthorizeAsync(tool, operation, Digest, http, environment,
            authorization, cancellationToken);
    }

    private static IResult Failure(string code, McpOperatorControlPlaneContext context)
    {
        var (status, layer, remediation) = code switch
        {
            "delegated_identity_required" or "delegated_identity_invalid" => (401, "delegation", "Use the authenticated HTTP MCP host for this resource."),
            "oauth_scope_missing" or "oauth_role_missing" => (403, "oauth_scope", $"Request {context.RequiredScope} with its mapped role and refresh OAuth."),
            "validation_error" => (400, "input", "Use bounded input: q/contains up to 512 characters, limit 1–500 and nonnegative since."),
            "telemetry_unavailable" => (503, "capability", "The current telemetry authority is unavailable; inspect service readiness."),
            _ => (403, "policy", "An Observability ControlPlane or full Dev testing policy is required.")
        };
        return Results.Problem(statusCode: status, title: "MCP platform observation did not complete.",
            extensions: new Dictionary<string, object?>
            {
                ["success"] = false,
                ["failure"] = new { code, layer, retryable = status == 503, requiredScopes = new[] { context.RequiredScope },
                    requiredOperation = $"{context.Tool}/{context.Operation}", remediation },
                ["correlationId"] = context.CorrelationId
            });
    }
}

/// <summary>Discovery metadata only; no script content, task output, request payload or credentials.</summary>
public sealed record McpOperatorDirectoryItem(string Kind, string Id, string? Name, string? FolderPath,
    int? TenantId, Guid? AgentId, string? Status);
