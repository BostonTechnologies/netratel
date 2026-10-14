extern alias WebHost;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NetRatel.API.Models;
using NetRatel.API.Services;
using NetRatel.Infrastructure.Persistence;
using WebHost::NetRatel.Web.Configuration;
using Yarp.ReverseProxy.Forwarder;
using Xunit;

[Collection(ApiIntegrationCollection.Name)]
public sealed class ClientUpdateWebRoutingTests(ApiFactory factory)
{
    [Fact]
    public async Task ClaimAndResultTraverseProductionWebRoutesAndGuardWithAgentBearerIdentity()
    {
        var agentId = Guid.NewGuid();
        var releaseId = Guid.NewGuid();
        int tenantId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            var tenant = new Tenant { Name = "Web update regression " + releaseId, AutoUpdate = true };
            db.Tenants.Add(tenant);
            await db.SaveChangesAsync();
            tenantId = tenant.Id;
            db.Agents.Add(new Agent { Id = agentId, TenantId = tenantId });
            await db.SaveChangesAsync();
            // The shared fixture can already contain publications. Allocate catalog
            // revisions through the authority, including when running this test alone.
            var publisher = scope.ServiceProvider.GetRequiredService<IClientUpdatePublisher>();
            foreach (var version in new[] { "91.2.3", "91.2.4" })
            {
                var release = await publisher.PublishArtifactAsync(new ClientArtifactSummaryDto
                {
                    Rid = "linux-x64", Version = version, FileName = $"linux-x64-{version}.zip",
                    Sha256 = new string('a', 64), Size = 42, UploadedAt = DateTimeOffset.UtcNow
                }, "{}", "Web routing fixture", CancellationToken.None);
                if (version == "91.2.4") releaseId = release.PublicId;
            }
        }

        // Compose the production generated defaults and shipped/operator settings,
        // changing only the destination transport to the authenticated API fixture.
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../"));
        var configuration = new ConfigurationBuilder().AddJsonFile(Path.Combine(root,
            "src/NetRatel/NetRatel.Web/appsettings.json")).Build();
        using var gatewayConfiguration = WebGatewayConfiguration.Create(configuration, WebGatewayOptions.Resolve(configuration));
        using var web = await new HostBuilder().ConfigureWebHost(builder => builder.UseTestServer()
            .ConfigureServices(services =>
            {
                services.AddRouting();
                services.AddAuthentication("browser").AddCookie("browser");
                services.AddAuthorization();
                services.AddReverseProxy().LoadFromConfig(gatewayConfiguration.GetSection("ReverseProxy"));
                services.AddSingleton<IForwarderHttpClientFactory>(new ApiTransport(factory.Server));
            })
            .Configure(app =>
            {
                app.UseRouting();
                app.UseAuthentication();
                app.UseAuthorization();
                app.UseApiRouteGuard();
                app.UseEndpoints(endpoints => endpoints.MapReverseProxy().AllowAnonymous());
            })).StartAsync();
        using var client = web.GetTestClient();
        var claimPath = $"/api/v2/agent-updates/releases/{releaseId}/claim";
        var nonce = new string('a', 64);
        var claimBody = new { runtimeId = "linux-x64", currentVersion = "91.2.3", channel = "stable", admissionNonce = nonce };

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.CreateAgentBearerToken(tenantId, agentId));
        using var response = await client.PostAsJsonAsync(claimPath, claimBody);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var claim = await response.Content.ReadFromJsonAsync<ClientUpdateClaimResult>();
        Assert.NotNull(claim);
        Assert.Equal(releaseId, claim.ReleaseId);
        Assert.Equal(nonce, claim.AdmissionNonce);
        Assert.Equal("91.2.4", claim.TargetVersion);

        // Losing a successful claim response can safely be recovered with the same nonce.
        using var repeated = await client.PostAsJsonAsync(claimPath, claimBody);
        Assert.Equal(claim, await repeated.Content.ReadFromJsonAsync<ClientUpdateClaimResult>());
        var resultPath = $"/api/v2/agent-updates/attempts/{claim.AttemptId}/result";
        // The coordinator marks Activating before the native updater validates its
        // immutable target. A target_exists failure is still before cutover.
        foreach (var state in new[] { "Downloading", "Staged", "Activating" })
        {
            using var progressResponse = await client.PostAsJsonAsync(resultPath, new { state });
            Assert.Equal(HttpStatusCode.Accepted, progressResponse.StatusCode);
        }
        using var result = await client.PostAsJsonAsync(resultPath, new { state = "FailedPreActivation", failureCode = "target_exists", message = "Immutable target version already exists." });
        Assert.Equal(HttpStatusCode.Accepted, result.StatusCode);
        Assert.Equal(ClientUpdateReportOutcome.Applied, (await result.Content.ReadFromJsonAsync<ClientUpdateReportResult>())!.Outcome);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            var attempt = await db.ClientUpdateAttempts.AsNoTracking().SingleAsync(x => x.PublicId == claim.AttemptId);
            Assert.Equal(tenantId, attempt.TenantId);
            Assert.Equal(agentId, attempt.AgentId);
            Assert.Equal(ClientUpdateAttemptState.FailedPreActivation, attempt.State);
            Assert.Equal("target_exists", attempt.FailureCode);
            Assert.Equal("Immutable target version already exists.", attempt.Message);
        }

        using var repeatedResult = await client.PostAsJsonAsync(resultPath,
            new { state = "FailedPreActivation", failureCode = "target_exists", message = "Immutable target version already exists." });
        Assert.Equal(HttpStatusCode.Accepted, repeatedResult.StatusCode);
        Assert.Equal(ClientUpdateReportOutcome.Duplicate, (await repeatedResult.Content.ReadFromJsonAsync<ClientUpdateReportResult>())!.Outcome);
        using var recoveredClaim = await client.PostAsJsonAsync(claimPath, new
        {
            runtimeId = "linux-x64", currentVersion = "91.2.3", channel = "stable", admissionNonce = nonce
        });
        var recovery = await recoveredClaim.Content.ReadFromJsonAsync<ClientUpdateClaimResult>();
        Assert.NotNull(recovery);
        Assert.NotEqual(claim.AttemptId, recovery.AttemptId);
        resultPath = $"/api/v2/agent-updates/attempts/{recovery.AttemptId}/result";
        using var progress = await client.PostAsJsonAsync(resultPath, new { state = "Downloading" });
        Assert.Equal(HttpStatusCode.Accepted, progress.StatusCode);
        foreach (var state in new[] { "Accepted", "GatewayReadmitted", "Claimed", "99", "3", "Downloading, Staged" })
        {
            using var invalidState = await client.PostAsJsonAsync(resultPath, new { state });
            Assert.Equal(HttpStatusCode.BadRequest, invalidState.StatusCode);
            Assert.Equal("invalid_report_state", (await invalidState.Content.ReadFromJsonAsync<ClientUpdateReportResult>())!.Code);
        }
        // Direct API uses the same structured contract as the existing Web/YARP route.
        using var direct = factory.CreateClient();
        direct.DefaultRequestHeaders.Authorization = client.DefaultRequestHeaders.Authorization;
        using var conflict = await direct.PostAsJsonAsync(resultPath, new { state = "RolledBack", failureCode = "activation_timeout" });
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal("state_conflict", (await conflict.Content.ReadFromJsonAsync<ClientUpdateReportResult>())!.Code);
        using var missing = await direct.PostAsJsonAsync($"/api/v2/agent-updates/attempts/{Guid.NewGuid()}/result", new { state = "FailedPreActivation" });
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal("attempt_not_found", (await missing.Content.ReadFromJsonAsync<ClientUpdateReportResult>())!.Code);

        client.DefaultRequestHeaders.Authorization = null;
        using var anonymous = await client.PostAsJsonAsync(claimPath, claimBody);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        using var anonymousResult = await client.PostAsJsonAsync(resultPath, new { state = "Downloading" });
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousResult.StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.CreateAgentBearerToken(tenantId, agentId, includeRole: false));
        using var invalidIdentity = await client.PostAsJsonAsync(claimPath, claimBody);
        Assert.Equal(HttpStatusCode.Forbidden, invalidIdentity.StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.CreateAgentBearerToken(tenantId + 1, agentId));
        using var wrongTenant = await client.PostAsJsonAsync(claimPath, claimBody);
        Assert.Equal(HttpStatusCode.Conflict, wrongTenant.StatusCode);
        using var wrongResult = await client.PostAsJsonAsync(resultPath, new { state = "Downloading" });
        Assert.Equal(HttpStatusCode.NotFound, wrongResult.StatusCode);
        Assert.Equal("attempt_not_found", (await wrongResult.Content.ReadFromJsonAsync<ClientUpdateReportResult>())!.Code);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", factory.CreateAgentBearerToken(tenantId, Guid.NewGuid()));
        using var wrongAgent = await client.PostAsJsonAsync(resultPath, new { state = "FailedPreActivation" });
        Assert.Equal(HttpStatusCode.NotFound, wrongAgent.StatusCode);
        Assert.Equal("attempt_not_found", (await wrongAgent.Content.ReadFromJsonAsync<ClientUpdateReportResult>())!.Code);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            var attempt = await db.ClientUpdateAttempts.AsNoTracking().SingleAsync(x => x.PublicId == recovery.AttemptId);
            Assert.Equal(ClientUpdateAttemptState.Downloading, attempt.State);
            Assert.Equal(ClientUpdateAttemptState.FailedPreActivation, (await db.ClientUpdateAttempts.AsNoTracking()
                .SingleAsync(x => x.PublicId == claim.AttemptId)).State);
        }

        foreach (var path in new[] { "/api/v2/unknown", "/api/v2/agent-updates-extra/releases/claim" })
        {
            using var unknown = await client.PostAsJsonAsync(path, claimBody);
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        }
    }

    private sealed class ApiTransport(TestServer api) : IForwarderHttpClientFactory
    {
        public HttpMessageInvoker CreateClient(ForwarderHttpClientContext context) => new(api.CreateHandler());
    }
}
