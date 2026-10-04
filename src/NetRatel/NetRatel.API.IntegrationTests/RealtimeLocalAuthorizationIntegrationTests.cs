using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.Infrastructure.Persistence;
using Xunit;

[Trait("category", "integration")]
[Collection(ApiIntegrationCollection.Name)]
public sealed class RealtimeLocalAuthorizationIntegrationTests(ApiFactory factory)
{
    [Fact]
    public async Task LocalInstanceAdministratorCanNegotiateAndSubscribeWhileUnprivilegedLocalUserIsDenied()
    {
        using var host = factory.CreateRuntimeSibling(new Dictionary<string, string?>
        {
            ["NetRatelAkka:ActorSystemName"] = $"RealtimeLocalAuthorization{Guid.NewGuid():N}"
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var cookie = await LoginAndReadLocalCookieAsync(host, timeout.Token);
        using var administrator = host.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        administrator.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", cookie).Should().BeTrue();

        using (var directory = await administrator.GetAsync("/api/v2/client-presence/?online=false&limit=1", timeout.Token))
        {
            directory.StatusCode.Should().Be(HttpStatusCode.OK,
                "the same Local principal already has working instance-admin authorization on the directory route");
        }

        var tenantId = await ReadOpenApiTenantIdAsync(host, timeout.Token);
        using var negotiation = await administrator.PostAsync(
            "/hubs/akka-authority/negotiate?negotiateVersion=1",
            new ByteArrayContent([]),
            timeout.Token);
        negotiation.StatusCode.Should().Be(HttpStatusCode.OK,
            "the real Local instance administrator is authorized by the application access evaluator");

        using var negotiationBody = await JsonDocument.ParseAsync(
            await negotiation.Content.ReadAsStreamAsync(timeout.Token),
            cancellationToken: timeout.Token);
        var connectionToken = negotiationBody.RootElement.GetProperty("connectionToken").GetString();
        connectionToken.Should().NotBeNullOrWhiteSpace();

        var webSocketClient = host.Server.CreateWebSocketClient();
        webSocketClient.ConfigureRequest = request => request.Headers["Cookie"] = cookie;
        var socketUri = new Uri($"ws://localhost/hubs/akka-authority?id={Uri.EscapeDataString(connectionToken!)}");
        using var socket = await webSocketClient.ConnectAsync(socketUri, timeout.Token);

        await SendSignalRRecordAsync(socket, "{\"protocol\":\"json\",\"version\":1}", timeout.Token);
        var handshake = await ReceiveSignalRRecordAsync(socket, timeout.Token);
        handshake.ValueKind.Should().Be(JsonValueKind.Object);
        handshake.EnumerateObject().Should().BeEmpty();

        await SendSignalRRecordAsync(
            socket,
            JsonSerializer.Serialize(new
            {
                type = 1,
                invocationId = "tenant-subscription",
                target = "SubscribeTenant",
                arguments = new[] { tenantId }
            }),
            timeout.Token);
        var subscription = await ReceiveSignalRRecordAsync(socket, timeout.Token);
        subscription.GetProperty("type").GetInt32().Should().Be(3);
        subscription.GetProperty("invocationId").GetString().Should().Be("tenant-subscription");
        subscription.TryGetProperty("error", out _).Should().BeFalse();
        subscription.GetProperty("result").ValueKind.Should().Be(JsonValueKind.Array);

        using var unprivileged = await host.CreateLocalUserClientAsync(
            $"realtime-reader-{Guid.NewGuid():N}@example.test",
            "A1! unprivileged local account");
        using var denied = await unprivileged.PostAsync(
            "/hubs/akka-authority/negotiate?negotiateVersion=1",
            new ByteArrayContent([]),
            timeout.Token);
        denied.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "a Local account without instance or tenant authorization must not gain the realtime hub");
    }

    [Fact]
    public async Task LocalInstanceAdministratorCanReadLogRoutesWhileUnprivilegedLocalUserIsDenied()
    {
        using var host = factory.CreateRuntimeSibling(new Dictionary<string, string?>
        {
            ["NetRatelAkka:ActorSystemName"] = $"RealtimeLocalLogAuthorization{Guid.NewGuid():N}"
        });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var tenantId = await ReadOpenApiTenantIdAsync(host, timeout.Token);
        var agentId = Guid.NewGuid();
        var cookie = await LoginAndReadLocalCookieAsync(host, timeout.Token);
        using var administrator = host.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        administrator.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", cookie).Should().BeTrue();

        using var sources = await administrator.GetAsync(
            $"/api/v2/agents/{tenantId}/{agentId:D}/logs/sources",
            timeout.Token);
        using var operationsNegotiation = await administrator.PostAsync(
            "/hubs/operations/negotiate?negotiateVersion=1",
            new ByteArrayContent([]),
            timeout.Token);

        using var unprivileged = await host.CreateLocalUserClientAsync(
            $"realtime-log-reader-{Guid.NewGuid():N}@example.test",
            "A1! unprivileged local account");
        using var deniedSources = await unprivileged.GetAsync(
            $"/api/v2/agents/{tenantId}/{agentId:D}/logs/sources",
            timeout.Token);
        using var deniedOperationsNegotiation = await unprivileged.PostAsync(
            "/hubs/operations/negotiate?negotiateVersion=1",
            new ByteArrayContent([]),
            timeout.Token);

        new
        {
            AdministratorLogSources = sources.StatusCode,
            AdministratorOperationsNegotiation = operationsNegotiation.StatusCode,
            UnprivilegedLogSources = deniedSources.StatusCode,
            UnprivilegedOperationsNegotiation = deniedOperationsNegotiation.StatusCode
        }.Should().BeEquivalentTo(new
        {
            AdministratorLogSources = HttpStatusCode.NotFound,
            AdministratorOperationsNegotiation = HttpStatusCode.OK,
            UnprivilegedLogSources = HttpStatusCode.Forbidden,
            UnprivilegedOperationsNegotiation = HttpStatusCode.Forbidden
        }, "a real Local administrator should pass effective telemetry/instance access, but a user without grants should remain denied");
    }

    private static async Task<string> LoginAndReadLocalCookieAsync(ApiFactory host, CancellationToken cancellationToken)
    {
        using var client = host.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        using var response = await client.PostAsJsonAsync("/api/v2/local-auth/login", new
        {
            Email = ApiFactory.LocalAdministratorEmail,
            Password = ApiFactory.LocalAdministratorPassword,
            RememberMe = false
        }, cancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.NoContent,
            "the fixture must authenticate through the shipped Local cookie flow");
        response.Headers.TryGetValues("Set-Cookie", out var values).Should().BeTrue();

        var cookie = string.Join("; ", values!.Select(static value => value.Split(';', 2)[0]));
        cookie.Should().NotBeNullOrWhiteSpace();
        return cookie;
    }

    private static async Task<int> ReadOpenApiTenantIdAsync(ApiFactory host, CancellationToken cancellationToken)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>().Tenants
            .Where(tenant => tenant.Name == "OpenAPI tenant")
            .Select(tenant => tenant.Id)
            .SingleAsync(cancellationToken);
    }

    private static ValueTask SendSignalRRecordAsync(WebSocket socket, string json, CancellationToken cancellationToken)
    {
        var payload = Encoding.UTF8.GetBytes(json + '\u001e');
        return socket.SendAsync(payload.AsMemory(), WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
    }

    private static async Task<JsonElement> ReceiveSignalRRecordAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        using var record = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer.AsMemory(), cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new InvalidOperationException("The realtime hub closed before completing the expected SignalR message.");
            }

            for (var index = 0; index < result.Count; index++)
            {
                if (buffer[index] == 0x1e)
                {
                    using var document = JsonDocument.Parse(record.ToArray());
                    return document.RootElement.Clone();
                }

                record.WriteByte(buffer[index]);
            }
        }
    }
}
