using System.Collections.Immutable;
using System.Text.Json;
using NetRatel.Application.Flows;
using NetRatel.Application.Monitoring;
using NetRatel.Shared.Contracts.Flows;
using NetRatel.Shared.Contracts.Monitoring;

namespace NetRatel.API.Services.Monitoring;

/// <summary>Resolves immutable published versions; no synthetic choices or publication by this adapter.</summary>
public sealed class PublishedMonitoringFlowProvider(IFlowDefinitionService flows) : IMonitoringPublishedFlowProvider
{
    public async Task<bool> IsPublishedAsync(int tenantId, Guid versionId, CancellationToken cancellationToken)
    {
        if (tenantId <= 0 || versionId == Guid.Empty) return false;
        var version = await flows.GetVersionAsync(tenantId, versionId, cancellationToken).ConfigureAwait(false);
        return version is not null && version.TenantId == tenantId && version.Id == versionId && HasValidImmutableGraph(version);
    }

    public async Task<ImmutableArray<MonitoringPublishedFlowDto>> ListPublishedAsync(int tenantId, int maximumCount, CancellationToken cancellationToken)
    {
        if (tenantId <= 0 || maximumCount is < 1 or > MonitoringLimits.MaximumRowsPerRead) throw new ArgumentException("invalid_published_flow_query");
        var definitions = await flows.ListAsync(tenantId, cancellationToken).ConfigureAwait(false);
        if (definitions.Count > FlowLimits.MaximumFlowsPerTenant || definitions.Any(item => item.TenantId != tenantId))
            throw new InvalidOperationException("published_flow_capacity_exceeded");
        var choices = ImmutableArray.CreateBuilder<MonitoringPublishedFlowDto>();
        foreach (var definition in definitions.Where(item => item.Enabled && item.PublishedVersionId is not null).OrderBy(item => item.Name).ThenBy(item => item.Id))
        {
            var version = await flows.GetVersionAsync(tenantId, definition.PublishedVersionId!.Value, cancellationToken).ConfigureAwait(false);
            if (version is null || version.Id != definition.PublishedVersionId || version.TenantId != tenantId ||
                version.FlowId != definition.Id || !HasValidImmutableGraph(version)) continue;
            if (choices.Count == maximumCount) throw new InvalidOperationException("published_flow_capacity_exceeded");
            choices.Add(new(version.Id, definition.Name, version.VersionNumber));
        }
        return choices.ToImmutable();
    }

    public static bool HasValidImmutableGraph(FlowVersionDto version) => version.Id != Guid.Empty && version.FlowId != Guid.Empty &&
        version.VersionNumber > 0 && FlowGraphValidator.ValidatePublished(version.Graph).Valid &&
        version.ConfigurationHash == FlowContractValidation.Hash(JsonSerializer.SerializeToUtf8Bytes(version.Graph,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)));
}
