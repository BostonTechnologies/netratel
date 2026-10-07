using AwesomeAssertions;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using NetRatel.Application.RatelDesk;
using NetRatel.Infrastructure.RatelDesk;
using NetRatel.Infrastructure.ServiceIdentity;
using NetRatel.Infrastructure.ServiceLinks;
using NetRatel.Shared.Contracts.RatelDesk;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

public sealed class ManualRatelDeskBindingResolverTests
{
    [Fact]
    public async Task Authenticated_capture_keeps_the_Flow_GUID_and_exact_receiver_namespace_without_OAuth_fields()
    {
        var f = new Fixture();
        var peer = await f.Resolver.CaptureAsync(f.State, Fixture.Manual, Fixture.Source, default);
        (peer.SourceInstanceId).Should().Be(Fixture.Source);
        (peer.SourceNamespaceId).Should().Be(Fixture.Namespace);
        (peer.ReceiverInstanceId).Should().Be(Fixture.Receiver.ToString("D"));
        (peer.ClientId).Should().BeNull(); (peer.Issuer).Should().BeNull(); (peer.TokenEndpoint).Should().BeNull(); (peer.LinkId).Should().BeNull();
        (f.Probe.Calls).Should().Be(1); (f.Continuity.Calls).Should().Be(2);
    }

    [Fact]
    public async Task Secret_only_rotation_uses_latest_bearer_for_the_same_prepared_peer()
    {
        var f = new Fixture();
        var peer = await f.Resolver.CaptureAsync(f.State, Fixture.Manual, Fixture.Source, default);
        var capturedConnector = f.State;
        f.Store.Current = f.State with { CredentialRevision = 2, RowVersion = 2, ProtectedCredential = "second" };
        var bearer = await f.Resolver.GetBearerAsync(capturedConnector, peer, "rateldesk.incident-receipts.read", default);
        (bearer).Should().Be(Fixture.Bearer2);
        f.State.Revision.Should().Be(capturedConnector.Revision);
        (peer.SourceNamespaceId).Should().Be(Fixture.Namespace);
        (f.Probe.Calls).Should().Be(1); // GetBearer never invents a new target capture after rotation.
    }

    [Theory]
    [InlineData("tenant")]
    [InlineData("connector")]
    [InlineData("revision")]
    [InlineData("disabled")]
    [InlineData("owner")]
    [InlineData("mode")]
    [InlineData("source")]
    [InlineData("mapping")]
    [InlineData("api-base")]
    [InlineData("oauth-field")]
    public async Task Every_operation_rejects_current_authority_or_captured_target_drift(string mutation)
    {
        var f = new Fixture(); var peer = f.Peer; var original = f.State;
        switch (mutation)
        {
            case "tenant": peer = peer with { LocalTenantId = 99 }; break;
            case "connector": peer = peer with { ConnectorId = Guid.NewGuid() }; break;
            case "revision": f.Store.Current = f.State with { Revision = 2 }; break;
            case "disabled": f.Store.Current = f.State with { Configuration = f.State.Configuration with { Enabled = false } }; break;
            case "owner": f.Authorization.Allowed = false; break;
            case "mode": f.Bindings.Current = new(RatelDeskAuthenticationMode.ManagedServiceLink, "bound-link"); break;
            case "source": f.Continuity.Allowed = false; break;
            case "mapping": peer = peer with { CustomerId = "another-customer" }; break;
            case "api-base": peer = peer with { ApiBaseUrl = "https://other.example" }; break;
            case "oauth-field": peer = peer with { ClientId = "must-never-be-used-in-manual-mode" }; break;
        }
        await ((Func<Task>)(() => f.Resolver.GetBearerAsync(original, peer, "rateldesk.incidents.create", default))).Should().ThrowAsync<UnauthorizedAccessException>();
        (f.Protector.Reads).Should().Be(0);
    }

    [Theory]
    [InlineData("jwt")]
    [InlineData("absent")]
    [InlineData("unprotect")]
    public async Task Manual_mode_never_substitutes_a_managed_secret_or_other_credential(string failure)
    {
        var f = new Fixture();
        if (failure == "absent") f.Store.Current = f.State with { ProtectedCredential = null };
        if (failure == "jwt") f.Protector.First = "ey.not.an.api.bearer";
        if (failure == "unprotect") f.Protector.Fail = true;
        await ((Func<Task>)(() => f.Resolver.CaptureAsync(f.State, Fixture.Manual, Fixture.Source, default))).Should().ThrowAsync<UnauthorizedAccessException>();
        (f.Probe.Calls).Should().Be(0);
    }

    [Fact]
    public async Task Capture_rechecks_credential_after_remote_reads_and_rejects_mixed_revision_observations()
    {
        var f = new Fixture();
        f.Probe.OnCapture = () => f.Store.Current = f.State with { CredentialRevision = 2, ProtectedCredential = "second" };
        var error = (await ((Func<Task>)(() => f.Resolver.CaptureAsync(f.State, Fixture.Manual, Fixture.Source, default))).Should().ThrowAsync<InvalidOperationException>()).Which;
        (error.Message).Should().Be("manual-credential-changed-during-capture");
    }

    [Theory]
    [InlineData("rateldesk.orchestration.callback")]
    [InlineData("bostec.service-link.control")]
    [InlineData("rateldesk.incidents.create other.scope")]
    public async Task Manual_resolver_denies_non_incident_and_mixed_scope_requests(string scope)
    {
        var f = new Fixture();
        await ((Func<Task>)(() => f.Resolver.GetBearerAsync(f.State, f.Peer, scope, default))).Should().ThrowAsync<UnauthorizedAccessException>();
        (f.Protector.Reads).Should().Be(0); (f.Probe.Calls).Should().Be(0);
    }

    [Theory]
    [InlineData(401, "receiver-authentication-rejected")]
    [InlineData(403, "receiver-current-grant-rejected")]
    [InlineData(422, "receiver-target-rejected")]
    [InlineData(429, "receiver-rate-limited")]
    [InlineData(302, "receiver-redirect-refused")]
    [InlineData(503, "receiver-capability-unavailable")]
    public void Capability_failures_remain_typed_and_never_bootstrap_identity(int status, string expected)
    {
        var error = ((Action)(() => RatelDeskReceiverReadException.RequireJsonSuccess(
            new(status, [], null, TimeSpan.FromSeconds(999), "application/json", true), "capability"))).Should().Throw<RatelDeskReceiverReadException>().Which;
        (error.Code).Should().Be(expected); (error.HttpStatus).Should().Be(status);
    }

    [Theory]
    [InlineData("sourceInstanceId")]
    [InlineData("sourceNamespaceId")]
    [InlineData("receiverInstanceId")]
    [InlineData("createEndpoint")]
    [InlineData("targetValidationEndpoint")]
    [InlineData("receiptEndpointTemplate")]
    [InlineData("authenticationModes")]
    [InlineData("minimumReceiptRetentionSeconds")]
    [InlineData("maximumAutomaticReplaySeconds")]
    [InlineData("supportsSafeSameKeyReplay")]
    public void Authenticated_capability_cannot_waive_identity_path_base_mode_or_replay_guarantees(string field)
    {
        var f = new Fixture(); var cap = Capability();
        cap[field] = field switch
        {
            "sourceInstanceId" => Guid.NewGuid().ToString("D"),
            "sourceNamespaceId" or "receiverInstanceId" => Guid.Empty.ToString("D"),
            "authenticationModes" => new[] { "oauth_client_credentials" },
            "minimumReceiptRetentionSeconds" => 60,
            "maximumAutomaticReplaySeconds" => 60,
            "supportsSafeSameKeyReplay" => false,
            _ => "https://redirect.example/api/v1/incidents/"
        };
        ((Action)(() => ReceiverWireValidation.CaptureManual(
            JsonSerializer.SerializeToUtf8Bytes(cap), f.State, Fixture.Api, Fixture.Source, DateTimeOffset.UnixEpoch))).Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void Target_response_must_confirm_the_same_authenticated_namespace_and_exact_approved_mapping()
    {
        var f = new Fixture();
        var body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            contractVersion = ReceiverWireValidation.Contract, receiverInstanceId = Fixture.Receiver.ToString("D"),
            sourceInstanceId = Fixture.Source.ToString("D"), sourceNamespaceId = Guid.NewGuid().ToString("D"), valid = true,
            mapping = new { organizationId = "org", customerId = "customer", assignedToId = (string?)null, categoryIds = Array.Empty<string>() }
        });
        ((Action)(() => ReceiverWireValidation.Target(body, f.Peer))).Should().Throw<InvalidDataException>();
    }

    [Fact]
    public async Task Empty_optional_deployment_values_allow_only_an_existing_owner_approved_connector()
    {
        var f = new Fixture();
        (f.Network.ValidateApprovedApiBase(RatelDeskAuthenticationMode.ManualApiBearer, Fixture.Api)).Should().Be(Fixture.Api);
        // Resolver still performs current owner/mode/source checks; policy alone is not a grant.
        f.Authorization.Allowed = false;
        await ((Func<Task>)(() => f.Resolver.CaptureAsync(f.State, Fixture.Manual, Fixture.Source, default))).Should().ThrowAsync<UnauthorizedAccessException>();
        (f.Probe.Calls).Should().Be(0);
    }

    [Fact]
    public void Blank_optional_deployment_entries_do_not_enable_restrictions_but_invalid_nonempty_entries_fail()
    {
        var f = new Fixture(); f.Options.Value.AllowedOrigins = ["", " "]; f.Options.Value.AllowedApiBases = [""];
        (new RatelDeskReceiverOptionsValidator().Validate(null, f.Options.Value).Succeeded).Should().BeTrue();
        (f.Network.ValidateApprovedApiBase(RatelDeskAuthenticationMode.ManualApiBearer, Fixture.Api)).Should().Be(Fixture.Api);
        f.Options.Value.RestrictToConfiguredPeers = true;
        ((Action)(() => f.Network.ValidateApprovedApiBase(RatelDeskAuthenticationMode.ManualApiBearer, Fixture.Api))).Should().Throw<UnauthorizedAccessException>();
        f.Options.Value.AllowedOrigins = ["https://valid.example?query=not-allowed"];
        (new RatelDeskReceiverOptionsValidator().Validate(null, f.Options.Value).Failed).Should().BeTrue();
    }

    [Fact]
    public void Existing_explicit_origin_restriction_applies_to_manual_and_managed_modes()
    {
        var f = new Fixture(); f.Options.Value.AllowedOrigins = ["https://another.example"];
        ((Action)(() => f.Network.ValidateApprovedApiBase(RatelDeskAuthenticationMode.ManualApiBearer, Fixture.Api))).Should().Throw<UnauthorizedAccessException>();
        ((Action)(() => f.Network.ValidateApprovedApiBase(RatelDeskAuthenticationMode.ManagedServiceLink, Fixture.Api))).Should().Throw<UnauthorizedAccessException>();
    }

    [Fact]
    public void Restriction_and_private_permission_removal_are_observed_without_recreating_the_policy()
    {
        var f = new Fixture(); f.Options.Value.AllowPrivateHttp = true;
        (f.Network.ValidateApprovedApiBase(RatelDeskAuthenticationMode.ManualApiBearer, "http://127.0.0.1:9000/helpdesk")).Should().Be("http://127.0.0.1:9000/helpdesk");
        f.Options.Value.AllowPrivateHttp = false;
        ((Action)(() => f.Network.ValidateApprovedApiBase(RatelDeskAuthenticationMode.ManualApiBearer, "http://127.0.0.1:9000/helpdesk"))).Should().Throw<ArgumentException>();
        f.Options.Value.RestrictToConfiguredPeers = true;
        ((Action)(() => f.Network.ValidateApprovedApiBase(RatelDeskAuthenticationMode.ManualApiBearer, Fixture.Api))).Should().Throw<UnauthorizedAccessException>();
    }

    [Fact]
    public void Managed_opt_in_does_not_enable_manual_private_HTTP()
    {
        var f = new Fixture(); f.Links.Value.AllowPrivateHttp = true;
        (f.Network.CurrentAllowPrivateHttp(RatelDeskAuthenticationMode.ManagedServiceLink)).Should().BeTrue();
        (f.Network.CurrentAllowPrivateHttp(RatelDeskAuthenticationMode.ManualApiBearer)).Should().BeFalse();
    }

    public static Dictionary<string, object> Capability() => new()
    {
        ["contractVersion"] = ReceiverWireValidation.Contract, ["receiverInstanceId"] = Fixture.Receiver.ToString("D"),
        ["sourceInstanceId"] = Fixture.Source.ToString("D"), ["sourceNamespaceId"] = Fixture.Namespace.ToString("D"),
        ["createEndpoint"] = Fixture.Api + ReceiverWireValidation.CreatePath,
        ["receiptEndpointTemplate"] = Fixture.Api + ReceiverWireValidation.ReceiptPath,
        ["targetValidationEndpoint"] = Fixture.Api + ReceiverWireValidation.TargetsPath,
        ["keyHeader"] = "Idempotency-Key", ["sourceHeader"] = "X-NetRatel-Source-Instance", ["maxKeyLength"] = 256,
        ["keyPattern"] = ReceiverWireValidation.KeyPattern, ["minimumReceiptRetentionSeconds"] = ReceiverWireValidation.MinimumRetention,
        ["maximumAutomaticReplaySeconds"] = ReceiverWireValidation.MaximumReplay, ["receiptEvictionEnabled"] = false,
        ["atomicIncidentReceiptAndEffects"] = true, ["supportsReceiptLookup"] = true, ["supportsSafeSameKeyReplay"] = true,
        ["authenticationModes"] = new[] { "api_bearer", "oauth_client_credentials" }
    };

    private sealed class Fixture
    {
        public const string Api = "https://receiver.example/helpdesk";
        public static readonly Guid Source = Guid.Parse("11111111-1111-4111-8111-111111111111");
        public static readonly Guid Namespace = Guid.Parse("22222222-2222-4222-8222-222222222222");
        public static readonly Guid Receiver = Guid.Parse("33333333-3333-4333-8333-333333333333");
        public const string Bearer1 = "rdk_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        public const string Bearer2 = "rdk_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        public static readonly RatelDeskConnectorAuthentication Manual = new(RatelDeskAuthenticationMode.ManualApiBearer, null);
        public RatelDeskConnectorState State => Store.Current!;
        public Store Store { get; } = new();
        public Bindings Bindings { get; } = new();
        public Authorization Authorization { get; } = new();
        public Protector Protector { get; } = new();
        public Continuity Continuity { get; } = new();
        public Probe Probe { get; }
        public Monitor<RatelDeskReceiverOptions> Options { get; } = new(new());
        public Monitor<ServiceLinkOptions> Links { get; } = new(new());
        public RatelDeskReceiverNetworkPolicy Network { get; }
        public ManualRatelDeskBindingResolver Resolver { get; }
        public RatelDeskSemanticPeer Peer { get; }
        public Fixture()
        {
            Store.Current = new(Guid.Parse("44444444-4444-4444-8444-444444444444"), 1, 1, 1, "owner",
                new("manual", Api, "org", "customer", null, Array.Empty<Guid>(), new(), true), "first", 1);
            Peer = ReceiverWireValidation.CaptureManual(JsonSerializer.SerializeToUtf8Bytes(Capability()), State, Api, Source, DateTimeOffset.UnixEpoch).Peer;
            Probe = new(new(Peer, ReceiverWireValidation.Capability(JsonSerializer.SerializeToUtf8Bytes(Capability()), Peer, DateTimeOffset.UnixEpoch)));
            Network = new(Options, Links, new Monitor<ServiceIdentityOptions>(new()));
            Resolver = new(Store, Bindings, Authorization, Protector, Continuity, Probe, Network);
        }
    }
    private sealed class Store : IRatelDeskConnectorStore
    {
        public RatelDeskConnectorState? Current { get; set; }
        public Task<RatelDeskConnectorState?> GetAsync(int tenantId, Guid id, CancellationToken ct) => Task.FromResult(Current);
        public Task<IReadOnlyList<RatelDeskConnectorState>> ListAsync(int tenantId, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> SaveAsync(RatelDeskConnectorState state, long version, CancellationToken ct) => throw new NotSupportedException();
    }
    private sealed class Bindings : IRatelDeskConnectorBindingStore
    {
        public RatelDeskConnectorAuthentication Current { get; set; } = Fixture.Manual;
        public Task<RatelDeskConnectorAuthentication> GetAuthenticationAsync(int tenant, Guid connector, CancellationToken ct) => Task.FromResult(Current);
    }
    private sealed class Authorization : IRatelDeskConnectorAuthorization
    {
        public bool Allowed { get; set; } = true;
        public Task<bool> CanExecuteAsync(string owner, string? credential, int tenant, CancellationToken ct) => Task.FromResult(Allowed);
        public Task<bool> CanManageAsync(ClaimsPrincipal actor, int tenant, CancellationToken ct) => Task.FromResult(Allowed);
    }
    private sealed class Protector : IRatelDeskCredentialProtector
    {
        public int Reads { get; private set; }
        public bool Fail { get; set; }
        public string First { get; set; } = Fixture.Bearer1;
        public string Protect(int tenant, Guid connector, string value) => throw new NotSupportedException();
        public string Unprotect(int tenant, Guid connector, string value)
        { Reads++; if (Fail) throw new CryptographicException(); return value == "second" ? Fixture.Bearer2 : First; }
    }
    private sealed class Continuity : IRatelDeskProducerContinuity
    {
        public int Calls { get; private set; }
        public bool Allowed { get; set; } = true;
        public Task RequireCurrentAsync(Guid expected, CancellationToken ct)
        { Calls++; if (!Allowed || expected != Fixture.Source) throw new UnauthorizedAccessException("source-denied"); return Task.CompletedTask; }
    }
    private sealed class Probe(RatelDeskManualProfileObservation observation) : IRatelDeskManualProfileProbe
    {
        public int Calls { get; private set; }
        public Action? OnCapture { get; set; }
        public Task<RatelDeskManualProfileObservation> CaptureAsync(RatelDeskConnectorState connector, Guid source, string bearer, CancellationToken ct)
        { Calls++; OnCapture?.Invoke(); return Task.FromResult(observation); }
    }
    private sealed class Monitor<T>(T value) : IOptionsMonitor<T>
    {
        public T Value { get; set; } = value;
        public T CurrentValue => Value;
        public T Get(string? name) => Value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
