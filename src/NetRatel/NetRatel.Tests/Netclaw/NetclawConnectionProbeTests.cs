using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Netclaw;
using NetRatel.Infrastructure.Netclaw;
using NetRatel.Infrastructure.Persistence;
using Xunit;

namespace NetRatel.Tests.Netclaw;

public sealed class NetclawConnectionProbeTests
{
    private const string ProtectedSentinel = "synthetic-probe-credential-must-not-leak";

    [Theory]
    [InlineData("dns", "daemon-name-resolution-failed", "DNS from the API host")]
    [InlineData("refused", "daemon-connection-refused", "Start its listener")]
    [InlineData("unreachable", "daemon-network-unreachable", "routing, firewall")]
    [InlineData("unauthorized", "daemon-authentication-rejected", "device is still authorized")]
    [InlineData("forbidden", "daemon-authentication-rejected", "device is still authorized")]
    [InlineData("tls", "daemon-tls-verification-failed", "trusted chain")]
    [InlineData("missing-hub", "daemon-session-endpoint-unavailable", "/hub/session")]
    [InlineData("endpoint-policy", "daemon-endpoint-rejected", "outbound endpoint validation")]
    [InlineData("session-protocol", "daemon-session-protocol-rejected", "supported SignalR session protocol")]
    [InlineData("timeout", "session-test-timeout", "timed out")]
    [InlineData("upstream", "daemon-http-request-failed", "reverse proxy")]
    [InlineData("unknown", "authenticated-session-unavailable", "could not be verified")]
    public async Task Failed_authenticated_probe_preserves_pairing_and_returns_sanitized_actionable_diagnosis(
        string scenario, string expectedCode, string expectedRemediation)
    {
        var protection = new EphemeralDataProtectionProvider();
        await using var db = Database();
        var saved = await SeedAsync(db, protection);
        var daemon = new ProbeDaemon(_ => Task.FromException<SessionEnsureResult>(Failure(scenario)));
        var profiles = new NetclawProfileService(db, protection, new UnusedPairing(), daemon, TimeProvider.System);

        var result = await profiles.TestAsync(17, saved.Revision);

        Assert.False(result.Success);
        Assert.Equal(expectedCode, result.Code);
        Assert.Contains(expectedRemediation, result.Remediation!);
        Assert.DoesNotContain(ProtectedSentinel, JsonSerializer.Serialize(result));
        Assert.Equal(1, daemon.Connections);
        Assert.Equal(1, daemon.Disposals);
        var persisted = await db.NetclawConnections.AsNoTracking().SingleAsync(x => x.TenantId == 17);
        Assert.Equal(saved.Revision, persisted.Revision);
        Assert.Equal(saved.ProtectedDeviceToken, persisted.ProtectedDeviceToken);
        Assert.Equal(expectedCode, persisted.LastTestCode);
        Assert.False(persisted.LastTestSucceeded);
        Assert.NotNull(persisted.LastTestedAtUtc);
        Assert.Equal(result.Remediation, (await profiles.GetAsync(17)).LastTestRemediation);
        Assert.Null((await profiles.GetAsync(18)).LastTestCode);
    }

    [Fact]
    public async Task Lost_keyring_reports_credential_recovery_without_pairing_or_network_dispatch()
    {
        await using var db = Database();
        var saved = await SeedAsync(db, new EphemeralDataProtectionProvider());
        var daemon = new ProbeDaemon(_ => Task.FromResult(new SessionEnsureResult("unexpected", true)));
        var profiles = new NetclawProfileService(db, new EphemeralDataProtectionProvider(), new UnusedPairing(), daemon, TimeProvider.System);

        var result = await profiles.TestAsync(17, saved.Revision);

        Assert.False(result.Success);
        Assert.Equal("device-token-unavailable", result.Code);
        Assert.Contains("original Data Protection keyring", result.Remediation!);
        Assert.True(result.Connection.HasDeviceToken);
        Assert.Equal(0, daemon.Connections);
        Assert.Equal(saved.ProtectedDeviceToken, (await db.NetclawConnections.AsNoTracking().SingleAsync()).ProtectedDeviceToken);
        Assert.DoesNotContain(ProtectedSentinel, JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task Caller_cancellation_does_not_record_a_daemon_failure_or_change_the_saved_profile()
    {
        var protection = new EphemeralDataProtectionProvider();
        await using var db = Database();
        var saved = await SeedAsync(db, protection);
        using var cancellation = new CancellationTokenSource();
        var daemon = new ProbeDaemon(ct =>
        {
            cancellation.Cancel();
            return Task.FromCanceled<SessionEnsureResult>(ct);
        });
        var profiles = new NetclawProfileService(db, protection, new UnusedPairing(), daemon, TimeProvider.System);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => profiles.TestAsync(17, saved.Revision, cancellation.Token));

        var persisted = await db.NetclawConnections.AsNoTracking().SingleAsync();
        Assert.Null(persisted.LastTestCode);
        Assert.Null(persisted.LastTestedAtUtc);
        Assert.Equal(saved.Revision, persisted.Revision);
        Assert.Equal(1, daemon.Disposals);
    }

    [Fact]
    public async Task A_profile_change_during_a_failed_probe_does_not_attach_stale_diagnostics_to_the_successor()
    {
        var protection = new EphemeralDataProtectionProvider();
        var options = new DbContextOptionsBuilder<OrchestratorDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options;
        await using var db = new OrchestratorDbContext(options);
        var saved = await SeedAsync(db, protection);
        var daemon = new ProbeDaemon(async _ =>
        {
            await using var concurrent = new OrchestratorDbContext(options);
            var changed = await concurrent.NetclawConnections.SingleAsync();
            changed.Revision++;
            changed.Enabled = false;
            await concurrent.SaveChangesAsync();
            throw new HttpRequestException(ProtectedSentinel, null, HttpStatusCode.Unauthorized);
        });
        var profiles = new NetclawProfileService(db, protection, new UnusedPairing(), daemon, TimeProvider.System);

        await Assert.ThrowsAsync<InvestigationConflictException>(() => profiles.TestAsync(17, saved.Revision));

        var persisted = await db.NetclawConnections.AsNoTracking().SingleAsync();
        Assert.Equal(saved.Revision + 1, persisted.Revision);
        Assert.False(persisted.Enabled);
        Assert.Null(persisted.LastTestCode);
        Assert.Null(persisted.LastTestedAtUtc);
    }

    [Theory]
    [InlineData("authenticated-session", true, "authenticated-signalr-verified")]
    [InlineData("", false, "session-not-confirmed")]
    public async Task Only_a_confirmed_session_is_a_success(string sessionId, bool success, string code)
    {
        var protection = new EphemeralDataProtectionProvider();
        await using var db = Database();
        await SeedAsync(db, protection);
        var daemon = new ProbeDaemon(_ => Task.FromResult(new SessionEnsureResult(sessionId, true)));
        var result = await new NetclawProfileService(db, protection, new UnusedPairing(), daemon, TimeProvider.System).TestAsync(17);
        Assert.Equal(success, result.Success);
        Assert.Equal(code, result.Code);
        Assert.Equal(success, result.Remediation is null);
    }

    [Fact]
    public async Task A_transport_failure_during_probe_disposal_cannot_keep_a_success_result()
    {
        var protection = new EphemeralDataProtectionProvider();
        await using var db = Database();
        await SeedAsync(db, protection);
        var daemon = new ProbeDaemon(_ => Task.FromResult(new SessionEnsureResult("confirmed", true)))
        {
            DisposeFailure = new HttpRequestException(ProtectedSentinel, null, HttpStatusCode.ServiceUnavailable)
        };

        var result = await new NetclawProfileService(db, protection, new UnusedPairing(), daemon, TimeProvider.System).TestAsync(17);

        Assert.False(result.Success);
        Assert.False(result.Connection.LastTestSucceeded);
        Assert.Equal("daemon-http-request-failed", result.Code);
        Assert.DoesNotContain(ProtectedSentinel, JsonSerializer.Serialize(result));
    }

    private static Exception Failure(string scenario) => scenario switch
    {
        "dns" => new AggregateException(new HttpRequestException(ProtectedSentinel, new SocketException((int)SocketError.HostNotFound))),
        "refused" => new HttpRequestException(HttpRequestError.ConnectionError, ProtectedSentinel, new SocketException((int)SocketError.ConnectionRefused)),
        "unreachable" => new HttpRequestException(ProtectedSentinel, new SocketException((int)SocketError.NetworkUnreachable)),
        "unauthorized" => new HttpRequestException(ProtectedSentinel, null, HttpStatusCode.Unauthorized),
        "forbidden" => new HttpRequestException(ProtectedSentinel, null, HttpStatusCode.Forbidden),
        "tls" => new HttpRequestException(ProtectedSentinel, new AuthenticationException(ProtectedSentinel)),
        "missing-hub" => new HttpRequestException(ProtectedSentinel, null, HttpStatusCode.NotFound),
        "endpoint-policy" => new ArgumentException(ProtectedSentinel),
        "session-protocol" => new HubException(ProtectedSentinel),
        "timeout" => new OperationCanceledException(ProtectedSentinel),
        "upstream" => new HttpRequestException(ProtectedSentinel, null, HttpStatusCode.ServiceUnavailable),
        _ => new InvalidOperationException(ProtectedSentinel)
    };

    private static OrchestratorDbContext Database() => new(new DbContextOptionsBuilder<OrchestratorDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);

    private static async Task<NetclawConnectionRecord> SeedAsync(OrchestratorDbContext db, IDataProtectionProvider protection)
    {
        var row = new NetclawConnectionRecord
        {
            TenantId = 17, Revision = 3, Enabled = true, Endpoint = "https://daemon.example/hub/session",
            OwnerPrincipalId = "test-operator", ProfileFingerprint = NetclawProfileService.Fingerprint(17, "https://daemon.example/hub/session")
        };
        row.ProtectedDeviceToken = protection.CreateProtector("NetRatel.Netclaw.DeviceToken.v1",
            row.TenantId.ToString(CultureInfo.InvariantCulture), row.Id.ToString("N"), row.ProfileFingerprint).Protect(ProtectedSentinel);
        db.NetclawConnections.Add(row);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return row;
    }

    private sealed class UnusedPairing : INetclawPairingService
    {
        public Task<string> ExchangeCodeAsync(NetclawPairingTarget target, string pairingCode, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("A connection test must not exchange another pairing code.");
    }

    private sealed class ProbeDaemon(Func<CancellationToken, Task<SessionEnsureResult>> connect) : IAiAssistantChatClientFactory
    {
        public int Connections { get; private set; }
        public int Disposals { get; private set; }
        public Exception? DisposeFailure { get; init; }
        public IAiAssistantChatClient Create(NetclawRuntimeProfile profile)
        {
            Assert.Equal(17, profile.TenantId);
            Assert.Equal(ProtectedSentinel, profile.DeviceToken);
            return new Client(this, connect);
        }

        private sealed class Client(ProbeDaemon owner, Func<CancellationToken, Task<SessionEnsureResult>> connect) : IAiAssistantChatClient
        {
            public bool IsConnected => false;
            public Task<SessionEnsureResult> ConnectAsync(string? sessionId, Func<JsonElement, Task> output, CancellationToken ct)
            {
                Assert.Null(sessionId);
                owner.Connections++;
                return connect(ct);
            }
            public Task SendAsync(string sessionId, string text, CancellationToken ct) => throw new InvalidOperationException("A probe must not send a message.");
            public Task RespondAsync(string sessionId, string callId, string key, CancellationToken ct) => throw new InvalidOperationException("A probe must not answer an approval.");
            public ValueTask DisposeAsync()
            {
                owner.Disposals++;
                return owner.DisposeFailure is { } failure ? ValueTask.FromException(failure) : ValueTask.CompletedTask;
            }
        }
    }
}
