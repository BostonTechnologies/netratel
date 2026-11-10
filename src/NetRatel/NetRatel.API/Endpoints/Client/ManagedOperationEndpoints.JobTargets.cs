using System.Text.Json;
using NetRatel.API.Services.Jobs;
using NetRatel.Application.Jobs;
using NetRatel.Shared.Contracts.Jobs;
using NetRatel.Shared.Operations;

namespace NetRatel.API.Endpoints.Client;

public static partial class ManagedOperationEndpoints
{
    private static async Task<IResult> JobTargetsAsync(ManagedOperationDescriptor descriptor, JsonElement request,
        HttpContext http, ManagedOperationAdmissionResult? admitted, CancellationToken ct)
    {
        var tenant = request.GetProperty("tenantId").GetInt32();
        var groups = http.RequestServices.GetRequiredService<IExecutionGroupService>();
        try
        {
            if (descriptor.Tool == "netratel_execution_groups")
            {
                object? groupResult;
                switch (descriptor.Operation)
                {
                    case "list":
                        var page = Data<JobTargetDirectoryRequest>(request);
                        groupResult = await groups.ListAsync(tenant, page.Search, page.Page, page.PageSize, http.User, ct).ConfigureAwait(false);
                        break;
                    case "get": groupResult = await groups.GetAsync(tenant, Id(request, "groupId"), http.User, ct).ConfigureAwait(false); break;
                    case "create": groupResult = await groups.CreateAsync(tenant, Data<ExecutionGroupWriteDto>(request), http.User, ct).ConfigureAwait(false); break;
                    case "update": groupResult = await groups.UpdateAsync(tenant, Id(request, "groupId"), Data<ExecutionGroupWriteDto>(request), http.User, ct).ConfigureAwait(false); break;
                    case "membership": groupResult = await groups.ChangeMembershipAsync(tenant, Id(request, "groupId"), Data<ExecutionGroupMembershipWriteDto>(request), http.User, ct).ConfigureAwait(false); break;
                    case "delete":
                        return await groups.DeleteAsync(tenant, Id(request, "groupId"), Data<ExecutionGroupDeleteRequest>(request).ExpectedRevision,
                            http.User, ct).ConfigureAwait(false) ? Results.NoContent() : Results.NotFound();
                    default: throw new InvalidOperationException("Unmapped execution group operation.");
                }
                return groupResult is null ? Results.NotFound() : Results.Ok(groupResult);
            }

            var authorized = admitted?.AuthorizedJobAgentIds ?? throw new InvalidOperationException("Bounded target discovery authority is unavailable.");
            var targets = http.RequestServices.GetRequiredService<JobTargetApiService>();
            if (descriptor.Operation == "groups")
            {
                var query = Data<JobTargetDirectoryRequest>(request);
                return Results.Ok(await groups.ListForTargetsAsync(tenant, query.Search, query.Page, query.PageSize,
                    authorized, http.User, ct).ConfigureAwait(false));
            }
            if (descriptor.Operation == "group")
            {
                var group = await groups.GetAsync(tenant, Id(request, "groupId"), http.User, ct).ConfigureAwait(false);
                return group is not null && group.Members.All(member => authorized.Contains(member.AgentId))
                    ? Results.Ok(group) : Results.NotFound();
            }
            if (descriptor.Operation == "nodes")
            {
                var query = Data<JobTargetDirectoryRequest>(request);
                return Results.Ok(await targets.SearchAsync(tenant, query.Search, query.Page, query.PageSize,
                    http.User, ct, authorized).ConfigureAwait(false));
            }
            return descriptor.Operation switch
            {
                "identities" => Results.Ok(await targets.IdentitiesAsync(tenant, Data<JobTargetIdentitiesRequest>(request).AgentIds,
                    http.User, ct, authorized).ConfigureAwait(false)),
                "preview" => Results.Ok(await targets.PreviewAsync(tenant, Data<JobNodePreviewRequest>(request), http.User, ct, authorized).ConfigureAwait(false)),
                "resolve_preview" => Results.Ok(await targets.ResolvePreviewAsync(tenant, JobTargetHttpJson.Read<JobTargetPreviewRequest>(request.GetProperty("data")), http.User, ct, authorized).ConfigureAwait(false)),
                "validate" => Results.Ok(await targets.ValidateAsync(tenant, Data<JobNodePreviewRequest>(request), http.User, ct).ConfigureAwait(false)),
                "properties" => Results.Ok(await targets.PropertiesAsync(tenant, http.User, ct).ConfigureAwait(false)),
                _ => throw new InvalidOperationException("Unmapped target discovery operation.")
            };
        }
        catch (JsonException error)
        { return Results.BadRequest(new { code = "target_request_invalid", message = error.Message }); }
        catch (JobTargetValidationException error)
        { return Results.Json(new { code = error.Code, message = error.Message }, statusCode: error.StatusCode); }
        catch (ExecutionGroupReferencedException error)
        { return Results.Conflict(new { code = "execution_group_referenced", message = error.Message, references = error.References }); }
    }
}
