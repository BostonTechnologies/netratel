using System.Text.Json;
using NetRatel.Application.Notifications;
using Xunit;

namespace NetRatel.Tests.Application;

public sealed class NetRatelNotificationDisplaySanitizerTests
{
    private const string KnownClientId = "C2009662A43EF5AF08CC173C5E32E73447A98BD99CC7DDA641F3C466B95CDAD7";
    private const string UnknownClientId = "A1009662A43EF5AF08CC173C5E32E73447A98BD99CC7DDA641F3C466B95CDAD7";

    [Fact]
    public void Sanitize_ReplacesKnownClientIdentityInMessageAndCorrelation()
    {
        var sut = new NetRatelNotificationDisplaySanitizer(new StubClientDisplayNameResolver(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [KnownClientId] = "btstodevagent01"
            }));

        var result = sut.Sanitize(new NetRatelNotificationDto
        {
            CorrelationId = $"netratel-client-{KnownClientId}",
            EntityId = KnownClientId,
            Message = $"Task exec-shell-cmd submitted for client {KnownClientId}.",
            PayloadJson = "{}"
        });

        Assert.Equal("netratel-client-btstodevagent01", result.CorrelationId);
        Assert.Equal("btstodevagent01", result.EntityId);
        Assert.Equal("Task exec-shell-cmd submitted for client btstodevagent01.", result.Message);
        Assert.DoesNotContain(KnownClientId, result.CorrelationId);
        Assert.DoesNotContain(KnownClientId, result.Message);
    }

    [Fact]
    public void Sanitize_RewritesClientIdentityPayloadFieldToClientDisplayField()
    {
        var sut = new NetRatelNotificationDisplaySanitizer(new StubClientDisplayNameResolver(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [KnownClientId] = "btstodevagent01"
            }));

        var result = sut.Sanitize(new NetRatelNotificationDto
        {
            CorrelationId = "corr-test",
            PayloadJson = JsonSerializer.Serialize(new
            {
                requestId = "request-1",
                clientIdentity = KnownClientId,
                nested = new { clientId = KnownClientId }
            })
        });

        using var doc = JsonDocument.Parse(result.PayloadJson);
        Assert.False(doc.RootElement.TryGetProperty("clientIdentity", out _));
        Assert.Equal("btstodevagent01", doc.RootElement.GetProperty("client").GetString());
        Assert.Equal("btstodevagent01", doc.RootElement.GetProperty("nested").GetProperty("client").GetString());
        Assert.DoesNotContain(KnownClientId, result.PayloadJson);
    }

    [Fact]
    public void Sanitize_UsesUnavailableFallbackWhenClientCannotBeResolved()
    {
        var sut = new NetRatelNotificationDisplaySanitizer(new StubClientDisplayNameResolver(
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)));

        var result = sut.Sanitize(new NetRatelNotificationDto
        {
            CorrelationId = $"netratel-client-{UnknownClientId}",
            EntityId = UnknownClientId,
            Message = $"Client {UnknownClientId} state changed.",
            PayloadJson = JsonSerializer.Serialize(new { clientIdentity = UnknownClientId })
        });

        Assert.Equal("netratel-client-Client unavailable", result.CorrelationId);
        Assert.Equal("Client unavailable", result.EntityId);
        Assert.Equal("Client Client unavailable state changed.", result.Message);
        Assert.Contains("\"client\":\"Client unavailable\"", result.PayloadJson);
        Assert.DoesNotContain(UnknownClientId, result.PayloadJson);
    }

    private sealed class StubClientDisplayNameResolver(IReadOnlyDictionary<string, string> displayNames) : IClientDisplayNameResolver
    {
        public IReadOnlyDictionary<string, string> ResolveDisplayNames(IEnumerable<string> clientIdentities)
            => displayNames
                .Where(pair => clientIdentities.Contains(pair.Key, StringComparer.OrdinalIgnoreCase))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
    }
}
