using FluentAssertions;
using NetRatel.Application.ClientAuth;
using NetRatel.Client.Service.Auth;
using Xunit;

namespace NetRatel.Tests.Client;

public sealed class DisabledAgentTokenRetryTests
{
    [Fact]
    public async Task DisabledThenEnabled_ReturnsTokenWithBoundedBackoff()
    {
        var service = new DelegateTokenService(attempt => attempt <= 8
            ? throw Disabled()
            : ("enabled-token", DateTimeOffset.UtcNow.AddHours(1)));
        var delays = new List<TimeSpan>();
        var logs = new List<string>();
        var result = await DisabledAgentTokenRetry.GetAccessTokenAsync(service, logs.Add, CancellationToken.None,
            (delay, _) => { delays.Add(delay); return Task.CompletedTask; });

        result.AccessToken.Should().Be("enabled-token");
        service.Attempts.Should().Be(9);
        delays.Select(delay => delay.TotalSeconds).Should().Equal(1, 2, 4, 8, 16, 30, 30, 30);
        logs.Should().HaveCount(8);
    }

    [Fact]
    public async Task ShutdownDuringDisabledBackoff_DoesNotRequestAnotherToken()
    {
        var service = new DelegateTokenService(_ => throw Disabled());
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stopping = new CancellationTokenSource();
        var acquisition = DisabledAgentTokenRetry.GetAccessTokenAsync(service, _ => { }, stopping.Token,
            (_, token) => { waiting.TrySetResult(); return release.Task.WaitAsync(token); });
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        stopping.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => acquisition.WaitAsync(TimeSpan.FromSeconds(5)));
        service.Attempts.Should().Be(1);
    }

    [Theory]
    [InlineData(403, "agent_not_found", true)]
    [InlineData(401, null, true)]
    [InlineData(403, "policy_denied", false)]
    [InlineData(403, null, false)]
    [InlineData(403, "agent_disabled", true)]
    public async Task OtherAuthenticationFailures_RemainTerminal(int status, string? code, bool clear)
    {
        var failure = new AgentClientAuthException("Agent disabled.", status, clear, code);
        var service = new DelegateTokenService(_ => throw failure);
        var waits = 0;
        var acquisition = DisabledAgentTokenRetry.GetAccessTokenAsync(service, _ => { }, CancellationToken.None,
            (_, _) => { waits++; return Task.CompletedTask; });

        var observed = await Assert.ThrowsAsync<AgentClientAuthException>(() => acquisition);
        observed.Should().BeSameAs(failure);
        service.Attempts.Should().Be(1);
        waits.Should().Be(0);
    }

    private static AgentClientAuthException Disabled() => new("Agent disabled.", 403, code: "agent_disabled");

    private sealed class DelegateTokenService(Func<int, (string, DateTimeOffset)> acquire) : IAgentTokenService
    {
        public int Attempts { get; private set; }
        public Task<(string AccessToken, DateTimeOffset ExpiresAtUtc)> GetAccessTokenAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(acquire(++Attempts));
        }
    }
}
