using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using AwesomeAssertions;
using Grpc.Core;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.API.Gateway;
using NetRatel.Application.Agents;
using NetRatel.Application.Presence;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class AgentGatewayRenewalAuthenticationTests
{
    [Fact]
    public async Task ValidSignedCredential_RechecksEnrollmentAndReturnsActualExpiry()
    {
        using var fixture = new AuthenticationFixture();
        var expiry = fixture.Clock.GetUtcNow().AddMinutes(5);
        var result = await fixture.Authenticator.ValidateAsync(fixture.Credentials.CreateToken(fixture.Identity, expiry), fixture.Identity,
            fixture.Clock.GetUtcNow().AddMinutes(2), CancellationToken.None);
        result.Should().Be(expiry);
        fixture.Agents.Lookups.Should().Be(1);
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("not-yet-valid")]
    [InlineData("tenant")]
    [InlineData("agent")]
    [InlineData("issuer")]
    [InlineData("audience")]
    [InlineData("signature")]
    [InlineData("unsigned")]
    [InlineData("role")]
    [InlineData("scope")]
    [InlineData("same-expiry")]
    [InlineData("oversized")]
    public async Task InvalidRenewal_FailsBeforeAuthorityCanBeExtended(string variant)
    {
        using var fixture = new AuthenticationFixture();
        var now = fixture.Clock.GetUtcNow();
        var identity = variant switch
        {
            "tenant" => fixture.Identity with { TenantId = fixture.Identity.TenantId + 1 },
            "agent" => fixture.Identity with { AgentId = Guid.NewGuid() },
            _ => fixture.Identity
        };
        var expiry = variant == "expired" ? now.AddSeconds(-1) : now.AddMinutes(5);
        using var differentKey = new AgentGatewayRenewalTestCredentials();
        var token = variant == "oversized" ? new string('x', 16385) :
            (variant == "signature" ? differentKey : fixture.Credentials).CreateToken(identity, expiry,
                notBefore: variant == "not-yet-valid" ? now.AddMinutes(1) : now.AddMinutes(-1),
                issuer: variant == "issuer" ? "different-issuer" : null,
                audience: variant == "audience" ? "different-audience" : null,
                role: variant == "role" ? "operator" : "agent",
                scope: variant == "scope" ? "netratel:telemetry" : "netratel:connect",
                signed: variant != "unsigned");
        var validate = () => fixture.Authenticator.ValidateAsync(token, fixture.Identity,
            variant == "same-expiry" ? expiry : now.AddMinutes(2), CancellationToken.None);
        (await validate.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.Unauthenticated);
        fixture.Agents.Lookups.Should().Be(0);
    }

    [Theory]
    [InlineData("revoked")]
    [InlineData("disabled")]
    [InlineData("deleted")]
    [InlineData("missing")]
    public async Task SignedCredential_ForInactiveEnrollmentCannotRenew(string state)
    {
        using var fixture = new AuthenticationFixture();
        fixture.Agents.State = state;
        var validate = () => fixture.Authenticator.ValidateAsync(
            fixture.Credentials.CreateToken(fixture.Identity, fixture.Clock.GetUtcNow().AddMinutes(5)), fixture.Identity,
            fixture.Clock.GetUtcNow().AddMinutes(2), CancellationToken.None);
        (await validate.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.PermissionDenied);
        fixture.Agents.Lookups.Should().Be(1);
    }

    [Fact]
    public void Lease_TwoRenewalsKeepTheExactOwnerAndCancelAtFinalCredentialExpiry()
    {
        var clock = new RenewalManualTimeProvider();
        var leases = new AgentGatewayAuthenticationLeaseRegistry(clock);
        var client = new ClientKey(71, Guid.NewGuid());
        var connection = Guid.NewGuid();
        using var lease = leases.Register(client, connection, 8, clock.GetUtcNow().AddMinutes(2));
        var originalExpiryCallback = clock.LastTimerCallback;
        lease.TryRenew(Guid.NewGuid(), 2, clock.GetUtcNow().AddMinutes(4)).Should().BeTrue();
        lease.TryRenew(Guid.NewGuid(), 3, clock.GetUtcNow().AddMinutes(6)).Should().BeTrue();
        clock.Advance(TimeSpan.FromMinutes(2));
        originalExpiryCallback();
        lease.CompletionToken.IsCancellationRequested.Should().BeFalse("a queued predecessor expiry callback rechecks the extended credential");
        leases.Find(client, connection, 8).Should().BeSameAs(lease);
        clock.Advance(TimeSpan.FromMinutes(4));
        lease.CompletionToken.IsCancellationRequested.Should().BeTrue();
        leases.Find(client, connection, 8).Should().BeNull();
        lease.TryRenew(Guid.NewGuid(), 4, clock.GetUtcNow().AddMinutes(5)).Should().BeFalse();
        clock.TimerCount.Should().Be(0);
    }

    [Fact]
    public void Lease_ReplayedRenewalAndLatePredecessorDisposeCannotAffectSuccessor()
    {
        var clock = new RenewalManualTimeProvider();
        var leases = new AgentGatewayAuthenticationLeaseRegistry(clock);
        var client = new ClientKey(71, Guid.NewGuid());
        using var previous = leases.Register(client, Guid.NewGuid(), 8, clock.GetUtcNow().AddMinutes(2));
        var operation = Guid.NewGuid();
        previous.TryRenew(operation, 5, clock.GetUtcNow().AddMinutes(4)).Should().BeTrue();
        previous.TryRenew(operation, 5, clock.GetUtcNow().AddMinutes(8)).Should().BeFalse();
        previous.TryRenew(operation, 6, clock.GetUtcNow().AddMinutes(8)).Should().BeFalse();
        previous.TryRenew(Guid.NewGuid(), 4, clock.GetUtcNow().AddMinutes(8)).Should().BeFalse();
        previous.TryRenew(Guid.NewGuid(), 6, clock.GetUtcNow().AddMinutes(4)).Should().BeFalse();
        var connection = Guid.NewGuid();
        using var successor = leases.Register(client, connection, 9, clock.GetUtcNow().AddMinutes(5));
        previous.CompletionToken.IsCancellationRequested.Should().BeFalse("only the authoritative actor fences different logical owners");
        previous.Dispose();
        previous.TryRenew(Guid.NewGuid(), 7, clock.GetUtcNow().AddMinutes(8)).Should().BeFalse();
        successor.IsCurrent.Should().BeTrue();
        successor.CompletionToken.IsCancellationRequested.Should().BeFalse();
        leases.Find(client, connection, 9).Should().BeSameAs(successor);
        leases.Find(client, connection, 8).Should().BeNull();
    }

    [Fact]
    public void LocalExpiryLease_AcceptsAnActorRestartEpochWithoutClaimingGlobalAuthority()
    {
        var clock = new RenewalManualTimeProvider();
        var leases = new AgentGatewayAuthenticationLeaseRegistry(clock);
        var client = new ClientKey(71, Guid.NewGuid());
        using var old = leases.Register(client, Guid.NewGuid(), 8, clock.GetUtcNow().AddMinutes(2));
        var restartedConnection = Guid.NewGuid();
        using var current = leases.Register(client, restartedConnection, 1, clock.GetUtcNow().AddMinutes(5));
        leases.Find(client, restartedConnection, 1).Should().BeSameAs(current);
        old.Dispose();
        leases.Find(client, restartedConnection, 1).Should().BeSameAs(current);
        current.CompletionToken.IsCancellationRequested.Should().BeFalse();
    }

    [Fact]
    public async Task FailedFreshAuthorization_DoesNotExtendExistingOwner()
    {
        using var fixture = new AuthenticationFixture();
        var client = new ClientKey(fixture.Identity.TenantId, fixture.Identity.AgentId);
        var leases = new AgentGatewayAuthenticationLeaseRegistry(fixture.Clock);
        using var lease = leases.Register(client, Guid.NewGuid(), 1, fixture.Clock.GetUtcNow().AddMinutes(2));
        fixture.Agents.State = "revoked";
        await FluentActions.Awaiting(() => fixture.Authenticator.ValidateAsync(
            fixture.Credentials.CreateToken(fixture.Identity, fixture.Clock.GetUtcNow().AddMinutes(10)), fixture.Identity,
            lease.ExpiresAtUtc, CancellationToken.None)).Should().ThrowAsync<RpcException>();
        fixture.Clock.Advance(TimeSpan.FromMinutes(2));
        lease.CompletionToken.IsCancellationRequested.Should().BeTrue();
    }

    [Fact]
    public void RenewalProtocol_BindsTenantAgentConnectionEpochAndOrderedConsumption()
    {
        var identity = new AuthenticatedAgentIdentity(71, Guid.NewGuid());
        var connection = Guid.NewGuid();
        var frame = new AgentFrame
        {
            ProtocolVersion = "1.0", TenantId = identity.TenantId, ClientId = identity.ClientId,
            ConnectionId = connection.ToString("D"), ConnectionEpoch = 8, Sequence = 3,
            OperationId = Guid.NewGuid().ToString("D"), Renew = new PresenceAuthRenewal { AccessToken = "signed-credential" }
        };
        AgentGatewayProtocolValidator.ValidateRenewal(frame, identity, "1.0", connection, 8, 2).IsValid.Should().BeTrue();
        AgentGatewayProtocolValidator.ValidateRenewal(frame, identity, "1.0", connection, 8, 3).IsValid.Should().BeFalse();
        AgentGatewayProtocolValidator.ValidateRenewal(frame, identity, "1.0", Guid.NewGuid(), 8, 2).IsValid.Should().BeFalse();
        AgentGatewayProtocolValidator.ValidateRenewal(frame, identity, "1.0", connection, 9, 2).IsValid.Should().BeFalse();
        AgentGatewayProtocolValidator.ValidateRenewal(frame, identity with { TenantId = 72 }, "1.0", connection, 8, 2).IsValid.Should().BeFalse();
        AgentGatewayProtocolValidator.ValidateRenewal(frame, identity with { AgentId = Guid.NewGuid() }, "1.0", connection, 8, 2).IsValid.Should().BeFalse();
    }

    private sealed class AuthenticationFixture : IDisposable
    {
        private readonly ServiceProvider _services;
        public RenewalManualTimeProvider Clock { get; } = new();
        public AgentGatewayRenewalTestCredentials Credentials { get; } = new();
        public AuthenticatedAgentIdentity Identity { get; } = new(71, Guid.NewGuid());
        public RenewalAgentManagementService Agents { get; }
        public AgentGatewayRenewalAuthenticator Authenticator => _services.GetRequiredService<AgentGatewayRenewalAuthenticator>();
        public AuthenticationFixture()
        {
            Agents = new(Identity);
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddAuthentication().AddJwtBearer("Agent", Credentials.Configure);
            services.AddSingleton<TimeProvider>(Clock);
            services.AddSingleton<IAgentManagementService>(Agents);
            services.AddSingleton<AgentGatewayRenewalAuthenticator>();
            _services = services.BuildServiceProvider();
        }
        public void Dispose() { _services.Dispose(); Credentials.Dispose(); }
    }
}

internal sealed class AgentGatewayRenewalTestCredentials : IDisposable
{
    public const string Issuer = "netratel-renewal-fixture";
    public const string Audience = "netratel-agent-gateway";
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    public void Configure(JwtBearerOptions options)
    {
        options.MapInboundClaims = false;
        options.RequireHttpsMetadata = false;
        options.TokenValidationParameters = new()
        {
            ValidateIssuer = true, ValidIssuer = Issuer,
            ValidateAudience = true, ValidAudience = Audience,
            ValidateIssuerSigningKey = true, IssuerSigningKey = new ECDsaSecurityKey(_key),
            ValidateLifetime = true, ClockSkew = TimeSpan.FromMinutes(2), NameClaimType = "sub", RoleClaimType = "role"
        };
    }
    public string CreateToken(AuthenticatedAgentIdentity identity, DateTimeOffset expires,
        DateTimeOffset? notBefore = null, string? issuer = null, string? audience = null, string role = "agent", string scope = "netratel:connect", bool signed = true) =>
        new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(issuer ?? Issuer, audience ?? Audience,
        [new("sub", identity.ClientId), new("agent_id", identity.ClientId), new("tenant_id", identity.TenantId.ToString()),
            new("role", role), new("scope", scope)],
            (notBefore ?? expires.AddMinutes(-10)).UtcDateTime, expires.UtcDateTime,
            signed ? new SigningCredentials(new ECDsaSecurityKey(_key), SecurityAlgorithms.EcdsaSha256) : null));
    public void Dispose() => _key.Dispose();
}

internal sealed class RenewalAgentManagementService(AuthenticatedAgentIdentity identity) : IAgentManagementService
{
    public string State { get; set; } = "active";
    public int Lookups { get; private set; }
    public Task<AgentDetailDto?> GetAsync(int tenantId, Guid agentId, CancellationToken ct)
    {
        Lookups++;
        return Task.FromResult(tenantId != identity.TenantId || agentId != identity.AgentId || State == "missing" ? null :
            new AgentDetailDto(tenantId, agentId, null, State != "disabled", null, DateTimeOffset.UtcNow, null, null, null,
                State == "revoked" ? DateTimeOffset.UtcNow : null, State == "deleted" ? DateTimeOffset.UtcNow : null));
    }
    public Task<AgentListResponse> ListAsync(int tenantId, AgentListQuery query, CancellationToken ct) => throw new NotSupportedException();
    public Task DisableAsync(int tenantId, Guid agentId, string reason, string actor, CancellationToken ct) => throw new NotSupportedException();
    public Task EnableAsync(int tenantId, Guid agentId, string actor, CancellationToken ct) => throw new NotSupportedException();
    public Task DeleteAsync(int tenantId, Guid agentId, string reason, string actor, CancellationToken ct) => throw new NotSupportedException();
}

internal sealed class RenewalManualTimeProvider : TimeProvider
{
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _utc = DateTimeOffset.Parse("2026-10-04T12:00:00Z");
    public int TimerCount => _timers.Count;
    public Action LastTimerCallback { get; private set; } = () => { };
    public override DateTimeOffset GetUtcNow() => _utc;
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state, _utc + dueTime);
        _timers.Add(timer);
        LastTimerCallback = () => callback(state);
        return timer;
    }
    public void Advance(TimeSpan duration)
    {
        _utc += duration;
        foreach (var timer in _timers.ToArray()) timer.FireIfDue(_utc);
    }
    private sealed class ManualTimer(RenewalManualTimeProvider owner, TimerCallback callback, object? state, DateTimeOffset due) : ITimer
    {
        private DateTimeOffset _due = due;
        private bool _disposed;
        public bool Change(TimeSpan dueTime, TimeSpan period) { _due = owner.GetUtcNow() + dueTime; return !_disposed; }
        public void Dispose() { _disposed = true; owner._timers.Remove(this); }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        public void FireIfDue(DateTimeOffset now)
        {
            if (!_disposed && now >= _due) callback(state);
        }
    }
}
