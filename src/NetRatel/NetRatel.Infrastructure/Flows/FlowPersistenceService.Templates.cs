using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Flows;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.Flows;

namespace NetRatel.Infrastructure.Flows;

public sealed partial class FlowPersistenceService : IFlowTemplateService
{
    public Task<FlowDefinitionWriteResult> CreateFromTemplateAsync(int tenantId, FlowCreateFromTemplateRequest request,
        string actorId, CancellationToken cancellationToken = default) => WithDb(async (db, _) =>
    {
        if (tenantId <= 0 || !ValidName(request.Name) || !ValidActor(actorId))
            return new FlowDefinitionWriteResult(FlowWriteDisposition.Invalid, Code: "flow-name");
        if (!FlowOverhaulTemplates.Descriptors.Any(template => template.Id == request.TemplateId))
            return new(FlowWriteDisposition.Invalid, Code: "flow-template");
        var graph = FlowOverhaulTemplates.CreateDraft(request.TemplateId);
        var validation = graph.SchemaVersion == 1
            ? FlowGraphValidator.ValidateDraft(graph) : FlowGraphV2Validator.ValidateDraft(graph);
        if (!validation.Valid)
            return new(FlowWriteDisposition.Invalid, Code: validation.Issues[0].Code);
        await using var transaction = await BeginAsync(db, cancellationToken).ConfigureAwait(false);
        await TenantLockAsync(db, tenantId, cancellationToken).ConfigureAwait(false);
        if (!await db.Tenants.AnyAsync(row => row.Id == tenantId && row.DeletedAtUtc == null, cancellationToken).ConfigureAwait(false))
            return new(FlowWriteDisposition.NotFound);
        if (await db.FlowDefinitions.CountAsync(row => row.TenantId == tenantId && row.DeletedAtUtc == null, cancellationToken).ConfigureAwait(false) >= FlowLimits.MaximumFlowsPerTenant)
            return new(FlowWriteDisposition.CapacityExceeded);
        var now = clock.GetUtcNow();
        var row = new FlowDefinitionRecord
        {
            Id = Guid.NewGuid(), TenantId = tenantId, Name = request.Name.Trim(), Revision = 1, Enabled = false,
            DraftJson = Serialize(graph), CreatedAtUtc = now, UpdatedAtUtc = now
        };
        db.FlowDefinitions.Add(row); Audit(db, row, actorId, "created-from-template");
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        if (transaction is not null) await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(FlowWriteDisposition.Stored, Definition(row));
    });
}
