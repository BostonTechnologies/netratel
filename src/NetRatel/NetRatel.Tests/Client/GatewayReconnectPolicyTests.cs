using AwesomeAssertions;
using NetRatel.Client.Service.Gateway;
using Xunit;

namespace NetRatel.Tests.Client;

public sealed class GatewayReconnectPolicyTests
{
    [Fact]
    public void FailedAdmissionsAndSixtySecondFlapsRetainBackoffUntilValidatedStableHealth()
    {
        var clock = new GatewayPresenceTestClock();
        var policy = new GatewayReconnectPolicy(clock, () => 0.5, TimeSpan.FromSeconds(120));
        policy.BeginAttempt();
        policy.FailureDelay().Should().Be(TimeSpan.FromSeconds(1));
        policy.BeginAttempt();
        policy.Acknowledged();
        clock.Advance(TimeSpan.FromSeconds(60));
        policy.Acknowledged();
        policy.FailureDelay().Should().Be(TimeSpan.FromSeconds(2));
        policy.BeginAttempt();
        policy.Acknowledged();
        for (var heartbeat = 0; heartbeat < 8; heartbeat++)
        {
            clock.Advance(TimeSpan.FromSeconds(15));
            policy.Acknowledged();
        }
        policy.FailureDelay().Should().Be(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void RetryJitterIsInjectedAndCapped()
    {
        var policy = new GatewayReconnectPolicy(new GatewayPresenceTestClock(), () => 1, TimeSpan.FromSeconds(120));
        policy.FailureDelay().Should().Be(TimeSpan.FromSeconds(1.2));
        for (var failure = 0; failure < 20; failure++)
            policy.FailureDelay().Should().BeLessThanOrEqualTo(TimeSpan.FromSeconds(30));
    }
}
