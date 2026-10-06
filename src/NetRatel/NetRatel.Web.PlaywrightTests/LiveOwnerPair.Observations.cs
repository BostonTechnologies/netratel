using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Playwright;
using NetRatel.Shared.ServiceLinks;
using static Microsoft.Playwright.Assertions;

namespace NetRatel.Web.PlaywrightTests;

internal sealed partial class LiveOwnerPair
{
    public async Task VerifyPublicMetadataAsync()
    {
        foreach (var netRatel in new[] { true, false })
        {
            var web = netRatel ? NetRatelWeb : RatelDeskWeb;
            var api = netRatel ? NetRatelApi : RatelDeskApi;
            var version = netRatel ? pins.NetRatel.Version : pins.RatelDesk.Version;
            using var webResponse = await anonymous.GetAsync(web + ServiceLinkContract.MetadataPath);
            Check((int)webResponse.StatusCode == 200 && webResponse.Headers.CacheControl?.NoStore == true, "The actual public product Web does not disclose the fixed metadata contract with no-store.");
            var metadata = await webResponse.Content.ReadFromJsonAsync<ServiceLinkMetadata>(Json) ?? throw new InvalidOperationException("Actual public Web metadata is absent.");
            Check(metadata.Contract == ServiceLinkContract.Version && metadata.SupportedContracts.Contains(ServiceLinkContract.Version)
                && metadata.Product == (netRatel ? "netratel" : "rateldesk") && metadata.ProductVersion == version
                && metadata.InstanceId == (netRatel ? netRatelInstanceId : ratelDeskInstanceId.ToString("D"))
                && metadata.WebBaseUrl == web && metadata.ApiBaseUrl == api && metadata.OauthIssuer == (netRatel ? NetRatelIssuer : RatelDeskIssuer)
                && metadata.Audience == (netRatel ? "netratel.owner-browser.services" : "rateldesk.owner-browser.services")
                && metadata.OauthMetadataUrl == api + "/.well-known/oauth-authorization-server"
                && metadata.JwksUri == api + (netRatel ? "/.well-known/service-jwks.json" : "/.well-known/jwks.json")
                && metadata.TokenEndpointAuthMethodsSupported.SequenceEqual(["client_secret_post"])
                && metadata.TokenEndpoint == api + "/connect/token" && metadata.ServiceLinkEndpoint == api + ServiceLinkContract.EndpointPath
                && metadata.ApprovalEndpoint == web + "/account/integration-credentials/link/approve"
                && metadata.CallbackEndpoint == web + "/account/integration-credentials/link/callback", "The actual Web metadata differs from the exact product version/instance/separate Web/API identity.");
            if (netRatel) Check(metadata.SourceInstanceId == sourceInstanceId.ToString("D"), "The discovered actual NetRatel metadata does not retain its explicitly adopted producer.");
            using var apiResponse = await anonymous.GetAsync(api + ServiceLinkContract.MetadataPath);
            Check((int)apiResponse.StatusCode == 200, "The separately advertised actual API metadata is unavailable.");
            var apiMetadata = await apiResponse.Content.ReadFromJsonAsync<ServiceLinkMetadata>(Json);
            Check(JsonSerializer.Serialize(metadata, Json) == JsonSerializer.Serialize(apiMetadata, Json), "Actual Web and API metadata disagree.");
            if (netRatel) nrMetadata = metadata; else rdMetadata = metadata;
        }
        // Companion exposure remains a single exact discovery exception.
        using var blocked = await anonymous.GetAsync(RatelDeskWeb + "/api/integrations/unrelated-owner-fixture");
        Check((int)blocked.StatusCode == 404, "The companion discovery exception exposes an unrelated integration API path.");
    }

    public async Task<OriginalObservation> ReadOriginalObservationAsync(IAPIRequestContext api)
    {
        var list = await ReadJsonAsync(await api.GetAsync(InitiatorWeb + "/api/v1/admin/service-links"), 200);
        Check(list.GetArrayLength() == 1, "The initiating owner has an unexpected attempt count.");
        var status = JsonSerializer.Deserialize<ServiceLinkAdminStatus>(list[0].GetRawText(), Json)!;
        if (attemptId.Length == 0) attemptId = status.AttemptId;
        Check(status.AttemptId == attemptId, "The current owner status does not refer to the original attempt.");
        return new(status.AttemptId, status.Descriptor.DescriptorHash, status.Decision, status.LocalInboundActive, status.LocalBusinessSenderEnabled,
            await CountAsync(nrInitiates, "attempts"), OwnerCommandCount("start"));
    }
    private async Task<ServiceLinkAdminStatus> StatusAsync(bool netRatel)
    {
        Check(browserApi is not null && attemptId.Length > 0, "The actual browser/attempt observation boundary is unavailable.");
        var json = await ReadJsonAsync(await browserApi!.GetAsync((netRatel ? NetRatelWeb : RatelDeskWeb) + "/api/v1/admin/service-links/attempts/" + Uri.EscapeDataString(attemptId)), 200);
        var status = JsonSerializer.Deserialize<ServiceLinkAdminStatus>(json.GetRawText(), Json) ?? throw new InvalidOperationException("The redacted actual status is absent.");
        if (netRatel) finalNrStatus = status; else finalRdStatus = status;
        return status;
    }
    public async Task AssertNoBusinessAuthorityAsync(IAPIRequestContext api)
    {
        browserApi = api;
        foreach (var product in new[] { true, false })
        {
            var status = await StatusAsync(product);
            Check(status.Decision == "undecided" && !status.LocalInboundActive && !status.LocalBusinessSenderEnabled && !status.PeerActiveAcknowledged && status.LifecycleState != "active",
                "Discovery or responder consent enabled business authority before both exact final consents.");
        }
    }
    public async Task AssertExactStoredGrantsAndDisplayedDetailsAsync(IPage page)
    {
        var nr = await StatusAsync(netRatel: true);
        var rd = await StatusAsync(netRatel: false);
        Check(nr.GrantSummary is not null && rd.GrantSummary is not null && nr.GrantHash == rd.GrantHash && nr.GrantHash is { Length: > 0 }
            && nr.LinkId == rd.LinkId && nr.LinkId is { Length: > 0 } && nr.LinkRevision == rd.LinkRevision,
            "The actual peers do not retain the same exact protected grant summary/link revision.");
        Check(JsonSerializer.Serialize(nr.GrantSummary, Json) == JsonSerializer.Serialize(rd.GrantSummary, Json), "The actual protected summaries differ across peers.");
        RequireExactGrants(nr);
        await AssertDisplayedDetailsAsync(page, responder: false);
        var namespaceId = nr.GrantSummary.Grants.Single(x => x.TargetProduct == "rateldesk").SourceNamespaceId;
        Check(Guid.TryParseExact(namespaceId, "D", out _), "The final protected grant lacks its actual receiver source namespace.");
        await Expect(page.GetByTestId("service-link-consent-details")).ToContainTextAsync("Stable receiver source namespace: " + namespaceId);
    }
    private void RequireExactGrants(ServiceLinkAdminStatus status)
    {
        var grants = status.GrantSummary!.Grants;
        Check(grants.Length == 2, "The final stored consent has an unexpected direction count.");
        Check(nrMetadata is not null && rdMetadata is not null
            && JsonSerializer.Serialize(status.Descriptor.InitiatorEndpointSnapshot, Json) == JsonSerializer.Serialize(nrInitiates ? nrMetadata : rdMetadata, Json)
            && JsonSerializer.Serialize(status.Descriptor.ResponderEndpointSnapshot, Json) == JsonSerializer.Serialize(nrInitiates ? rdMetadata : nrMetadata, Json),
            "The stored grant's pinned endpoint snapshots differ from the actual previously verified product identities.");
        var nr = grants.Single(x => x.TargetProduct == "netratel");
        var rd = grants.Single(x => x.TargetProduct == "rateldesk");
        Check(nr.DirectionId == (nrInitiates ? ServiceLinkContract.ResponderToInitiator : ServiceLinkContract.InitiatorToResponder)
            && nr.CallerSnapshot == (nrInitiates ? "responder" : "initiator") && nr.TargetSnapshot == (nrInitiates ? "initiator" : "responder")
            && nr.CallerProduct == "rateldesk" && nr.CallerInstanceId == ratelDeskInstanceId.ToString("D") && nr.TargetInstanceId == netRatelInstanceId && nr.CallerTenantId == organizationId && nr.TargetTenantId == tenantId
            && nr.Issuer == NetRatelIssuer && nr.Audience == nrMetadata!.Audience && nr.SourceInstanceId is null && nr.SourceNamespaceId is null
            && nr.Capabilities.Order(StringComparer.Ordinal).SequenceEqual(status.Descriptor.RequestedGrants.Single(x => x.TargetProduct == "netratel").Capabilities.Order(StringComparer.Ordinal))
            && nr.ResourceConstraints.TenantId == tenantId && nr.ResourceConstraints.OrganizationId is null
            && nr.ResourceConstraints.CustomerIds.Length == 0 && nr.ResourceConstraints.RequestIds.Length == 0 && nr.ResourceConstraints.TaskIds.Length == 0
            && nr.ResourceConstraints.ResourceIds.SequenceEqual([agentId]) && nr.ResourceConstraints.RequestDefinitionIds.SequenceEqual([definitionId])
            && nr.Scopes.Order(StringComparer.Ordinal).SequenceEqual(new[] { "netratel.orchestration.invoke", "netratel.orchestration.read" }), "The exact NetRatel resource/definition grant was broadened or rebound.");
        Check(rd.DirectionId == (nrInitiates ? ServiceLinkContract.InitiatorToResponder : ServiceLinkContract.ResponderToInitiator)
            && rd.CallerSnapshot == (nrInitiates ? "initiator" : "responder") && rd.TargetSnapshot == (nrInitiates ? "responder" : "initiator")
            && rd.CallerProduct == "netratel" && rd.CallerInstanceId == netRatelInstanceId && rd.TargetInstanceId == ratelDeskInstanceId.ToString("D") && rd.CallerTenantId == tenantId && rd.TargetTenantId == organizationId
            && rd.Issuer == RatelDeskIssuer && rd.Audience == rdMetadata!.Audience && rd.SourceInstanceId == sourceInstanceId.ToString("D")
            && rd.Capabilities.Order(StringComparer.Ordinal).SequenceEqual(status.Descriptor.RequestedGrants.Single(x => x.TargetProduct == "rateldesk").Capabilities.Order(StringComparer.Ordinal))
            && rd.ResourceConstraints.OrganizationId == organizationId && rd.ResourceConstraints.TenantId is null
            && rd.ResourceConstraints.CustomerIds.SequenceEqual([customerId]) && rd.ResourceConstraints.RequestIds.Length == 0 && rd.ResourceConstraints.TaskIds.Length == 0
            && rd.ResourceConstraints.ResourceIds.Length == 0 && rd.ResourceConstraints.RequestDefinitionIds.Length == 0
            && rd.Scopes.Order(StringComparer.Ordinal).SequenceEqual(new[] { "rateldesk.incident-receipts.read", "rateldesk.incident-targets.read", "rateldesk.incidents.create", "rateldesk.orchestration.callback" }), "The exact RatelDesk organization/customer/source grant was broadened or rebound.");
    }

    public async Task ExpectIncompleteUiAndDurableConsentAsync(IPage page)
    {
        Check(responderStopped, "A truthful interruption check requires the actual responder to be stopped.");
        Check(await CountAsync(nrInitiates, "consents") == 1, "The original owner's final local consent was not durably saved before the actual peer loss.");
        var status = await StatusAsync(nrInitiates);
        Check(!Connected(status) && !status.LocalInboundActive && !status.LocalBusinessSenderEnabled && !status.PeerActiveAcknowledged,
            "The original owner reported business activation without the stopped peer's required activation acknowledgement.");
        await page.GotoAsync(IntegrationUrl(nrInitiates));
        var list = LinkList(page, nrInitiates);
        await list.GetByRole(AriaRole.Button, new() { Name = "Refresh status", Exact = true }).ClickAsync();
        var card = LinkCard(page, nrInitiates);
        await Expect(card).ToBeVisibleAsync();
        await Expect(card.GetByText("Connected", new() { Exact = true })).ToHaveCountAsync(0);
        await Expect(card.GetByRole(AriaRole.Button, new() { Name = "Resume", Exact = true })).ToBeVisibleAsync();
        // Commit may still be undecided before exchange. This proves a durable
        // final consent and honest partial state, not a fabricated lost-ack timing claim.
    }
    public async Task ResumeThroughUiAsync(IPage page)
    {
        Check(!responderStopped, "Resume must use the restored actual peer.");
        await page.GotoAsync(IntegrationUrl(nrInitiates));
        await LinkCard(page, nrInitiates).GetByRole(AriaRole.Button, new() { Name = "Resume", Exact = true }).ClickAsync();
        // Live workers can already have progressed after restoration; an idempotent
        // real rendered Resume is required, without attributing all recovery to it.
    }
    public async Task WaitForConnectedAsync(IPage page)
    {
        for (var step = 0; step < 90; step++)
        {
            finalNrStatus = await StatusAsync(netRatel: true);
            finalRdStatus = await StatusAsync(netRatel: false);
            if (Connected(finalNrStatus) && Connected(finalRdStatus)) break;
            await Task.Delay(1000);
        }
        Check(finalNrStatus is not null && finalRdStatus is not null && Connected(finalNrStatus) && Connected(finalRdStatus), "The two actual products did not converge to complete fresh directional authority.");
        Check(finalNrStatus!.AttemptId == attemptId && finalRdStatus!.AttemptId == attemptId && finalNrStatus.LinkId == finalRdStatus.LinkId && finalNrStatus.GrantHash == finalRdStatus.GrantHash
            && finalNrStatus.CommitId == finalRdStatus.CommitId && finalNrStatus.CommitId is { Length: > 0 }, "The active actual products disagree on the original durable decision/link.");
        foreach (var product in new[] { true, false })
        {
            await page.GotoAsync(IntegrationUrl(product));
            await LinkList(page, product).GetByRole(AriaRole.Button, new() { Name = "Refresh status", Exact = true }).ClickAsync();
            await Expect(LinkCard(page, product).GetByText("Connected", new() { Exact = true })).ToBeVisibleAsync();
        }
    }
    public async Task AssertOnlyOriginalAttemptsClientsAndConsentsAsync()
    {
        foreach (var product in new[] { true, false })
        {
            Check(await CountAsync(product, "attempts") == 1 && await CountAsync(product, "principals") == 1 && await CountAsync(product, "consents") == 1,
                "Recovery/continuation created an additional attempt, service principal or local consent.");
        }
        Check(OwnerCommandCount("start") == 1 && OwnerCommandCount("respond") == 1 && OwnerCommandCount("confirm") == 1
            && OwnerCommandCount("continue") == (scenario == "signed-out" ? 1 : 0), "The actual owner journey performed an unexpected start or consent count.");
        Check(pageErrorCount == 0, "The actual owner journey emitted a browser page error.");
        Check(approvalHopCount == (scenario == "signed-out" ? 2 : 1) && callbackHopCount == 1 && protectedHopFailures == 0,
            "The actual approval/callback hops did not preserve the required secrecy headers or expected continuation count.");
    }
    public async Task AssertReadOnlyProbeThroughUiAsync(IPage page)
    {
        var before = await BusinessCountsAsync();
        await page.GotoAsync(IntegrationUrl(netRatel: true));
        await page.GetByTestId("helpdesk-test-connection").ClickAsync();
        var result = page.GetByTestId("helpdesk-connection-test-result");
        await Expect(result).ToContainTextAsync("NetRatel → RatelDesk fresh authenticated probe: passed");
        await Expect(result).ToContainTextAsync("RatelDesk → NetRatel peer acknowledgement: confirmed");
        await Expect(result).ToContainTextAsync("Incident delivery: requires separate receiver capability and target validation");
        await Expect(result).ToContainTextAsync("The test creates no incident and invokes no task.");
        var after = await BusinessCountsAsync();
        Check(before.SequenceEqual(after), "The actual read-only connection probe changed business incident/request/run/task counts.");
        readOnlyProbePassed = true;
        // The production server uses its own protected outbound credential.
        // This test never reads/copies a saved secret or acquires a service token.
    }
    private static bool Connected(ServiceLinkAdminStatus status) => status.LifecycleState == "active" && status.LocalInboundActive && status.LocalBusinessSenderEnabled && status.PeerActiveAcknowledged;
    private static ILocator LinkList(IPage page, bool netRatel) => page.GetByTestId(netRatel ? "helpdesk-service-link-list" : "netratel-service-link-list");
    private static ILocator LinkCard(IPage page, bool netRatel) => page.GetByTestId(netRatel ? "helpdesk-link-status" : "netratel-link-status");
    private int OwnerCommandCount(string action) { lock (commandCounts) return commandCounts.GetValueOrDefault(action); }
    private async Task<KeyValuePair<string, long>[]> BusinessCountsAsync()
    {
        var counts = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach (var key in new[] { "runs", "requests", "task-activities" }) counts["netratel-" + key] = await CountAsync(netRatel: true, key);
        foreach (var key in new[] { "incidents", "requests", "tasks", "incident-receipts" }) counts["rateldesk-" + key] = await CountAsync(netRatel: false, key);
        return counts.ToArray();
    }
    private async Task<long> CountAsync(bool netRatel, string key)
    {
        // Fixed allowlisted SELECTs only. No grant seeds or mutable DB shortcuts.
        var sql = (netRatel, key) switch
        {
            (_, "attempts") => "SELECT count(*) FROM \"ServiceLinkAttempts\"",
            (_, "principals") => "SELECT count(*) FROM \"ServicePrincipalRegistrations\"",
            (_, "consents") => "SELECT count(*) FROM \"ServiceLinkAttempts\" WHERE \"ConsentId\" IS NOT NULL",
            (true, "runs") => "SELECT count(*) FROM \"JobRuns\"",
            (true, "requests") => "SELECT count(*) FROM \"Requests\"",
            (true, "task-activities") => "SELECT count(*) FROM \"JobTaskActivities\"",
            (false, "incidents") => "SELECT count(*) FROM \"Tickets\" WHERE \"Discriminator\" = 'Incident'",
            (false, "requests") => "SELECT count(*) FROM \"Tickets\" WHERE \"Discriminator\" = 'Request'",
            (false, "tasks") => "SELECT count(*) FROM \"Tickets\" WHERE \"Discriminator\" = 'RequestTask'",
            (false, "incident-receipts") => "SELECT count(*) FROM \"IncidentCreateReceipts\"",
            _ => throw new InvalidOperationException("The owner fixture count selector is not allowlisted.")
        };
        var database = netRatel ? "netratel" : "rateldesk";
        var output = await ComposeAsync(["exec", "-T", netRatel ? "nr-postgres" : "rd-postgres", "psql", "-U", database, "-d", database, "-At", "-c", sql]);
        return long.Parse(output.Trim(), CultureInfo.InvariantCulture);
    }

    public async Task WriteReceiptAsync(string outcome, string phase, IPage? page)
    {
        string? product = null, route = null;
        if (page is null && phase == "wait-product-readiness")
        {
            product = readinessProduct;
            route = readinessRoute;
        }
        if (page is not null && Uri.TryCreate(page.Url, UriKind.Absolute, out var current))
        {
            product = current.GetLeftPart(UriPartial.Authority) == NetRatelWeb ? "netratel" : current.GetLeftPart(UriPartial.Authority) == RatelDeskWeb ? "rateldesk" : "unknown";
            // Transient /approve or /callback paths are safe; the query is never read/exported.
            route = current.AbsolutePath;
        }
        var receipt = new
        {
            outcome, phase, scenario, netRatelInitiates = nrInitiates, contract = ServiceLinkContract.Version,
            actualRuntime = "production NetRatel source-image Web/API/migrations/Pg + independently pinned published RatelDesk Web/API/Pg",
            proofScope = "actual foundation owner browser provisioning, exact two consents, original-session Continue, truthful partial/recovery and read-only connection test; no Flow delivery or recorded callback execution claim",
            transport = "disposable private HTTP with explicit fixture-only opt-in; separate canonical Web/API addresses",
            product, route, lastHttpStatus = page is null && phase == "wait-product-readiness" ? readinessHttpStatus : lastHttpStatus,
            images, databaseVersions, identities = new { netRatelInstanceId, ratelDeskInstanceId, sourceInstanceId, NetRatelWeb, NetRatelApi, RatelDeskWeb, RatelDeskApi, tenantId, organizationId, customerId, agentId, definitionId },
            original, ownerCommandCounts = new { start = OwnerCommandCount("start"), responderConsent = OwnerCommandCount("respond"), finalConsent = OwnerCommandCount("confirm"), continuation = OwnerCommandCount("continue") },
            netRatel = SafeStatus(finalNrStatus), ratelDesk = SafeStatus(finalRdStatus), readOnlyProbePassed,
            incidentDeliveryReady = false, browserPageErrorCount = pageErrorCount,
            protectedBrowserHops = new { approvalHopCount, callbackHopCount, protectedHopFailures }, visuals,
            visualInspection = "required separately; screenshot creation/geometry checks do not substitute for human/model inspection"
        };
        await File.WriteAllTextAsync(Path.Combine(evidenceRoot, CaseId + ".json"), JsonSerializer.Serialize(receipt, new JsonSerializerOptions(Json) { WriteIndented = true }));
    }
    private static object? SafeStatus(ServiceLinkAdminStatus? status) => status is null ? null : new
    {
        status.AttemptId, status.LinkId, status.LinkRevision, status.LifecycleState, status.Decision, status.GrantHash, status.CommitId,
        status.LocalInboundActive, status.LocalBusinessSenderEnabled, status.PeerActiveAcknowledged, status.LastErrorCode
    };
    internal sealed record OriginalObservation(string AttemptId, string DescriptorHash, string Decision, bool LocalInboundActive, bool LocalBusinessSenderEnabled, long InitiatorAttemptCount, int OwnerStartCount);
}
