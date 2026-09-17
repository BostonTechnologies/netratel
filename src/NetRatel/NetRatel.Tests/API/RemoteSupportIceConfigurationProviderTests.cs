using FluentAssertions;
using Microsoft.Extensions.Options;
using NetRatel.API.Services.RemoteSupport;
using NetRatel.Shared.Contracts.RemoteSupport;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class RemoteSupportIceConfigurationProviderTests
{
    [Fact]
    public void TurnRestCredential_matches_the_standard_hmac_sha1_vector()
    {
        RemoteSupportIceConfigurationProvider.CreateCredential("1700000000:netratel-example", "turn-secret")
            .Should().Be("P012ws/Ko/FjWaQHNnfmbml9y5A=");
    }

    [Fact]
    public void Session_configuration_is_deterministic_scoped_and_does_not_expose_the_shared_secret()
    {
        var expires = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var provider = new RemoteSupportIceConfigurationProvider(
            Options.Create(new RemoteSupportIceOptions
            {
                SessionLifetime = TimeSpan.FromMinutes(30),
                StunUrls = ["stun:stun.example.invalid:3478"],
                Turn = new RemoteSupportTurnOptions
                {
                    Enabled = true,
                    Urls = ["turn:turn.example.invalid:3478?transport=udp", "turn:turn.example.invalid:3478?transport=tcp"],
                    Realm = "example.invalid",
                    SharedSecret = "turn-secret",
                    UsernamePrefix = "netratel"
                }
            }),
            new FixedTimeProvider(expires));
        var session = Snapshot(expires);

        var first = provider.GetForSession(session, 2);
        var second = provider.GetForSession(session, 2);

        second.Session.Should().Be(first.Session);
        second.Generation.Should().Be(first.Generation);
        second.ExpiresAtUtc.Should().Be(first.ExpiresAtUtc);
        second.Servers.Should().BeEquivalentTo(first.Servers, options => options.WithStrictOrdering());
        first.Session.Should().Be(session.Session);
        first.Generation.Should().Be(2);
        first.ExpiresAtUtc.Should().Be(expires);
        first.Servers.Should().HaveCount(2);
        var turn = first.Servers.Single(server => server.Urls.Any(url => url.StartsWith("turn:", StringComparison.Ordinal)));
        turn.Username.Should().StartWith("1700000000:netratel-");
        turn.Credential.Should().NotBeNullOrWhiteSpace().And.NotBe("turn-secret");
        first.ToString().Should().NotContain("turn-secret");
    }

    [Fact]
    public void Validator_rejects_an_enabled_turn_server_without_a_shared_secret()
    {
        var result = new RemoteSupportIceOptionsValidator().Validate(null, new RemoteSupportIceOptions
        {
            Turn = new RemoteSupportTurnOptions { Enabled = true, Urls = ["turn:turn.example.invalid:3478"] }
        });

        result.Failed.Should().BeTrue();
    }

    private static RemoteSupportSessionSnapshot Snapshot(DateTimeOffset expiresAtUtc) => new(
        1,
        new RemoteSupportSessionKey(7, Guid.Parse("11111111-1111-1111-1111-111111111111"), Guid.Parse("22222222-2222-2222-2222-222222222222")),
        Guid.Parse("33333333-3333-3333-3333-333333333333"),
        new RemoteSupportOperatorBinding("operator"),
        new RemoteSupportTargetDescriptor(RemoteSupportV2TargetKinds.ConsoleLogin),
        ["view"],
        RemoteSupportV2SessionStates.ReadyForOffer,
        1,
        expiresAtUtc.AddMinutes(-1),
        expiresAtUtc.AddMinutes(-1),
        expiresAtUtc);

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
