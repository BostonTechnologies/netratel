using System.Globalization;
using Microsoft.EntityFrameworkCore;
using NetRatel.API.Services.Orchestration;
using NetRatel.Application.Jobs;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using NetRatel.Shared.Contracts.Jobs;

namespace NetRatel.API.Endpoints;

public static partial class InternalEndpoints
{
    private static IResult AutomationError(HttpContext http, int status, string operation, string code,
        string? detail = null) => Results.Json(new { operation, status, code,
            detail = detail ?? "The selected automation operation is unavailable for this connection.",
            correlationId = http.TraceIdentifier }, statusCode: status);

    private static async Task<NetRatelAutomationTarget> DescribeTargetAsync(OrchestratorDbContext db,
        JobDefinitionInfo job, List<NetRatelCatalogInputDefinitionDto> inputs, CancellationToken ct)
    {
        var target = job.Target;
        var kind = target?.Source switch
        {
            JobTargetSource.ExecutionGroup => "executionGroup", JobTargetSource.ExplicitNodes => "explicitNodes",
            JobTargetSource.NodeFilter => "nodeFilter", JobTargetSource.RuntimeInput => "runtimeInput", _ => "legacyAgent"
        };
        var required = new List<string>();
        if (target?.RuntimeInput is { } runtime)
        {
            required.Add(runtime.Name);
            if (!inputs.Any(input => input.Key.Equals(runtime.Name, StringComparison.OrdinalIgnoreCase)))
                inputs.Add(new() { Key = runtime.Name, Label = runtime.Name, Type = "text",
                    Required = runtime.DefaultValues is not { Count: > 0 },
                    DefaultValue = runtime.DefaultValues is { Count: > 0 } defaults ? System.Text.Json.JsonSerializer.Serialize(defaults) : null,
                    HelpText = $"Execution target ({runtime.Kind}): a literal value or JSON array of literal values." });
        }
        if (target?.Source == JobTargetSource.NodeFilter)
            required.AddRange(JobNodeFilterParser.ParameterNames(JobNodeFilterParser.Parse(target.Filter)));
        foreach (var key in required)
            if (!inputs.Any(input => input.Key.Equals(key, StringComparison.OrdinalIgnoreCase)))
                inputs.Add(new() { Key = key, Label = key, Type = "text", Required = true });
        var summary = target?.Source switch
        {
            JobTargetSource.ExecutionGroup => "HA group: " + await db.ExecutionGroups.AsNoTracking()
                .Where(row => row.TenantId == job.TenantId && row.Id == target.ExecutionGroupId).Select(row => row.Name).SingleAsync(ct) + " (one eligible worker)",
            JobTargetSource.ExplicitNodes => $"{target.AgentIds!.Distinct().Count()} selected node(s)",
            JobTargetSource.NodeFilter => "Node selector: " + target.Filter,
            JobTargetSource.RuntimeInput => $"Runtime target: {target.RuntimeInput!.Name} ({target.RuntimeInput.Kind})",
            _ => "Fixed agent: " + (job.AgentId?.ToString("D") ?? job.ClientIdentity)
        };
        var executable = await db.JobSteps.AsNoTracking().AnyAsync(step => step.JobId == checked((long)job.Id) && step.Enabled, ct);
        var readiness = !executable ? "incomplete" : inputs.Any(input => input.Required && input.DefaultValue is null)
            ? "requires-input" : "worker-check-at-run";
        return new(1, kind, summary, target, job.AgentId, readiness,
            inputs.Where(input => input.Required && input.DefaultValue is null).Select(input => input.Key).ToArray());
    }
}
