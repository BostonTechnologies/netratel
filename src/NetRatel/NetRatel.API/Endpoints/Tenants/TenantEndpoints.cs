using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NetRatel.Application.Events;
using NetRatel.Application.Tenants;
using NetRatel.API.Models.TenantModels;
using NetRatel.Shared.Contracts.Requests;

namespace NetRatel.API.Endpoints;

public static class TenantEndpoints
{
    public static IEndpointRouteBuilder MapTenantEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/tenants").WithTags("Tenants");

        group.MapGet("/", async (ITenantService tenants, CancellationToken ct) =>
        {
            var response = (await tenants.ListAsync(ct)).Select(MapResponse);
            return Results.Ok(response);
        }).RequireAuthorization("InstanceAdministrator");

        group.MapGet("/{id:int}", async (int id, ITenantService tenants, CancellationToken ct) =>
        {
            var tenant = await tenants.GetAsync(id, ct);
            return tenant is null ? Results.NotFound() : Results.Ok(MapResponse(tenant));
        }).RequireAuthorization("TenantAdministrator");

        group.MapPost("/", async (
            [FromBody] CreateTenantRequest request,
            ITenantService tenants,
            IEventRecorder events,
            ICorrelationContext correlation,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var logger = loggerFactory.CreateLogger("TenantEndpoints");
            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return Results.BadRequest("Name is required.");
            }

            var tenantName = request.Name.Trim();
            var domains = NormalizeDomains(request.Domains);
            logger.LogInformation("CreateTenant requested via Postgres service: name={TenantName}", tenantName);

            try
            {
                var tenant = await tenants.CreateAsync(
                    new CreateTenantCommand(
                        tenantName,
                        request.Description?.Trim(),
                        request.Location?.Trim(),
                        domains,
                        request.ContactPerson?.Trim(),
                        request.ContactEmail?.Trim(),
                        request.AutoUpdate,
                        request.AutoUpdateChannel,
                        request.AutoUpdateTargetVersion),
                    ct);

                await events.RecordAsync(new DomainEvent
                {
                    EventType = NetRatelEventTypes.Tenant.Created,
                    Source = "Orchestration",
                    CorrelationId = correlation.GetOrCreate(),
                    TenantId = tenant.TenantId.ToString(),
                    EntityId = tenant.TenantId.ToString(),
                    Severity = "Info",
                    Message = $"Tenant {tenant.Name} created.",
                    Payload = new TenantChangedPayload(
                        tenant.TenantId,
                        tenant.Name,
                        Actor: null,
                        ChangedFields: ["name", "description", "location", "domains", "contact"])
                }, ct);

                return Results.Created($"/api/v1/tenants/{tenant.TenantId}", MapResponse(tenant));
            }
            catch (InvalidOperationException ex)
            {
                logger.LogWarning(ex, "CreateTenant rejected for {TenantName}", tenantName);
                return Results.BadRequest(ex.Message);
            }
        }).RequireAuthorization("InstanceAdministrator");

        group.MapPut("/{id:int}", async (
            int id,
            [FromBody] UpdateTenantRequest request,
            ITenantService tenants,
            IEventRecorder events,
            ICorrelationContext correlation,
            CancellationToken ct) =>
        {
            var existing = await tenants.GetAsync(id, ct);
            if (existing is null)
            {
                return Results.NotFound();
            }

            var domains = NormalizeDomains(request.Domains);

            try
            {
                var updated = await tenants.UpdateAsync(
                    new UpdateTenantCommand(
                        id,
                        request.Name?.Trim() ?? existing.Name,
                        request.Description?.Trim(),
                        request.Location?.Trim(),
                        domains,
                        request.ContactPerson?.Trim(),
                        request.ContactEmail?.Trim(),
                        request.AutoUpdate ?? existing.AutoUpdate,
                        request.AutoUpdateChannel ?? existing.AutoUpdateChannel,
                        request.AutoUpdateTargetVersion ?? existing.AutoUpdateTargetVersion),
                    ct);

            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(ex.Message);
            }

            var changedFields = new List<string>();
            if (!string.Equals(existing.Name, request.Name?.Trim() ?? existing.Name, StringComparison.Ordinal))
                changedFields.Add("name");
            if (!string.Equals(existing.Description, request.Description?.Trim(), StringComparison.Ordinal))
                changedFields.Add("description");
            if (!string.Equals(existing.Location, request.Location?.Trim(), StringComparison.Ordinal))
                changedFields.Add("location");
            if (!string.Equals(existing.ContactPerson, request.ContactPerson?.Trim(), StringComparison.Ordinal))
                changedFields.Add("contactPerson");
            if (!string.Equals(existing.ContactEmail, request.ContactEmail?.Trim(), StringComparison.Ordinal))
                changedFields.Add("contactEmail");
            if (request.AutoUpdate.HasValue && existing.AutoUpdate != request.AutoUpdate.Value)
                changedFields.Add("autoUpdate");
            if (!existing.Domains.SequenceEqual(domains, StringComparer.OrdinalIgnoreCase))
                changedFields.Add("domains");

            if (changedFields.Count > 0)
            {
                await events.RecordAsync(new DomainEvent
                {
                    EventType = NetRatelEventTypes.Tenant.Updated,
                    Source = "Orchestration",
                    CorrelationId = correlation.GetOrCreate(),
                    TenantId = id.ToString(),
                    EntityId = id.ToString(),
                    Severity = "Info",
                    Message = $"Tenant {id} updated.",
                    Payload = new TenantChangedPayload(id, request.Name?.Trim() ?? existing.Name, Actor: null, changedFields)
                }, ct);
            }

            return Results.Accepted($"Tenant update request for ID {id} received.");
        }).RequireAuthorization("TenantAdministrator");

        group.MapDelete("/{id:int}", async (
            int id,
            ITenantService tenants,
            IEventRecorder events,
            ICorrelationContext correlation,
            CancellationToken ct) =>
        {
            var existing = await tenants.DeleteAsync(id, ct);
            if (existing is null)
            {
                return Results.NotFound();
            }

            await events.RecordAsync(new DomainEvent
            {
                EventType = NetRatelEventTypes.Tenant.Deleted,
                Source = "Orchestration",
                CorrelationId = correlation.GetOrCreate(),
                TenantId = id.ToString(),
                EntityId = id.ToString(),
                Severity = "Warning",
                Message = $"Tenant {id} deleted.",
                Payload = new TenantChangedPayload(id, existing.Name, Actor: null)
            }, ct);

            return Results.Accepted($"Tenant delete request for ID {id} received.");
        }).RequireAuthorization("TenantAdministrator");

        return app;
    }

    private static List<string> NormalizeDomains(List<string>? domains) =>
        (domains ?? [])
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .Select(d => d.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static TenantResponse MapResponse(TenantInfo tenant) =>
        new(
            tenant.TenantId,
            tenant.Name,
            tenant.Description,
            tenant.Location,
            tenant.Domains.ToList(),
            tenant.ContactPerson,
            tenant.ContactEmail,
            tenant.AutoUpdate,
            tenant.AutoUpdateChannel,
            tenant.AutoUpdateTargetVersion,
            tenant.CreatedAtUtc,
            tenant.UpdatedAtUtc);
}
