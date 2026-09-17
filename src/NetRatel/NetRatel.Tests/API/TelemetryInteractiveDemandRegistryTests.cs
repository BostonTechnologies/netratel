using FluentAssertions;
using Microsoft.Extensions.Configuration;
using NetRatel.API.Gateway;
using NetRatel.Application.Presence;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class TelemetryInteractiveDemandRegistryTests
{
    [Fact]
    public void Leases_SelectMinimumPeriod_AndFinalReleaseRestoresBaseline()
    {
        var registry = CreateRegistry();
        var client = new ClientKey(7, Guid.NewGuid());
        var first = registry.Acquire(client, 3000);
        var second = registry.Acquire(client, 1000);

        registry.GetPolicy(client).Interactive.Should().BeTrue();
        registry.GetPolicy(client).FastIntervalMilliseconds.Should().Be(1000);

        registry.Release(second).Should().BeTrue();
        registry.GetPolicy(client).FastIntervalMilliseconds.Should().Be(3000);
        registry.Release(first).Should().BeTrue();
        registry.Release(first).Should().BeFalse();
        registry.GetPolicy(client).Interactive.Should().BeFalse();
        registry.GetPolicy(client).FastIntervalMilliseconds.Should().Be(5000);
    }

    [Fact]
    public void InvalidRequestedPeriod_ClampsToOneSecond()
    {
        var registry = CreateRegistry();
        var lease = registry.Acquire(new ClientKey(7, Guid.NewGuid()), 0);
        lease.RequestedPeriodMilliseconds.Should().Be(1000);
    }

    [Fact]
    public void Renew_NearPolicyExpiry_PublishesNewRevisionWithoutChangingDemand()
    {
        var clock = new AdjustableTimeProvider(new DateTimeOffset(2026, 8, 23, 8, 0, 0, TimeSpan.Zero));
        var registry = CreateRegistry(clock, leaseSeconds: 45, policySeconds: 30, renewalLeadSeconds: 10);
        var client = new ClientKey(7, Guid.NewGuid());
        var lease = registry.Acquire(client, 1000);
        var first = registry.GetPolicy(client);

        clock.Advance(TimeSpan.FromSeconds(21));
        registry.Renew(lease).Should().BeTrue();
        var refreshed = registry.GetPolicy(client);

        refreshed.Interactive.Should().BeTrue();
        refreshed.FastIntervalMilliseconds.Should().Be(1000);
        refreshed.Revision.Should().BeGreaterThan(first.Revision);
        refreshed.ExpiresAtUtc.Should().BeAfter(first.ExpiresAtUtc);
    }

    [Fact]
    public void RepeatedRenewal_ThroughThreePolicyLifetimes_KeepsInteractiveDemandUntilFinalRelease()
    {
        var clock = new AdjustableTimeProvider(new DateTimeOffset(2026, 8, 23, 8, 0, 0, TimeSpan.Zero));
        var registry = CreateRegistry(clock, leaseSeconds: 120, policySeconds: 30, renewalLeadSeconds: 10);
        var client = new ClientKey(7, Guid.NewGuid());
        var lease = registry.Acquire(client, 1000);
        var policies = new List<TelemetrySamplingPolicyState>();
        registry.PolicyChanged += (changedClient, policy) =>
        {
            if (changedClient == client) policies.Add(policy);
        };

        for (var lifetime = 0; lifetime < 3; lifetime++)
        {
            clock.Advance(TimeSpan.FromSeconds(21));
            registry.Renew(lease).Should().BeTrue();
            registry.GetPolicy(client).Interactive.Should().BeTrue();
            registry.GetPolicy(client).FastIntervalMilliseconds.Should().Be(1000);
        }

        policies.Should().HaveCount(3).And.OnlyContain(policy => policy.Interactive);
        policies.Select(static policy => policy.Revision).Should().OnlyHaveUniqueItems();
        registry.Release(lease).Should().BeTrue();
        registry.GetPolicy(client).Should().Match<TelemetrySamplingPolicyState>(policy =>
            !policy.Interactive && policy.FastIntervalMilliseconds == 5000);
    }

    [Fact]
    public void Sweep_ExpiredLease_RestoresBaselineAndRejectsLateRenewal()
    {
        var clock = new AdjustableTimeProvider(new DateTimeOffset(2026, 8, 23, 8, 0, 0, TimeSpan.Zero));
        var registry = CreateRegistry(clock, leaseSeconds: 30, policySeconds: 60, renewalLeadSeconds: 15);
        var client = new ClientKey(7, Guid.NewGuid());
        var lease = registry.Acquire(client, 1000);
        var interactive = registry.GetPolicy(client);

        clock.Advance(TimeSpan.FromSeconds(31));
        registry.Sweep();

        registry.Renew(lease).Should().BeFalse();
        var baseline = registry.GetPolicy(client);
        baseline.Interactive.Should().BeFalse();
        baseline.FastIntervalMilliseconds.Should().Be(5000);
        baseline.Revision.Should().BeGreaterThan(interactive.Revision);
    }

    private static TelemetryInteractiveDemandRegistry CreateRegistry(
        TimeProvider? timeProvider = null,
        int leaseSeconds = 45,
        int policySeconds = 60,
        int renewalLeadSeconds = 20) => new(
        timeProvider ?? TimeProvider.System,
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TelemetryInteractive:LeaseLifetimeSeconds"] = leaseSeconds.ToString(),
            ["TelemetryInteractive:PolicyLifetimeSeconds"] = policySeconds.ToString(),
            ["TelemetryInteractive:PolicyRenewalLeadSeconds"] = renewalLeadSeconds.ToString()
        }).Build());

    private sealed class AdjustableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now = _now.Add(duration);
    }
}
