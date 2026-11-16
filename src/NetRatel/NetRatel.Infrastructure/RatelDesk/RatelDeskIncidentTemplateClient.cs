using System.Text.Json;
using AutomationTrace;
using AutomationTrace.Contracts;
using NetRatel.Application.RatelDesk;
using NetRatel.Shared.Contracts.RatelDesk;

namespace NetRatel.Infrastructure.RatelDesk;

/// <summary>Only the saved connector's bounded machine authority is used; browser clients never receive credentials.</summary>
public sealed class RatelDeskIncidentTemplateClient(IRatelDeskConnectorStore connectors, IRatelDeskConnectorBindingStore bindings,
    IRatelDeskConnectorAuthorization authorization, IRatelDeskOutboundBindingResolver credentials,
    IRatelDeskReceiverTransport receiver, IFlowSourceIdentityResolver identity, RatelDeskReceiverHttpPipeline http)
{
    public async Task<(RatelDeskConnectorState Connector, RatelDeskSemanticPeer Peer, RatelDeskVerifiedCapability Capability)> OpenAsync(int tenant, Guid id, CancellationToken ct)
    {
        var connector = await connectors.GetAsync(tenant, id, ct) ?? throw new KeyNotFoundException();
        if (!connector.Configuration.Enabled || !await authorization.CanExecuteAsync(connector.OwnerPrincipalId, null, tenant, ct))
            throw new UnauthorizedAccessException();
        var authentication = await bindings.GetAuthenticationAsync(tenant, id, ct);
        var peer = await credentials.CaptureAsync(connector, authentication, await identity.EnsureAsync(ct), ct);
        var bearer = await credentials.GetBearerAsync(connector, peer, "rateldesk.incident-receipts.read", ct);
        var capability = await receiver.CapabilitiesAsync(peer, bearer, ct);
        if (!ReceiverWireValidation.ValidPresentationCapability(capability.AutomationPresentation, peer))
            throw new InvalidOperationException("incident-presentation-peer-incompatible");
        return (connector, peer, capability);
    }
    public async Task<RatelDeskIncidentPresentationCatalogue> BrowseAsync(int tenant, Guid id, CancellationToken ct)
    {
        var (connector, peer, capability) = await OpenAsync(tenant, id, ct);
        var presentation = capability.AutomationPresentation!;
        string bearer;
        try { bearer = await credentials.GetBearerAsync(connector, peer, presentation.TemplateScope, ct); }
        catch (Exception error) when (error is UnauthorizedAccessException or NetRatel.Shared.SystemPairing.PairingException)
        { return new(presentation, [], "approval-required"); }
        var response = await http.ReadAsync(peer.Mode, tenant, id, peer.ApiBaseUrl, peer.SourceInstanceId, bearer,
            HttpMethod.Get, presentation.CatalogueEndpoint, null, ct);
        RatelDeskReceiverReadException.RequireJsonSuccess(response, "template-catalogue");
        using var parsed = ReceiverWireValidation.Parse(response.Body);
        var items = parsed.Deserialize<IncidentTemplateCatalogueItem[]>(AutomationTraceValidation.Json) ?? [];
        if (items.Length > 256 || items.Any(x => x.Purpose != "netratel-internal-incident" || x.Retired || x.Revoked ||
                x.OrganizationId is not null && x.OrganizationId != peer.OrganizationId || !AutomationTraceValidation.Presentation(x.Presentation) ||
                !AutomationTraceValidation.Text(x.Name, 256) || x.SourceKinds is not { Count: > 0 and <= 6 }))
            throw new InvalidDataException("template-catalogue-unverified");
        return new(presentation, items);
    }
    public async Task<IncidentTemplatePreview> PreviewAsync(int tenant, Guid id, IncidentTemplatePreviewRequest input, CancellationToken ct)
    {
        var (connector, peer, capability) = await OpenAsync(tenant, id, ct);
        return await CheckAsync(connector, peer, capability, input, ct);
    }
    public async Task ValidateSelectionAsync(int tenant, Guid connectorId, IncidentPresentationReference selected,
        IReadOnlyList<string> sourceKinds, CancellationToken ct)
    {
        var (connector, peer, capability) = await OpenAsync(tenant, connectorId, ct);
        if (selected.Mode == "template")
        {
            var catalogue = await BrowseAsync(tenant, connectorId, ct);
            var item = catalogue.Templates.SingleOrDefault(x => x.Presentation == selected);
            if (item is null || sourceKinds.Any(source => !item.SourceKinds.Contains(source)))
                throw new InvalidOperationException("incident-presentation-selection-stale");
        }
        foreach (var source in sourceKinds.Distinct()) _ = await CheckAsync(connector, peer, capability, new(selected, Sample(source)), ct);
    }
    public async Task<IncidentTemplatePreview> CheckAsync(RatelDeskConnectorState connector, RatelDeskSemanticPeer peer,
        RatelDeskVerifiedCapability capability, IncidentTemplatePreviewRequest input, CancellationToken ct)
    {
        if (!ReceiverWireValidation.ValidPresentationCapability(capability.AutomationPresentation, peer) ||
            !AutomationTraceValidation.Context(input.Context) || !AutomationTraceValidation.Presentation(input.Presentation))
            throw new InvalidOperationException("incident-presentation-incompatible");
        if (input.Presentation.Mode == "standard")
        {
            if (input.Presentation != capability.AutomationPresentation!.Standard) throw new InvalidOperationException("standard-presentation-revision-changed");
        }
        var presentation = capability.AutomationPresentation!;
        var bearer = await credentials.GetBearerAsync(connector, peer, input.Presentation.Mode == "standard"
            ? "rateldesk.incident-receipts.read" : presentation.TemplateScope, ct);
        var body = JsonSerializer.SerializeToUtf8Bytes(input, AutomationTraceValidation.Json);
        var response = await http.ReadAsync(peer.Mode, peer.LocalTenantId, peer.ConnectorId, peer.ApiBaseUrl, peer.SourceInstanceId,
            bearer, HttpMethod.Post, presentation.PreviewEndpoint, body, ct);
        RatelDeskReceiverReadException.RequireJsonSuccess(response, "template-preview");
        using var parsed = ReceiverWireValidation.Parse(response.Body);
        var preview = parsed.Deserialize<IncidentTemplatePreview>(AutomationTraceValidation.Json);
        if (preview is null || preview.Presentation != input.Presentation || !AutomationTraceValidation.Text(preview.Subject, 200) ||
            !AutomationTraceValidation.Text(preview.Summary, 2000, multiline: true) || preview.Html is not { Length: <= 65536 } ||
            preview.PlainText is not { Length: <= 65536 }) throw new InvalidDataException("template-preview-unverified");
        return preview;
    }
    public static AutomationIncidentContext Sample(string kind) => new(AutomationTraceContract.ContextVersion,
        kind, null, null, new("67", "Multi-node directory test"), "639300000000000001", "33333333-3333-4333-8333-333333333333",
        "44444444-4444-4444-8444-444444444444", "Failed", null, null, new(5, 0, 5, 0, 0, 0, 0),
        Enumerable.Range(1, 5).Select(i => new IncidentTargetEvidence("demo-node-" + i, "Demo node " + i, $"demo-{i}.example.test",
            (639300000000000001L + i).ToString(System.Globalization.CultureInfo.InvariantCulture), "Failed",
            [new("activity-" + i, 6, "List requested directory", "command", "Failed", 2, "Directory not found",
                "ls: cannot access '/demo/missing': No such file or directory", "available")], 0)).ToArray(), 0, "available", []);
}
