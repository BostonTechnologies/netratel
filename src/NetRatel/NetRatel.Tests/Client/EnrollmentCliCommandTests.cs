using FluentAssertions;
using NetRatel.Application.ClientAuth;
using NetRatel.Client.Service.Auth;
using Xunit;

namespace NetRatel.Tests.Client;

public sealed class EnrollmentCliCommandTests
{
    [Fact]
    public async Task TryExecuteAsync_WhenAlreadyEnrolled_ReturnsZero()
    {
        var cmd = new EnrollmentCliCommand();
        var enrollment = new FakeEnrollmentService();
        var store = new FakeCredentialStore();
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var result = await cmd.TryExecuteAsync(true, "ENR-X", ("a", "b"), enrollment, store, stdout, stderr, CancellationToken.None);

        result.Handled.Should().BeTrue();
        result.ExitCode.Should().Be(0);
        stdout.ToString().Should().Contain("Already enrolled");
    }

    [Fact]
    public async Task TryExecuteAsync_WithCode_EnrollsAndStoresCredential()
    {
        var cmd = new EnrollmentCliCommand();
        var enrollment = new FakeEnrollmentService();
        var store = new FakeCredentialStore();
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var result = await cmd.TryExecuteAsync(true, "ENR-OK", null, enrollment, store, stdout, stderr, CancellationToken.None);

        result.ExitCode.Should().Be(0);
        enrollment.LastCode.Should().Be("ENR-OK");
        store.Saved.Should().Be(("agent-1", "refresh-1"));
    }

    [Fact]
    public async Task TryExecuteAsync_OnFailure_ReturnsNonZero()
    {
        var cmd = new EnrollmentCliCommand();
        var enrollment = new FakeEnrollmentService { ThrowOnEnroll = true };
        var store = new FakeCredentialStore();
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        var result = await cmd.TryExecuteAsync(true, "ENR-BAD", null, enrollment, store, stdout, stderr, CancellationToken.None);

        result.ExitCode.Should().Be(1);
        stderr.ToString().Should().Contain("Enrollment failed");
    }

    private sealed class FakeEnrollmentService : IAgentEnrollmentService
    {
        public bool ThrowOnEnroll { get; set; }
        public string? LastCode { get; private set; }

        public Task<(string AgentId, string RefreshToken)> EnrollAsync(string enrollmentCode, CancellationToken ct)
        {
            if (ThrowOnEnroll)
            {
                throw new AgentClientAuthException("bad code");
            }

            LastCode = enrollmentCode;
            return Task.FromResult(("agent-1", "refresh-1"));
        }
    }

    private sealed class FakeCredentialStore : IAgentCredentialStore
    {
        public (string, string)? Saved { get; private set; }

        public Task SaveAsync(string agentId, string refreshToken)
        {
            Saved = (agentId, refreshToken);
            return Task.CompletedTask;
        }

        public Task<(string AgentId, string RefreshToken)?> LoadAsync()
            => Task.FromResult<(string AgentId, string RefreshToken)?>(null);

        public Task ClearRefreshCredentialsAsync() => Task.CompletedTask;

        public Task ResetInstallationIdentityAsync() => Task.CompletedTask;

        public Task ClearAsync() => ClearRefreshCredentialsAsync();
    }
}
