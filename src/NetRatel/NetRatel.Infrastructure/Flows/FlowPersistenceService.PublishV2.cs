using System.Security.Claims;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NetRatel.Application.Flows;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.Flows;
using Microsoft.Extensions.DependencyInjection;

namespace NetRatel.Infrastructure.Flows;

public sealed partial class FlowPersistenceService
{
    private async Task<FlowPublishResult> PublishSchemaTwoAsync(OrchestratorDbContext db, IServiceProvider services,
        FlowDefinitionRecord row, FlowGraphDto graph, FlowExecutionAuthorityDto authority, IDbContextTransaction? transaction, CancellationToken ct)
    {
        if (!FlowSchemaCapabilities.Current.ExecutableSchemas.Contains(FlowGraphV2Limits.SchemaVersion))
            return new(FlowWriteDisposition.Invalid, Code: "flow-runtime-schema-unavailable");
        if (transaction is null || !db.Database.IsNpgsql())
            return new(FlowWriteDisposition.Invalid, Code: "flow-publish-transaction-required");
        var validation = FlowGraphV2Validator.ValidateComplete(graph);
        if (!validation.Valid) return new(FlowWriteDisposition.Invalid, Code: validation.Issues[0].Code);
        var verifier = services.GetRequiredService<IFlowExecutionAuthorityVerifier>();
        if (!await verifier.AuthorizeAsync(row.TenantId, authority, ct).ConfigureAwait(false))
            return new(FlowWriteDisposition.ConnectorDenied, Code: "execution-authority-revoked");
        var trigger = graph.Nodes.Single(node => FlowNodeCatalogue.IsTrigger(node.Kind));
        var sources = trigger.ExecutionTrigger?.Sources;
        // Native Request records currently have trusted Job backing only. Script executions
        // produce their own Script facts; a Request-only selector must not promise that source.
        if (trigger.ExecutionTrigger is { Scope: FlowExecutionScope.SelectedDefinitions } subscription &&
            !subscription.Sources.Contains(FlowExecutionSourceKind.Script) &&
            subscription.DefinitionReferences?.Any(reference => reference.SourceKind == FlowExecutionSourceKind.Script) == true)
            return new(FlowWriteDisposition.Invalid, Code: "execution-trigger-source-unavailable");
        var access = services.GetRequiredService<IEffectiveAccessService>();
        var principal = FlowAuthorityPrincipal.Create(authority);
        foreach (var permission in (sources ?? []).Select(source => source == FlowExecutionSourceKind.Script
            ? NetRatelPermissions.ScriptExecute : NetRatelPermissions.JobManagement).Distinct())
            if (!await access.AuthorizeAsync(principal, permission, row.TenantId, ct).ConfigureAwait(false))
                return new(FlowWriteDisposition.ConnectorDenied, Code: "execution-source-read-denied");
        var versionId = Guid.NewGuid();
        FlowExecutionPinsCapture capture;
        try
        {
            capture = graph.Nodes.Any(node => node.Kind is FlowNodeKind.RunLibraryScript or FlowNodeKind.RunJob)
                ? await services.GetRequiredService<IFlowExecutionDefinitionPins>().CaptureAsync(row.TenantId, authority,
                    versionId, graph, ct).ConfigureAwait(false)
                : new FlowExecutionPinsCapture(graph, null);
        }
        catch (FlowExecutionPinException error) { return new(FlowWriteDisposition.Invalid, Code: error.Code); }
        graph = capture.Graph;
        var ordinary = services.GetRequiredService<IFlowConnectorCatalog>();
        var sourceCatalog = services.GetService<IFlowExecutionConnectorCatalog>();
        var pinned = new Dictionary<Guid, FlowActionTargetDto>();
        foreach (var action in FlowGraphValidator.ActionTargets(graph))
        {
            var connector = sources is not null
                ? sourceCatalog is null ? null : await sourceCatalog.GetForExecutionAsync(row.TenantId, action.ConnectorId!.Value,
                    authority, sources, ct).ConfigureAwait(false)
                : await ordinary.GetAsync(row.TenantId, action.ConnectorId!.Value, authority, ct).ConfigureAwait(false);
            if (connector is null || connector.TenantId != row.TenantId || connector.Id != action.ConnectorId ||
                connector.Kind != action.Kind || connector.Revision <= 0)
                return new(FlowWriteDisposition.ConnectorDenied, Code: "connector-authority");
            if (!connector.Enabled || !connector.CanExecute)
                return new(FlowWriteDisposition.ConnectorDenied, Code: connector.UnavailableReason ?? "connector-unavailable");
            if (action.ConnectorRevision is { } selected && selected != connector.Revision)
                return new(FlowWriteDisposition.Conflict, Code: "connector-revision-conflict");
            pinned.Add(action.Id, action with { ConnectorRevision = connector.Revision, Name = connector.Name });
        }
        graph = graph with { Nodes = graph.Nodes.Select(node => node.Kind switch
        {
            FlowNodeKind.CreateIncident or FlowNodeKind.InvestigateWithAi => node with { ConnectorRevision = pinned[node.Id].ConnectorRevision },
            FlowNodeKind.ActionGroup => node with { Actions = node.Actions!.Select(action => pinned[action.Id]).ToArray() },
            _ => node
        }).ToArray() };
        graph = await ResolveFlowPromptsAsync(row.TenantId, graph, true, ct).ConfigureAwait(false);
        if (graph is null || !FlowGraphV2Validator.ValidatePublished(graph).Valid)
            return new(FlowWriteDisposition.Invalid, Code: "published-graph");
        if (!await verifier.AuthorizeAsync(row.TenantId, authority, ct).ConfigureAwait(false))
            return new(FlowWriteDisposition.ConnectorDenied, Code: "execution-authority-revoked");
        var json = FlowGraphSerialization.Serialize(graph);
        var version = new FlowVersionRecord { Id = versionId, FlowId = row.Id, TenantId = row.TenantId,
            VersionNumber = row.PublishedVersionNumber + 1, GraphJson = json,
            ConfigurationHash = FlowGraphSerialization.Hash(graph), PublishedBy = authority.PrincipalId,
            PublishedAtUtc = await ActivationNowAsync(db, ct).ConfigureAwait(false), ProtectedExecutionPinsJson = capture.ProtectedPinsJson, ExecutionAuthorityJson = Serialize(authority) };
        db.FlowVersions.Add(version);
        row.PublishedVersionId = version.Id; row.PublishedVersionNumber = version.VersionNumber; row.Revision++;
        row.UpdatedAtUtc = version.PublishedAtUtc;
        if (row.Enabled && row.EnabledSinceUtc is null) row.EnabledSinceUtc = version.PublishedAtUtc;
        Audit(db, row, authority.PrincipalId, "version-published");
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return new(FlowWriteDisposition.Stored, Version(version));
    }
}
