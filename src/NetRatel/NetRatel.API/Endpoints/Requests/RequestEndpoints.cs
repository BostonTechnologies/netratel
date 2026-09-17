using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NetRatel.Application.Jobs;
using NetRatel.Application.Requests;
using NetRatel.API.Models.RequestModels;
using NetRatel.API.Services.Requests;
using NetRatel.API.Services.Jobs;

namespace NetRatel.API.Endpoints;

public static class RequestEndpoints
{
    private static readonly JsonSerializerOptions SseJsonOptions = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapRequestEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/requests")
            .WithTags("Requests")
            .RequireAuthorization("Operator");

        group.MapGet("/", async (
            IRequestService requests,
            IJobRunService jobRuns,
            IJobDefinitionService jobs,
            CancellationToken ct) =>
        {
            var requestList = await requests.ListAsync(ct);
            var runLookup = (await jobRuns.ListAsync(ct)).ToDictionary(run => run.Id);
            var jobLookup = (await jobs.ListAsync(ct)).ToDictionary(job => job.Id);
            var payload = requestList.Select(request => MapRequestDto(
                request,
                RequestJobRunLinkResolver.Resolve(request, runLookup),
                jobLookup));
            return Results.Ok(payload);
        });

        group.MapGet("/{id:int}", async (
            int id,
            IRequestService requests,
            IJobRunService jobRuns,
            IJobDefinitionService jobs,
            CancellationToken ct) =>
        {
            var request = await requests.GetAsync(id, ct);
            if (request is null)
            {
                return Results.NotFound();
            }

            var run = await ResolveLinkedRunAsync(request, jobRuns, ct);
            var job = run is null ? null : await jobs.GetAsync(run.JobId, ct);
            return Results.Ok(MapRequestDto(request, run, job));
        });

        group.MapGet("/stream", StreamRequestsAsync);

        group.MapPost("/", async (
            SubmitRequestRequest request,
            IRequestService requests,
            IRequestEventBus requestEvents,
            IJobDefinitionService jobs,
            IAkkaJobAuthorityService jobAuthority,
            CancellationToken ct) =>
        {
            var validation = ValidateCreateRequest(request, out var jobId);
            if (validation is not null)
            {
                return Results.ValidationProblem(validation);
            }

            var job = await jobs.GetAsync(jobId!.Value, ct);
            if (job is null)
            {
                return Results.NotFound(new { code = "job_not_found" });
            }
            if (job.TenantId is null || job.AgentId is null)
                return Results.Conflict(new { code = "job_target_requires_current_agent" });
            if ((request.TenantId.HasValue && request.TenantId != job.TenantId) ||
                (request.AgentId.HasValue && request.AgentId != job.AgentId))
                return Results.Conflict(new { code = "request_target_must_match_job_target" });

            var created = await requests.CreateAsync(new CreateRequestCommand(
                request.SourceSystem!,
                string.Empty,
                request.ResolveJobDefinitionId(),
                request.JobInputsJson,
                job.TenantId,
                job.AgentId), ct);
            try
            {
                var run = await jobAuthority.StartAsync(job.Id, new NetRatel.Shared.Contracts.Jobs.RunJobRequest(
                    request.SourceSystem!, request.JobInputsJson, null), ct);
                created = (await requests.UpdateAsync(new UpdateRequestCommand(
                    created.Id, null, null, null, run.Id.ToString(), "Processing", null, null, null,
                    created.Logs.Concat([$"[{DateTimeOffset.UtcNow:O}] Dispatched to Akka job run {run.Id}."]).ToList()), ct))!;
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { code = "job_authority_unavailable", detail = ex.Message, requestId = created.Id });
            }
            requestEvents.Publish(new RequestChangedEvent(created.Id, "created", created.UpdatedAtUtc));
            return Results.Accepted($"/api/v1/requests/{created.Id}", MapRequestDto(created));
        });

        group.MapPut("/{id:int}", async (
            int id,
            [FromBody] UpdateRequestRequest update,
            IRequestService requests,
            IRequestEventBus requestEvents,
            CancellationToken ct) =>
        {
            var updated = await requests.UpdateAsync(new UpdateRequestCommand(
                id,
                update.SourceSystem,
                update.TargetClientIdentity,
                update.JobDefinitionId,
                update.ExecutionId,
                update.Status,
                update.ResultMessage,
                update.ResultData,
                update.JobInputs,
                update.Logs), ct);

            if (updated is null)
            {
                return Results.NotFound();
            }

            requestEvents.Publish(new RequestChangedEvent(updated.Id, "updated", updated.UpdatedAtUtc));
            return Results.Accepted($"/api/v1/requests/{id}");
        });

        group.MapPut("/{id:int}/claim", async (
            int id,
            ClaimRequestRequest request,
            IRequestService requests,
            IRequestEventBus requestEvents,
            CancellationToken ct) =>
        {
            var current = await requests.GetAsync(id, ct);
            if (current is null)
            {
                return Results.NotFound();
            }

            var updated = await requests.UpdateAsync(new UpdateRequestCommand(
                id,
                null,
                null,
                null,
                request.ExecutionId,
                "Processing",
                null,
                null,
                null,
                current.Logs.Concat([$"[{DateTimeOffset.UtcNow:O}] Claimed. Status -> Processing."]).ToList()), ct);
            if (updated is not null)
            {
                requestEvents.Publish(new RequestChangedEvent(updated.Id, "claimed", updated.UpdatedAtUtc));
            }

            return Results.Accepted();
        });

        group.MapPut("/{id:int}/complete", async (
            int id,
            CompleteRequestRequest request,
            IRequestService requests,
            IRequestEventBus requestEvents,
            CancellationToken ct) =>
        {
            var current = await requests.GetAsync(id, ct);
            if (current is null)
            {
                return Results.NotFound();
            }

            var updated = await requests.UpdateAsync(new UpdateRequestCommand(
                id,
                null,
                null,
                null,
                null,
                "Success",
                request.ResultMessage,
                request.ResultData,
                null,
                current.Logs.Concat([$"[{DateTimeOffset.UtcNow:O}] Completed successfully."]).ToList()), ct);
            if (updated is not null)
            {
                requestEvents.Publish(new RequestChangedEvent(updated.Id, "completed", updated.UpdatedAtUtc));
            }

            return Results.Accepted();
        });

        group.MapPut("/{id:int}/fail", async (
            int id,
            FailRequestRequest request,
            IRequestService requests,
            IRequestEventBus requestEvents,
            CancellationToken ct) =>
        {
            var current = await requests.GetAsync(id, ct);
            if (current is null)
            {
                return Results.NotFound();
            }

            var updated = await requests.UpdateAsync(new UpdateRequestCommand(
                id,
                null,
                null,
                null,
                null,
                "Failed",
                request.ErrorMessage,
                null,
                null,
                current.Logs.Concat([$"[{DateTimeOffset.UtcNow:O}] Failed: {request.ErrorMessage}"]).ToList()), ct);
            if (updated is not null)
            {
                requestEvents.Publish(new RequestChangedEvent(updated.Id, "failed", updated.UpdatedAtUtc));
            }

            return Results.Accepted();
        });

        return app;
    }

    private static async Task StreamRequestsAsync(
        HttpContext http,
        IRequestEventBus bus,
        IServiceScopeFactory scopeFactory,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("NetRatel.API.Endpoints.RequestStream");
        http.Response.Headers.Append("Cache-Control", "no-cache");
        http.Response.Headers.Append("Connection", "keep-alive");
        http.Response.Headers.Append("X-Accel-Buffering", "no");
        http.Response.ContentType = "text/event-stream";

        var reader = bus.Subscribe();
        var abort = http.RequestAborted;
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, abort);
        var token = linkedCts.Token;

        logger.LogInformation("NetRatel request stream connected.");

        try
        {
            await foreach (var change in reader.ReadAllAsync(token))
            {
                var payload = await BuildRequestDtoAsync(change.RequestId, scopeFactory, token);
                if (payload is null)
                {
                    continue;
                }

                var data = JsonSerializer.Serialize(payload, SseJsonOptions);
                await http.Response.WriteAsync("event: request-upsert\n", token);
                await http.Response.WriteAsync($"id: {payload.Id}:{payload.Updated.UtcTicks}\n", token);
                await http.Response.WriteAsync($"data: {data}\n\n", token);
                await http.Response.Body.FlushAsync(token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested || abort.IsCancellationRequested)
        {
            // Expected when the browser leaves the page or reconnects.
        }
        catch (IOException) when (abort.IsCancellationRequested)
        {
            // Expected broken pipe / connection reset on client disconnect.
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "NetRatel request stream failed.");
            throw;
        }
        finally
        {
            bus.Unsubscribe(reader);
            logger.LogInformation("NetRatel request stream disconnected.");
        }
    }

    private static async Task<RequestDto?> BuildRequestDtoAsync(
        int requestId,
        IServiceScopeFactory scopeFactory,
        CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var requests = scope.ServiceProvider.GetRequiredService<IRequestService>();
        var jobRuns = scope.ServiceProvider.GetRequiredService<IJobRunService>();
        var jobs = scope.ServiceProvider.GetRequiredService<IJobDefinitionService>();

        var request = await requests.GetAsync(requestId, ct);
        if (request is null)
        {
            return null;
        }

        var run = await ResolveLinkedRunAsync(request, jobRuns, ct);
        var job = run is null ? null : await jobs.GetAsync(run.JobId, ct);
        return MapRequestDto(request, run, job);
    }

    private static async Task<JobRunInfo?> ResolveLinkedRunAsync(RequestInfo request, IJobRunService jobRuns, CancellationToken ct)
    {
        foreach (var runId in RequestJobRunLinkResolver.EnumerateCandidateRunIds(request))
        {
            var run = await jobRuns.GetAsync(runId, ct);
            if (run is not null)
            {
                return run;
            }
        }

        return null;
    }

    private static RequestDto MapRequestDto(
        RequestInfo request,
        JobRunInfo? run = null,
        IReadOnlyDictionary<ulong, JobDefinitionInfo>? jobsById = null)
        => MapRequestDto(
            request,
            run,
            run is null || jobsById is null || !jobsById.TryGetValue(run.JobId, out var job) ? null : job);

    private static RequestDto MapRequestDto(RequestInfo request, JobRunInfo? run, JobDefinitionInfo? job)
        => new(
            request.Id,
            request.SourceSystem,
            request.TargetClientIdentity,
            request.JobDefinitionId,
            request.ExecutionId,
            request.Status,
            request.ResultMessage,
            request.ResultData,
            request.JobInputs,
            request.Logs,
            request.CreatedAtUtc,
            request.UpdatedAtUtc,
            run?.Id,
            run?.JobId,
            job?.Name,
            request.TargetTenantId,
            request.TargetAgentId
        );

    private static Dictionary<string, string[]>? ValidateCreateRequest(SubmitRequestRequest request, out ulong? jobId)
    {
        jobId = null;
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(request.SourceSystem)) errors["sourceSystem"] = ["sourceSystem is required."];
        var id = request.ResolveJobDefinitionId();
        if (!string.IsNullOrWhiteSpace(id))
        {
            if (ulong.TryParse(id, out var parsedJobId)) jobId = parsedJobId;
            else errors["jobDefinitionId"] = ["jobDefinitionId must be a numeric current job definition identifier."];
        }

        var isDirectTask = !string.IsNullOrWhiteSpace(request.TaskType) || request.ScriptId.HasValue;
        if (!isDirectTask && !jobId.HasValue)
            errors["jobDefinitionId"] = ["jobDefinitionId is required for job-backed requests."];
        if (isDirectTask)
            errors["taskType"] = ["Direct agent tasks must be created through /api/v2/tasks."];
        if (request.ScriptId is <= 0)
            errors["scriptId"] = ["scriptId must be positive when supplied."];

        return errors.Count == 0 ? null : errors;
    }
}
