using System.Threading.Channels;
using FluentAssertions;
using Google.Protobuf;
using Microsoft.Extensions.Logging.Abstractions;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.API.Gateway;
using NetRatel.API.Services;
using NetRatel.Application.Presence;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class GatewayTerminalBrowserAttachmentLeaseRegistryTests
{
    [Fact]
    public async Task ExpiredAttachment_QueuesExactClose_AndRetiresOnlyAfterTheRemoteSessionIsFinal()
    {
        var clock = new ManualTimeProvider();
        var terminals = new RecordingTerminalRegistry(CreateSession());
        var attachments = CreateAttachments(clock);
        attachments.Track(terminals.Session!);
        clock.Advance(GatewayTerminalBrowserAttachmentLeaseRegistry.ReattachGracePeriod);

        (await attachments.CloseDueAsync(terminals, CancellationToken.None)).Should().Be(0);
        terminals.CloseRequests.Should().ContainSingle().Which.Should().Be((
            terminals.Session!.SessionId,
            terminals.Session.Generation,
            "terminal_browser_attachment_expired"));

        terminals.MarkFinal();
        (await attachments.CloseDueAsync(terminals, CancellationToken.None)).Should().Be(1);
        (await attachments.CloseDueAsync(terminals, CancellationToken.None)).Should().Be(0);
        terminals.CloseRequests.Should().ContainSingle("a remote acknowledgement is required before lease retirement");
    }

    [Fact]
    public async Task RenewedAttachment_SurvivesTheOriginalDeadline_ThenExpiresAtTheRenewedDeadline()
    {
        var clock = new ManualTimeProvider();
        var terminals = new RecordingTerminalRegistry(CreateSession());
        var attachments = CreateAttachments(clock);
        attachments.Track(terminals.Session!);

        clock.Advance(TimeSpan.FromSeconds(90));
        Renew(attachments, terminals.Session!)
            .Outcome.Should().Be(GatewayTerminalBrowserAttachmentRenewalOutcome.Renewed);
        clock.Advance(TimeSpan.FromSeconds(91));

        (await attachments.CloseDueAsync(terminals, CancellationToken.None)).Should().Be(0);
        terminals.CloseRequests.Should().BeEmpty("browser lifecycle activity renewed the attachment lease");

        clock.Advance(TimeSpan.FromSeconds(30));
        await attachments.CloseDueAsync(terminals, CancellationToken.None);
        terminals.CloseRequests.Should().ContainSingle();
    }

    [Fact]
    public async Task ExplicitCloseLease_RetriesAfterTransportReconnectInsteadOfForgettingThePty()
    {
        var clock = new ManualTimeProvider();
        var terminals = new RecordingTerminalRegistry(CreateSession(), closeFailures: 1);
        var attachments = CreateAttachments(clock);
        attachments.Track(terminals.Session!);
        attachments.MarkClosePending(terminals.Session!);

        await attachments.CloseDueAsync(terminals, CancellationToken.None);
        await attachments.CloseDueAsync(terminals, CancellationToken.None);

        terminals.CloseRequests.Should().HaveCount(2);
        terminals.CloseRequests.Should().OnlyContain(request => request.Reason == "terminal_browser_attachment_closed");
    }

    [Fact]
    public async Task RenewAtTheExpiryBoundary_WinsOverAnAlreadyEnumeratedSweep()
    {
        var clock = new ManualTimeProvider();
        var terminals = new RecordingTerminalRegistry(CreateSession());
        var attachments = CreateAttachments(clock);
        attachments.Track(terminals.Session!);
        clock.Advance(GatewayTerminalBrowserAttachmentLeaseRegistry.ReattachGracePeriod);
        terminals.BeforeGet = () => Renew(attachments, terminals.Session!);

        await attachments.CloseDueAsync(terminals, CancellationToken.None);

        terminals.CloseRequests.Should().BeEmpty("the current attachment lease was renewed after the sweep snapshot");
    }

    [Fact]
    public async Task StaleGenerationHeartbeat_CannotExtendAReplacementAttachment()
    {
        var clock = new ManualTimeProvider();
        var original = CreateSession();
        var replacement = original with { Generation = original.Generation + 1 };
        var terminals = new RecordingTerminalRegistry(original);
        var attachments = CreateAttachments(clock);
        attachments.Track(original);

        terminals.Replace(replacement);
        attachments.Track(replacement);

        attachments.TryRenew(
                replacement,
                original.Generation,
                attachments.GetAttachmentLeaseId(replacement)!,
                "browser-a",
                claimOwnership: true)
            .Outcome.Should().Be(GatewayTerminalBrowserAttachmentRenewalOutcome.StaleGeneration);

        clock.Advance(GatewayTerminalBrowserAttachmentLeaseRegistry.ReattachGracePeriod);
        await attachments.CloseDueAsync(terminals, CancellationToken.None);

        terminals.CloseRequests.Should().ContainSingle().Which.Should().Be((
            replacement.SessionId,
            replacement.Generation,
            "terminal_browser_attachment_expired"));
    }

    [Fact]
    public async Task ClosePendingOrFinalAttachment_RejectsHeartbeatAndKeepsItsCloseLifecycle()
    {
        var clock = new ManualTimeProvider();
        var terminals = new RecordingTerminalRegistry(CreateSession());
        var attachments = CreateAttachments(clock);
        attachments.Track(terminals.Session!);
        attachments.MarkClosePending(terminals.Session!);

        Renew(attachments, terminals.Session!)
            .Outcome.Should().Be(GatewayTerminalBrowserAttachmentRenewalOutcome.ClosePending);

        await attachments.CloseDueAsync(terminals, CancellationToken.None);
        terminals.CloseRequests.Should().ContainSingle().Which.Reason
            .Should().Be("terminal_browser_attachment_closed");

        terminals.MarkFinal();
        Renew(attachments, terminals.Session!)
            .Outcome.Should().Be(GatewayTerminalBrowserAttachmentRenewalOutcome.Final);
    }

    [Fact]
    public async Task ClaimedAttachment_RotatesTheLeaseFence_AndRejectsAClonedTabsCopiedHeartbeat()
    {
        var clock = new ManualTimeProvider();
        var terminals = new RecordingTerminalRegistry(CreateSession());
        var attachments = CreateAttachments(clock);
        attachments.Track(terminals.Session!);
        var copiedLeaseId = attachments.GetAttachmentLeaseId(terminals.Session!)!;

        var original = attachments.TryRenew(
            terminals.Session!,
            terminals.Session!.Generation,
            copiedLeaseId,
            "browser-original",
            claimOwnership: true);
        original.Outcome.Should().Be(GatewayTerminalBrowserAttachmentRenewalOutcome.Renewed);

        var clone = attachments.TryRenew(
            terminals.Session!,
            terminals.Session!.Generation,
            copiedLeaseId,
            "browser-clone",
            claimOwnership: true);
        clone.Outcome.Should().Be(GatewayTerminalBrowserAttachmentRenewalOutcome.Renewed);
        clone.AttachmentLeaseId.Should().NotBe(copiedLeaseId);

        attachments.TryRenew(
                terminals.Session!,
                terminals.Session!.Generation,
                copiedLeaseId,
                "browser-clone",
                claimOwnership: false)
            .Outcome.Should().Be(GatewayTerminalBrowserAttachmentRenewalOutcome.Renewed,
                "the newly claimed runtime may retry after a lost rotation response");

        attachments.TryRenew(
                terminals.Session!,
                terminals.Session!.Generation,
                copiedLeaseId,
                "browser-original",
                claimOwnership: false)
            .Outcome.Should().Be(GatewayTerminalBrowserAttachmentRenewalOutcome.StaleAttachment);

        clock.Advance(TimeSpan.FromSeconds(90));
        attachments.TryRenew(
                terminals.Session!,
                terminals.Session!.Generation,
                clone.AttachmentLeaseId!,
                "browser-clone",
                claimOwnership: false)
            .Outcome.Should().Be(GatewayTerminalBrowserAttachmentRenewalOutcome.Renewed);
        clock.Advance(TimeSpan.FromSeconds(91));
        await attachments.CloseDueAsync(terminals, CancellationToken.None);
        terminals.CloseRequests.Should().BeEmpty("only the atomically claimed browser attachment may extend the lease");
    }

    private static GatewayTerminalBrowserAttachmentRenewalResult Renew(
        GatewayTerminalBrowserAttachmentLeaseRegistry attachments,
        GatewayTerminalSession session,
        string browserAttachmentId = "browser-a",
        bool claimOwnership = true) =>
        attachments.TryRenew(
            session,
            session.Generation,
            attachments.GetAttachmentLeaseId(session)!,
            browserAttachmentId,
            claimOwnership);

    private static GatewayTerminalBrowserAttachmentLeaseRegistry CreateAttachments(TimeProvider clock) =>
        new(clock, NullLogger<GatewayTerminalBrowserAttachmentLeaseRegistry>.Instance);

    private static GatewayTerminalSession CreateSession() => new(
        SessionId: "browser-attachment-984",
        TenantId: 984,
        AgentId: Guid.NewGuid(),
        Generation: 7,
        ShellType: "bash",
        Columns: 120,
        Rows: 32,
        State: "opened",
        CreatedAtUtc: DateTimeOffset.UtcNow,
        Authority: "test");

    private sealed class RecordingTerminalRegistry(GatewayTerminalSession session, int closeFailures = 0) : IAgentTerminalSessionRegistry
    {
        private int _remainingCloseFailures = closeFailures;

        public GatewayTerminalSession? Session { get; private set; } = session;
        public List<(string SessionId, ulong Generation, string Reason)> CloseRequests { get; } = [];
        public Action? BeforeGet { get; set; }

        public void MarkFinal() => Session = Session! with { State = "closed" };

        public void Replace(GatewayTerminalSession replacement) => Session = replacement;

        public Task CloseAsync(string sessionId, ulong generation, string reason, CancellationToken ct)
        {
            CloseRequests.Add((sessionId, generation, reason));
            if (Interlocked.Decrement(ref _remainingCloseFailures) >= 0)
            {
                return Task.FromException(new TerminalGatewayActionException(
                    "terminal_transport_reconnecting",
                    "The terminal transport is reconnecting."));
            }

            Session = Session! with { State = "closing" };
            return Task.CompletedTask;
        }

        public GatewayTerminalSession? Get(string sessionId)
        {
            BeforeGet?.Invoke();
            return string.Equals(Session?.SessionId, sessionId, StringComparison.Ordinal) ? Session : null;
        }

        public AgentTerminalGatewayRegistration Register(ClientKey client, Guid connectionId, ulong connectionEpoch, IReadOnlyList<string> availableShells, IReadOnlyList<string>? capabilities = null) =>
            throw new NotSupportedException();

        public GatewayTerminalAvailability? GetAvailability(ClientKey client) => throw new NotSupportedException();
        public Task<GatewayTerminalSession> OpenAsync(ClientKey client, string shellType, string? workingDirectory, int columns, int rows, CancellationToken ct) => throw new NotSupportedException();
        public Task SendInputAsync(string sessionId, ulong generation, ReadOnlyMemory<byte> content, CancellationToken ct) => throw new NotSupportedException();
        public Task ResizeAsync(string sessionId, ulong generation, int columns, int rows, CancellationToken ct) => throw new NotSupportedException();
        public GatewayTerminalOutputSubscription Subscribe(string sessionId, ulong generation) => new(Channel.CreateUnbounded<ReadOnlyMemory<byte>>().Reader);
        public bool TryReceiveOpened(ClientKey client, TerminalSessionOpened opened) => false;
        public Task<bool> TryReceiveOutputAsync(ClientKey client, TerminalOutput output, CancellationToken ct) => Task.FromResult(false);
        public bool TryReceiveResizeApplied(ClientKey client, TerminalResizeApplied resize) => false;
        public bool TryReceiveClosed(ClientKey client, TerminalSessionClosed closed) => false;
        public bool TryReceiveFailed(ClientKey client, TerminalSessionFailed failed) => false;
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 9, 4, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan duration) => _now = _now.Add(duration);
    }
}
