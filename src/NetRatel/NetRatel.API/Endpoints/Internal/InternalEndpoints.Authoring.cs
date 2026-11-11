using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NetRatel.API.Services.Orchestration;
using NetRatel.Application.Jobs;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;

namespace NetRatel.API.Endpoints;

public static partial class InternalEndpoints
{
    private static void MapInternalCatalogAuthoringEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/catalog/request-definitions", async (
            CreateNetRatelRequestDefinitionDto request, HttpContext http,
            IJobDefinitionService jobs, OrchestratorDbContext db, CancellationToken ct) =>
        {
            var principal = await OrchestrationManagedAuthorization.ResolveAsync(http, OrchestrationManagedAuthorization.InvokeScope, ct);
            if (principal is null) return AutomationError(http, 403, "authoring", "access_denied");
            if (request.TenantId != principal.TenantId) return AutomationError(http, 403, "authoring", "access_denied");
            if (!ValidCatalogText(request.Name, 256) || request.Target is null && !ValidCatalogText(request.ClientIdentity, 256) ||
                request.FolderPath is not { Length: > 0 and <= 512 } || request.FolderPath.Any(char.IsControl) ||
                request.Description is { Length: > 2048 } || !ValidCatalogInputs(request.Inputs) ||
                request.Target is not null && (!string.IsNullOrWhiteSpace(request.ClientIdentity) || string.IsNullOrWhiteSpace(request.Command)) ||
                request.Command is not null && (string.IsNullOrWhiteSpace(request.Command) || request.Command.Length > 65536 || request.Runner is not ("bash" or "pwsh" or "powershell" or "cmd")))
                return AutomationError(http, 400, "create-job", "invalid_request_definition", "Choose a target and provide a supported runner, command and bounded job inputs.");
            var agent = request.Target is null ? await PairedJobTargets.LegacyAgentAsync(db, principal.TenantId, null, request.ClientIdentity, ct) : null;
            if (!await PairedJobTargets.ValidAsync(db, principal.TenantId, request.Target, agent, request.ClientIdentity, ct))
                return AutomationError(http, 422, "create-job", "target_invalid", "Choose current nodes or an enabled execution group in this connection's tenant.");
            try { request.ExecutionPolicy?.Validate(); }
            catch (ArgumentException) { return AutomationError(http, 422, "create-job", "execution_policy_invalid"); }

            var name = request.Name.Trim();
            var folder = NormalizeCatalogFolder(request.FolderPath);
            await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null;
            if (db.Database.IsNpgsql())
            {
                var fence = JsonSerializer.Serialize(new { principal.TenantId, Folder = folder.ToUpperInvariant(), Name = name.ToUpperInvariant() });
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({fence}, 0))", ct);
            }
            var sameName = await db.Jobs.AsNoTracking().Where(x => x.TenantId == principal.TenantId && x.Name.ToUpper() == name.ToUpper()).ToArrayAsync(ct);
            var existing = sameName.FirstOrDefault(x => string.Equals(NormalizeCatalogFolder(x.FolderPath), folder, StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                var description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
                var existingInfo = await jobs.GetAsync(checked((ulong)existing.Id), ct);
                var steps = await jobs.ListStepsAsync(checked((ulong)existing.Id), ct);
                if (existing.AgentId != agent || !string.Equals(existing.Description, description, StringComparison.Ordinal) ||
                    JsonSerializer.Serialize(existingInfo?.Target) != JsonSerializer.Serialize(request.Target) ||
                    existingInfo?.ExecutionPolicy != request.ExecutionPolicy ||
                    request.Command is not null && (steps.Count != 1 || steps[0].Command != request.Command || steps[0].Runner != request.Runner))
                    return AutomationError(http, 409, "authoring", "request_definition_name_conflict");
                var replay = await MapCatalogJobsAsync(db, [await jobs.GetAsync(checked((ulong)existing.Id), ct) ?? throw new InvalidOperationException("Existing definition disappeared.")], ct);
                return Results.Ok(ToRequestDefinition(replay.Single()));
            }
            var created = await jobs.CreateAsync(new CreateJobDefinitionCommand(name, folder, request.Description,
                principal.TenantId, string.Empty, AgentId: agent) { Target = request.Target, ExecutionPolicy = request.ExecutionPolicy }, ct);
            if (request.Command is not null)
                await jobs.AddStepAsync(new(created.Id, 1, JobStepKind.RunCommand, request.Runner, request.Command, null, null, true), ct);
            foreach (var input in request.Inputs)
                await jobs.AddParamAsync(new(created.Id, input.Key, input.Type, input.Required, input.DefaultValue, input.HelpText, input.OptionsJson), ct);
            var catalog = await MapCatalogJobsAsync(db, [created], ct);
            if (transaction is not null) await transaction.CommitAsync(ct);
            return Results.Created($"/internal/catalog/request-definitions/{created.Id}", ToRequestDefinition(catalog.Single()));
        }).RequireAuthorization(OrchestrationManagedAuthorization.InvokePolicy);

        group.MapPost("/catalog/request-definitions/{id}/inputs/sync", async (
            string id, SyncNetRatelRequestDefinitionInputsDto request, HttpContext http,
            IJobDefinitionService jobs, OrchestratorDbContext db, CancellationToken ct) =>
        {
            var principal = await OrchestrationManagedAuthorization.ResolveAsync(http, OrchestrationManagedAuthorization.InvokeScope, ct);
            if (principal is null) return AutomationError(http, 403, "authoring", "access_denied");
            if (!ulong.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var jobId) || jobId == 0 || jobId > long.MaxValue ||
                jobId.ToString(CultureInfo.InvariantCulture) != id)
                return AutomationError(http, 400, "authoring", "invalid_request_definition_id");
            var constraints = OrchestrationManagedAuthorization.Constraints(principal);
            var job = await jobs.GetAsync(jobId, ct);
            if (job is null || job.TenantId != principal.TenantId) return AutomationError(http, 403, "authoring", "access_denied");
            var target = await ValidateTargetAsync(db, job, ct);
            if (target is null) return AutomationError(http, 422, "sync-inputs", "target_unavailable");
            if (!constraints.RequestDefinitionIds.Contains(id, StringComparer.Ordinal) || target.AgentId is { } fixedAgent && !constraints.ResourceIds.Contains(fixedAgent.ToString("D"), StringComparer.Ordinal))
                return AutomationError(http, 403, "authoring", "access_denied");
            if (!ValidCatalogInputs(request.Inputs)) return AutomationError(http, 400, "sync-inputs", "invalid_request_definition_inputs");

            await using var transaction = db.Database.IsRelational() ? await db.Database.BeginTransactionAsync(ct) : null;
            if (db.Database.IsNpgsql())
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({checked((long)jobId)})", ct);
            // Share definition retirement's row fence until the input replacement commits.
            if (db.Database.IsRelational())
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE \"Jobs\" SET \"UpdatedAtUtc\" = \"UpdatedAtUtc\" WHERE \"Id\" = {checked((long)jobId)}", ct);
            // Recheck the live target under the write fence, including revocation or a changed job assignment.
            var originalTarget = JsonSerializer.Serialize(job.Target);
            job = await jobs.GetAsync(jobId, ct);
            if (job is null || job.TenantId != principal.TenantId ||
                await ValidateTargetAsync(db, job, ct) is not { } currentTarget || currentTarget.AgentId != target.AgentId || JsonSerializer.Serialize(job.Target) != originalTarget)
                return AutomationError(http, 403, "authoring", "access_denied");
            var rows = await db.JobParameters.Where(x => x.JobId == checked((long)jobId)).ToListAsync(ct);
            if (rows.Select(x => x.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != rows.Count)
                return AutomationError(http, 409, "authoring", "request_definition_existing_inputs_conflict");
            var desiredKeys = request.Inputs.Select(x => x.Key.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            db.JobParameters.RemoveRange(rows.Where(row => !desiredKeys.Contains(row.Name)));
            foreach (var input in request.Inputs)
            {
                var key = input.Key.Trim();
                var row = rows.SingleOrDefault(x => string.Equals(x.Name, key, StringComparison.OrdinalIgnoreCase));
                if (row is null)
                {
                    row = new JobParameterDefinition { JobId = checked((long)jobId), Name = key };
                    db.JobParameters.Add(row);
                }
                row.Name = key;
                row.Label = input.Label.Trim();
                row.SortOrder = input.Order;
                row.Type = input.Type.Trim();
                row.Required = input.Required;
                row.DefaultValue = input.DefaultValue;
                row.Description = input.HelpText;
                row.OptionsJson = input.OptionsJson;
            }
            await db.SaveChangesAsync(ct);
            var refreshed = await jobs.GetAsync(jobId, ct) ?? throw new InvalidOperationException("Updated definition disappeared.");
            var catalog = await MapCatalogJobsAsync(db, [refreshed], ct);
            if (transaction is not null) await transaction.CommitAsync(ct);
            return Results.Ok(ToRequestDefinition(catalog.Single()));
        }).RequireAuthorization(OrchestrationManagedAuthorization.InvokePolicy);
    }

    private static bool ValidCatalogText(string? value, int maximum) =>
        value is { Length: > 0 } && value.Length <= maximum && value == value.Trim() && !value.Any(char.IsControl);

    private static string NormalizeCatalogFolder(string folder)
    {
        var result = folder.Trim().Replace('\\', '/').TrimEnd('/');
        return (result.StartsWith('/') ? result : "/" + result) + "/";
    }

    private static bool ValidCatalogInputs(IReadOnlyList<NetRatelCatalogInputDefinitionDto>? inputs)
    {
        if (inputs is null || inputs.Count > 128) return false;
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var input in inputs)
        {
            if (input is null || !ValidCatalogText(input.Key, 128) || !keys.Add(input.Key) ||
                !ValidCatalogText(input.Label, 256) || !ValidCatalogText(input.Type, 64) ||
                input.Order is < 0 or > 10000 || input.DefaultValue is { Length: > 4096 } ||
                input.HelpText is { Length: > 2048 } || input.OptionsJson is { Length: > 16384 }) return false;
            if (input.OptionsJson is not null)
            {
                try { using var _ = JsonDocument.Parse(input.OptionsJson); }
                catch (JsonException) { return false; }
            }
        }
        return true;
    }
}
