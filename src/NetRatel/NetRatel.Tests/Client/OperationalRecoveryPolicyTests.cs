using System.Globalization;
using System.Security.Authentication;
using AwesomeAssertions;
using Grpc.Core;
using NetRatel.Client.Service.Auth;
using Xunit;

namespace NetRatel.Tests.Client;

public sealed class OperationalRecoveryPolicyTests
{
    [Theory]
    [InlineData(StatusCode.DataLoss, false)]
    [InlineData(StatusCode.Unavailable, false)]
    [InlineData(StatusCode.Unauthenticated, false)]
    [InlineData(StatusCode.PermissionDenied, true)]
    public void RpcClassification_ProtocolLossDoesNotClaimInvalidCredentials(StatusCode status, bool attention)
    {
        OperationalRecoveryFailure.RequiresAttention(new RpcException(new Status(status, "redacted"))).Should().Be(attention);
    }

    [Fact]
    public void DataLoss_WithTypedTrustFailureStillRequiresAttention()
    {
        OperationalRecoveryFailure.RequiresAttention(new RpcException(
            new Status(StatusCode.DataLoss, "redacted", new AuthenticationException("redacted")))).Should().BeTrue();
    }

    [Theory]
    [InlineData(0, 1, 2, 4, 8, 15)]
    [InlineData(1, 2, 4, 8, 16, 30)]
    public void FastFailuresUseIndependentEqualJitter(double random, double first, double second,
        double third, double fourth, double fifth)
    {
        var clock = new GatewayPresenceTestClock();
        var draws = 0;
        var policy = new OperationalRecoveryPolicy(clock, () => { draws++; return random; });
        foreach (var expected in new[] { first, second, third, fourth, fifth, fifth })
        {
            policy.BeginAttempt();
            var delay = policy.FailureDelay();
            delay.Should().Be(TimeSpan.FromSeconds(expected));
            clock.Advance(delay);
        }
        draws.Should().Be(6);
        policy.AttemptCount.Should().Be(6);
    }

    [Fact]
    public void CompletedIoSelectsExactFiveAndThirtyMinutePhases()
    {
        var clock = new GatewayPresenceTestClock();
        var policy = new OperationalRecoveryPolicy(clock, () => 0);
        policy.BeginAttempt();
        clock.Advance(TimeSpan.FromSeconds(299));
        policy.FailureDelay().Should().Be(TimeSpan.FromSeconds(1));
        clock.Advance(TimeSpan.FromSeconds(1));
        policy.BeginAttempt();
        policy.FailureDelay().Should().Be(TimeSpan.FromSeconds(60));
        clock.Advance(TimeSpan.FromSeconds(60));
        policy.BeginAttempt();
        clock.Advance(TimeSpan.FromSeconds(1439));
        policy.FailureDelay().Should().Be(TimeSpan.FromSeconds(60));
        clock.Advance(TimeSpan.FromSeconds(1));
        policy.Phase.Should().Be(OperationalRetryPhase.Extended);
        policy.NextDelay.Should().Be(TimeSpan.FromSeconds(59), "crossing a phase boundary does not add an attempt");
        clock.Advance(TimeSpan.FromSeconds(59));
        policy.BeginAttempt();
        policy.FailureDelay().Should().Be(TimeSpan.FromSeconds(300));
        policy.Phase.Should().Be(OperationalRetryPhase.Extended);

        var spanning = new OperationalRecoveryPolicy(clock, () => 1);
        spanning.BeginAttempt();
        clock.Advance(TimeSpan.FromMinutes(5));
        spanning.FailureDelay().Should().Be(TimeSpan.FromSeconds(120), "bounded I/O time belongs to the episode");
    }

    [Theory]
    [InlineData(24)]
    [InlineData(72)]
    public void RepeatedVirtualOutageAttemptsContinueIndefinitelyWithBoundedFreshJitter(int hours)
    {
        var clock = new GatewayPresenceTestClock();
        var draw = 0;
        var policy = new OperationalRecoveryPolicy(clock, () => draw++ % 2);
        var attempts = 0;
        while (policy.OutageDuration < TimeSpan.FromHours(hours))
        {
            policy.BeginAttempt();
            clock.Advance(TimeSpan.FromSeconds(7));
            var phase = policy.Phase;
            var delay = policy.FailureDelay();
            delay.Should().BeGreaterThan(TimeSpan.Zero).And.BeLessThanOrEqualTo(TimeSpan.FromSeconds(600));
            if (phase == OperationalRetryPhase.Medium)
                delay.TotalSeconds.Should().BeInRange(60, 120);
            if (phase == OperationalRetryPhase.Extended)
                delay.TotalSeconds.Should().BeInRange(300, 600);
            clock.Advance(delay);
            attempts++;
        }
        attempts.Should().BeGreaterThan(hours * 5, "the test exercises many scheduled failures, not one jump");
        policy.AttemptCount.Should().Be(attempts);
        policy.Phase.Should().Be(OperationalRetryPhase.Extended);
        draw.Should().Be(attempts);
    }

    [Fact]
    public void OnlyContinuousValidatedHeartbeatProgressResetsAtOneHundredTwentySeconds()
    {
        var clock = new GatewayPresenceTestClock();
        var policy = new OperationalRecoveryPolicy(clock, () => 0);
        policy.BeginAttempt();
        clock.Advance(TimeSpan.FromMinutes(30));
        clock.Advance(policy.FailureDelay());
        policy.BeginAttempt();
        policy.AuthoritativeHeartbeat(TimeSpan.FromSeconds(30)).Should().BeFalse();
        for (var heartbeat = 0; heartbeat < 7; heartbeat++)
        {
            clock.Advance(TimeSpan.FromSeconds(17));
            policy.AuthoritativeHeartbeat(TimeSpan.FromSeconds(30)).Should().BeFalse();
        }
        policy.Phase.Should().Be(OperationalRetryPhase.Extended);
        clock.Advance(TimeSpan.FromSeconds(1));
        policy.AuthoritativeHeartbeat(TimeSpan.FromSeconds(30)).Should().BeTrue();
        policy.OutageDuration.Should().Be(TimeSpan.Zero);
        policy.AttemptCount.Should().Be(0);
        policy.BeginAttempt();
        policy.FailureDelay().Should().Be(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void BriefFlapsAndOpenButStalledStreamsRetainHistory()
    {
        var clock = new GatewayPresenceTestClock();
        var policy = new OperationalRecoveryPolicy(clock, () => 0);
        policy.BeginAttempt();
        clock.Advance(TimeSpan.FromMinutes(30));
        clock.Advance(policy.FailureDelay());
        policy.BeginAttempt();
        policy.AuthoritativeHeartbeat(TimeSpan.FromSeconds(30));
        clock.Advance(TimeSpan.FromSeconds(120));
        // A lone heartbeat after a stalled stream cannot claim continuous progress.
        policy.AuthoritativeHeartbeat(TimeSpan.FromSeconds(30)).Should().BeFalse();
        clock.Advance(TimeSpan.FromSeconds(15));
        policy.AuthoritativeHeartbeat(TimeSpan.FromSeconds(30)).Should().BeFalse();
        // Renewal success is deliberately no input to this owner policy.
        policy.FailureDelay().Should().Be(TimeSpan.FromSeconds(300));
        clock.Advance(TimeSpan.FromSeconds(300));
        policy.BeginAttempt();
        policy.AuthoritativeHeartbeat(TimeSpan.FromSeconds(30)).Should().BeFalse();
        policy.Phase.Should().Be(OperationalRetryPhase.Extended);
    }

    [Fact]
    public void RetryAfterParsesOnceRaisesTheFloorAddsJitterAndCapsExcessiveHints()
    {
        var clock = new GatewayPresenceTestClock();
        var policy = new OperationalRecoveryPolicy(clock, () => 1);
        policy.BeginAttempt();
        var hint = OperationalRecoveryPolicy.ParseRetryAfter("503", clock.GetUtcNow());
        policy.FailureDelay(hint).Should().Be(TimeSpan.FromSeconds(533));
        policy.RetryAfterCapped.Should().BeFalse();
        clock.Advance(TimeSpan.FromSeconds(533));
        policy.BeginAttempt();
        policy.FailureDelay(OperationalRecoveryPolicy.ParseRetryAfter("18446744073709551615", clock.GetUtcNow()))
            .Should().Be(TimeSpan.FromSeconds(600));
        policy.RetryAfterCapped.Should().BeTrue();
        OperationalRecoveryPolicy.ParseRetryAfter("99999999999999999999999999999999999999999", clock.GetUtcNow())
            .Should().Be(TimeSpan.FromSeconds(601));

        var now = clock.GetUtcNow();
        var future = now.AddSeconds(90).ToString("R", CultureInfo.InvariantCulture);
        OperationalRecoveryPolicy.ParseRetryAfter(future, now)!.Value.TotalSeconds.Should().BeInRange(89, 90);
        foreach (var invalid in new[] { "0", "-1", "1.5", "garbage", now.AddDays(-1).ToString("R", CultureInfo.InvariantCulture) })
            OperationalRecoveryPolicy.ParseRetryAfter(invalid, now).Should().BeNull();
    }

    [Fact]
    public async Task WallClockChangesAndRepeatedHintsDoNotAdvanceTheSingleMonotonicWait()
    {
        var clock = new GatewayPresenceTestClock();
        var policy = new OperationalRecoveryPolicy(clock, () => 0);
        policy.BeginAttempt();
        policy.FailureDelay(TimeSpan.FromSeconds(120));
        using var stopping = new CancellationTokenSource();
        var waiting = policy.WaitForNextAttemptAsync(stopping.Token);
        clock.ActiveTimerCount.Should().Be(1);
        clock.AdjustUtc(TimeSpan.FromDays(10));
        for (var hint = 0; hint < 20; hint++) policy.NotifyRecoveryHint();
        waiting.IsCompleted.Should().BeFalse();
        clock.ActiveTimerCount.Should().Be(1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => policy.WaitForNextAttemptAsync(stopping.Token));
        Action early = policy.BeginAttempt;
        early.Should().Throw<InvalidOperationException>();
        clock.Advance(TimeSpan.FromSeconds(119));
        policy.NotifyRecoveryHint();
        waiting.IsCompleted.Should().BeFalse();
        clock.Advance(TimeSpan.FromSeconds(1));
        for (var hint = 0; hint < 20; hint++) policy.NotifyRecoveryHint();
        await waiting;
        clock.ActiveTimerCount.Should().Be(0);
        policy.BeginAttempt();
        policy.AttemptCount.Should().Be(2);
    }

    [Fact]
    public async Task StopCancelsAndDisposesTheScheduledTimer()
    {
        var clock = new GatewayPresenceTestClock();
        var policy = new OperationalRecoveryPolicy(clock, () => 0);
        policy.BeginAttempt();
        policy.FailureDelay(TimeSpan.FromSeconds(600));
        using var stopping = new CancellationTokenSource();
        var waiting = policy.WaitForNextAttemptAsync(stopping.Token);
        stopping.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        clock.ActiveTimerCount.Should().Be(0);
    }

    [Fact]
    public void RestartRetainsPhaseAndDeadlineAndUpdateExceptionIsPersistedOnce()
    {
        using var fixture = new AdvisoryFixture();
        var clock = new GatewayPresenceTestClock();
        var policy = new OperationalRecoveryPolicy(clock, () => 0, stateStore: fixture.Store);
        policy.BeginAttempt();
        clock.Advance(TimeSpan.FromMinutes(30));
        policy.FailureDelay();
        var restarted = new OperationalRecoveryPolicy(clock, () => 0, stateStore: fixture.Store);
        restarted.Phase.Should().Be(OperationalRetryPhase.Extended);
        restarted.NextDelay.Should().Be(TimeSpan.FromSeconds(300));
        var attempt = Guid.NewGuid();
        restarted.TryConsumeUpdateAttempt(attempt).Should().BeTrue();
        restarted.NextDelay.Should().Be(TimeSpan.Zero);
        restarted.BeginAttempt();
        var repeatedStart = new OperationalRecoveryPolicy(clock, () => 0, stateStore: fixture.Store);
        repeatedStart.TryConsumeUpdateAttempt(attempt).Should().BeFalse();
        repeatedStart.NextDelay.Should().Be(TimeSpan.FromSeconds(300));
        new FileInfo(fixture.Path).Length.Should().BeLessThan(4096);
        Directory.GetFiles(fixture.Directory, "*.tmp").Should().BeEmpty();
    }

    [Fact]
    public void UpdateExceptionCannotBypassActiveServerHintButAttentionFloorIsAdvisory()
    {
        using var fixture = new AdvisoryFixture();
        var clock = new GatewayPresenceTestClock();
        var policy = new OperationalRecoveryPolicy(clock, () => 0, stateStore: fixture.Store);
        policy.BeginAttempt();
        clock.Advance(TimeSpan.FromMinutes(30));
        policy.FailureDelay(retryAfter: TimeSpan.FromSeconds(120));
        var restarted = new OperationalRecoveryPolicy(clock, () => 0, stateStore: fixture.Store);
        var attempt = Guid.NewGuid();
        restarted.TryConsumeUpdateAttempt(attempt).Should().BeFalse();
        clock.Advance(TimeSpan.FromSeconds(120));
        restarted.TryConsumeUpdateAttempt(attempt).Should().BeTrue();

        using var attention = new AdvisoryFixture();
        var attentionPolicy = new OperationalRecoveryPolicy(clock, () => 0, stateStore: attention.Store);
        attentionPolicy.BeginAttempt();
        clock.Advance(TimeSpan.FromMinutes(30));
        attentionPolicy.FailureDelay(minimumDelay: TimeSpan.FromSeconds(300));
        new OperationalRecoveryPolicy(clock, () => 0, stateStore: attention.Store)
            .TryConsumeUpdateAttempt(Guid.NewGuid()).Should().BeTrue();
    }

    [Fact]
    public void CorruptAndUnwritableStateNeverStopRecoveryAndCannotGrantUpdateExceptions()
    {
        using var fixture = new AdvisoryFixture();
        var clock = new GatewayPresenceTestClock();
        File.WriteAllText(fixture.Path, "{truncated");
        var policy = new OperationalRecoveryPolicy(clock, () => 0, stateStore: fixture.Store);
        policy.NextDelay.Should().Be(TimeSpan.Zero);
        policy.BeginAttempt();
        clock.Advance(TimeSpan.FromMinutes(30));
        policy.FailureDelay().Should().Be(TimeSpan.FromSeconds(300));
        // A regular file in place of the directory deterministically models unwritable state, even as root.
        File.WriteAllText(System.IO.Path.Combine(fixture.Directory, "blocked"), "file");
        var unwritable = new OperationalRecoveryStateStore(System.IO.Path.Combine(fixture.Directory, "blocked", "state.json"));
        var blocked = new OperationalRecoveryPolicy(clock, () => 0, stateStore: unwritable);
        blocked.BeginAttempt();
        clock.Advance(TimeSpan.FromMinutes(30));
        blocked.FailureDelay().Should().Be(TimeSpan.FromSeconds(300));

        var snapshot = fixture.Store.Read()!;
        fixture.Store.Write(snapshot with { RecordedAtUtc = clock.GetUtcNow(), NextAttemptUtc = clock.GetUtcNow().AddDays(365), RetryAfterUntilUtc = clock.GetUtcNow().AddDays(365) });
        new OperationalRecoveryPolicy(clock, () => 0, stateStore: fixture.Store).NextDelay.Should().Be(TimeSpan.FromSeconds(600));
        var inherited = new OperationalRecoveryPolicy(clock, () => 0, stateStore: fixture.Store);
        clock.Advance(TimeSpan.FromSeconds(600));
        inherited.BeginAttempt();
        inherited.FailureDelay();
        var cannotPersistConsumption = new OperationalRecoveryPolicy(clock, () => 0, stateStore: fixture.Store);
        File.Delete(fixture.Path);
        Directory.CreateDirectory(fixture.Path);
        cannotPersistConsumption.TryConsumeUpdateAttempt(Guid.NewGuid()).Should().BeFalse();
        cannotPersistConsumption.NextDelay.Should().Be(TimeSpan.FromSeconds(300));
        Directory.Delete(fixture.Path);
        File.WriteAllText(fixture.Path, new string('x', 5000));
        fixture.Store.Read().Should().BeNull();
    }

    [Fact]
    public void InvalidOptionsAndRandomnessFailLocally()
    {
        Action invalid = () => new OperationalRecoveryPolicy(new GatewayPresenceTestClock(), () => 0,
            new OperationalRecoveryOptions { ExtendedPhaseSeconds = 300 });
        invalid.Should().Throw<ArgumentException>();
        var policy = new OperationalRecoveryPolicy(new GatewayPresenceTestClock(), () => double.NaN);
        Action failure = () => policy.FailureDelay();
        failure.Should().Throw<InvalidOperationException>();
    }

    private sealed class AdvisoryFixture : IDisposable
    {
        internal string Directory { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"netratel-retry-{Guid.NewGuid():N}");
        internal string Path => System.IO.Path.Combine(Directory, "operational-recovery.json");
        internal OperationalRecoveryStateStore Store { get; }
        internal AdvisoryFixture()
        {
            System.IO.Directory.CreateDirectory(Directory);
            Store = new(Path);
        }
        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }
}
