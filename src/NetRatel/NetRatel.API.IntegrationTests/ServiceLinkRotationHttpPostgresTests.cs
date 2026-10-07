using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.ServiceIdentity;
using NetRatel.Infrastructure.ServiceLinks;
using NetRatel.Shared.ServiceLinks;
using Xunit;

namespace NetRatel.API.IntegrationTests.ServiceLinks;

// The optional fixture configuration affects only these actual peer tests. Existing
// deterministic recovery tests and all product defaults retain their configuration.
internal sealed record ServiceLinkRotationTestPolicy(bool NetRatelIssuer, bool Automatic);
internal sealed record ServiceLinkHistoricalAgePrecondition(int RowsChanged, Guid PrincipalId, long CredentialRevision,
    DateTimeOffset OriginalCreatedAtUtc, DateTimeOffset HistoricalCreatedAtUtc, DateTimeOffset OriginalHardExpiryUtc,
    bool AllOtherSecretColumnsUnchanged, bool PrincipalUnchanged);

[Collection(ServiceLinkRotationPeerCollection.Name)]
[Trait("category", "manual-integration")]
public sealed class ServiceLinkRotationHttpPostgresTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task Manual_and_policy_rotation_survive_each_committed_response_loss_and_restart_with_live_traffic_and_fixed_real_retirement(
        bool netRatelIssuer, bool automatic)
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var ct = budget.Token;
        var startedAt = DateTimeOffset.UtcNow;
        await using var pair = await ServiceLinkPair.CreateAsync(netRatelInitiates: netRatelIssuer,
            rotationPolicy: new(netRatelIssuer, automatic));
        await pair.ActivateAsync(ct);
        await Task.WhenAll(pair.NetRatel.WaitForInitialSensitiveWindowAsync(ct),
            pair.RatelDesk.WaitForPostActivationSensitiveWindowAsync(ct));
        var baseline = await ReadPostActivationBaselineAsync(pair, ct);
        var issuer = netRatelIssuer ? pair.NetRatel.Administrator : pair.RatelDesk.Administrator;
        var issuerProxy = netRatelIssuer ? pair.NetRatel.Proxy : pair.RatelDesk.Proxy;
        var callerProxy = netRatelIssuer ? pair.RatelDesk.Proxy : pair.NetRatel.Proxy;
        var issuerClient = netRatelIssuer ? pair.NetRatel.Anonymous : pair.RatelDesk.Anonymous;
        var direction = baseline.GrantSummary!.Grants.Single(g => g.TargetProduct == (netRatelIssuer ? "netratel" : "rateldesk")).DirectionId;
        var link = baseline.LinkId!;
        var route = ServiceLinkContract.EndpointPath + $"/links/{link}";
        var oldClient = Assert.Single(issuerProxy.ObservedClients).Value;
        var cachedPredecessor = await AcquireTokenAsync(issuerClient, oldClient.ClientId, oldClient.Secret,
            netRatelIssuer ? "netratel.orchestration.read" : "rateldesk.orchestration.callback", ct);
        Assert.Equal(HttpStatusCode.OK, await BusinessReadAsync(pair, netRatelIssuer, cachedPredecessor, ct));
        var incidentKey = "rotation-proof-" + Guid.NewGuid().ToString("N");
        await AssertIncidentReplayAsync(pair, incidentKey, first: true, ct);
        await AssertCurrentSenderTrafficAsync(pair, incidentKey, ct);

        using var offerFault = callerProxy.LoseNextCommittedRotationResponse(route + "/rotate", "offer");
        using var verifyFault = issuerProxy.LoseNextCommittedRotationResponse(route + "/verify", "verify", bindToOffer: offerFault);
        using var activationFault = issuerProxy.LoseNextCommittedRotationResponse(route + "/rotate", "verified", bindToOffer: offerFault);
        using var switchFault = issuerProxy.LoseNextCommittedRotationResponse(route + "/rotate", "switched", bindToOffer: offerFault);
        ServiceLinkHistoricalAgePrecondition? historicalAge = null;
        if (automatic)
        {
            var policyStatus = await pair.StatusAsync(issuer, ct);
            Assert.True(policyStatus.AutomaticRotationEnabled);
            Assert.Equal(1, policyStatus.RotationAgeDays);
            Assert.Equal(60, policyStatus.RotationOverlapSeconds);
            Assert.Empty(policyStatus.Rotations);
            // Explicit synthetic historical-age input, applied only after genuine human
            // consent, probe, commit and active business authority. No clock is advanced;
            // no link, principal, rotation or successor authority is seeded.
            historicalAge = netRatelIssuer
                ? await ApplyHistoricalNetRatelPredecessorAgeAsync(pair, link, direction, ct)
                : await pair.RatelDesk.ApplyHistoricalPredecessorAgeAsync(link, direction, 1);
        }
        else
        {
            var manual = await ServiceLinkPair.PostAsync<ServiceLinkAdminStatus>(issuer,
                $"/api/v1/admin/service-links/links/{link}/rotate", new ServiceLinkAdminAction(DirectionId: direction), ct);
            Assert.Single(manual.Rotations);
        }

        var offer = await offerFault.Completed.WaitAsync(TimeSpan.FromSeconds(30), ct);
        var offered = offer.ReadRequest<ServiceLinkLifecycleRequest>();
        var candidate = offered.CredentialForCaller!;
        Assert.Equal("offer", offered.RotationPhase);
        Assert.Equal(link, offered.LinkId);
        Assert.Equal(direction, offered.DirectionId);
        Assert.Equal(1, offered.ExpectedCurrentCredentialRevision);
        Assert.Equal(2, offered.SuccessorCredentialRevision);
        Assert.True(candidate.ClientId == oldClient.ClientId, "Rotation changed the logical client identity.");
        Assert.Equal(2, candidate.CredentialRevision);
        var callerPrepared = await RotationAsync(pair, !netRatelIssuer, offered.RotationId!, ct);
        Assert.Equal("prepared", callerPrepared.RotationState);
        Assert.Null(callerPrepared.ActivateDecisionId);
        Assert.Null(callerPrepared.CallerSwitchRevision);
        await AssertPendingCandidateRestrictionsAsync(pair, netRatelIssuer, candidate, offered, ct);
        await AssertStableIdentityAsync(pair, baseline, ct);
        await AssertCurrentSenderTrafficAsync(pair, incidentKey, ct);
        await RestartBothAndObserveRecoveryAsync(pair, offerFault, ct);
        await AssertCurrentSenderTrafficAsync(pair, incidentKey, ct);
        offerFault.Release();

        var verified = await verifyFault.Completed.WaitAsync(TimeSpan.FromSeconds(30), ct);
        var probe = verified.ReadRequest<ServiceLinkLifecycleRequest>();
        var probeResult = verified.ReadResponse<JsonElement>();
        Assert.Equal(offered.RotationId, probe.RotationId);
        Assert.Equal(candidate.CredentialRevision, probe.CredentialRevision);
        var receiptId = probeResult.GetProperty("verification_receipt_id").GetString()!;
        Assert.False(string.IsNullOrWhiteSpace(receiptId));
        Assert.Null((await RotationAsync(pair, netRatelIssuer, offered.RotationId!, ct)).ActivateDecisionId);
        await AssertCurrentSenderTrafficAsync(pair, incidentKey, ct);
        await RestartBothAndObserveRecoveryAsync(pair, verifyFault, ct);
        verifyFault.Release();

        var activated = await activationFault.Completed.WaitAsync(TimeSpan.FromSeconds(30), ct);
        var activation = activated.ReadRequest<ServiceLinkLifecycleRequest>();
        var activationResult = activated.ReadResponse<JsonElement>();
        Assert.Equal(offered.RotationId, activation.RotationId);
        Assert.Equal(receiptId, activation.SuccessorVerificationReceiptId);
        var decisionId = activationResult.GetProperty("activate_decision_id").GetString()!;
        Assert.False(string.IsNullOrWhiteSpace(decisionId));
        var durableActivation = await RotationAsync(pair, netRatelIssuer, offered.RotationId!, ct);
        Assert.Equal(decisionId, durableActivation.ActivateDecisionId);
        Assert.Null(durableActivation.CallerSwitchRevision);
        Assert.Null((await RotationAsync(pair, !netRatelIssuer, offered.RotationId!, ct)).CallerSwitchRevision);
        Assert.Equal(HttpStatusCode.OK, await BusinessReadAsync(pair, netRatelIssuer, cachedPredecessor, ct));
        await AssertCurrentSenderTrafficAsync(pair, incidentKey, ct);
        await RestartBothAndObserveRecoveryAsync(pair, activationFault, ct);
        activationFault.Release();

        var switched = await switchFault.Completed.WaitAsync(TimeSpan.FromSeconds(30), ct);
        var switchRequest = switched.ReadRequest<ServiceLinkLifecycleRequest>();
        var switchResult = switched.ReadResponse<JsonElement>();
        Assert.Equal(offered.RotationId, switchRequest.RotationId);
        Assert.Equal(decisionId, switchRequest.ActivateDecisionId);
        Assert.Equal(receiptId, switchRequest.SuccessorVerificationReceiptId);
        Assert.True(switchRequest.CallerSwitchRevision >= 1);
        var retirement = ServiceLinkCanonicalJson.ParseWholeSecondUtcTimestamp(switchResult.GetProperty("predecessor_retire_at").GetString()!);
        Assert.InRange(retirement - DateTimeOffset.UtcNow, TimeSpan.Zero, TimeSpan.FromSeconds(60));
        var durableSwitch = await RotationAsync(pair, !netRatelIssuer, offered.RotationId!, ct);
        Assert.Equal(switchRequest.CallerSwitchRevision, durableSwitch.CallerSwitchRevision);
        var cachedJwtExpiry = new JwtSecurityTokenHandler().ReadJwtToken(cachedPredecessor).ValidTo;
        Assert.True(cachedJwtExpiry > retirement.UtcDateTime.AddMinutes(1), "The cached predecessor JWT must still be unexpired when credential retirement rejects it.");
        Assert.Equal(HttpStatusCode.OK, await BusinessReadAsync(pair, netRatelIssuer, cachedPredecessor, ct));
        await AssertCurrentSenderTrafficAsync(pair, incidentKey, ct);
        await RestartBothAndObserveRecoveryAsync(pair, switchFault, ct);
        // Keep the successful switched response lost across restart and real retirement.
        // The issuer's fixed deadline cannot be extended by background recovery retries.
        while (DateTimeOffset.UtcNow <= retirement)
        {
            var current = await RotationAsync(pair, netRatelIssuer, offered.RotationId!, ct);
            Assert.Equal(retirement, ServiceLinkCanonicalJson.ParseWholeSecondUtcTimestamp(current.PredecessorRetireAt!));
            await AssertStableIdentityAsync(pair, baseline, ct);
            await AssertCurrentSenderTrafficAsync(pair, incidentKey, ct);
            // Leave admission headroom for the real workers' token requests under the unchanged 20/minute limit.
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
        }
        await RestartBothAndObserveRecoveryAsync(pair, switchFault, ct);
        Assert.Equal(retirement, ServiceLinkCanonicalJson.ParseWholeSecondUtcTimestamp(
            (await RotationAsync(pair, netRatelIssuer, offered.RotationId!, ct)).PredecessorRetireAt!));
        switchFault.Release();
        await WaitForCompletedAsync(pair, offered.RotationId!, ct);
        Assert.Contains(await BusinessReadAsync(pair, netRatelIssuer, cachedPredecessor, ct),
            new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden });
        using (var retiredIssuance = await TokenResponseAsync(netRatelIssuer ? pair.NetRatel.Anonymous : pair.RatelDesk.Anonymous,
                   oldClient.ClientId, oldClient.Secret, netRatelIssuer ? "netratel.orchestration.read" : "rateldesk.orchestration.callback", ct))
            Assert.False(retiredIssuance.IsSuccessStatusCode, "The retired predecessor still issued a business token.");
        var successorToken = await AcquireTokenAsync(netRatelIssuer ? pair.NetRatel.Anonymous : pair.RatelDesk.Anonymous,
            candidate.ClientId, candidate.ClientSecret, netRatelIssuer ? "netratel.orchestration.read" : "rateldesk.orchestration.callback", ct);
        Assert.Equal(HttpStatusCode.OK, await BusinessReadAsync(pair, netRatelIssuer, successorToken, ct));
        await AssertCurrentSenderTrafficAsync(pair, incidentKey, ct);
        await AssertIncidentReplayAsync(pair, incidentKey, first: false, ct);
        await AssertStableIdentityAsync(pair, baseline, ct);
        Assert.Equal(1, await pair.RatelDesk.CountAsync("Incidents"));
        Assert.Equal(1, await pair.RatelDesk.CountAsync("IncidentCreateReceipts"));
        Assert.Equal(1, await pair.RatelDesk.CountAsync("IncidentReceiverSources"));
        Assert.Equal(4, issuerProxy.LostResponses + callerProxy.LostResponses);
        Assert.Equal(1, offerFault.PostRestartRecoveriesProven);
        Assert.Equal(1, verifyFault.PostRestartRecoveriesProven);
        Assert.Equal(1, activationFault.PostRestartRecoveriesProven);
        Assert.Equal(2, switchFault.PostRestartRecoveriesProven);
        WriteEvidence(pair, netRatelIssuer, automatic, startedAt, historicalAge, baseline, offered, probe, activation, switchRequest,
            receiptId, decisionId, retirement, incidentKey, new
            {
                offer = offerFault.PostRestartRecoveriesProven, verification = verifyFault.PostRestartRecoveriesProven,
                activation = activationFault.PostRestartRecoveriesProven, switchedAndRetirement = switchFault.PostRestartRecoveriesProven
            });
    }

    private static async Task<ServiceLinkAdminStatus> ReadPostActivationBaselineAsync(ServiceLinkPair pair, CancellationToken ct)
    {
        // Human and peer traffic share this fixture proxy's real 20-per-minute IP
        // partition. Pace only this first post-activation status read; all later
        // requests and rotation phase deadlines retain their existing behavior.
        for (var admission = 0; admission < 2; admission++)
        {
            using var response = await pair.NetRatel.Administrator.GetAsync(
                $"/api/v1/admin/service-links/attempts/{pair.Start.AttemptId}", ct);
            if (response.StatusCode == HttpStatusCode.TooManyRequests && admission == 0)
            {
                var retry = response.Headers.RetryAfter;
                Assert.True(retry is not null || !response.Headers.Contains("Retry-After"),
                    "The post-activation status returned a malformed retry window.");
                var wait = retry?.Delta ?? (retry?.Date is { } at ? at - DateTimeOffset.UtcNow : TimeSpan.FromMinutes(1));
                Assert.True(wait > TimeSpan.Zero && wait <= TimeSpan.FromMinutes(1),
                    "The post-activation status returned an unsupported retry window.");
                response.Dispose();
                await Task.Delay(wait, ct);
                continue;
            }
            Assert.True(response.IsSuccessStatusCode,
                $"The actual post-activation administrator status returned HTTP {(int)response.StatusCode}.");
            return (await response.Content.ReadFromJsonAsync<ServiceLinkAdminStatus>(cancellationToken: ct))!;
        }
        throw new InvalidOperationException("The bounded post-activation status retry did not return a response.");
    }

    private static async Task AssertPendingCandidateRestrictionsAsync(ServiceLinkPair pair, bool netRatelIssuer,
        ServiceDirectionalCredential candidate, ServiceLinkLifecycleRequest offer, CancellationToken ct)
    {
        var client = netRatelIssuer ? pair.NetRatel.Anonymous : pair.RatelDesk.Anonymous;
        using (var business = await TokenResponseAsync(client, candidate.ClientId, candidate.ClientSecret,
                   netRatelIssuer ? "netratel.orchestration.read" : "rateldesk.orchestration.callback", ct))
            Assert.False(business.IsSuccessStatusCode, "An unactivated candidate acquired business authority.");
        var control = await AcquireTokenAsync(client, candidate.ClientId, candidate.ClientSecret, ServiceLinkContract.ControlScope, ct);
        var unrelated = offer with
        {
            OperationId = Guid.NewGuid().ToString("N"), RotationId = Guid.NewGuid().ToString("N"), RotationPhase = "request",
            ExpectedCurrentCredentialRevision = candidate.CredentialRevision, SuccessorCredentialRevision = null,
            CredentialForCaller = null, OfferExpiresAt = null,
            RequestedByInstanceId = netRatelIssuer ? pair.RatelDesk.InstanceId.ToString("D") : pair.NetRatel.InstanceId.ToString("D")
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, ServiceLinkContract.EndpointPath + $"/links/{offer.LinkId}/rotate");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", control);
        request.Content = JsonContent.Create(ServiceLinkLifecycleProjection.Build("rotate", unrelated));
        using var response = await client.SendAsync(request, ct);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    private static async Task AssertCurrentSenderTrafficAsync(ServiceLinkPair pair, string incidentKey, CancellationToken ct)
    {
        // Resolve the actual current production sender, including its token cache identity.
        // Explicit candidate credentials are never substituted for the selected sender.
        await using var scope = pair.NetRatel.Services.CreateAsyncScope();
        var profiles = scope.ServiceProvider.GetRequiredService<ServiceLinkProfileService>();
        var profile = await profiles.ResolveAsync(int.Parse(pair.NetRatel.TenantId, System.Globalization.CultureInfo.InvariantCulture),
            pair.Review.GrantSummary.LinkId, "rateldesk.incident-receipts.read", ct);
        var token = await profiles.GetAccessTokenAsync(profile, "rateldesk.incident-receipts.read", ct);
        using var receipt = new HttpRequestMessage(HttpMethod.Get, "/api/v1/integrations/netratel/incident-receipts/" + incidentKey);
        receipt.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        receipt.Headers.Add("X-NetRatel-Source-Instance", profile.SourceInstanceId);
        using var response = await pair.RatelDesk.Anonymous.SendAsync(receipt, ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var catalog = await pair.RatelDesk.Administrator.GetAsync("/api/v1/admin/orchestration/catalog/jobs", ct);
        Assert.Equal(HttpStatusCode.OK, catalog.StatusCode);
        var jobs = await catalog.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
        Assert.Equal(JsonValueKind.Array, jobs.ValueKind);
        Assert.Single(jobs.EnumerateArray());
    }

    private static async Task AssertIncidentReplayAsync(ServiceLinkPair pair, string key, bool first, CancellationToken ct)
    {
        await using var scope = pair.NetRatel.Services.CreateAsyncScope();
        var profiles = scope.ServiceProvider.GetRequiredService<ServiceLinkProfileService>();
        var profile = await profiles.ResolveAsync(int.Parse(pair.NetRatel.TenantId, System.Globalization.CultureInfo.InvariantCulture),
            pair.Review.GrantSummary.LinkId, "rateldesk.incidents.create", ct);
        var token = await profiles.GetAccessTokenAsync(profile, "rateldesk.incidents.create", ct);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/incidents/");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("X-NetRatel-Source-Instance", profile.SourceInstanceId);
        request.Headers.Add("Idempotency-Key", key);
        request.Content = JsonContent.Create(new { title = "Synthetic real rotation receipt", description = "Stable semantic action through reciprocal credential rotation",
            priority = 0, customerId = pair.RatelDesk.CustomerId, organizationId = pair.RatelDesk.OrganizationId });
        using var response = await pair.RatelDesk.Anonymous.SendAsync(request, ct);
        Assert.Equal(first ? HttpStatusCode.Created : HttpStatusCode.OK, response.StatusCode);
    }

    private static Task<HttpStatusCode> BusinessReadAsync(ServiceLinkPair pair, bool netRatelIssuer, string token, CancellationToken ct) =>
        ServiceLinkPair.GetWithTokenAsync(netRatelIssuer ? pair.NetRatel.Anonymous : pair.RatelDesk.Anonymous,
            netRatelIssuer ? "/internal/health" : "/api/v1/orchestration/provider/m2m/ping", token, ct);

    private static Task<HttpResponseMessage> TokenResponseAsync(HttpClient client, string clientId, string secret, string scope, CancellationToken ct) =>
        client.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["grant_type"] = "client_credentials", ["client_id"] = clientId, ["client_secret"] = secret, ["scope"] = scope }), ct);

    private static async Task<string> AcquireTokenAsync(HttpClient client, string clientId, string secret, string scope, CancellationToken ct)
    {
        using var response = await TokenResponseAsync(client, clientId, secret, scope, ct);
        Assert.True(response.IsSuccessStatusCode, $"The actual scoped rotation credential returned HTTP {(int)response.StatusCode}.");
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
        return body.GetProperty("access_token").GetString()!;
    }

    private static async Task<ServiceLinkRotationSummary> RotationAsync(ServiceLinkPair pair, bool netRatelProduct, string id, CancellationToken ct) =>
        Assert.Single((await pair.StatusAsync(netRatelProduct ? pair.NetRatel.Administrator : pair.RatelDesk.Administrator, ct)).Rotations,
            r => r.RotationId == id);

    private static async Task RestartBothAsync(ServiceLinkPair pair)
    { await pair.NetRatel.RestartAsync(); await pair.RatelDesk.RestartAsync(); }

    private static async Task RestartBothAndObserveRecoveryAsync(ServiceLinkPair pair, ServiceLinkRotationResponseFault fault, CancellationToken ct)
    {
        await RestartBothAsync(pair);
        // Arm only AFTER both previous application processes have stopped and their
        // genuine replacements have started. Earlier duplicates cannot satisfy this.
        await fault.ObserveRecoveryAfterRestart().WaitAsync(TimeSpan.FromSeconds(30), ct);
    }

    private static async Task WaitForCompletedAsync(ServiceLinkPair pair, string rotationId, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var nr = await RotationAsync(pair, true, rotationId, ct); var rd = await RotationAsync(pair, false, rotationId, ct);
            if (nr.RotationState == "completed" && rd.RotationState == "completed") return;
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
        }
        var finalNetRatel = await RotationAsync(pair, true, rotationId, ct);
        var finalRatelDesk = await RotationAsync(pair, false, rotationId, ct);
        if (finalNetRatel.RotationState == "completed" && finalRatelDesk.RotationState == "completed") return;
        Assert.Fail("The actual background workers did not complete the same rotation after its fixed real retirement deadline.");
    }

    private static async Task AssertStableIdentityAsync(ServiceLinkPair pair, ServiceLinkAdminStatus baseline, CancellationToken ct)
    {
        foreach (var administrator in new[] { pair.NetRatel.Administrator, pair.RatelDesk.Administrator })
        {
            var current = await pair.StatusAsync(administrator, ct);
            Assert.Equal(baseline.LinkId, current.LinkId); Assert.Equal(baseline.LinkRevision, current.LinkRevision);
            Assert.Equal(baseline.GrantHash, current.GrantHash); Assert.Equal(baseline.CommitId, current.CommitId);
            Assert.Equal(ServiceLinkCanonicalJson.HashObject(baseline.GrantSummary!), ServiceLinkCanonicalJson.HashObject(current.GrantSummary!));
            Assert.True(current.LocalInboundActive && current.LocalBusinessSenderEnabled);
        }
    }

    private static async Task<ServiceLinkHistoricalAgePrecondition> ApplyHistoricalNetRatelPredecessorAgeAsync(
        ServiceLinkPair pair, string link, string direction, CancellationToken ct)
    {
        await using var scope = pair.NetRatel.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var principal = await db.Set<ServicePrincipalRegistration>().FromSqlInterpolated(
            $"SELECT * FROM \"ServicePrincipalRegistrations\" WHERE \"LinkId\" = {link} AND \"DirectionId\" = {direction} FOR UPDATE")
            .SingleAsync(ct);
        Assert.Equal("active", principal.Status); Assert.Equal(1, principal.CurrentCredentialRevision);
        var attempt = await db.Set<ServiceLinkAttempt>().AsNoTracking().SingleAsync(a => a.LinkId == link && a.InboundPrincipalId == principal.Id, ct);
        Assert.Equal("commit", attempt.Decision); Assert.Equal("active", attempt.LifecycleState);
        Assert.True(attempt.LocalInboundActive && attempt.LocalBusinessSenderEnabled);
        Assert.False(await db.Set<ServiceLinkRotation>().AnyAsync(r => r.LinkId == link, ct));
        var predecessor = await db.Set<ServicePrincipalSecret>().FromSqlInterpolated(
            $"SELECT * FROM \"ServicePrincipalSecrets\" WHERE \"ServicePrincipalId\" = {principal.Id} AND \"CredentialRevision\" = {principal.CurrentCredentialRevision} FOR UPDATE")
            .SingleAsync(ct);
        Assert.Equal("active", predecessor.Status); Assert.Null(predecessor.RetireAtUtc);
        Assert.True(predecessor.ExpiresAtUtc > DateTimeOffset.UtcNow);
        var original = predecessor.CreatedAtUtc;
        var materialBefore = SecretMaterialHash(predecessor);
        var principalBefore = ServiceLinkCanonicalJson.HashObject(principal);
        var rows = await db.Set<ServicePrincipalSecret>().Where(s => s.ServicePrincipalId == principal.Id &&
                s.CredentialRevision == principal.CurrentCredentialRevision && s.CreatedAtUtc == original && s.Status == "active")
            .ExecuteUpdateAsync(setters => setters.SetProperty(s => s.CreatedAtUtc, original.AddDays(-2)), ct);
        Assert.Equal(1, rows);
        await db.Entry(predecessor).ReloadAsync(ct);
        await db.Entry(principal).ReloadAsync(ct);
        Assert.True(materialBefore == SecretMaterialHash(predecessor), "Historical aging changed another predecessor secret column.");
        Assert.True(principalBefore == ServiceLinkCanonicalJson.HashObject(principal), "Historical aging changed current principal authority.");
        Assert.Equal(original.AddDays(-2), predecessor.CreatedAtUtc);
        await transaction.CommitAsync(ct);
        return new(rows, principal.Id, predecessor.CredentialRevision, original, predecessor.CreatedAtUtc, predecessor.ExpiresAtUtc, true, true);
    }

    private static string SecretMaterialHash(ServicePrincipalSecret secret) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        JsonSerializer.Serialize(new { secret.ServicePrincipalId, secret.CredentialRevision, secret.SecretHash, secret.Salt,
            secret.Status, secret.ExpiresAtUtc, secret.RetireAtUtc }))));

    private static void WriteEvidence(ServiceLinkPair pair, bool netRatelIssuer, bool automatic, DateTimeOffset startedAt,
        ServiceLinkHistoricalAgePrecondition? historicalAge, ServiceLinkAdminStatus baseline, ServiceLinkLifecycleRequest offer,
        ServiceLinkLifecycleRequest probe, ServiceLinkLifecycleRequest activation, ServiceLinkLifecycleRequest switched,
        string receiptId, string decisionId, DateTimeOffset retirement, string incidentKey, object postRestartMatchingRecoveryCounts)
    {
        var directory = Environment.GetEnvironmentVariable("NETRATEL_SERVICE_LINK_EVIDENCE_DIRECTORY") ?? Path.Combine("TestResults", "service-link-proof");
        Directory.CreateDirectory(directory);
        var proof = new
        {
            phase = "actual-reciprocal-rotation-completed", netRatelIssuer, initiation = automatic ? "actual-background-policy" : "human-manual",
            integrationBuildSourceSha = typeof(ServiceLinkRotationHttpPostgresTests).Assembly
                .GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
                .Cast<System.Reflection.AssemblyMetadataAttribute>().Single(a => a.Key == "ServiceLinkProofSourceSha").Value,
            startedAtUtc = startedAt, completedAtUtc = DateTimeOffset.UtcNow,
            timeProvider = "System", historicalAgePrecondition = historicalAge, historicalAgeIsSyntheticInputNotTwentyFourHourSoak = automatic,
            companion = new { ServiceLinkPublishedRatelDeskPeer.PublishedSource, ServiceLinkPublishedRatelDeskPeer.PublishedVersion,
                ServiceLinkPublishedRatelDeskPeer.ApiImage, ServiceLinkPublishedRatelDeskPeer.WebImage },
            baseline.LinkId, baseline.LinkRevision, baseline.GrantHash, baseline.CommitId,
            sourceInstanceId = pair.NetRatel.SourceInstanceId, sourceNamespaceId = baseline.GrantSummary!.Grants.Single(g => g.TargetProduct == "rateldesk").SourceNamespaceId,
            offer.RotationId, offer.DirectionId, offer.ExpectedCurrentCredentialRevision, offer.SuccessorCredentialRevision,
            offerOperationId = offer.OperationId, verificationOperationId = probe.OperationId, activationOperationId = activation.OperationId,
            switchOperationId = switched.OperationId, successorVerificationReceiptId = receiptId, activateDecisionId = decisionId,
            callerSwitchRevision = switched.CallerSwitchRevision, predecessorRetireAtUtc = retirement, configuredOverlapSeconds = 60,
            committedResponsesLost = 4, bothProductsRestartedAfterEachCommittedPhaseAndAtRetirement = true,
            postRestartMatchingRecoveryCounts,
            currentProductionSenderTrafficBothDirections = true, unexpiredCachedPredecessorDeniedAfterRetirement = true,
            successorBusinessTokenAndCurrentSendersWork = true, incidentKey, incidents = 1, incidentReceipts = 1,
            logicalClientAndSemanticGrantPreserved = true, pendingCandidateBusinessAndUnrelatedControlDenied = true
        };
        File.WriteAllText(Path.Combine(directory, "rotation-" + Guid.NewGuid().ToString("N") + ".json"), JsonSerializer.Serialize(proof));
    }
}
