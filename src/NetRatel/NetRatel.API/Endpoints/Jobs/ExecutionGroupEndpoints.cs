using Microsoft.AspNetCore.Mvc;
using NetRatel.API.Services.Jobs;
using NetRatel.API.Middleware;
using NetRatel.Application.Jobs;
using NetRatel.Shared.Contracts.Jobs;

namespace NetRatel.API.Endpoints;

public static class ExecutionGroupEndpoints
{
    public static IEndpointRouteBuilder MapExecutionGroupEndpoints(this IEndpointRouteBuilder app)
    {
        var groups = app.MapGroup("/api/v2/tenants/{tenantId:int}/execution-groups").WithTags("Execution groups").RequireAuthorization().WithMetadata(TargetBounds());
        groups.MapGet("/", (int tenantId, string? search, int? page, int? pageSize, HttpContext http, IExecutionGroupService service, CancellationToken ct) =>
            Result(() => service.ListAsync(tenantId, search, page ?? 1, pageSize ?? 50, http.User, ct)));
        groups.MapGet("/{groupId:guid}", (int tenantId, Guid groupId, HttpContext http, IExecutionGroupService service, CancellationToken ct) =>
            Result(() => service.GetAsync(tenantId, groupId, http.User, ct)));
        groups.MapPost("/", (int tenantId, [FromBody] ExecutionGroupWriteDto request, HttpContext http, IExecutionGroupService service, CancellationToken ct) =>
            Result(() => service.CreateAsync(tenantId, request, http.User, ct), created: true));
        groups.MapPut("/{groupId:guid}", (int tenantId, Guid groupId, [FromBody] ExecutionGroupWriteDto request, HttpContext http, IExecutionGroupService service, CancellationToken ct) =>
            Result(() => service.UpdateAsync(tenantId, groupId, request, http.User, ct)));
        groups.MapPatch("/{groupId:guid}/members", (int tenantId, Guid groupId, [FromBody] ExecutionGroupMembershipWriteDto request, HttpContext http, IExecutionGroupService service, CancellationToken ct) =>
            Result(() => service.ChangeMembershipAsync(tenantId, groupId, request, http.User, ct)));
        groups.MapDelete("/{groupId:guid}", (int tenantId, Guid groupId, long expectedRevision, HttpContext http, IExecutionGroupService service, CancellationToken ct) =>
            Delete(() => service.DeleteAsync(tenantId, groupId, expectedRevision, http.User, ct)));
        var targets = app.MapGroup("/api/v2/tenants/{tenantId:int}/job-targets").WithTags("Job targets").RequireAuthorization().WithMetadata(TargetBounds());
        targets.MapGet("/nodes", (int tenantId, string? search, int? page, int? pageSize, HttpContext http, JobTargetApiService service, CancellationToken ct) =>
            Result(() => service.SearchAsync(tenantId, search, page ?? 1, pageSize ?? 50, http.User, ct)));
        targets.MapPost("/nodes/identities", (int tenantId, [FromBody] IReadOnlyList<Guid> request, HttpContext http, JobTargetApiService service, CancellationToken ct) =>
            Result(() => service.IdentitiesAsync(tenantId, request, http.User, ct)));
        targets.MapPost("/preview", (int tenantId, [FromBody] JobNodePreviewRequest request, HttpContext http, JobTargetApiService service, CancellationToken ct) =>
            Result(() => service.PreviewAsync(tenantId, request, http.User, ct)));
        targets.MapPost("/resolve-preview", (int tenantId, [FromBody] JobTargetPreviewRequest request, HttpContext http, JobTargetApiService service, CancellationToken ct) =>
            Result(() => service.ResolvePreviewAsync(tenantId, request, http.User, ct)));
        targets.MapPost("/validate", (int tenantId, [FromBody] JobNodePreviewRequest request, HttpContext http, JobTargetApiService service, CancellationToken ct) =>
            Result(() => service.ValidateAsync(tenantId, request, http.User, ct)));
        targets.MapGet("/properties", (int tenantId, HttpContext http, JobTargetApiService service, CancellationToken ct) =>
            Result(() => service.PropertiesAsync(tenantId, http.User, ct)));
        return app;
    }
    private static MonitoringHttpBounds TargetBounds() => new() { MaximumBytes = 1024 * 1024, ErrorCode = "target_payload_capacity_exceeded" };

    private static async Task<IResult> Result<T>(Func<Task<T>> operation, bool created = false)
    {
        try
        {
            var value = await operation().ConfigureAwait(false);
            return value is null ? Results.NotFound() : created ? Results.Json(value, statusCode: StatusCodes.Status201Created) : Results.Ok(value);
        }
        catch (JobTargetValidationException error) { return Results.Json(new { code = error.Code, message = error.Message }, statusCode: error.StatusCode); }
        catch (ExecutionGroupReferencedException error) { return Results.Conflict(new { code = "execution_group_referenced", message = error.Message, references = error.References }); }
    }
    private static async Task<IResult> Delete(Func<Task<bool>> operation)
    {
        try { return await operation().ConfigureAwait(false) ? Results.NoContent() : Results.NotFound(); }
        catch (JobTargetValidationException error) { return Results.Json(new { code = error.Code, message = error.Message }, statusCode: error.StatusCode); }
        catch (ExecutionGroupReferencedException error) { return Results.Conflict(new { code = "execution_group_referenced", message = error.Message, references = error.References }); }
    }
}
