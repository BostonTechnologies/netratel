using System.Security.Cryptography;
using System.Text;
using AwesomeAssertions;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.API.Gateway;
using NetRatel.API.Services;
using NetRatel.Application.Agents;
using NetRatel.Application.Presence;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class ClientOffboardingGatewayAuthorizationTests
{
    [Fact]
    public async Task Production_mapping_allows_only_scoped_completion_without_agent_JWT_and_keeps_connect_authenticated()
    {
        using var credentials = new AgentGatewayRenewalTestCredentials();
        using var host = await CreateHostAsync(credentials);
        var clientKey = new ClientKey(7, Guid.NewGuid());
        var operationId = Guid.NewGuid();
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await SeedPendingAsync(host.Services, clientKey, operationId, secret);
        using var channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
        {
            HttpHandler = host.GetTestServer().CreateHandler()
        });
        var client = new AgentControlGateway.AgentControlGatewayClient(channel);

        // No Authorization header is supplied to either RPC. The normal connection is still protected.
        using (var connect = client.Connect())
        {
            var unauthorized = await Assert.ThrowsAsync<RpcException>(() =>
                connect.ResponseStream.MoveNext(default).WaitAsync(TimeSpan.FromSeconds(5)));
            unauthorized.StatusCode.Should().Be(StatusCode.Unauthenticated);
        }

        var receipt = new UninstallReceipt
        {
            TenantId = clientKey.TenantId, ClientId = clientKey.AgentId.ToString("D"),
            OperationId = operationId.ToString("D"), CompletionToken = new string('Z', 64),
            Success = true, ServiceRemoved = true, FilesRemoved = true
        };
        using (var invalid = client.CompleteUninstallAsync(receipt))
        {
            var denied = await Assert.ThrowsAsync<RpcException>(() => invalid.ResponseAsync);
            denied.StatusCode.Should().Be(StatusCode.PermissionDenied,
                "the anonymous receipt must reach the scoped validator, which must reject an invalid secret");
        }
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            (await db.Agents.CountAsync()).Should().Be(1);
            (await db.ClientOffboardingOperations.SingleAsync()).FinishedAtUtc.Should().BeNull();
            (await db.AgentCredentials.CountAsync()).Should().Be(0);
        }

        receipt.CompletionToken = secret;
        using (var completed = client.CompleteUninstallAsync(receipt))
            (await completed.ResponseAsync).Accepted.Should().BeTrue();
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            (await db.Agents.CountAsync()).Should().Be(0);
            (await db.Agents.IgnoreQueryFilters().SingleAsync()).DeletedAtUtc.Should().NotBeNull();
            (await db.ClientOffboardingOperations.SingleAsync()).State.Should().Be(ClientOffboardingState.Completed);
        }
        using var retry = client.CompleteUninstallAsync(receipt.Clone());
        (await retry.ResponseAsync).Accepted.Should().BeTrue(
            "a lost completion response must remain recoverable after the client and its credentials are gone");
    }

    private static Task<IHost> CreateHostAsync(AgentGatewayRenewalTestCredentials credentials)
    {
        var databaseName = Guid.NewGuid().ToString("N");
        return Host.CreateDefaultBuilder().ConfigureWebHost(web =>
        {
            web.UseTestServer();
            web.ConfigureServices(services =>
            {
                services.AddRouting();
                services.AddGrpc();
                services.AddAuthentication("Agent").AddJwtBearer("Agent", credentials.Configure);
                services.AddAuthorization(options =>
                {
                    options.FallbackPolicy = new AuthorizationPolicyBuilder()
                        .RequireAuthenticatedUser().RequireRole("Operator").Build();
                    options.AddPolicy("AgentGatewayAccess", policy =>
                    {
                        policy.AddAuthenticationSchemes("Agent");
                        policy.RequireAuthenticatedUser();
                        policy.RequireAssertion(context => AgentGatewayIdentityResolver.TryResolve(context.User, out _, out _));
                    });
                });
                services.AddDbContext<OrchestratorDbContext>(options => options.UseInMemoryDatabase(databaseName));
                services.AddSingleton(TimeProvider.System);
                services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
                services.AddScoped<IAgentManagementService, AgentManagementService>();
                services.AddScoped<ClientOffboardingService>();
                services.AddSingleton<IClientPresenceRouter, UnusedPresence>();
                services.AddSingleton<IAgentControlSessionRegistry, AgentControlSessionRegistry>();
            });
            web.Configure(app =>
            {
                app.UseRouting();
                app.UseAuthentication();
                app.UseAuthorization();
                // Exercise the production mapping, including its service-wide RequireAuthorization metadata.
                app.UseEndpoints(endpoints => endpoints.MapAgentGatewayEndpoints());
            });
        }).StartAsync();
    }

    private static async Task SeedPendingAsync(IServiceProvider services, ClientKey client, Guid operationId, string secret)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var now = DateTimeOffset.UtcNow;
        db.Agents.Add(new Agent
        {
            Id = client.AgentId, TenantId = client.TenantId, Name = "removed-installation",
            IsEnabled = true, Status = AgentStatus.Active, PublicKeyFingerprint = "fixture-device-key"
        });
        // No agent credentials remain; the helper has only the operation-specific completion secret.
        db.ClientOffboardingOperations.Add(new ClientOffboardingOperation
        {
            Id = operationId, TenantId = client.TenantId, AgentId = client.AgentId,
            ConnectionId = Guid.NewGuid(), ConnectionEpoch = 5, State = ClientOffboardingState.Accepted,
            RequestedBy = "operator", Reason = "Retired", ExpectedKeyFingerprint = "fixture-device-key",
            CompletionTokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret))),
            CreatedAtUtc = now, UpdatedAtUtc = now, StartDeadlineUtc = now.AddMinutes(5), ReceiptExpiresAtUtc = now.AddHours(24)
        });
        await db.SaveChangesAsync();
    }

    private sealed class UnusedPresence : IClientPresenceRouter
    {
        public Task<ClientPresenceSnapshot> GetSnapshotAsync(ClientKey client, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<GatewayPresenceSessionStarted> StartSessionAsync(StartGatewayPresenceSession message, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PresenceMessageResult> RecordHeartbeatAsync(RecordGatewayHeartbeat message, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PresenceMessageResult> EndSessionAsync(EndGatewayPresenceSession message, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ClientPresenceRouteStatus> ProbeAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
