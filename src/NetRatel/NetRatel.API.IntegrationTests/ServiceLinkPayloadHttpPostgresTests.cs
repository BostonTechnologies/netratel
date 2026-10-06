using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.ServiceIdentity;
using NetRatel.Infrastructure.ServiceLinks;
using NetRatel.Shared.ServiceLinks;
using Xunit;

namespace NetRatel.API.IntegrationTests.ServiceLinks;

[Collection(ServiceLinkRealPeerCollection.Name)]
public sealed class ServiceLinkPayloadHttpPostgresTests
{
    [Fact]
    public async Task Responder_consent_rejects_a_different_reciprocal_tenant_pair_and_null_grants_before_creating_authority()
    {
        await using var pair = await ServiceLinkPair.CreateAsync(false);
        await pair.PrepareAsync();
        var before = await PrivateStateDigestAsync(pair);
        var state = QueryHelpers.ParseQuery(new Uri(pair.Start.NavigationUrl).Query)["browser_state"].ToString();
        var otherInitiatorTenant = Guid.NewGuid().ToString("D");
        var changedGrants = pair.Descriptor.RequestedGrants.Select(g => g.DirectionId == ServiceLinkContract.InitiatorToResponder
            ? g with { CallerTenantId = otherInitiatorTenant }
            : g with { TargetTenantId = otherInitiatorTenant, ResourceConstraints = g.ResourceConstraints with { OrganizationId = otherInitiatorTenant } }).ToArray();
        // Both directions are internally reciprocal, but differ from the actual
        // retained initiator tenant. The selected NR inbound resources are real.
        var request = new ServiceLinkRemoteApproveRequest(pair.Start.AttemptId, pair.NetRatel.TenantId, changedGrants, state)
        { SessionBinding = pair.SessionBinding };
        using (var rejected = await SendJsonAsync(pair.NetRatel.Administrator, "/api/v1/admin/service-links/remote-approve", ToNode(request)))
        {
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
            using var problem = await rejected.Content.ReadFromJsonAsync<JsonDocument>();
            Assert.True(problem!.RootElement.GetProperty("code").GetString() == "tenant-pair-mismatch",
                "The retained consent tenant guard did not reject the changed reciprocal pair.");
        }
        await AssertUnchangedAsync(pair, before);
        var nullGrants = ToNode(request);
        nullGrants["grants"] = null;
        using (var rejected = await SendJsonAsync(pair.NetRatel.Administrator, "/api/v1/admin/service-links/remote-approve", nullGrants))
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        await AssertUnchangedAsync(pair, before);
        var retained = await ReadAttemptAsync(pair);
        Assert.Null(retained.InboundPrincipalId);
        Assert.Null(retained.LinkId);
        Assert.Null(retained.GrantSummaryJson);
        Assert.Null(retained.ProtectedOutboundCredential);
        Assert.False(retained.LocalInboundActive);
        Assert.False(retained.LocalBusinessSenderEnabled);
    }

    [Fact]
    public async Task Accepted_exchange_retries_ignore_credential_scope_order_after_restart_and_reject_changed_material_without_writes()
    {
        // Actual published RatelDesk and actual NetRatel/PostgreSQL. The fixture
        // must be repinned to the verified published companion before this runs;
        // the known older Web metadata 404 must never be bypassed or skipped.
        await using var pair = await ServiceLinkPair.CreateAsync(false);
        await pair.PrepareAsync();
        await pair.ReviewAsync();
        await pair.ApproveAsync();
        var path = ServiceLinkContract.EndpointPath + $"/attempts/{pair.Start.AttemptId}/exchange";
        var observation = Observe(pair, path);
        await pair.ResumeAsync(pair.Initiator);
        using var accepted = await observation.WaitAsync(TimeSpan.FromSeconds(25));
        var original = accepted.ReadRequest<ServiceLinkExchangeRequest>();
        Assert.True(original.CredentialForResponder.Scopes.Length > 1, "The real exchange needs multiple accepted scopes to exercise ordering.");
        var initial = await ReadAttemptAsync(pair);
        Assert.NotNull(initial.ExchangeFingerprint);
        Assert.NotNull(initial.ProtectedExchangeResponse);
        Assert.NotNull(initial.ProtectedOutboundCredential);
        Assert.False(initial.LocalInboundActive);
        Assert.False(initial.LocalBusinessSenderEnabled);

        await pair.NetRatel.RestartAsync();
        var before = await PrivateStateDigestAsync(pair);
        var reordered = original with
        { CredentialForResponder = original.CredentialForResponder with { Scopes = original.CredentialForResponder.Scopes.Reverse().ToArray() } };
        Assert.True(!original.CredentialForResponder.Scopes.SequenceEqual(reordered.CredentialForResponder.Scopes),
            "The scope-order retry did not change the real wire array order.");
        using (var retry = await SendJsonAsync(pair.NetRatel.Anonymous, path, ToNode(reordered)))
        {
            Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
            Assert.True(accepted.ResponseMatches(await retry.Content.ReadAsStringAsync()), "The replay changed the finite accepted exchange response.");
        }
        await AssertUnchangedAsync(pair, before);
        using (var changedSecret = await SendJsonAsync(pair.NetRatel.Anonymous, path,
            ToNode(reordered with { CredentialForResponder = reordered.CredentialForResponder with { ClientSecret = ChangedSecret(reordered.CredentialForResponder.ClientSecret) } })))
            Assert.Equal(HttpStatusCode.Conflict, changedSecret.StatusCode);
        using (var changedConsent = await SendJsonAsync(pair.NetRatel.Anonymous, path,
            ToNode(reordered with { InitiatorConsentId = Guid.NewGuid().ToString("N") })))
            Assert.Equal(HttpStatusCode.Conflict, changedConsent.StatusCode);
        await AssertUnchangedAsync(pair, before);

        var quotedRevision = ToNode(original);
        quotedRevision["credential_for_responder"]!["credential_revision"] = original.CredentialForResponder.CredentialRevision.ToString(System.Globalization.CultureInfo.InvariantCulture);
        using (var malformed = await SendJsonAsync(pair.NetRatel.Anonymous, path, quotedRevision))
            Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        var nullScope = ToNode(original);
        nullScope["credential_for_responder"]!["scopes"]!.AsArray()[0] = null;
        using (var malformed = await SendJsonAsync(pair.NetRatel.Anonymous, path, nullScope))
            Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        var nullProof = ToNode(original);
        nullProof["pairing_code"] = null;
        using (var malformed = await SendJsonAsync(pair.NetRatel.Anonymous, path, nullProof))
            Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        await AssertUnchangedAsync(pair, before);
        var retained = await ReadAttemptAsync(pair);
        Assert.True(initial.InboundPrincipalId == retained.InboundPrincipalId, "A replay changed the retained logical inbound principal.");
        Assert.True(string.Equals(initial.ExchangeFingerprint, retained.ExchangeFingerprint, StringComparison.Ordinal), "A replay changed the accepted exchange fingerprint.");
        Assert.True(string.Equals(initial.ExchangeResponseHash, retained.ExchangeResponseHash, StringComparison.Ordinal), "A replay changed the accepted exchange response binding.");
        Assert.False(retained.LocalInboundActive);
        Assert.False(retained.LocalBusinessSenderEnabled);
    }

    [Fact]
    public async Task Accepted_rotation_offer_retries_ignore_scope_order_after_restart_and_reject_numeric_strings_before_new_journals_or_receipts()
    {
        // This issuer is the actual published peer, whose offer expiry uses real
        // time. Its receiving fixture must share that advancing clock.
        await using var pair = await ServiceLinkPair.CreateAsync(true, useSystemTime: true);
        await pair.ActivateAsync();
        await pair.NetRatel.WaitForInitialSensitiveWindowAsync(TestContext.Current.CancellationToken);
        var link = pair.Review.GrantSummary.LinkId;
        var path = ServiceLinkContract.EndpointPath + $"/links/{link}/rotate";
        var observation = Observe(pair, path);
        using (var started = await pair.RatelDesk.Administrator.PostAsJsonAsync($"/api/v1/admin/service-links/links/{link}/rotate", new ServiceLinkAdminAction()))
            Assert.Equal(HttpStatusCode.OK, started.StatusCode);
        await pair.ResumeAsync(pair.RatelDesk.Administrator);
        using var accepted = await observation.WaitAsync(TimeSpan.FromSeconds(25));
        var original = accepted.ReadRequest<ServiceLinkLifecycleRequest>();
        Assert.Equal("offer", original.RotationPhase);
        var credential = Assert.IsType<ServiceDirectionalCredential>(original.CredentialForCaller);
        Assert.True(credential.Scopes.Length > 1, "The real rotation offer needs multiple accepted scopes to exercise ordering.");
        var operation = await ReadOperationAsync(pair, original.OperationId);
        var rotation = await ReadRotationAsync(pair, original.RotationId!);
        Assert.False(operation.Outbound);
        Assert.True(operation.Completed);
        Assert.Equal("rotate", operation.Kind);
        Assert.Equal("prepared", rotation.RotationState);
        Assert.NotNull(rotation.ProtectedCandidate);
        Assert.False(rotation.IsIssuer);
        Assert.Null(rotation.ActivateDecisionId);
        Assert.Null(rotation.CallerSwitchRevision);

        await pair.NetRatel.RestartAsync();
        var token = await pair.TokenAsync(true, ServiceLinkContract.ControlScope);
        var before = await PrivateStateDigestAsync(pair);
        var reorderedCredential = credential with { Scopes = credential.Scopes.Reverse().ToArray() };
        var reordered = original with { CredentialForCaller = reorderedCredential };
        Assert.True(!credential.Scopes.SequenceEqual(reorderedCredential.Scopes),
            "The rotation retry did not change the real wire array order.");
        using (var retry = await SendJsonAsync(pair.NetRatel.Anonymous, path, RotationNode(reordered), token))
        {
            Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
            Assert.True(accepted.ResponseMatches(await retry.Content.ReadAsStringAsync()), "The replay changed the accepted rotation response.");
        }
        await AssertUnchangedAsync(pair, before);
        using (var changedSecret = await SendJsonAsync(pair.NetRatel.Anonymous, path,
            RotationNode(reordered with { CredentialForCaller = reorderedCredential with { ClientSecret = ChangedSecret(reorderedCredential.ClientSecret) } }), token))
            Assert.Equal(HttpStatusCode.Conflict, changedSecret.StatusCode);
        using (var changedExpiry = await SendJsonAsync(pair.NetRatel.Anonymous, path,
            RotationNode(reordered with { OfferExpiresAt = ServiceLinkValidation.Timestamp(ServiceLinkCanonicalJson.ParseWholeSecondUtcTimestamp(original.OfferExpiresAt!).ToUnixTimeSeconds() + 1) }), token))
            Assert.Equal(HttpStatusCode.Conflict, changedExpiry.StatusCode);
        await AssertUnchangedAsync(pair, before);

        foreach (var field in new[] { "link_revision", "expected_current_credential_revision", "successor_credential_revision", "credential_for_caller.credential_revision" })
        {
            var malformed = RotationNode(original with { OperationId = Guid.NewGuid().ToString("N") });
            QuoteNumber(malformed, field);
            using var response = await SendJsonAsync(pair.NetRatel.Anonymous, path, malformed, token);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            await AssertUnchangedAsync(pair, before);
        }
        var nullOperation = RotationNode(original with { OperationId = Guid.NewGuid().ToString("N") });
        nullOperation["operation_id"] = null;
        using (var malformed = await SendJsonAsync(pair.NetRatel.Anonymous, path, nullOperation, token))
            Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        await AssertUnchangedAsync(pair, before);
        var retainedOperation = await ReadOperationAsync(pair, original.OperationId);
        Assert.True(string.Equals(operation.RequestFingerprint, retainedOperation.RequestFingerprint, StringComparison.Ordinal), "A replay changed the retained operation fingerprint.");
        Assert.True(string.Equals(operation.ResponseJson, retainedOperation.ResponseJson, StringComparison.Ordinal), "The retained journal response changed.");
        var retainedRotation = await ReadRotationAsync(pair, original.RotationId!);
        Assert.True(string.Equals(rotation.ProtectedCandidate, retainedRotation.ProtectedCandidate, StringComparison.Ordinal), "A retry replaced the protected accepted candidate.");
        Assert.Null(retainedRotation.ActivateDecisionId);
        Assert.Null(retainedRotation.CallerSwitchRevision);
    }

    private static Task<ServiceLinkObservedOperation> Observe(ServiceLinkPair pair, string path) =>
        pair.NetRatel.Proxy.ObserveNextSuccessfulOperation(path,
            pair.NetRatel.Services.GetRequiredService<IOptions<ServiceLinkOptions>>().Value.MaximumPayloadBytes);

    private static JsonObject ToNode<T>(T value) => JsonSerializer.SerializeToNode(value, ServiceLinkCanonicalJson.Json)!.AsObject();
    private static JsonObject RotationNode(ServiceLinkLifecycleRequest request) =>
        ToNode(ServiceLinkLifecycleProjection.Build("rotate", request, normalizeCredentialScopes: false));
    private static string ChangedSecret(string secret) => secret + "changed";
    private static void QuoteNumber(JsonObject payload, string path)
    {
        var parts = path.Split('.');
        var parent = payload;
        foreach (var part in parts[..^1]) parent = parent[part]!.AsObject();
        parent[parts[^1]] = parent[parts[^1]]!.GetValue<long>().ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private static Task<HttpResponseMessage> SendJsonAsync(HttpClient client, string path, JsonObject payload, string? token = null)
    {
        // Keep deliberately reordered and malformed JSON unchanged; the normal
        // typed test helper correctly normalizes outbound production payloads.
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        { Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json") };
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return SendAndDisposeAsync(client, request);
    }
    private static async Task<HttpResponseMessage> SendAndDisposeAsync(HttpClient client, HttpRequestMessage request)
    {
        using (request) return await client.SendAsync(request);
    }

    private static async Task<ServiceLinkAttempt> ReadAttemptAsync(ServiceLinkPair pair)
    {
        await using var scope = pair.NetRatel.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>().Set<ServiceLinkAttempt>()
            .AsNoTracking().SingleAsync(a => a.AttemptId == pair.Start.AttemptId);
    }
    private static async Task<ServiceLinkOperation> ReadOperationAsync(ServiceLinkPair pair, string operationId)
    {
        await using var scope = pair.NetRatel.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>().Set<ServiceLinkOperation>()
            .AsNoTracking().SingleAsync(o => o.LinkId == pair.Review.GrantSummary.LinkId && o.OperationId == operationId);
    }
    private static async Task<ServiceLinkRotation> ReadRotationAsync(ServiceLinkPair pair, string rotationId)
    {
        await using var scope = pair.NetRatel.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>().Set<ServiceLinkRotation>()
            .AsNoTracking().SingleAsync(r => r.LinkId == pair.Review.GrantSummary.LinkId && r.RotationId == rotationId);
    }

    private static async Task AssertUnchangedAsync(ServiceLinkPair pair, string before) =>
        Assert.True(string.Equals(before, await PrivateStateDigestAsync(pair), StringComparison.Ordinal),
            "The retry or invalid body changed durable authority, protected material, journal, rotation or verification records.");

    private static async Task<string> PrivateStateDigestAsync(ServiceLinkPair pair)
    {
        await using var scope = pair.NetRatel.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", db.Database.ProviderName);
        var attempt = await db.Set<ServiceLinkAttempt>().AsNoTracking().SingleAsync(a => a.AttemptId == pair.Start.AttemptId);
        var operations = await db.Set<ServiceLinkOperation>().AsNoTracking().OrderBy(o => o.OperationId).ToArrayAsync();
        var receipts = await db.Set<ServiceLinkVerificationReceipt>().AsNoTracking().OrderBy(r => r.VerificationReceiptId).ToArrayAsync();
        var rotations = await db.Set<ServiceLinkRotation>().AsNoTracking().OrderBy(r => r.RotationId).ToArrayAsync();
        var principals = await db.Set<ServicePrincipalRegistration>().AsNoTracking().OrderBy(p => p.Id).ToArrayAsync();
        var secrets = await db.Set<ServicePrincipalSecret>().AsNoTracking().OrderBy(p => p.ServicePrincipalId).ThenBy(p => p.CredentialRevision).ToArrayAsync();
        // This digest and all protected state remain private memory. A failing
        // assertion prints only the value-free message, never any actual body.
        return Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        { attempt, operations, receipts, rotations, principals, secrets })));
    }
}
