using System.Security.Claims;
using AwesomeAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using NetRatel.Infrastructure.Identity;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Connectivity;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

[Collection(PostgreSqlPersistenceCollection.Name)]
public sealed class IntegrationPairingServiceTests(PostgreSqlPersistenceFixture postgres)
{
    private static readonly ClaimsPrincipal Owner = new(new ClaimsIdentity([new Claim("netratel_principal_id", "pairing-owner")], "local"));

    [Fact]
    public async Task Restart_and_lost_delivery_retry_keep_one_ordinary_credential_and_ack_purges_the_envelope()
    {
        await using var fixture = await CreateAsync();
        await using var session = fixture.Open();
        var started = await session.Pairing.StartAsync(new("CLI profile", PairingCredentialPurpose.Api, RequestedPermissions: [NetRatelPermissions.TelemetryRead]), "https://netratel.example.test");
        started.VerificationUri.Should().NotContain(started.UserCode).And.NotContain(started.DeviceCapability);
        started.ToString().Should().NotContain(started.DeviceCapability).And.NotContain(started.UserCode);
        var review = await session.Pairing.ReviewAsync(started.UserCode);
        review.Name.Should().Be("CLI profile");
        var approval = Approval(started, fixture.Clock);
        new[] { review.ToString(), approval.ToString(), new IntegrationPairingDeviceRequest(started.DeviceCapability).ToString() }
            .Should().OnlyContain(text => !text.Contains(started.UserCode, StringComparison.Ordinal) && !text.Contains(started.DeviceCapability, StringComparison.Ordinal));
        (await session.Pairing.PollAsync(started.DeviceCapability)).Status.Should().Be("slow_down");
        await session.Pairing.ApproveAsync(Owner, Approval(started, fixture.Clock));
        fixture.Clock.Advance(TimeSpan.FromSeconds(10));
        var first = (await session.Pairing.PollAsync(started.DeviceCapability)).Delivery!;
        first.ToString().Should().NotContain(first.Secret);
        first.Grants.Should().ContainSingle();
        first.Grants[0].TenantId.Should().Be(7);
        first.Grants[0].Permission.Should().Be(NetRatelPermissions.TelemetryRead);
        first.Grants[0].PermissionLabel.Should().Be("Read telemetry");
        first.Grants[0].TenantName.Should().Be("Tenant A");
        first.InstancePermissions.Should().BeEmpty();
        first.Purpose.Should().Be(PairingCredentialPurpose.Api);
        (await session.Db.IntegrationCredentials.CountAsync()).Should().Be(1);
        var stored = await session.Db.IntegrationPairingRequests.SingleAsync();
        stored.UserCodeHash.Should().NotContain(started.UserCode);
        stored.DeviceCapabilityHash.Should().NotContain(started.DeviceCapability);
        stored.ProtectedDelivery.Should().NotContain(first.Secret);
        (await session.Db.IntegrationCredentials.SingleAsync()).SecretHash.Should().Be(IntegrationCredentialService.Hash(first.Secret));

        // A new context and service model another API worker/restart after the successful response was lost.
        await using var restarted = fixture.Open();
        fixture.Clock.Advance(TimeSpan.FromSeconds(10));
        var retry = (await restarted.Pairing.PollAsync(started.DeviceCapability)).Delivery!;
        retry.Secret.Should().Be(first.Secret);
        retry.CredentialId.Should().Be(first.CredentialId);
        (await restarted.Db.IntegrationCredentials.CountAsync()).Should().Be(1);
        (await restarted.Pairing.AcknowledgeAsync(started.DeviceCapability, first.CredentialId)).Status.Should().Be("consumed");
        (await restarted.Db.IntegrationPairingRequests.SingleAsync()).ProtectedDelivery.Should().BeNull();
        (await restarted.Pairing.PollAsync(started.DeviceCapability)).Delivery.Should().BeNull();
        // Old device capabilities cannot revoke a credential after secure-save acknowledgement.
        (await restarted.Pairing.CancelAsync(started.DeviceCapability)).Status.Should().Be("consumed");
        (await restarted.Credentials.VerifyAsync(first.Secret, IntegrationCredentialPurpose.Api)).Should().NotBeNull();
        fixture.Clock.Advance(TimeSpan.FromDays(2));
        await restarted.Pairing.CleanupAsync();
        (await restarted.Db.IntegrationPairingRequests.AsNoTracking().CountAsync()).Should().Be(0);
        (await restarted.Credentials.VerifyAsync(first.Secret, IntegrationCredentialPurpose.Api)).Should().NotBeNull();
        await restarted.Credentials.RevokeAsync("pairing-owner", first.CredentialId, "pairing-owner");
        (await restarted.Credentials.VerifyAsync(first.Secret, IntegrationCredentialPurpose.Api)).Should().BeNull();
    }

    [Fact]
    public async Task Concurrent_redemption_and_replayed_approval_cannot_create_or_expand_a_second_credential()
    {
        await using var fixture = await CreateAsync();
        await using var initial = fixture.Open();
        var started = await initial.Pairing.StartAsync(new("Concurrent CLI", PairingCredentialPurpose.Api), "https://netratel.example.test");
        await initial.Pairing.ApproveAsync(Owner, Approval(started, fixture.Clock));
        var replay = () => initial.Pairing.ApproveAsync(Owner, new(started.UserCode, fixture.Clock.GetUtcNow().AddDays(30),
            [new(7, NetRatelPermissions.FileRead)]));
        await replay.Should().ThrowAsync<IntegrationPairingUnavailableException>();
        fixture.Clock.Advance(TimeSpan.FromSeconds(5));
        await using var workerA = fixture.Open();
        await using var workerB = fixture.Open();
        var replies = await Task.WhenAll(workerA.Pairing.PollAsync(started.DeviceCapability), workerB.Pairing.PollAsync(started.DeviceCapability));
        replies.Should().ContainSingle(reply => reply.Status == "issued");
        replies.Should().ContainSingle(reply => reply.Status == "slow_down");
        await using var observed = fixture.Open();
        (await observed.Db.IntegrationCredentials.CountAsync()).Should().Be(1);
        (await observed.Credentials.ListAsync("pairing-owner")).Single().Grants.Should().Equal(new IntegrationCredentialGrantRequest(7, NetRatelPermissions.TelemetryRead));
    }

    [Fact]
    public async Task Denied_expired_and_cancelled_requests_do_not_issue_and_unknown_codes_reveal_no_account_data()
    {
        await using var fixture = await CreateAsync();
        await using var session = fixture.Open();
        var denied = await session.Pairing.StartAsync(new("Denied", PairingCredentialPurpose.Api), "https://netratel.example.test");
        (await session.Pairing.DenyAsync(denied.UserCode)).Status.Should().Be("denied");
        (await session.Pairing.PollAsync(denied.DeviceCapability)).Status.Should().Be("denied");
        var cancelled = await session.Pairing.StartAsync(new("Cancelled", PairingCredentialPurpose.Api), "https://netratel.example.test");
        (await session.Pairing.CancelAsync(cancelled.DeviceCapability)).Status.Should().Be("cancelled");
        var expired = await session.Pairing.StartAsync(new("Expired", PairingCredentialPurpose.Api), "https://netratel.example.test");
        fixture.Clock.Advance(TimeSpan.FromMinutes(11));
        (await session.Pairing.PollAsync(expired.DeviceCapability)).Status.Should().Be("expired");
        var unknown = () => session.Pairing.ReviewAsync("ZZZZ-ZZZZ");
        await unknown.Should().ThrowAsync<IntegrationPairingUnavailableException>().WithMessage("The connection request is unavailable. Start a new connection request.");
        (await session.Db.IntegrationCredentials.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Browser_operator_claims_need_durable_roles_at_approval_and_changed_membership_denies_redemption()
    {
        await using var fixture = await CreateAsync();
        await using var session = fixture.Open();
        var started = await session.Pairing.StartAsync(new("Membership change", PairingCredentialPurpose.Api), "https://netratel.example.test");
        await session.Db.PrincipalRoleAssignments.ExecuteDeleteAsync();
        var browserOperator = new ClaimsPrincipal(new ClaimsIdentity([new Claim("netratel_principal_id", "pairing-owner"), new Claim("groups", "Operator")], "Oidc"));
        var claimsOnlyApproval = () => session.Pairing.ApproveAsync(browserOperator, Approval(started, fixture.Clock));
        (await claimsOnlyApproval.Should().ThrowAsync<IntegrationCredentialGrantValidationException>()).Which.Field.Should().Be("durableAuthority");
        (await session.Db.IntegrationPairingRequests.SingleAsync()).State.Should().Be(IntegrationPairingState.Pending);
        session.Db.PrincipalRoleAssignments.Add(new PrincipalRoleAssignment { PrincipalId = "pairing-owner", RoleId = (await session.Db.AccessRoles.SingleAsync()).Id, TenantId = 7 });
        await session.Db.SaveChangesAsync();
        await session.Pairing.ApproveAsync(browserOperator, Approval(started, fixture.Clock));
        await session.Db.PrincipalRoleAssignments.ExecuteDeleteAsync();
        fixture.Clock.Advance(TimeSpan.FromSeconds(5));
        (await session.Pairing.PollAsync(started.DeviceCapability)).Status.Should().Be("denied");
        (await session.Db.IntegrationCredentials.CountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Abandoned_delivery_is_revoked_on_cancellation_or_cleanup_without_revoking_an_existing_profile(bool cancel)
    {
        await using var fixture = await CreateAsync();
        await using var session = fixture.Open();
        var existing = await session.Credentials.CreateAsync("pairing-owner", new("Working profile", IntegrationCredentialPurpose.Api,
            fixture.Clock.GetUtcNow().AddDays(7), [new(7, NetRatelPermissions.TelemetryRead)]));
        var started = await session.Pairing.StartAsync(new("Abandoned profile", PairingCredentialPurpose.Api), "https://netratel.example.test");
        await session.Pairing.ApproveAsync(Owner, Approval(started, fixture.Clock));
        fixture.Clock.Advance(TimeSpan.FromSeconds(5));
        var issued = (await session.Pairing.PollAsync(started.DeviceCapability)).Delivery!;
        if (cancel) (await session.Pairing.CancelAsync(started.DeviceCapability)).Status.Should().Be("cancelled");
        else
        {
            fixture.Clock.Advance(TimeSpan.FromMinutes(6));
            // Inline verification enforces the deadline even before the cleanup worker can run.
            (await session.Credentials.VerifyAsync(issued.Secret, IntegrationCredentialPurpose.Api)).Should().BeNull();
            (await session.Credentials.VerifyCurrentAsync(issued.CredentialId, IntegrationCredentialPurpose.Api)).Should().BeNull();
            await session.Pairing.CleanupAsync();
        }
        (await session.Credentials.VerifyAsync(issued.Secret, IntegrationCredentialPurpose.Api)).Should().BeNull();
        (await session.Credentials.VerifyAsync(existing.Secret, IntegrationCredentialPurpose.Api)).Should().NotBeNull();
        (await session.Db.IntegrationPairingRequests.SingleAsync()).ProtectedDelivery.Should().BeNull();
    }

    [Fact]
    public async Task Approval_rejects_cross_tenant_nondelegable_and_expired_grants_and_start_binds_purpose_and_resource()
    {
        await using var fixture = await CreateAsync();
        await using var session = fixture.Open();
        var badResource = () => session.Pairing.StartAsync(new("Wrong resource", PairingCredentialPurpose.HttpMcp, "https://other.example.test/mcp"), "https://netratel.example.test");
        await badResource.Should().ThrowAsync<IntegrationPairingUnavailableException>();
        var apiResource = () => session.Pairing.StartAsync(new("Wrong purpose", PairingCredentialPurpose.Api, "https://netratel.example.test/mcp"), "https://netratel.example.test");
        await apiResource.Should().ThrowAsync<IntegrationPairingUnavailableException>();
        var started = await session.Pairing.StartAsync(new("HTTP client", PairingCredentialPurpose.HttpMcp, "https://mcp.example.test/mcp"),
            "https://netratel.example.test", "https://mcp.example.test/mcp");
        (await session.Pairing.ReviewAsync(started.UserCode)).Resource.Should().Be("https://mcp.example.test/mcp");
        var crossTenant = () => session.Pairing.ApproveAsync(Owner, new(started.UserCode, fixture.Clock.GetUtcNow().AddDays(7), [new(8, NetRatelPermissions.TelemetryRead)]));
        await crossTenant.Should().ThrowAsync<IntegrationCredentialGrantValidationException>();
        var management = () => session.Pairing.ApproveAsync(Owner, new(started.UserCode, fixture.Clock.GetUtcNow().AddDays(7), [new(7, NetRatelPermissions.IntegrationManagement)]));
        await management.Should().ThrowAsync<IntegrationCredentialGrantValidationException>();
        var expiry = () => session.Pairing.ApproveAsync(Owner, new(started.UserCode, fixture.Clock.GetUtcNow().AddDays(-1), [new(7, NetRatelPermissions.TelemetryRead)]));
        await expiry.Should().ThrowAsync<IntegrationCredentialGrantValidationException>();
        await session.Pairing.ApproveAsync(Owner, Approval(started, fixture.Clock));
        fixture.Clock.Advance(TimeSpan.FromSeconds(5));
        var delivery = (await session.Pairing.PollAsync(started.DeviceCapability)).Delivery!;
        delivery.Purpose.Should().Be(PairingCredentialPurpose.HttpMcp);
        (await session.Credentials.VerifyAsync(delivery.Secret, IntegrationCredentialPurpose.Api)).Should().BeNull();
        (await session.Credentials.VerifyAsync(delivery.Secret, IntegrationCredentialPurpose.HttpMcp))!.Resource.Should().Be("https://mcp.example.test/mcp");
    }

    private static IntegrationPairingApprovalRequest Approval(IntegrationPairingStartResponse start, ManualClock clock) =>
        new(start.UserCode, clock.GetUtcNow().AddDays(7), [new(7, NetRatelPermissions.TelemetryRead)]);
    private async Task<Fixture> CreateAsync()
    {
        var fixture = new Fixture(await postgres.CreateDatabaseAsync());
        await using var session = fixture.Open();
        await session.Db.Database.MigrateAsync();
        // Approval checks identifiers; delivery adds their human-readable names.
        await session.Db.Database.ExecuteSqlRawAsync("CREATE TABLE \"Tenants\" (\"Id\" integer PRIMARY KEY, \"Name\" text NOT NULL); INSERT INTO \"Tenants\" (\"Id\", \"Name\") VALUES (7, 'Tenant A'), (8, 'Tenant B')");
        session.Db.Users.Add(new LocalUser { Id = "pairing-local", UserName = "owner", PrincipalId = "pairing-owner", IsEnabled = true });
        var role = new AccessRole { Name = "Pairing reader" };
        role.Permissions.Add(new AccessRolePermission { Permission = NetRatelPermissions.TelemetryRead });
        role.Permissions.Add(new AccessRolePermission { Permission = NetRatelPermissions.FileRead });
        session.Db.AccessRoles.Add(role);
        session.Db.PrincipalRoleAssignments.Add(new PrincipalRoleAssignment { PrincipalId = "pairing-owner", RoleId = role.Id, TenantId = 7 });
        await session.Db.SaveChangesAsync();
        return fixture;
    }
    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan elapsed) => _now += elapsed;
    }
    private sealed class Fixture(string connectionString) : IAsyncDisposable
    {
        private readonly string _keyPath = Path.Combine(Path.GetTempPath(), "netratel-pairing-" + Guid.NewGuid().ToString("N"));
        public ManualClock Clock { get; } = new();
        public Session Open() => new(connectionString, Clock, DataProtectionProvider.Create(new DirectoryInfo(_keyPath)));
        public ValueTask DisposeAsync() { if (Directory.Exists(_keyPath)) Directory.Delete(_keyPath, true); return ValueTask.CompletedTask; }
    }
    private sealed class Session : IAsyncDisposable
    {
        private readonly OrchestratorDbContext _application;
        public NetRatelIdentityDbContext Db { get; }
        public IntegrationCredentialService Credentials { get; }
        public IntegrationPairingService Pairing { get; }
        public Session(string connectionString, TimeProvider clock, IDataProtectionProvider protection)
        {
            Db = new(new DbContextOptionsBuilder<NetRatelIdentityDbContext>().UseNpgsql(connectionString).Options);
            _application = new(new DbContextOptionsBuilder<OrchestratorDbContext>().UseNpgsql(connectionString).Options);
            Credentials = new(Db, clock);
            var access = new EffectiveAccessService(Db, new ConfigurationBuilder().Build());
            Pairing = new(Db, Credentials, new IntegrationCredentialGrantValidator(Db, _application, access, clock), protection, clock, applicationDb: _application);
        }
        public async ValueTask DisposeAsync() { await _application.DisposeAsync(); await Db.DisposeAsync(); }
    }
}
