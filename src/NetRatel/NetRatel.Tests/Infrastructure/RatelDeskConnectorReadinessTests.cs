using System.Security.Claims;
using AwesomeAssertions;
using Microsoft.Extensions.Options;
using NetRatel.Application.RatelDesk;
using NetRatel.Infrastructure.RatelDesk;
using NetRatel.Infrastructure.ServiceIdentity;
using NetRatel.Infrastructure.ServiceLinks;
using NetRatel.Shared.Contracts.RatelDesk;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

public sealed class RatelDeskConnectorReadinessTests
{
    [Fact]
    public async Task A_different_canonical_receiver_identity_has_a_specific_safe_result_without_authorizing_delivery()
    {
        var fixture = new Fixture(RatelDeskAuthenticationMode.ManagedServiceLink);
        var before = fixture.Store.Current;
        fixture.Bindings.OnCapture = (_, _) => Task.FromResult(before.Readiness!.Peer);
        fixture.Bindings.Bearer = "synthetic-receiver-bearer";
        fixture.Transport.OnCapabilities = peer => ReceiverWireValidation.Capability(
            RatelDeskReceiverFixture.Capability(peer with { ReceiverInstanceId = "99999999-9999-4999-8999-999999999999" }),
            peer, RatelDeskReceiverFixture.Now);

        var result = await fixture.Receiver.TestAsync(before, default);

        result.Should().Be(new RatelDeskConnectionTestResult(RatelDeskConnectionTestStatus.Unavailable,
            "receiver-identity-mismatch"));
        fixture.Store.Current.Readiness.Should().BeNull();
        fixture.Store.Current.Revision.Should().Be(before.Revision);
        fixture.Store.Current.CredentialRevision.Should().Be(before.CredentialRevision);
        fixture.Store.Current.Authentication.Should().Be(before.Authentication);
        fixture.Store.Current.Configuration.Should().Be(before.Configuration);
        fixture.Transport.Calls.Should().Be(1);
    }

    [Theory]
    [InlineData("invalid-json")]
    [InlineData("incomplete-response")]
    public async Task A_failed_receiver_response_returns_a_safe_test_result_without_retaining_previous_readiness(string failure)
    {
        var fixture = new Fixture();
        var before = fixture.Store.Current;
        fixture.Bindings.OnCapture = (_, _) => Task.FromResult(before.Readiness!.Peer);
        fixture.Bindings.Bearer = "synthetic-receiver-bearer";
        fixture.Transport.OnCapabilities = peer => failure == "invalid-json"
            ? ReceiverWireValidation.Capability("invalid receiver JSON"u8.ToArray(), peer, RatelDeskReceiverFixture.Now)
            : throw new IOException("synthetic private response detail");

        var result = await fixture.Receiver.TestAsync(before, default);

        result.Should().Be(new RatelDeskConnectionTestResult(RatelDeskConnectionTestStatus.Unavailable,
            "receiver-readiness-unverified"));
        fixture.Store.Current.Readiness.Should().BeNull();
        fixture.Store.Current.Revision.Should().Be(before.Revision);
        fixture.Store.Current.CredentialRevision.Should().Be(before.CredentialRevision);
        fixture.Store.Current.Authentication.Should().Be(before.Authentication);
        fixture.Store.Current.Configuration.Should().Be(before.Configuration);
        fixture.Transport.Calls.Should().Be(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_canceled_fresh_probe_cannot_reveal_the_previous_success(bool callerCancellation)
    {
        var fixture = new Fixture();
        var before = fixture.Store.Current;
        (await fixture.Readiness.CurrentAsync(before, default)).Available.Should().BeTrue();
        using var caller = new CancellationTokenSource();
        fixture.Bindings.OnCapture = (working, _) =>
        {
            fixture.Store.Current.Readiness.Should().BeNull();
            working.Readiness.Should().BeNull();
            working.RowVersion.Should().Be(before.RowVersion + 1);
            if (callerCancellation) caller.Cancel();
            throw new OperationCanceledException(new CancellationToken(canceled: true));
        };
        if (callerCancellation)
        {
            Func<Task> operation = () => fixture.Receiver.TestAsync(before, caller.Token);
            await operation.Should().ThrowAsync<OperationCanceledException>();
        }
        else
        {
            var result = await fixture.Receiver.TestAsync(before, caller.Token);
            result.Status.Should().Be(RatelDeskConnectionTestStatus.Unavailable);
            result.Code.Should().Be("receiver-readiness-timeout");
            result.AutomaticDeliveryAvailable.Should().BeFalse();
        }
        var after = fixture.Store.Current;
        after.Readiness.Should().BeNull();
        after.Revision.Should().Be(before.Revision);
        after.CredentialRevision.Should().Be(before.CredentialRevision);
        after.ProtectedCredential.Should().Be(before.ProtectedCredential);
        after.Authentication.Should().Be(before.Authentication);
        after.Configuration.Should().Be(before.Configuration);
        (await fixture.Readiness.CurrentAsync(after, default)).Should().Be((false, "receiver-readiness-required"));
        fixture.Transport.Calls.Should().Be(0);
    }

    [Fact]
    public async Task A_concurrent_success_is_preserved_when_old_readiness_invalidation_loses_its_row_CAS()
    {
        var fixture = new Fixture();
        var before = fixture.Store.Current;
        var previous = before.Readiness!;
        var newer = previous with { Capability = previous.Capability with { } };
        fixture.Store.BeforeClear = () => fixture.Store.Current = before with
        {
            RowVersion = before.RowVersion + 1,
            Readiness = newer
        };
        var result = await fixture.Receiver.TestAsync(before, default);
        result.Status.Should().Be(RatelDeskConnectionTestStatus.Unavailable);
        result.Code.Should().Be("connector-changed-during-readiness");
        result.AutomaticDeliveryAvailable.Should().BeFalse();
        fixture.Store.Current.Readiness.Should().BeSameAs(newer);
        fixture.Store.Current.RowVersion.Should().Be(before.RowVersion + 1);
        fixture.Bindings.CaptureCalls.Should().Be(0);
        fixture.Transport.Calls.Should().Be(0);
    }

    [Theory]
    [InlineData("peer")]
    [InlineData("capability")]
    [InlineData("endpoints")]
    [InlineData("categories")]
    public async Task Malformed_cached_observation_shape_is_unavailable_before_binding_or_profile_reads(string field)
    {
        var fixture = new Fixture();
        var observation = fixture.Store.Current.Readiness!;
        observation = field switch
        {
            "peer" => observation with { Peer = null! },
            "capability" => observation with { Capability = null! },
            "endpoints" => observation with { Capability = observation.Capability with { Endpoints = null! } },
            "categories" => observation with { Peer = observation.Peer with { CategoryIds = null! } },
            _ => throw new ArgumentException("unexpected-test-field")
        };
        var result = await fixture.Readiness.CurrentAsync(fixture.Store.Current with { Readiness = observation }, default);
        result.Should().Be((false, "receiver-readiness-required"));
        fixture.Store.AuthenticationReads.Should().Be(0);
        fixture.Continuity.Calls.Should().Be(0);
        fixture.Transport.Calls.Should().Be(0);
    }

    [Fact]
    public async Task An_expired_timestamp_remains_historical_evidence_without_authorizing_delivery()
    {
        var fixture = new Fixture();
        var observed = fixture.Store.Current.Readiness!;
        var expired = observed.TargetValidatedAtUtc - RatelDeskConnectorReadiness.MaximumObservationAge - TimeSpan.FromSeconds(1);
        var historical = observed with
        {
            TargetValidatedAtUtc = expired,
            Capability = observed.Capability with { ObservedAtUtc = expired }
        };
        var result = await fixture.Readiness.CurrentAsync(fixture.Store.Current with { Readiness = historical }, default);
        result.Should().Be((false, "receiver-readiness-expired"));
        historical.TargetValidatedAtUtc.Should().Be(expired);
        fixture.Store.AuthenticationReads.Should().Be(0);
        fixture.Transport.Calls.Should().Be(0);
    }

    private sealed class Fixture
    {
        internal Store Store { get; }
        internal Binding Bindings { get; } = new();
        internal Continuity Continuity { get; } = new();
        internal Transport Transport { get; } = new();
        internal RatelDeskConnectorReadiness Readiness { get; }
        internal RatelDeskConnectorReceiver Receiver { get; }
        internal Fixture(RatelDeskAuthenticationMode mode = RatelDeskAuthenticationMode.ManualApiBearer)
        {
            var peer = RatelDeskReceiverFixture.Peer(mode);
            var capability = ReceiverWireValidation.Capability(RatelDeskReceiverFixture.Capability(peer), peer, RatelDeskReceiverFixture.Now);
            Store = new(new(peer.ConnectorId, peer.LocalTenantId, 1, 7, "human-owner",
                new("receiver", peer.ApiBaseUrl, peer.OrganizationId, peer.CustomerId, peer.AssignedToId,
                    Array.Empty<Guid>(), new(), true), "synthetic-protected-credential", 2,
                new(mode, peer.LinkId), new(1, peer, capability, RatelDeskReceiverFixture.Now)));
            var network = new RatelDeskReceiverNetworkPolicy(new Options<RatelDeskReceiverOptions>(new()),
                new Options<ServiceLinkOptions>(new()), new Options<ServiceIdentityOptions>(new()));
            var clock = new Clock();
            // Manual readiness does not use the managed profile service. Null makes accidental access fail this test.
            Readiness = new(new Authority(), Store, Continuity, null!, network, clock);
            Receiver = new(Store, Store, Store, Readiness, Bindings, Transport, new Source(), network, clock);
        }
    }

    private sealed class Store(RatelDeskConnectorState initial) : IRatelDeskConnectorStore,
        IRatelDeskConnectorBindingStore, IRatelDeskConnectorReadinessStore
    {
        internal RatelDeskConnectorState Current { get; set; } = initial;
        internal Action? BeforeClear { get; set; }
        internal int AuthenticationReads { get; private set; }
        public Task<RatelDeskConnectorState?> GetAsync(int tenantId, Guid id, CancellationToken ct) =>
            Task.FromResult<RatelDeskConnectorState?>(Current.TenantId == tenantId && Current.Id == id ? Current : null);
        public Task<IReadOnlyList<RatelDeskConnectorState>> ListAsync(int tenantId, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> SaveAsync(RatelDeskConnectorState state, long expectedRowVersion, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (Current.RowVersion != expectedRowVersion || Current.TenantId != state.TenantId || Current.Id != state.Id)
                return Task.FromResult(false);
            Current = state;
            return Task.FromResult(true);
        }
        public Task<RatelDeskConnectorAuthentication> GetAuthenticationAsync(int tenantId, Guid connectorId, CancellationToken ct)
        {
            AuthenticationReads++;
            return Task.FromResult(Current.Authentication!);
        }
        public Task<bool> SaveReadinessAsync(RatelDeskConnectorState current, RatelDeskReadinessObservation observation, CancellationToken ct) =>
            SaveAsync(current with { RowVersion = current.RowVersion + 1, Readiness = observation }, current.RowVersion, ct);
        public Task<bool> ClearReadinessAsync(RatelDeskConnectorState current, CancellationToken ct)
        {
            BeforeClear?.Invoke();
            return SaveAsync(current with { RowVersion = current.RowVersion + 1, Readiness = null }, current.RowVersion, ct);
        }
    }

    private sealed class Binding : IRatelDeskOutboundBindingResolver
    {
        internal int CaptureCalls { get; private set; }
        internal string? Bearer { get; set; }
        internal Func<RatelDeskConnectorState, CancellationToken, Task<RatelDeskSemanticPeer>>? OnCapture { get; set; }
        public Task<RatelDeskSemanticPeer> CaptureAsync(RatelDeskConnectorState connector,
            RatelDeskConnectorAuthentication authentication, Guid source, CancellationToken ct)
        {
            CaptureCalls++;
            return OnCapture?.Invoke(connector, ct) ?? throw new InvalidOperationException("unexpected-capture");
        }
        public Task<string> GetBearerAsync(RatelDeskConnectorState current, RatelDeskSemanticPeer captured,
            string scope, CancellationToken ct) => Task.FromResult(Bearer ?? throw new InvalidOperationException("unexpected-token-read"));
    }
    private sealed class Transport : IRatelDeskReceiverTransport
    {
        internal int Calls { get; private set; }
        internal Func<RatelDeskSemanticPeer, RatelDeskVerifiedCapability>? OnCapabilities { get; set; }
        public Task<RatelDeskVerifiedCapability> CapabilitiesAsync(RatelDeskSemanticPeer peer, string bearer, CancellationToken ct)
        { Calls++; return Task.FromResult(OnCapabilities?.Invoke(peer) ?? throw new InvalidOperationException("unexpected-capability-HTTP")); }
        public Task ValidateTargetsAsync(RatelDeskSemanticPeer peer, RatelDeskVerifiedCapability capability, string bearer, CancellationToken ct)
        { Calls++; throw new InvalidOperationException("unexpected-target-HTTP"); }
        public Task<RatelDeskReceiverObservation> LookupAsync(RatelDeskReceiverPreparationV2 prepared, string bearer, CancellationToken ct)
        { Calls++; throw new InvalidOperationException("unexpected-receipt-HTTP"); }
        public Task<RatelDeskReceiverObservation> CreateAsync(RatelDeskReceiverPreparationV2 prepared, string bearer, CancellationToken ct)
        { Calls++; throw new InvalidOperationException("unexpected-create-HTTP"); }
    }
    private sealed class Source : IFlowSourceIdentityResolver
    {
        public Task<Guid> EnsureAsync(CancellationToken ct) => Task.FromResult(RatelDeskReceiverFixture.Peer().SourceInstanceId);
    }
    private sealed class Continuity : IRatelDeskProducerContinuity
    {
        internal int Calls { get; private set; }
        public Task RequireCurrentAsync(Guid expectedFlowSource, CancellationToken ct) { Calls++; return Task.CompletedTask; }
    }
    private sealed class Authority : IRatelDeskConnectorAuthorization
    {
        public Task<bool> CanManageAsync(ClaimsPrincipal principal, int tenantId, CancellationToken ct) => Task.FromResult(true);
        public Task<bool> CanExecuteAsync(string principalId, string? integrationCredentialId, int tenantId, CancellationToken ct) => Task.FromResult(true);
    }
    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => RatelDeskReceiverFixture.Now;
    }
    private sealed class Options<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
