using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NetRatel.Shared.ServiceLinks;
using Xunit;

namespace NetRatel.API.IntegrationTests.ServiceLinks;

/// <summary>Real NetRatel candidate and an immutable published RatelDesk peer, each with PostgreSQL.</summary>
internal sealed class ServiceLinkPair : IAsyncDisposable
{
    public ServiceLinkPublishedRatelDeskPeer RatelDesk { get; private set; } = null!;
    public ServiceLinkNetRatelPeer NetRatel { get; private set; } = null!;
    public bool NetRatelInitiates { get; private set; }
    public string SessionBinding { get; } = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
    public ServiceLinkNavigation Start { get; private set; } = null!;
    public ServiceLinkRequestDescriptor Descriptor { get; private set; } = null!;
    public ServiceLinkReviewResponse Review { get; private set; } = null!;
    private readonly string evidenceId = Guid.NewGuid().ToString("N");
    public HttpClient Initiator => NetRatelInitiates ? NetRatel.Administrator : RatelDesk.Administrator;
    public HttpClient Responder => NetRatelInitiates ? RatelDesk.Administrator : NetRatel.Administrator;
    public ServiceLinkHttpProxy ResponderProxy => NetRatelInitiates ? RatelDesk.Proxy : NetRatel.Proxy;

    public static async Task<ServiceLinkPair> CreateAsync(bool netRatelInitiates, IInterceptor? interceptor = null, ServiceLinkNativeListener? nativeListener = null,
        ServiceLinkRotationTestPolicy? rotationPolicy = null)
    {
        var pair = new ServiceLinkPair { NetRatelInitiates = netRatelInitiates };
        try
        {
            pair.RatelDesk = await ServiceLinkPublishedRatelDeskPeer.CreateAsync(rotationPolicy);
            pair.NetRatel = await ServiceLinkNetRatelPeer.CreateAsync(pair.RatelDesk.ReachableHost, interceptor, nativeListener, rotationPolicy);
            pair.WriteEvidence("isolated-products-started");
            return pair;
        }
        catch { await pair.DisposeAsync(); throw; }
    }

    public async Task PrepareAsync(bool includeCallback = true, CancellationToken ct = default)
    {
        object body = NetRatelInitiates ? new ServiceLinkStartRequest(RatelDesk.WebBaseUrl, NetRatel.TenantId,
            RatelDesk.OrganizationId, [], SessionBinding)
        {
            InboundResourceIds = [NetRatel.ResourceId.ToString("D")],
            InboundRequestDefinitionIds = [NetRatel.RequestDefinitionId.ToString(System.Globalization.CultureInfo.InvariantCulture)],
            OutboundOrganizationId = RatelDesk.OrganizationId,
            OutboundCustomerIds = [RatelDesk.CustomerId],
            OutboundScopes = includeCallback ? RatelDeskScopes : RatelDeskScopes.Where(s => s != "rateldesk.orchestration.callback").ToArray()
        } : new
        {
            PeerWebBaseUrl = NetRatel.WebBaseUrl, LocalTenantId = RatelDesk.OrganizationId,
            RequestedResponderTenantId = NetRatel.TenantId, RequestedGrants = Array.Empty<ServiceLinkGrant>(), SessionBinding,
            LocalCustomerIds = new[] { RatelDesk.CustomerId },
            InboundScopes = includeCallback ? RatelDeskScopes : RatelDeskScopes.Where(s => s != "rateldesk.orchestration.callback").ToArray(),
            OutboundScopes = new[] { "netratel.orchestration.read", "netratel.orchestration.invoke" },
            OutboundResourceIds = new[] { NetRatel.ResourceId.ToString("D") },
            OutboundRequestDefinitionIds = new[] { NetRatel.RequestDefinitionId.ToString(System.Globalization.CultureInfo.InvariantCulture) }
        };
        Start = await PostAsync<ServiceLinkNavigation>(Initiator, "/api/v1/admin/service-links/start", body, ct);
        var state = QueryHelpers.ParseQuery(new Uri(Start.NavigationUrl).Query)["browser_state"].ToString();
        Descriptor = await PostAsync<ServiceLinkRequestDescriptor>(Responder, "/api/v1/admin/service-links/remote-review",
            new ServiceLinkRemoteReviewRequest(NetRatelInitiates ? NetRatel.WebBaseUrl : RatelDesk.WebBaseUrl, Start.AttemptId, state)
            { SessionBinding = NetRatelInitiates ? null : SessionBinding }, ct);
    }

    public async Task ReviewAsync(CancellationToken ct = default)
    {
        var state = QueryHelpers.ParseQuery(new Uri(Start.NavigationUrl).Query)["browser_state"].ToString();
        var navigation = await PostAsync<ServiceLinkNavigation>(Responder, "/api/v1/admin/service-links/remote-approve",
            new ServiceLinkRemoteApproveRequest(Start.AttemptId, NetRatelInitiates ? RatelDesk.OrganizationId : NetRatel.TenantId,
                Descriptor.RequestedGrants, state) { SessionBinding = NetRatelInitiates ? null : SessionBinding }, ct);
        var query = QueryHelpers.ParseQuery(new Uri(navigation.NavigationUrl).Query);
        Review = await PostAsync<ServiceLinkReviewResponse>(Initiator, "/api/v1/admin/service-links/callback",
            new ServiceLinkCallbackRequest(Start.AttemptId, query["pairing_code"].ToString(), query["browser_state"].ToString(),
                query["responder_instance_id"].ToString(), query["oauth_issuer"].ToString(), SessionBinding), ct);
    }

    public Task<ServiceLinkAdminStatus> ApproveAsync(CancellationToken ct = default) => PostAsync<ServiceLinkAdminStatus>(Initiator,
        $"/api/v1/admin/service-links/attempts/{Start.AttemptId}/approve", new ServiceLinkLocalApproveRequest(Review.GrantHash, SessionBinding), ct);

    public async Task ActivateAsync(CancellationToken ct = default)
    {
        await PrepareAsync(ct: ct); await ReviewAsync(ct); await ApproveAsync(ct); await FinishAsync(ct);
    }

    public async Task FinishAsync(CancellationToken ct = default)
    {
        for (var step = 0; step < 20; step++)
        {
            ct.ThrowIfCancellationRequested();
            var local = await StatusAsync(NetRatel.Administrator, ct); var remote = await StatusAsync(RatelDesk.Administrator, ct);
            if (local.LifecycleState == "active" && local.LocalBusinessSenderEnabled && remote.LifecycleState == "active" && remote.LocalBusinessSenderEnabled)
            {
                WriteEvidence("reciprocal-link-active", local, remote);
                return;
            }
            await ResumeAsync(Initiator, ct); await ResumeAsync(Responder, ct);
        }
        var nr = await StatusAsync(NetRatel.Administrator, ct); var rd = await StatusAsync(RatelDesk.Administrator, ct);
        Assert.Fail($"The real pair did not converge: NetRatel={nr.LifecycleState}/{nr.LastErrorCode}, RatelDesk={rd.LifecycleState}/{rd.LastErrorCode}.");
    }

    public Task<ServiceLinkAdminStatus> StatusAsync(HttpClient administrator, CancellationToken ct = default) =>
        ReadAsync<ServiceLinkAdminStatus>(administrator, $"/api/v1/admin/service-links/attempts/{Start.AttemptId}", ct);

    public async Task ResumeAsync(HttpClient administrator, CancellationToken ct = default)
    {
        using var response = await administrator.PostAsJsonAsync($"/api/v1/admin/service-links/links/{Review.GrantSummary.LinkId}/resume", new ServiceLinkAdminAction(), ct);
        Assert.True(response.IsSuccessStatusCode || response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.BadGateway,
            $"A real lifecycle step returned HTTP {(int)response.StatusCode}.");
    }

    public async Task<string> TokenAsync(bool netRatelIssuer, string scope, CancellationToken ct = default)
    {
        var proxy = netRatelIssuer ? NetRatel.Proxy : RatelDesk.Proxy;
        var client = netRatelIssuer ? NetRatel.Anonymous : RatelDesk.Anonymous;
        Assert.Single(proxy.ObservedClients);
        var accepted = proxy.ObservedClients.Values.Single();
        using var response = await client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["grant_type"] = "client_credentials", ["client_id"] = accepted.ClientId, ["client_secret"] = accepted.Secret, ["scope"] = scope }), ct);
        Assert.True(response.IsSuccessStatusCode, $"Actual accepted peer credentials could not request their stored scope: HTTP {(int)response.StatusCode}.");
        var token = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
        return token.GetProperty("access_token").GetString()!;
    }

    public static async Task<HttpStatusCode> GetWithTokenAsync(HttpClient client, string path, string token, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request, ct);
        return response.StatusCode;
    }

    public static async Task<T> PostAsync<T>(HttpClient client, string path, object body, CancellationToken ct = default)
    {
        using var response = await client.PostAsJsonAsync(path, ServiceLinkNetRatelPeer.ProjectLifecycle(path, body), ct);
        var errorCode = response.IsSuccessStatusCode ? null : await SafeErrorCodeAsync(response, ct);
        Assert.True(response.IsSuccessStatusCode, $"The actual administrator command {path} returned HTTP {(int)response.StatusCode} ({errorCode}).");
        return (await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct))!;
    }

    private static async Task<string> SafeErrorCodeAsync(HttpResponseMessage response, CancellationToken ct = default)
    {
        // Only a bounded protocol code enters a public test log. Never log the
        // response body, browser proof, credentials, tokens or arbitrary peer text.
        if (response.Content.Headers.ContentType?.MediaType is not ("application/problem+json" or "application/json") ||
            response.Content.Headers.ContentLength is > 16384)
            return "unclassified";
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var buffer = new byte[16385];
            var length = 0;
            while (length < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(length), ct);
                if (read == 0) break;
                length += read;
            }
            if (length > 16384) return "unclassified";
            using var problem = JsonDocument.Parse(buffer.AsMemory(0, length));
            if (!problem.RootElement.TryGetProperty("code", out var code) || code.ValueKind != JsonValueKind.String)
                return "unclassified";
            var value = code.GetString();
            return value is { Length: > 0 and <= 128 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
                ? value : "unclassified";
        }
        catch (JsonException) { return "unclassified"; }
    }

    private static async Task<T> ReadAsync<T>(HttpClient client, string path, CancellationToken ct = default)
    {
        using var response = await client.GetAsync(path, ct);
        Assert.True(response.IsSuccessStatusCode, $"The actual administrator status returned HTTP {(int)response.StatusCode}.");
        return (await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct))!;
    }

    private static readonly string[] RatelDeskScopes = ["rateldesk.incidents.create", "rateldesk.incident-receipts.read", "rateldesk.incident-targets.read", "rateldesk.orchestration.callback"];

    private void WriteEvidence(string phase, ServiceLinkAdminStatus? local = null, ServiceLinkAdminStatus? remote = null)
    {
        var directory = Environment.GetEnvironmentVariable("NETRATEL_SERVICE_LINK_EVIDENCE_DIRECTORY")
            ?? Path.Combine("TestResults", "service-link-proof");
        Directory.CreateDirectory(directory);
        // A bounded public receipt deliberately excludes browser state, codes, cookies, tokens and all credential material.
        var receipt = new
        {
            contract = ServiceLinkContract.Version, phase, netRatelInitiates = NetRatelInitiates,
            netRatel = new { instanceId = NetRatel.InstanceId, sourceInstanceId = NetRatel.SourceInstanceId, tenantId = NetRatel.TenantId,
                assemblyInformationalVersion = typeof(global::Program).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                    .Cast<System.Reflection.AssemblyInformationalVersionAttribute>().Single().InformationalVersion,
                ciSourceSha = Environment.GetEnvironmentVariable("GITHUB_SHA"),
                runtime = "complete Production API Kestrel and PostgreSQL" },
            ratelDesk = new { instanceId = RatelDesk.InstanceId, version = ServiceLinkPublishedRatelDeskPeer.PublishedVersion,
                sourceSha = ServiceLinkPublishedRatelDeskPeer.PublishedSource, apiImage = ServiceLinkPublishedRatelDeskPeer.ApiImage, webImage = ServiceLinkPublishedRatelDeskPeer.WebImage,
                runtime = "published API/Web images with independently bootstrapped PostgreSQL; actual image labels and HTTP metadata verified" },
            link = local is null ? null : new { local.AttemptId, local.LinkId, local.LinkRevision, local.GrantHash, local.CommitId,
                local.Decision, local.LocalInboundActive, local.LocalBusinessSenderEnabled, peerInboundActive = remote!.LocalInboundActive,
                peerBusinessSenderEnabled = remote.LocalBusinessSenderEnabled },
            lostNetRatelResponses = NetRatel.Proxy.LostResponses, lostRatelDeskResponses = RatelDesk.Proxy.LostResponses,
            scope = "reciprocal credential lifecycle only; Flow sender and recorded task callback require separate end-to-end receipts"
        };
        File.WriteAllText(Path.Combine(directory, evidenceId + ".json"), JsonSerializer.Serialize(receipt, new JsonSerializerOptions { WriteIndented = true }));
    }

    public async ValueTask DisposeAsync()
    {
        if (NetRatel is not null) await NetRatel.DisposeAsync();
        if (RatelDesk is not null) await RatelDesk.DisposeAsync();
    }
}
