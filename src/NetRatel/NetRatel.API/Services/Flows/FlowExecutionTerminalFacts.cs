using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Flows;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.Flows;

namespace NetRatel.API.Services.Flows;

/// <summary>Reads a bounded native terminal fact from the same execution family as its canonical receipt.</summary>
internal static class FlowExecutionTerminalFacts
{
    internal static async Task<FlowExecutionEventDto?> ReadAsync(OrchestratorDbContext db, int tenantId,
        FlowExecutionSourceKind kind, string executionId, CancellationToken ct = default)
    {
        var provenance = kind switch
        {
            FlowExecutionSourceKind.Job => "job-terminal-ready",
            FlowExecutionSourceKind.Script => "script-terminal-commit",
            _ => null
        };
        if (provenance is null) return null;
        var tenant = tenantId.ToString(CultureInfo.InvariantCulture);
        // Native script request IDs and job run IDs occupy separate namespaces and can have identical text.
        // Apply the family filter in SQL before the bound so another family cannot hide this result.
        var candidates = await db.OutboxMessages.AsNoTracking().Where(row =>
            row.Type == FlowExecutionEventLimits.OutboxType && row.TenantId == tenant &&
            row.EntityId == executionId && row.Source == provenance)
            .OrderByDescending(row => row.OccurredUtc).ThenByDescending(row => row.Id)
            .Take(8).Select(row => new { row.Id, row.PayloadJson }).ToArrayAsync(ct);
        foreach (var row in candidates)
        {
            if (Encoding.UTF8.GetByteCount(row.PayloadJson) > FlowExecutionEventLimits.MaximumEnvelopeBytes) continue;
            FlowExecutionEventDto? value;
            try { value = JsonSerializer.Deserialize(row.PayloadJson, FlowExecutionEventJsonContext.Default.FlowExecutionEventDto); }
            catch (JsonException) { continue; }
            if (value?.EventId == row.Id && value.TenantId == tenantId && value.SourceKind == kind &&
                value.ExecutionId == executionId && value.Provenance == provenance &&
                FlowExecutionEventProvenance.IsNative(value) && FlowExecutionEventValidator.Validate(value).Valid)
                return value;
        }
        return null;
    }
}
