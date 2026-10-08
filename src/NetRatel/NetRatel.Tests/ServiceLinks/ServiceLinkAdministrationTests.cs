using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.ServiceIdentity;
using NetRatel.Infrastructure.ServiceLinks;
using NetRatel.Shared.ServiceLinks;
using Xunit;

namespace NetRatel.Tests.ServiceLinks;

public sealed class ServiceLinkAdministrationTests
{
    [Fact]
    public async Task Repeated_start_in_the_original_session_reuses_the_unchanged_pending_attempt()
    {
        await using var fixture = await Fixture.CreateAsync(initiator: true);
        var request = fixture.StartRequest();
        var first = await fixture.Coordinator.StartAsync(request, Owner, default);
        var second = await fixture.Coordinator.StartAsync(request, Owner, default);
        Assert.Equal(first, second);
        Assert.Single(await fixture.Db.Set<ServiceLinkAttempt>().ToListAsync());
    }

    [Fact]
    public async Task A_product_version_only_change_reuses_pending_consent_without_rewriting_its_descriptor()
    {
        await using var fixture = await Fixture.CreateAsync(initiator: true);
        var request = fixture.StartRequest();
        var first = await fixture.Coordinator.StartAsync(request, Owner, default);
        var row = await fixture.Db.Set<ServiceLinkAttempt>().SingleAsync();
        var descriptor = ServiceLinkCanonicalJson.Deserialize<ServiceLinkRequestDescriptor>(row.DescriptorJson);
        descriptor = descriptor with
        {
            InitiatorEndpointSnapshot = descriptor.InitiatorEndpointSnapshot with { ProductVersion = "previous-version" },
            ResponderEndpointSnapshot = descriptor.ResponderEndpointSnapshot with { ProductVersion = "previous-version" }
        };
        descriptor = descriptor with { DescriptorHash = ServiceLinkCanonicalJson.HashObject(descriptor, "descriptor_hash") };
        row.DescriptorJson = JsonSerializer.Serialize(descriptor); row.DescriptorHash = descriptor.DescriptorHash;
        await fixture.Db.SaveChangesAsync();
        var before = JsonSerializer.Serialize(row);
        var repeated = await fixture.Coordinator.StartAsync(request, Owner, default);
        Assert.Equal(first, repeated);
        Assert.Equal(before, JsonSerializer.Serialize(row));
        Assert.Single(await fixture.Db.Set<ServiceLinkAttempt>().ToListAsync());
    }

    [Fact]
    public async Task Starting_an_already_approved_relationship_reports_the_existing_attempt_without_creating_another()
    {
        await using var fixture = await Fixture.CreateAsync(initiator: true);
        var request = fixture.StartRequest();
        var first = await fixture.Coordinator.StartAsync(request, Owner, default);
        var row = await fixture.Db.Set<ServiceLinkAttempt>().SingleAsync();
        row.LifecycleState = "approved";
        row.ActiveRelationshipKey = ServiceLinkValidation.Digest(row.LocalTenantId + "\n" + row.PeerInstanceId + "\n" + row.PeerTenantId);
        await fixture.Db.SaveChangesAsync();
        var error = await Assert.ThrowsAsync<ServiceLinkProtocolException>(() => fixture.Coordinator.StartAsync(request, Owner, default));
        Assert.Equal("relationship-already-exists", error.Code);
        Assert.Equal(first.AttemptId, error.ExistingAttemptId);
        Assert.Single(await fixture.Db.Set<ServiceLinkAttempt>().ToListAsync());
        Assert.Equal(first.AttemptId, row.AttemptId);
    }

    [Fact]
    public async Task Approved_responder_returns_the_retained_callback_without_creating_new_consent()
    {
        await using var fixture = await Fixture.CreateAsync();
        var row = await fixture.ApprovedResponderAsync();
        var before = JsonSerializer.Serialize(row);
        var status = await fixture.Coordinator.AdminStatusAsync(row.AttemptId, Owner, default);
        Assert.Equal("return", status.AvailableAction);
        var navigation = await fixture.Coordinator.ContinueAsync(row.AttemptId, new(new string('s', 43)), Owner, default);
        Assert.Equal(row.AttemptId, navigation.AttemptId);
        Assert.StartsWith(fixture.Summary.InitiatorEndpointSnapshot.CallbackEndpoint + "?", navigation.NavigationUrl);
        Assert.Contains("pairing_code=" + new string('p', 43), navigation.NavigationUrl);
        Assert.Contains("browser_state=" + new string('b', 43), navigation.NavigationUrl);
        Assert.Equal(before, JsonSerializer.Serialize(row));
        Assert.Single(await fixture.Db.Set<ServiceLinkAttempt>().ToListAsync());
    }

    [Fact]
    public async Task A_second_responder_approval_reports_the_existing_relationship_before_creating_credentials()
    {
        await using var fixture = await Fixture.CreateAsync();
        var existing = await fixture.ApprovedResponderAsync();
        existing.PeerTenantId = fixture.Summary.Grants[0].CallerTenantId;
        existing.ActiveRelationshipKey = ServiceLinkValidation.Digest(existing.LocalTenantId + "\n" + existing.PeerInstanceId + "\n" + existing.PeerTenantId);
        var pending = await fixture.AddAsync("responder", "42");
        pending.PeerTenantId = existing.PeerTenantId;
        pending.ProtectedBrowserState = fixture.BrowserState(pending);
        await fixture.Db.SaveChangesAsync();
        var before = JsonSerializer.Serialize(pending);
        var error = await Assert.ThrowsAsync<ServiceLinkProtocolException>(() => fixture.Coordinator.RemoteApproveAsync(
            new(pending.AttemptId, "42", fixture.Summary.Grants, new string('b', 43)) { SessionBinding = new string('s', 43) }, Owner, default));
        Assert.Equal("relationship-already-exists", error.Code);
        Assert.Equal(existing.AttemptId, error.ExistingAttemptId);
        Assert.Equal(before, JsonSerializer.Serialize(pending));
        Assert.Equal(2, await fixture.Db.Set<ServiceLinkAttempt>().CountAsync());
    }

    [Theory]
    [InlineData("actor")]
    [InlineData("session")]
    [InlineData("authority")]
    [InlineData("expired")]
    [InlineData("grant-hash")]
    public async Task Returning_an_approved_callback_preserves_original_actor_session_authority_and_grant_fences(string fence)
    {
        await using var fixture = await Fixture.CreateAsync();
        var row = await fixture.ApprovedResponderAsync();
        var actor = fence == "actor" ? Administrator("other-actor") : Owner;
        var session = new string(fence == "session" ? 'x' : 's', 43);
        if (fence == "authority") fixture.Access.Tenants = [];
        if (fence == "expired") row.ExpiresAtUnixSeconds = 1;
        if (fence == "grant-hash") row.GrantHash = new string('0', 64);
        await fixture.Db.SaveChangesAsync();
        var before = JsonSerializer.Serialize(row);
        var error = await Assert.ThrowsAsync<ServiceLinkProtocolException>(() =>
            fixture.Coordinator.ContinueAsync(row.AttemptId, new(session), actor, default));
        Assert.Equal(fence == "grant-hash" ? 409 : 403, error.StatusCode);
        Assert.Equal(before, JsonSerializer.Serialize(row));
    }

    [Fact]
    public async Task A_different_browser_session_gets_an_existing_attempt_hint_without_reusing_its_proof()
    {
        await using var fixture = await Fixture.CreateAsync(initiator: true);
        var request = fixture.StartRequest();
        var first = await fixture.Coordinator.StartAsync(request, Owner, default);
        var error = await Assert.ThrowsAsync<ServiceLinkProtocolException>(() =>
            fixture.Coordinator.StartAsync(request with { SessionBinding = new string('x', 43) }, Owner, default));
        Assert.Equal("relationship-already-exists", error.Code);
        Assert.Equal(first.AttemptId, error.ExistingAttemptId);
        Assert.Single(await fixture.Db.Set<ServiceLinkAttempt>().ToListAsync());
        fixture.Access.Tenants = [];
        var denied = await Assert.ThrowsAsync<ServiceLinkProtocolException>(() => fixture.Coordinator.StartAsync(request, Owner, default));
        Assert.Null(denied.ExistingAttemptId);
        Assert.Equal(403, denied.StatusCode);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("42", true)]
    [InlineData("not-a-tenant", false)]
    [InlineData("999", false)]
    public async Task Remote_review_validates_explicit_tenant_before_binding_and_preserves_absent_selection(string? tenant, bool accepted)
    {
        await using var fixture = await Fixture.CreateAsync();
        var descriptor = await fixture.ProposalAsync(tenant);
        var request = new ServiceLinkRemoteReviewRequest(descriptor.InitiatorEndpointSnapshot.WebBaseUrl,
            descriptor.AttemptId, new string('b', 43)) { SessionBinding = new string('s', 43) };
        if (!accepted)
        {
            var error = await Assert.ThrowsAsync<ServiceLinkProtocolException>(() => fixture.Coordinator.RemoteReviewAsync(request, Owner, default));
            Assert.Equal("invalid-tenant", error.Code);
            Assert.Empty(await fixture.Db.Set<ServiceLinkAttempt>().ToListAsync());
            return;
        }
        await fixture.Coordinator.RemoteReviewAsync(request, Owner, default);
        var row = await fixture.Db.Set<ServiceLinkAttempt>().SingleAsync();
        Assert.Equal(tenant ?? "", row.LocalTenantId);
        Assert.Equal(descriptor.DescriptorHash, row.DescriptorHash);
        var status = await fixture.Coordinator.AdminStatusAsync(row.AttemptId, Owner, default);
        Assert.Equal("respond", status.AvailableAction);
        Assert.Null(status.GrantSummary);
        Assert.Equal(status.AvailableAction, Assert.Single(await fixture.Coordinator.AdminListAsync(Owner, default)).AvailableAction);
    }

    [Fact]
    public async Task Explicit_existing_tenant_requires_current_authority_before_a_responder_record_is_saved()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.Access.Tenants = [71];
        var descriptor = await fixture.ProposalAsync("42");
        var error = await Assert.ThrowsAsync<ServiceLinkProtocolException>(() => fixture.Coordinator.RemoteReviewAsync(
            new(descriptor.InitiatorEndpointSnapshot.WebBaseUrl, descriptor.AttemptId, new string('b', 43))
            { SessionBinding = new string('s', 43) }, Owner, default));
        Assert.Equal(403, error.StatusCode);
        Assert.Empty(await fixture.Db.Set<ServiceLinkAttempt>().ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Retained_malformed_unprepared_attempt_can_be_inspected_and_cancelled_idempotently_even_after_expiry(bool expired)
    {
        await using var fixture = await Fixture.CreateAsync();
        var row = await fixture.AddAsync("responder", "arbitrary-invalid-tenant");
        if (expired) { row.LifecycleState = "expired"; row.ExpiresAtUnixSeconds = 1; row.Decision = "abort"; row.AbortId = "retained-expiry-decision"; row.ProtectedBrowserState = null; }
        await fixture.Db.SaveChangesAsync();
        var descriptor = row.DescriptorJson; var hash = row.DescriptorHash;
        var before = await fixture.Coordinator.AdminStatusAsync(row.AttemptId, Owner, default);
        Assert.True(before.OrganizationBindingInvalid);
        Assert.Equal(!expired, before.CanCancel);
        Assert.Equal(expired, before.CanStartFresh);
        Assert.Equal("none", before.AvailableAction);
        Assert.Single(await fixture.Coordinator.AdminListAsync(Owner, default));
        var status = await fixture.Coordinator.AdminActionAsync(row.AttemptId, "cancel", new(), Owner, default);
        Assert.Equal("expired", status.LifecycleState);
        Assert.Equal("abort", status.Decision);
        var revision = row.Revision; var abort = row.AbortId;
        await fixture.Coordinator.AdminActionAsync(row.AttemptId, "cancel", new(), Owner, default);
        Assert.Equal(revision, row.Revision);
        Assert.Equal(abort, row.AbortId);
        Assert.Equal(descriptor, row.DescriptorJson);
        Assert.Equal(hash, row.DescriptorHash);
        Assert.Null(row.ProtectedBrowserState);
        Assert.Null(row.InboundPrincipalId);
        Assert.Null(row.ProtectedOutboundCredential);
        Assert.Single(await fixture.Db.Set<ServiceLinkAttempt>().ToListAsync());
        Assert.Single(await fixture.Coordinator.AdminListAsync(Owner, default));
    }

    [Theory]
    [InlineData("other-actor")]
    [InlineData("tenant-scoped")]
    [InlineData("permission-removed")]
    [InlineData("approved")]
    [InlineData("prepared")]
    [InlineData("committed")]
    [InlineData("exchange-dispatched")]
    [InlineData("orphan-principal")]
    public async Task Malformed_recovery_does_not_grant_authority_or_bypass_preparation_fences(string barrier)
    {
        await using var fixture = await Fixture.CreateAsync();
        var row = await fixture.AddAsync("responder", "invalid-id");
        var actor = Owner;
        switch (barrier)
        {
            case "other-actor": actor = Administrator("someone-else"); break;
            case "tenant-scoped": fixture.Access.Tenants = [42]; break;
            case "permission-removed": fixture.Access.Tenants = []; break;
            case "approved": row.GrantSummaryJson = JsonSerializer.Serialize(fixture.Summary); break;
            case "prepared": row.InboundPrincipalId = Guid.NewGuid(); row.LifecycleState = "prepared"; break;
            case "committed": row.Decision = "commit"; row.LifecycleState = "active"; break;
            case "exchange-dispatched": row.ExchangeDispatched = true; break;
            case "orphan-principal": fixture.Db.Set<ServicePrincipalRegistration>().Add(new() { Id = Guid.NewGuid(), AttemptId = row.AttemptId }); break;
        }
        await fixture.Db.SaveChangesAsync();
        var before = JsonSerializer.Serialize(row);
        foreach (var action in new[] { "cancel", "resume", "revoke" })
        {
            var error = await Assert.ThrowsAsync<ServiceLinkProtocolException>(() => fixture.Coordinator.AdminActionAsync(row.AttemptId, action, new(), actor, default));
            Assert.Equal(403, error.StatusCode);
        }
        await Assert.ThrowsAsync<ServiceLinkProtocolException>(() => fixture.Coordinator.AdminStatusAsync(row.AttemptId, actor, default));
        Assert.Equal(before, JsonSerializer.Serialize(row));
    }

    [Theory]
    [InlineData("initiator", "awaiting_approval", false, false, "continue")]
    [InlineData("responder", "awaiting_approval", false, false, "respond")]
    [InlineData("initiator", "approved", true, false, "review")]
    [InlineData("initiator", "approved", true, true, "resume")]
    [InlineData("responder", "prepared", true, true, "resume")]
    [InlineData("responder", "expired", false, false, "none")]
    [InlineData("initiator", "in_doubt", true, true, "resume")]
    public async Task List_and_status_project_the_durable_role_and_next_action(string role, string state, bool summary, bool principal, string action)
    {
        await using var fixture = await Fixture.CreateAsync();
        var row = await fixture.AddAsync(role, "42");
        row.LifecycleState = state;
        if (state == "in_doubt") { row.Decision = "abort"; row.ExchangeDispatched = true; }
        if (summary) { row.GrantSummaryJson = JsonSerializer.Serialize(fixture.Summary); row.LinkId = fixture.Summary.LinkId; }
        if (principal) row.InboundPrincipalId = Guid.NewGuid();
        await fixture.Db.SaveChangesAsync();
        var status = await fixture.Coordinator.AdminStatusAsync(row.AttemptId, Owner, default);
        Assert.Equal(role, status.LocalRole);
        Assert.Equal(action, status.AvailableAction);
        if (state == "in_doubt") Assert.False(status.CanStartFresh);
        Assert.Equal(action, Assert.Single(await fixture.Coordinator.AdminListAsync(Owner, default)).AvailableAction);
        if (action is "continue" or "respond" or "review")
        {
            var other = await fixture.Coordinator.AdminStatusAsync(row.AttemptId, Administrator("other"), default);
            Assert.Equal("none", other.AvailableAction);
            var error = await Assert.ThrowsAsync<ServiceLinkProtocolException>(() => fixture.Coordinator.AdminActionAsync(row.AttemptId, "resume", new(), Owner, default));
            Assert.Equal("approval-required", error.Code);
        }
    }

    [Fact]
    public async Task Expired_final_review_offers_fresh_setup_before_the_worker_sweeps_it()
    {
        await using var fixture = await Fixture.CreateAsync();
        var row = await fixture.AddAsync("initiator", "42");
        row.GrantSummaryJson = JsonSerializer.Serialize(fixture.Summary);
        row.LifecycleState = "approved";
        row.ExpiresAtUnixSeconds = 1;
        await fixture.Db.SaveChangesAsync();
        var status = await fixture.Coordinator.AdminStatusAsync(row.AttemptId, Owner, default);
        Assert.Equal("none", status.AvailableAction);
        Assert.True(status.CanStartFresh);
        Assert.True(status.CanCancel);
    }

    [Fact]
    public async Task Pending_responder_without_preset_remains_inspectable_after_cancel_and_requires_original_session_for_approval()
    {
        await using var fixture = await Fixture.CreateAsync();
        var row = await fixture.AddAsync("responder", "");
        var error = await Assert.ThrowsAsync<ServiceLinkProtocolException>(() => fixture.Coordinator.RemoteApproveAsync(
            new(row.AttemptId, "42", fixture.Summary.Grants, "") { SessionBinding = new string('x', 43) }, Owner, default));
        Assert.Equal("invalid-local-consent", error.Code);
        await fixture.Coordinator.AdminActionAsync(row.AttemptId, "cancel", new(), Owner, default);
        var status = await fixture.Coordinator.AdminStatusAsync(row.AttemptId, Owner, default);
        Assert.Equal("expired", status.LifecycleState);
        Assert.True(status.CanStartFresh);
        Assert.False(status.CanCancel);
    }

    private static ClaimsPrincipal Administrator(string id) => new(new ClaimsIdentity([new("netratel_principal_id", id)], "test"));
    private static ClaimsPrincipal Owner => Administrator("original-actor");

    private sealed class Fixture : IAsyncDisposable
    {
        public OrchestratorDbContext Db { get; }
        public ServiceLinkCoordinator Coordinator { get; }
        public Access Access { get; } = new();
        public ServiceLinkGrantSummary Summary { get; }
        private readonly PeerHandler peer = new();
        private readonly MemoryCache cache = new(new MemoryCacheOptions());
        private readonly HttpClient client;
        private readonly EphemeralDataProtectionProvider protection = new();

        private Fixture(ServiceLinkGrantSummary summary)
        {
            Summary = summary;
            // Transaction races remain in the existing PostgreSQL harness; this fixture covers authorization and durable projection.
            Db = new(new DbContextOptionsBuilder<OrchestratorDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString())
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)).Options);
            var local = summary.InitiatorEndpointSnapshot.Product == "netratel" ? summary.InitiatorEndpointSnapshot : summary.ResponderEndpointSnapshot;
            peer.Metadata = summary.InitiatorEndpointSnapshot.Product == "rateldesk" ? summary.InitiatorEndpointSnapshot : summary.ResponderEndpointSnapshot;
            var linking = new ServiceLinkOptions { Enabled = true, WebBaseUrl = local.WebBaseUrl, ApiBaseUrl = local.ApiBaseUrl,
                SourceInstanceId = local.SourceInstanceId, GatewayBaseUrl = local.GatewayBaseUrl };
            var settings = new Settings(new(new ServiceIdentityOptions { Enabled = true, InstanceId = local.InstanceId,
                WebBaseUrl = local.WebBaseUrl, ApiBaseUrl = local.ApiBaseUrl, Issuer = local.OauthIssuer, Audience = local.Audience }, linking, 1, []));
            client = new HttpClient(peer);
            var transport = new ServiceLinkTransport(client, Options.Create(linking));
            var profiles = new ServiceLinkProfileService(Db, settings, protection, transport, cache, TimeProvider.System);
            Coordinator = new(Db, null!, Access, profiles, transport, protection, Options.Create(linking), settings, TimeProvider.System,
                new ServiceLinkProtocolTokenCache(transport, cache, TimeProvider.System));
        }

        public static async Task<Fixture> CreateAsync(bool initiator = false)
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root is not null && !File.Exists(Path.Combine(root.FullName, "docs/contracts/bostec-service-link.incident-only.v1.fixtures.json"))) root = root.Parent;
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root!.FullName, "docs/contracts/bostec-service-link.incident-only.v1.fixtures.json")));
            var fixture = new Fixture(ServiceLinkCanonicalJson.Deserialize<ServiceLinkGrantSummary>(json.RootElement.GetProperty("roles")[initiator ? 1 : 0].GetProperty("grant_summary").GetRawText()));
            fixture.Db.Tenants.Add(new() { Id = 42, Name = "Fixture tenant" });
            await fixture.Db.SaveChangesAsync();
            return fixture;
        }

        public ServiceLinkStartRequest StartRequest() => new(Summary.ResponderEndpointSnapshot.WebBaseUrl, "42",
            Summary.Grants[0].TargetTenantId, Summary.Grants, new string('s', 43));

        public async Task<ServiceLinkAttempt> ApprovedResponderAsync()
        {
            var row = await AddAsync("responder", "42");
            var descriptor = ServiceLinkCanonicalJson.Deserialize<ServiceLinkRequestDescriptor>(row.DescriptorJson);
            var summary = ServiceLinkPayloadNormalization.Summary(Summary with { AttemptId = row.AttemptId,
                DescriptorHash = row.DescriptorHash, ExpiresAt = descriptor.ExpiresAt,
                ResponderEndpointSnapshot = descriptor.ResponderEndpointSnapshot });
            row.LinkId = summary.LinkId; row.GrantSummaryJson = JsonSerializer.Serialize(summary);
            row.GrantHash = ServiceLinkCanonicalJson.HashObject(summary, "grant_hash");
            row.LifecycleState = "approved"; row.InboundPrincipalId = Guid.NewGuid();
            row.PairingCodeHash = ServiceLinkValidation.Digest(new string('p', 43));
            row.ProtectedPairingCode = Protect(row, "pairing-code", new string('p', 43));
            row.ProtectedBrowserState = Protect(row, "browser-state", new string('b', 43));
            await Db.SaveChangesAsync();
            return row;
        }

        private string Protect(ServiceLinkAttempt row, string purpose, string clear) => protection.CreateProtector(
            "NetRatel.ServiceLink.v1", row.AttemptId, row.PeerInstanceId, purpose, "bootstrap").Protect(clear);

        public string BrowserState(ServiceLinkAttempt row) => Protect(row, "browser-state", new string('b', 43));

        public async Task<ServiceLinkRequestDescriptor> ProposalAsync(string? tenant)
        {
            var local = await Coordinator.MetadataAsync(default);
            var grants = Summary.Grants.Select(g => g.TargetProduct == "netratel"
                ? g with { TargetTenantId = tenant ?? "", ResourceConstraints = g.ResourceConstraints with { TenantId = tenant } }
                : g with { CallerTenantId = tenant ?? "" }).ToArray();
            var descriptor = new ServiceLinkRequestDescriptor { AttemptId = ServiceLinkValidation.NewId(),
                ExpiresAt = ServiceLinkValidation.Timestamp(DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds()),
                InitiatorInstanceId = Summary.InitiatorInstanceId, InitiatorTenantId = grants[0].CallerTenantId,
                ExpectedResponderInstanceId = local.InstanceId, RequestedResponderTenantId = tenant,
                InitiatorEndpointSnapshot = Summary.InitiatorEndpointSnapshot, ResponderEndpointSnapshot = local,
                InitiatorCallbackEndpoint = Summary.InitiatorEndpointSnapshot.CallbackEndpoint,
                CodeChallenge = new string('c', 43), RequestedGrants = grants };
            descriptor = ServiceLinkPayloadNormalization.Descriptor(descriptor);
            descriptor = descriptor with { DescriptorHash = ServiceLinkCanonicalJson.HashObject(descriptor, "descriptor_hash") };
            peer.Descriptor = descriptor;
            return descriptor;
        }

        public async Task<ServiceLinkAttempt> AddAsync(string role, string tenant)
        {
            var descriptor = await ProposalAsync(tenant);
            var row = new ServiceLinkAttempt { AttemptId = descriptor.AttemptId, Role = role, LocalTenantId = tenant,
                LocalActorId = "original-actor", PeerInstanceId = Summary.InitiatorInstanceId,
                DescriptorJson = JsonSerializer.Serialize(descriptor), DescriptorHash = descriptor.DescriptorHash,
                SessionBindingHash = ServiceLinkValidation.Digest(new string('s', 43)), ProtectedBrowserState = "unused-test-escrow", ProtectedVerifier = role == "initiator" ? "unused-test-verifier" : null,
                ExpiresAtUnixSeconds = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds() };
            Db.Add(row); await Db.SaveChangesAsync(); return row;
        }

        public async ValueTask DisposeAsync() { client.Dispose(); cache.Dispose(); await Db.DisposeAsync(); }
    }

    private sealed class PeerHandler : HttpMessageHandler
    {
        public ServiceLinkMetadata Metadata { get; set; } = null!;
        public ServiceLinkRequestDescriptor Descriptor { get; set; } = null!;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = request.RequestUri!.AbsolutePath == ServiceLinkContract.MetadataPath
                ? JsonContent.Create(Metadata) : JsonContent.Create(Descriptor) });
    }

    private sealed class Access : IEffectiveAccessService
    {
        public int[]? Tenants { get; set; }
        public Task<bool> AuthorizeAsync(ClaimsPrincipal actor, string permission, int? tenant, CancellationToken ct = default) => Task.FromResult(Tenants is null || tenant is not null && Tenants.Contains(tenant.Value));
        public Task<int[]?> GetAuthorizedTenantIdsAsync(ClaimsPrincipal actor, string permission, CancellationToken ct = default) => Task.FromResult(Tenants);
        public Task<EffectiveAccessSnapshot> GetSnapshotAsync(ClaimsPrincipal actor, int? tenant, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ReconcileBuiltInRolesAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class Settings(ServicePublicSettingsEffective settings) : IServicePublicSettingsResolver
    {
        public Task<ServicePublicSettingsEffective> ResolveAsync(CancellationToken ct = default) => Task.FromResult(settings);
        public Task<ServicePublicSettingsEffective> UpdateAsync(NetRatel.Shared.ServiceIdentity.ServicePublicSettingsUpdate update, string actorId, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
