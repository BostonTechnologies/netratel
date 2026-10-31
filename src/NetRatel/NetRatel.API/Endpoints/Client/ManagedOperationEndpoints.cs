using System.Text.Json;
using NetRatel.API.Endpoints.Monitoring;
using NetRatel.API.Services;
using NetRatel.API.Services.Monitoring;
using NetRatel.API.Security.Authorization;
using NetRatel.Application.Agents;
using NetRatel.Application.Flows;
using NetRatel.Application.Events;
using NetRatel.Application.Operations;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Netclaw;
using NetRatel.Shared.Contracts;
using NetRatel.Shared.Contracts.Flows;
using NetRatel.Shared.Contracts.Monitoring;
using NetRatel.Shared.Contracts.Netclaw;
using NetRatel.Shared.Contracts.Services;
using NetRatel.Shared.Operations;

namespace NetRatel.API.Endpoints.Client;

/// <summary>Closed CLI/MCP adapters over the same permission-checked application handlers as the UI.</summary>
public static class ManagedOperationEndpoints
{
    public static IEndpointRouteBuilder MapManagedOperationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v2/mcp/operator/application/{tool}/{operation}", InvokeAsync)
            .RequireAuthorization(ManagedOperationAuthorization.PolicyName).WithTags("Managed Operations");
        return app;
    }

    internal static async Task<IResult> InvokeAsync(string tool, string operation, HttpContext http,
        ManagedOperationAdmission admission, CancellationToken cancellationToken)
    {
        var descriptor = ManagedOperationCatalog.Find(tool, operation);
        if (descriptor is null)
            return Invalid("Unknown managed operation.");
        var bytes = new byte[ManagedOperationCatalog.MaximumEnvelopeBytes + 1];
        var length = 0;
        while (length < bytes.Length)
        {
            var read = await http.Request.Body.ReadAsync(bytes.AsMemory(length), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            length += read;
        }
        if (length == bytes.Length)
            return Results.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "managed_request_too_large");
        JsonDocument document;
        try { document = JsonDocument.Parse(bytes.AsMemory(0, length), new JsonDocumentOptions { MaxDepth = 48 }); }
        catch (JsonException) { return Invalid("The body must contain valid JSON."); }
        using (document)
        {
            var envelope = document.RootElement;
            if (envelope.ValueKind != JsonValueKind.Object)
                return Invalid("The body must be an object containing request and optional confirm.");
            var names = envelope.EnumerateObject().Select(property => property.Name).ToArray();
            if (names.Distinct(StringComparer.Ordinal).Count() != names.Length || names.Except(["request", "confirm"], StringComparer.Ordinal).Any() ||
                !envelope.TryGetProperty("request", out var request))
                return Invalid("Supply request and optional confirm only.");
            if (envelope.TryGetProperty("confirm", out var confirmation) && confirmation.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return Invalid("confirm must be a boolean.");
            var confirmed = confirmation.ValueKind == JsonValueKind.True;
            if (!ManagedOperationCatalog.TryValidate(descriptor, request, out var error))
                return Invalid(error!);
            var admitted = await admission.EvaluateAsync(descriptor, request, http, cancellationToken).ConfigureAwait(false);
            if (admitted.FailureCode is { } failure)
                return Denied(failure);
            if (descriptor.Mutation && !confirmed)
                return Results.Ok(new
                {
                    requiresConfirm = true, tool, operation,
                    tenantId = request.GetProperty("tenantId").GetInt32(),
                    agentId = descriptor.AgentTarget ? request.GetProperty("agentId").GetGuid() : (Guid?)null,
                    summary = "Current permissions and MCP policy admit this operation. Review its arguments and repeat with confirm=true to apply it. No business state was changed."
                });

            var original = http.User;
            try
            {
                await admission.RecordAcceptedAsync(admitted, cancellationToken).ConfigureAwait(false);
                http.User = admitted.User!;
                var result = await DispatchAsync(descriptor, request, http, cancellationToken).ConfigureAwait(false);
                return await admission.FilterAiResponseAsync(descriptor, request, admitted, result, cancellationToken).ConfigureAwait(false);
            }
            catch (McpOperatorAdmissionRejectedException exception) { return Denied(exception.FailureCode); }
            finally { http.User = original; }
        }
    }

    internal static Task<IResult> DispatchAsync(ManagedOperationDescriptor descriptor, JsonElement request,
        HttpContext http, CancellationToken ct)
    {
        var tenant = request.GetProperty("tenantId").GetInt32();
        var access = http.RequestServices.GetRequiredService<IEffectiveAccessService>();
        var db = http.RequestServices.GetRequiredService<OrchestratorDbContext>();
        if (descriptor.Tool == "netratel_software")
            return SoftwareInventoryEndpoints.ManagedAsync(descriptor, request, http, ct);
        if (descriptor.Tool == "netratel_clients")
            return descriptor.Operation switch
            {
                "metadata" => ClientMetadataEndpoints.ReadAsync(tenant, Id(request, "agentId"), http, access, db, ct),
                "set_metadata" => ClientMetadataEndpoints.UpdateAsync(tenant, Id(request, "agentId"), Data<UpdateClientMetadataRequest>(request),
                    http, access, db, http.RequestServices.GetRequiredService<TimeProvider>(), ct),
                _ => throw new InvalidOperationException("Unmapped managed client operation.")
            };
        if (descriptor.Tool == "netratel_services")
        {
            var agent = Id(request, "agentId");
            var agents = http.RequestServices.GetRequiredService<IAgentManagementService>();
            return descriptor.Operation switch
            {
                "inventory" => ClientServicesEndpoints.ReadAsync(tenant, agent, http, access, agents, http.RequestServices.GetRequiredService<ClientServicesCoordinator>(), ct),
                "refresh" => ClientServicesEndpoints.RefreshAsync(tenant, agent, http, access, agents, http.RequestServices.GetRequiredService<ClientServicesCoordinator>(), ct),
                "control" => ClientServicesEndpoints.ControlAsync(tenant, agent, Data<ClientServiceControlRequest>(request), http, access, agents,
                    http.RequestServices.GetRequiredService<ClientServiceControlService>(), ct),
                "operation" => ClientServicesEndpoints.OperationAsync(tenant, agent, Id(request, "operationId"), http, access, agents,
                    http.RequestServices.GetRequiredService<ClientServiceControlService>(), ct),
                _ => throw new InvalidOperationException("Unmapped managed service operation.")
            };
        }
        if (descriptor.Tool == "netratel_monitoring")
            return MonitoringAsync(descriptor.Operation, request, http, ct);
        if (descriptor.Tool == "netratel_flows")
            return FlowsAsync(descriptor.Operation, request, http, access, db, ct);
        if (descriptor.Tool == "netratel_ai")
            return NetclawEndpoints.ExecuteAsync(() => InvestigationsAsync(descriptor.Operation, request, http, access, ct), ct);
        throw new InvalidOperationException("Unmapped managed operation tool.");
    }

    private static Task<IResult> MonitoringAsync(string operation, JsonElement request, HttpContext http, CancellationToken ct)
    {
        var service = http.RequestServices.GetRequiredService<MonitoringApiService>();
        var tenant = request.GetProperty("tenantId").GetInt32();
        var limit = request.TryGetProperty("limit", out var size) ? size.GetInt32() : 50;
        var cursor = Text(request, "cursor");
        var user = http.User;
        return operation switch
        {
            "permissions" => MonitoringEndpoints.ExecuteAsync(() => service.GetPermissionsAsync(tenant, user, ct)),
            "configuration" => MonitoringEndpoints.ExecuteAsync(() => service.GetConfigurationAsync(tenant, user, ct)),
            "summary" => MonitoringEndpoints.ExecuteAsync(() => service.GetSummaryAsync(tenant, user, ct)),
            "series" => MonitoringEndpoints.ExecuteAsync(() => service.GetSeriesAsync(tenant, limit, cursor, user, ct)),
            "events" => MonitoringEndpoints.ExecuteAsync(() => service.GetEventsAsync(tenant, limit, cursor, user, ct)),
            "clients" => MonitoringEndpoints.ExecuteAsync(() => service.GetClientsAsync(tenant, limit, cursor, user, ct, Text(request, "search"))),
            "client_series" => MonitoringEndpoints.ExecuteAsync(() => service.GetClientAsync(tenant, Id(request, "agentId"), user, ct)),
            "published_flows" => MonitoringEndpoints.ExecuteAsync(() => service.GetPublishedFlowsAsync(tenant, user, ct)),
            "preview_targets" => MonitoringEndpoints.ExecuteAsync(() => service.PreviewTargetsAsync(tenant, Data<MonitoringTargetPreviewRequest>(request), user, ct)),
            "save_rule" => MonitoringEndpoints.ExecuteAsync(() => service.SaveRuleAsync(tenant, Id(request, "ruleId"), Data<MonitoringRuleWriteDto>(request), user, ct)),
            "save_group" => MonitoringEndpoints.ExecuteAsync(() => service.SaveGroupAsync(tenant, Id(request, "groupId"), Data<MonitoringGroupWriteDto>(request), user, ct)),
            "save_silence" => MonitoringEndpoints.ExecuteAsync(() => service.SaveBypassAsync(tenant, Id(request, "bypassId"), Data<MonitoringBypassWriteDto>(request), user, ct)),
            "delete_rule" => MonitoringEndpoints.ExecuteAsync(() => service.DeleteAsync(tenant, Id(request, "ruleId"), MonitoringResourceKind.Rule, Data<MonitoringDeleteDto>(request), user, ct)),
            "delete_group" => MonitoringEndpoints.ExecuteAsync(() => service.DeleteAsync(tenant, Id(request, "groupId"), MonitoringResourceKind.Group, Data<MonitoringDeleteDto>(request), user, ct)),
            "delete_silence" => MonitoringEndpoints.ExecuteAsync(() => service.DeleteAsync(tenant, Id(request, "bypassId"), MonitoringResourceKind.Bypass, Data<MonitoringDeleteDto>(request), user, ct)),
            "acknowledge" or "clear" => MonitoringEndpoints.ExecuteAsync(() => service.ActAsync(tenant, Id(request, "agentId"), Id(request, "ruleId"), Text(request, "resourceKey")!,
                Data<MonitoringOperatorActionDto>(request), operation == "clear", user, ct)),
            "acknowledge_offline" or "clear_offline" => MonitoringEndpoints.ExecuteAsync(() => service.ActAsync(tenant, Guid.Empty,
                Id(request, "ruleId"), MonitoringOfflinePresentation.ResourceKey, Data<MonitoringOperatorActionDto>(request),
                operation == "clear_offline", user, ct)),
            _ => throw new InvalidOperationException("Unmapped managed monitoring operation.")
        };
    }

    private static Task<IResult> FlowsAsync(string operation, JsonElement request, HttpContext http,
        IEffectiveAccessService access, OrchestratorDbContext db, CancellationToken ct)
    {
        var tenant = request.GetProperty("tenantId").GetInt32();
        var flows = http.RequestServices.GetRequiredService<IFlowDefinitionService>();
        return operation switch
        {
            "list" => FlowEndpoints.ListAsync(tenant, http, access, db, flows, ct),
            "get" => FlowEndpoints.GetAsync(tenant, Id(request, "flowId"), http, access, db, flows, ct),
            "template" => FlowEndpoints.TemplateAsync(tenant, http, access, db, ct),
            "connectors" => FlowEndpoints.ConnectorsAsync(tenant, http, access, db, http.RequestServices.GetRequiredService<IFlowConnectorCatalog>(), ct),
            "create" => FlowEndpoints.CreateAsync(tenant, Data<FlowCreateRequest>(request), http, access, db, flows, ct),
            "save_draft" => FlowEndpoints.SaveAsync(tenant, Id(request, "flowId"), Data<FlowSaveDraftRequest>(request), http, access, db, flows, ct),
            "clone" => FlowEndpoints.CloneAsync(tenant, Id(request, "flowId"), Data<FlowCloneRequest>(request), http, access, db, flows, ct),
            "set_enabled" => FlowEndpoints.EnabledAsync(tenant, Id(request, "flowId"), Data<FlowEnabledRequest>(request), http, access, db, flows, ct),
            "publish" => FlowEndpoints.PublishAsync(tenant, Id(request, "flowId"), Data<FlowRevisionRequest>(request), http, access, db, flows, ct),
            "validate" => FlowEndpoints.ValidateAsync(tenant, Data<FlowGraphDto>(request), http, access, db, ct),
            "dry_run" => FlowEndpoints.DryRunAsync(tenant, Data<FlowDryRunRequest>(request), http, access, db, ct),
            "versions" => FlowEndpoints.VersionsAsync(tenant, Id(request, "flowId"), http, access, db, flows, ct),
            "version" => FlowEndpoints.VersionByIdAsync(tenant, Id(request, "versionId"), http, access, db, flows, ct),
            "runs" => FlowEndpoints.RunsAsync(tenant, Id(request, "flowId"), http, access, db, flows, ct),
            "run" => FlowEndpoints.RunByIdAsync(tenant, Id(request, "runId"), http, access, db, flows, ct),
            _ => throw new InvalidOperationException("Unmapped managed flow operation.")
        };
    }

    private static Task<IResult> InvestigationsAsync(string operation, JsonElement request, HttpContext http,
        IEffectiveAccessService access, CancellationToken ct)
    {
        var tenant = request.GetProperty("tenantId").GetInt32();
        var store = http.RequestServices.GetRequiredService<InvestigationStore>();
        if (operation == "clients")
            return NetclawEndpoints.ClientsAsync(tenant, http, access, store, ct);
        if (operation == "list")
            return NetclawEndpoints.ListAsync(tenant, request.TryGetProperty("skip", out var skip) ? skip.GetInt32() : 0, http, access, store, ct);
        var profiles = http.RequestServices.GetRequiredService<NetclawProfileService>();
        var worker = http.RequestServices.GetRequiredService<NetclawInvestigationWorker>();
        var authority = http.RequestServices.GetRequiredService<NetclawAuthority>();
        var audit = http.RequestServices.GetRequiredService<IEventRecorder>();
        long after = 0;
        if (request.TryGetProperty("after", out var sequence)) ManagedOperationCatalog.TryReadSequence(sequence, out after);
        return operation switch
        {
            "connection" => NetclawEndpoints.ConnectionAsync(tenant, http, access, profiles, ct),
            "pair" => NetclawEndpoints.PairAsync(tenant, Data<NetclawPairRequest>(request), http, access, profiles, audit, ct),
            "test_connection" => NetclawEndpoints.TestAsync(tenant, http, access, profiles, ct),
            "set_enabled" => NetclawEndpoints.EnabledAsync(tenant, Data<NetclawEnabledRequest>(request), http, access, profiles, audit, ct),
            "get" => NetclawEndpoints.GetAsync(tenant, Id(request, "investigationId"), after, http, access, store, worker, ct),
            "create" => NetclawEndpoints.CreateAsync(tenant, Data<InvestigationCreateRequest>(request), http, access, store, ct),
            "send" => NetclawEndpoints.SendAsync(tenant, Id(request, "investigationId"), Data<InvestigationMessageRequest>(request), http, authority, store, ct),
            "respond" => NetclawEndpoints.ApprovalAsync(tenant, Id(request, "investigationId"), Text(request, "callId")!, Data<InvestigationApprovalRequest>(request), http, authority, store, worker, ct),
            "reconcile" => NetclawEndpoints.ReconcileAsync(tenant, Id(request, "investigationId"), http, authority, store, worker, ct),
            "archive" => NetclawEndpoints.ArchiveAsync(tenant, Id(request, "investigationId"), Data<InvestigationArchiveRequest>(request), http, access, store, worker, ct),
            _ => throw new InvalidOperationException("Unmapped managed AI operation.")
        };
    }

    private static T Data<T>(JsonElement request) => request.GetProperty("data").Deserialize<T>(ManagedOperationCatalog.JsonOptions)!;
    private static Guid Id(JsonElement request, string name) => request.GetProperty(name).GetGuid();
    private static string? Text(JsonElement request, string name) => request.TryGetProperty(name, out var value) ? value.GetString() : null;
    private static IResult Invalid(string message) => Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "invalid_managed_request", detail: message);
    private static IResult Denied(string code) => Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: code,
        extensions: new Dictionary<string, object?> { ["code"] = code });
}
