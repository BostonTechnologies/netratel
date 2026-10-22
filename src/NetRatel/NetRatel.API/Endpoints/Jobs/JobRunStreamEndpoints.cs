using System.Globalization;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using NetRatel.API.Security.Local;
using NetRatel.API.Services;
using NetRatel.Application.Jobs;
using NetRatel.Application.Tenants;
using NetRatel.Infrastructure.Identity;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.Jobs;
using NetRatel.Shared.Contracts.Tasks;
using NetRatel.Shared.Security;

namespace NetRatel.API.Endpoints;

public static partial class JobRunEndpoints
{
    private static readonly JsonSerializerOptions StreamJson = new(JsonSerializerDefaults.Web);
    internal const int StreamOutputBytes = 64 * 1024;
    internal const int StreamOutputLines = 500;

    private static async Task StreamRunAsync(ulong id, HttpContext http, IEffectiveAccessService access,
        IJobRunService runs, IJobDefinitionService jobs, ITenantService tenants, OrchestratorDbContext db,
        ExecutionActivityBroker broker, TimeProvider timeProvider)
    {
        var ct = http.RequestAborted;
        var run = await runs.GetAsync(id, ct);
        if (run is null) { http.Response.StatusCode = StatusCodes.Status404NotFound; return; }
        if (!await StreamAuthorizedAsync(http, access, run.TenantId, timeProvider, ct))
        { http.Response.StatusCode = StatusCodes.Status403Forbidden; return; }
        // Subscribe first: changes committed during snapshot reads queue another replacement.
        using var subscription = broker.Subscribe(run.TenantId, id, null, null);
        if (subscription is null) { http.Response.StatusCode = StatusCodes.Status429TooManyRequests; return; }
        http.Response.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache, no-store";
        http.Response.Headers["X-Accel-Buffering"] = "no";
        http.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        try
        {
            if (!await WriteSnapshotAsync()) return;
            Task<bool>? pendingRead = null;
            Task<bool>? heartbeat = null;
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15), timeProvider);
            while (!ct.IsCancellationRequested)
            {
                pendingRead ??= subscription.Reader.WaitToReadAsync(ct).AsTask();
                heartbeat ??= timer.WaitForNextTickAsync(ct).AsTask();
                var completed = await Task.WhenAny(pendingRead, heartbeat);
                if (!await StreamAuthorizedAsync(http, access, run.TenantId, timeProvider, ct)) return;
                if (completed == pendingRead)
                {
                    if (!await pendingRead) return;
                    pendingRead = null;
                    while (subscription.Reader.TryRead(out _)) { }
                    if (!await WriteSnapshotAsync()) return;
                }
                else
                {
                    if (!await heartbeat) return;
                    heartbeat = null;
                    // Recover from missed notifications and listener downtime across API nodes.
                    if (!await WriteSnapshotAsync()) return;
                    await WriteStreamAsync(http, new { kind = "heartbeat", connected = broker.IsConnected }, ct);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        catch (IOException) when (ct.IsCancellationRequested) { return; }

        async Task<bool> WriteSnapshotAsync()
        {
            var snapshot = await BuildStreamSnapshotAsync(id, runs, jobs, tenants, db, ct);
            if (snapshot is null) { await WriteStreamAsync(http, new { kind = "deleted" }, ct); return false; }
            if (snapshot.Run.TenantId != run.TenantId ||
                !await StreamAuthorizedAsync(http, access, snapshot.Run.TenantId, timeProvider, ct)) return false;
            await WriteStreamAsync(http, new { kind = "snapshot", id = Guid.NewGuid().ToString("N"),
                snapshot, connected = broker.IsConnected }, ct);
            return !Terminal((JobRunState)snapshot.Run.Status);
        }
    }

    internal static async Task<JobRunStreamSnapshotDto?> BuildStreamSnapshotAsync(ulong id,
        IJobRunService runs, IJobDefinitionService jobs, ITenantService tenants,
        OrchestratorDbContext db, CancellationToken ct)
    {
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct) : null;
        var detail = await runs.GetDetailsAsync(id, ct);
        if (detail is null) return null;
        var run = detail.Run;
        var job = await db.Jobs.IgnoreQueryFilters().AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == checked((long)run.JobId) && x.TenantId == run.TenantId, ct);
        var tenant = (await tenants.ListAsync(ct)).FirstOrDefault(x => x.TenantId == run.TenantId);
        var agent = run.AgentId is { } agentId
            ? await db.Agents.AsNoTracking().SingleOrDefaultAsync(x => x.Id == agentId && x.TenantId == run.TenantId, ct) : null;
        var steps = detail.Steps.OrderBy(x => x.Ordinal).Select(x => MapStepRun(x,
            detail.Activities.FirstOrDefault(a => a.RequestId == x.TaskRequestId))).ToArray();
        var output = new List<JobStepRunLogsDto>();
        var remainingBytes = StreamOutputBytes;
        var remainingLines = StreamOutputLines;
        foreach (var step in detail.Steps.OrderByDescending(x => x.Ordinal))
        {
            if (step.TaskRequestId is not { Length: > 0 } requestId ||
                !detail.Activities.Any(a => a.RequestId == requestId && a.TenantId == run.TenantId)) continue;
            var rows = await runs.GetLogsByRequestIdAsync(requestId, ct);
            var logs = new List<TaskLogDto>();
            var truncated = false;
            foreach (var row in rows.Where(x => x.TenantId == run.TenantId).OrderByDescending(x => x.Sequence).ThenByDescending(x => x.Id))
            {
                if (remainingLines == 0 || remainingBytes == 0) { truncated = true; break; }
                var redacted = OperatorOutputRedactor.Redact(row.Message);
                var message = BoundStreamMessage(redacted, remainingBytes);
                truncated |= message.Length != redacted.Length;
                remainingBytes -= Encoding.UTF8.GetByteCount(message);
                remainingLines--;
                logs.Add(new(row.Id, row.RequestId, row.ClientIdentity, row.TimestampUtc, row.Stream, message, row.Sequence));
            }
            output.Add(new(step.Ordinal, logs.OrderBy(x => x.Seq).ThenBy(x => x.Id).ToArray(), truncated));
        }
        if (transaction is not null) await transaction.CommitAsync(ct);
        return new(Map(run, job?.Name ?? "Historical job", tenant?.Name, agent?.Name), steps,
            output.OrderBy(x => x.Ordinal).ToArray());
    }

    internal static JobStepRunDto MapStepRun(JobStepRunInfo step, JobTaskActivityInfo? activity) =>
        new(step.Id, step.JobRunId, step.JobStepId, (JobStepRunStatusDto)step.Status, step.Ordinal,
            step.TaskRequestId, Micros(step.StartedAtUtc), Micros(step.CompletedAtUtc), step.Error, null,
            activity?.ScriptName, activity?.ScriptType, activity?.CommandLabel, activity?.ExitCode);

    private static string BoundStreamMessage(string value, int budget)
    {
        if (Encoding.UTF8.GetByteCount(value) <= budget) return value;
        var length = Math.Min(value.Length, budget);
        while (length > 0 && Encoding.UTF8.GetByteCount(value.AsSpan(0, length)) > budget) length--;
        return value[..length];
    }

    private static async Task<bool> StreamAuthorizedAsync(HttpContext http, IEffectiveAccessService access,
        int? tenantId, TimeProvider timeProvider, CancellationToken ct)
    {
        if (http.User.Identity?.IsAuthenticated != true) return false;
        if (long.TryParse(http.User.FindFirstValue("exp"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var expiration) &&
            timeProvider.GetUtcNow().ToUnixTimeSeconds() >= expiration) return false;
        var ticket = await http.AuthenticateAsync();
        if (ticket.Properties?.ExpiresUtc is { } expires && timeProvider.GetUtcNow() >= expires) return false;
        if (http.User.HasClaim("auth_mode", "local"))
        {
            var identityDb = http.RequestServices.GetRequiredService<NetRatelIdentityDbContext>();
            var userId = http.User.FindFirstValue(ClaimTypes.NameIdentifier);
            var user = await identityDb.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == userId, ct);
            if (user is null || !LocalSessionValidator.IsValid(http.User, user)) return false;
        }
        return await CanManageAsync(http, access, tenantId, ct);
    }

    private static async Task WriteStreamAsync(HttpContext http, object payload, CancellationToken ct)
    {
        await http.Response.WriteAsync("data: " + JsonSerializer.Serialize(payload, StreamJson) + "\n\n", ct);
        await http.Response.Body.FlushAsync(ct);
    }
}
