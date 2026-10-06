using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.ServiceIdentity;
using NetRatel.Infrastructure.ServiceLinks;
using NetRatel.Shared.ServiceLinks;
using Xunit;

namespace NetRatel.API.IntegrationTests.ServiceLinks;

[Collection(ServiceLinkRealPeerCollection.Name)]
public sealed class ServiceLinkTerminalConvergencePostgresTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Opposing_precommit_cancellations_preserve_the_coordinator_decision_and_converge_after_restart(bool netRatelInitiates)
    {
        // Both-role acceptance requires the corrected immutable published RatelDesk
        // companion. Its prior prepared-responder request/decision collision must
        // remain a real failure until that companion is selected by the fixture.
        await using var pair = await ServiceLinkPair.CreateAsync(netRatelInitiates);
        await PrepareBothAsync(pair);
        var tokenNr = await pair.TokenAsync(true, ServiceLinkContract.ControlScope);
        var tokenRd = await pair.TokenAsync(false, ServiceLinkContract.ControlScope);
        using var pauseNr = pair.NetRatel.Proxy.PauseTerminalDelivery();
        using var pauseRd = pair.RatelDesk.Proxy.PauseTerminalDelivery();
        await CancelAsync(pair, pair.Initiator);
        await CancelAsync(pair, pair.Responder);
        var coordinator = await ControlStatusAsync(pair, netRatelInitiates, netRatelInitiates ? tokenNr : tokenRd);
        var decisionId = coordinator.GetProperty("abort_id").GetString();
        Assert.NotNull(decisionId);
        Assert.Equal("abort", coordinator.GetProperty("decision").GetString());
        var original = await AttemptAsync(pair);
        Assert.False(original.LocalInboundActive || original.LocalBusinessSenderEnabled);
        ServiceLinkOperation? participant = null;
        if (!netRatelInitiates)
        {
            Assert.Null(original.AbortId);
            participant = Assert.Single(await OperationsAsync(pair), x => x.Outbound && x.Kind == "abort-request");
            var request = await ParticipantRequestAsync(pair, original, participant);
            Assert.NotEqual(decisionId, request.AbortId);
            Assert.Equal(ServiceLinkLifecycleProjection.Hash("abort", request), participant.RequestFingerprint);
        }
        else Assert.Equal(decisionId, original.AbortId);
        AssertDisabled(await pair.StatusAsync(pair.NetRatel.Administrator));
        AssertDisabled(await pair.StatusAsync(pair.RatelDesk.Administrator));

        await pair.NetRatel.RestartAsync(); await pair.RatelDesk.RestartAsync();
        pauseNr.Dispose(); pauseRd.Dispose();
        for (var step = 0; step < 6; step++)
        {
            await pair.ResumeAsync(pair.Initiator); await pair.ResumeAsync(pair.Responder);
            if ((await pair.StatusAsync(pair.NetRatel.Administrator)).LifecycleState == "expired" &&
                (await pair.StatusAsync(pair.RatelDesk.Administrator)).LifecycleState == "expired") break;
        }
        var nr = await pair.StatusAsync(pair.NetRatel.Administrator);
        var rd = await pair.StatusAsync(pair.RatelDesk.Administrator);
        Assert.Equal("expired", nr.LifecycleState); Assert.Equal("expired", rd.LifecycleState);
        Assert.Equal("abort", nr.Decision); Assert.Equal("abort", rd.Decision);
        AssertDisabled(nr); AssertDisabled(rd);
        Assert.Equal(original.GrantHash, nr.GrantHash); Assert.Equal(original.LinkRevision, nr.LinkRevision);
        Assert.Equal(decisionId, (await ControlStatusAsync(pair, true, tokenNr)).GetProperty("abort_id").GetString());
        Assert.Equal(decisionId, (await ControlStatusAsync(pair, false, tokenRd)).GetProperty("abort_id").GetString());
        if (participant is not null)
        {
            var after = (await OperationsAsync(pair)).Single(x => x.OperationId == participant.OperationId);
            Assert.Equal(participant.RequestFingerprint, after.RequestFingerprint);
            Assert.Equal(1, (await OperationsAsync(pair)).Count(x => x.Outbound && x.Kind == "abort-request"));
        }
        Assert.Equal(1, await pair.RatelDesk.CountAsync("ServicePrincipalRegistrations"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Opposing_unlinks_disable_cached_business_and_confirm_both_intents_without_rewriting_dispatched_journal(bool netRatelInitiates)
    {
        await using var pair = await ServiceLinkPair.CreateAsync(netRatelInitiates);
        await pair.ActivateAsync();
        await Task.WhenAll(pair.NetRatel.WaitForInitialSensitiveWindowAsync(),
            pair.RatelDesk.WaitForPostActivationSensitiveWindowAsync());
        var business = await pair.TokenAsync(true, "netratel.orchestration.read");
        var businessExpiresAtUtc = new JwtSecurityTokenHandler().ReadJwtToken(business).ValidTo;
        Assert.Equal(HttpStatusCode.OK, await ServiceLinkPair.GetWithTokenAsync(pair.NetRatel.Anonymous, "/internal/health", business));
        using var pauseNr = pair.NetRatel.Proxy.PauseTerminalDelivery();
        using var pauseRd = pair.RatelDesk.Proxy.PauseTerminalDelivery();
        await RevokeAsync(pair, pair.NetRatel.Administrator);
        await RevokeAsync(pair, pair.RatelDesk.Administrator);
        var original = await AttemptAsync(pair);
        Assert.NotNull(original.RevocationId);
        Assert.Equal(1, await pair.RatelDesk.CountAsync("ServiceLinkAttempts",
            "\"RevocationId\" IS NOT NULL AND \"RevocationId\" <> '" + original.RevocationId + "'"));
        AssertDisabled(await pair.StatusAsync(pair.NetRatel.Administrator));
        AssertDisabled(await pair.StatusAsync(pair.RatelDesk.Administrator));
        Assert.True(businessExpiresAtUtc > DateTime.UtcNow, "The cached business token expired before the immediate unlink denial.");
        Assert.Equal(HttpStatusCode.Unauthorized, await ServiceLinkPair.GetWithTokenAsync(pair.NetRatel.Anonymous, "/internal/health", business));
        Assert.True(businessExpiresAtUtc > DateTime.UtcNow, "The cached business token expired during the immediate unlink denial.");

        // Arrival at the remote proxy proves the original encrypted request was
        // committed before either peer receives an unlink. The barrier holds HTTP,
        // never a database transaction or lock needed by the opposing local action.
        var firstDelivery = pair.ResumeAsync(pair.NetRatel.Administrator);
        ServiceLinkOperation dispatched;
        try
        {
            await pair.RatelDesk.Proxy.WaitForPausedTerminalDeliveryAsync();
            dispatched = Assert.Single(await OperationsAsync(pair), x => x.Outbound && x.Kind == "revoke-link");
            Assert.False(dispatched.Completed); Assert.NotNull(dispatched.ProtectedRequestJson);
        }
        finally
        {
            pauseRd.Dispose();
            await firstDelivery.WaitAsync(TimeSpan.FromSeconds(30));
        }
        pauseNr.Dispose();
        await pair.NetRatel.RestartAsync(); await pair.RatelDesk.RestartAsync();
        for (var step = 0; step < 6; step++)
        {
            await pair.ResumeAsync(pair.RatelDesk.Administrator); await pair.ResumeAsync(pair.NetRatel.Administrator);
            if ((await pair.StatusAsync(pair.NetRatel.Administrator)).LifecycleState == "revoked" &&
                (await pair.StatusAsync(pair.RatelDesk.Administrator)).LifecycleState == "revoked") break;
        }
        Assert.Equal("revoked", (await pair.StatusAsync(pair.NetRatel.Administrator)).LifecycleState);
        Assert.Equal("revoked", (await pair.StatusAsync(pair.RatelDesk.Administrator)).LifecycleState);
        var final = await AttemptAsync(pair);
        Assert.Equal(original.RevocationId, final.RevocationId);
        Assert.Equal(original.GrantHash, final.GrantHash); Assert.Equal(original.LinkRevision, final.LinkRevision);
        Assert.True(final.PeerRevocationAcknowledged);
        Assert.False(final.LocalInboundActive || final.LocalBusinessSenderEnabled);
        var journal = (await OperationsAsync(pair)).Single(x => x.OperationId == dispatched.OperationId);
        Assert.Equal(dispatched.RequestFingerprint, journal.RequestFingerprint);
        if (!journal.Completed)
            Assert.True(string.Equals(dispatched.ProtectedRequestJson, journal.ProtectedRequestJson, StringComparison.Ordinal), "The original pending encrypted unlink was rewritten.");
        Assert.True(businessExpiresAtUtc > DateTime.UtcNow, "The cached business token expired before the restarted unlink denial.");
        Assert.Equal(HttpStatusCode.Unauthorized, await ServiceLinkPair.GetWithTokenAsync(pair.NetRatel.Anonymous, "/internal/health", business));
        Assert.True(businessExpiresAtUtc > DateTime.UtcNow, "The cached business token expired during the restarted unlink denial.");
    }

    private static async Task PrepareBothAsync(ServiceLinkPair pair)
    {
        await pair.PrepareAsync(); await pair.ReviewAsync(); await pair.ApproveAsync();
        await pair.ResumeAsync(pair.Initiator); await pair.ResumeAsync(pair.Responder); await pair.ResumeAsync(pair.Initiator);
        foreach (var administrator in new[] { pair.NetRatel.Administrator, pair.RatelDesk.Administrator })
        {
            var status = await pair.StatusAsync(administrator);
            Assert.True(status.LocalOutboundPersisted); Assert.Equal("undecided", status.Decision); AssertDisabled(status);
        }
    }

    private static void AssertDisabled(ServiceLinkAdminStatus status)
    { Assert.False(status.LocalInboundActive); Assert.False(status.LocalBusinessSenderEnabled); }

    private static Task<ServiceLinkAdminStatus> CancelAsync(ServiceLinkPair pair, HttpClient administrator) =>
        ServiceLinkPair.PostAsync<ServiceLinkAdminStatus>(administrator, $"/api/v1/admin/service-links/links/{pair.Review.GrantSummary.LinkId}/cancel", new ServiceLinkAdminAction("opposing-cancel"));

    private static Task<ServiceLinkAdminStatus> RevokeAsync(ServiceLinkPair pair, HttpClient administrator) =>
        ServiceLinkPair.PostAsync<ServiceLinkAdminStatus>(administrator, $"/api/v1/admin/service-links/links/{pair.Review.GrantSummary.LinkId}/revoke", new ServiceLinkAdminAction("opposing-unlink"));

    private static async Task<JsonElement> ControlStatusAsync(ServiceLinkPair pair, bool netRatel, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{ServiceLinkContract.EndpointPath}/links/{pair.Review.GrantSummary.LinkId}/status");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await (netRatel ? pair.NetRatel.Anonymous : pair.RatelDesk.Anonymous).SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var status = await response.Content.ReadFromJsonAsync<JsonDocument>();
        return status!.RootElement.Clone();
    }

    private static async Task<ServiceLinkAttempt> AttemptAsync(ServiceLinkPair pair)
    {
        await using var scope = pair.NetRatel.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>().Set<ServiceLinkAttempt>().AsNoTracking().SingleAsync();
    }

    private static async Task<ServiceLinkOperation[]> OperationsAsync(ServiceLinkPair pair)
    {
        await using var scope = pair.NetRatel.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>().Set<ServiceLinkOperation>().AsNoTracking().ToArrayAsync();
    }

    private static async Task<ServiceLinkLifecycleRequest> ParticipantRequestAsync(ServiceLinkPair pair, ServiceLinkAttempt attempt, ServiceLinkOperation operation)
    {
        await using var scope = pair.NetRatel.Services.CreateAsyncScope();
        var protector = scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>().CreateProtector("NetRatel.ServiceLink.v1", attempt.AttemptId,
            attempt.PeerInstanceId, "operation/" + operation.OperationId, attempt.LocalTenantId + "/" + attempt.LinkId + "/" + attempt.GrantHash + "/" + attempt.LinkRevision);
        return ServiceLinkCanonicalJson.Deserialize<ServiceLinkLifecycleRequest>(protector.Unprotect(operation.ProtectedRequestJson!));
    }
}
