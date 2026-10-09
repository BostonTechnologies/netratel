using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using NetRatel.Application.Flows;
using NetRatel.API.Services.RatelDesk;
using NetRatel.Application.RatelDesk;
using NetRatel.Infrastructure.RatelDesk;
using NetRatel.Shared.Contracts.Flows;
using NetRatel.Shared.Contracts.RatelDesk;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

/// <summary>Connector ownership and read-only preparation remain separate from the authenticated receipt receiver.</summary>
public sealed class RatelDeskConnectorTests
{
    private const string Credential = "rdk_01234567890123456789012345678901_synthetic-token";
    private static readonly Guid ConnectorId = Guid.Parse("55aad20b-12ce-4ba6-9379-a2c93c9e8b99");
    private static readonly RatelDeskConnectorConfiguration Config = new("Helpdesk", "https://rateldesk.example", "org-1", "customer-1", null, [], new(), true);
    private static ClaimsPrincipal Principal => new(new ClaimsIdentity([new Claim("netratel_principal_id", "owner")], "Test"));

    [Fact]
    public void Protected_credential_is_bound_to_tenant_and_connector_and_never_returned_in_dto()
    {
        var protector = new RatelDeskCredentialProtector(new EphemeralDataProtectionProvider());
        var cipher = protector.Protect(4, ConnectorId, Credential);
        Assert.DoesNotContain(Credential, cipher);
        Assert.Equal(Credential, protector.Unprotect(4, ConnectorId, cipher));
        Assert.Throws<CryptographicException>(() => protector.Unprotect(5, ConnectorId, cipher));
        Assert.Throws<CryptographicException>(() => protector.Unprotect(4, Guid.NewGuid(), cipher));
        var json = JsonSerializer.Serialize(RatelDeskConnectorService.ToDto(State(cipher)));
        Assert.DoesNotContain(Credential, json); Assert.DoesNotContain(cipher, json);
    }

    [Fact]
    public async Task Paired_connector_dry_run_sanitizes_preview_without_sending_a_business_request()
    {
        var store = new Store { Value = State("protected") };
        var service = new RatelDeskConnectorService(store, new Authorization(), null!);
        var preview = await service.DryRunAsync(4, ConnectorId, new("<b>Disk warning</b>", "<p>Low space</p>", 1), Principal, CancellationToken.None);
        Assert.Equal("Disk warning", preview.Payload.Title); Assert.Equal("Low space", preview.Payload.Description);
        Assert.Equal("dry-run-only", preview.FakeIncidentId); Assert.False(preview.SendsIncident);
        Assert.Equal(1, store.Reads);
    }

    [Fact]
    public async Task Alternate_connector_editor_cannot_create_unpaired_business_authority()
    {
        var store = new Store();
        var service = new RatelDeskConnectorService(store, new Authorization(), null!);
        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveAsync(4, ConnectorId, new(0, Config), Principal, default));
        Assert.Null(store.Value);
    }

    [Fact]
    public async Task Alternate_connector_editor_cannot_retarget_a_saved_customer_mapping()
    {
        var current = State("protected"); var store = new Store { Value = current };
        var service = new RatelDeskConnectorService(store, new Authorization(), null!);
        await Assert.ThrowsAsync<ArgumentException>(() => service.SaveAsync(4, ConnectorId,
            new(1, Config with { CustomerId = "foreign-customer" }), Principal, default));
        Assert.Equal(current, store.Value);
    }

    [Fact]
    public async Task Current_tenant_authorization_precedes_read_and_disabled_or_rotated_configuration_cannot_dispatch()
    {
        var store = new Store { Value = State("protected") }; var auth = new Authorization();
        var connector = new RatelDeskFlowConnector(store, auth);
        auth.Allowed = false;
        Assert.Null(await connector.GetAsync(4, ConnectorId, new("owner"))); Assert.Equal(0, store.Reads);
        auth.Allowed = true;
        var reference = await connector.GetAsync(4, ConnectorId, new("owner"));
        Assert.True(reference!.Enabled); Assert.False(reference.CanExecute);
        Assert.Equal(RatelDeskConnectorLimits.ReceiverUnavailableCode, reference.UnavailableReason);
        var draft = Draft(); var prepared = await connector.PrepareAsync(draft);
        Assert.Equal(FlowIncidentPreparationStatus.Ready, prepared.Status);
        var action = prepared.Action!;
        Assert.False(action.SupportsSafeReplay); Assert.Equal(draft.IdempotencyKey, action.IdempotencyKey);
        Assert.Equal(FlowContractValidation.Fingerprint(action), action.SemanticFingerprint);
        Assert.Contains("Resource: C:", action.Fields.Description); Assert.Contains(draft.Event.OccurrenceId.ToString("D"), action.Fields.Description);
        Assert.Equal(FlowIncidentActionResultKind.Unavailable, (await connector.DispatchAsync(action)).Kind);
        Assert.Equal(RatelDeskConnectorLimits.ReceiverUnavailableCode, (await connector.DispatchAsync(action)).Code);
        store.Value = store.Value! with { CredentialRevision = 2, ProtectedCredential = "rotated", RowVersion = 2 };
        Assert.Equal(action.SemanticFingerprint, FlowContractValidation.Fingerprint(action));
        Assert.Equal(RatelDeskConnectorLimits.ReceiverUnavailableCode, (await connector.DispatchAsync(action)).Code);
        store.Value = store.Value with { Revision = 2, Configuration = Config with { CustomerId = "new-customer" } };
        Assert.Equal("connector-revision-unavailable", (await connector.DispatchAsync(action)).Code);
        store.Value = State("protected") with { Configuration = Config with { Enabled = false } };
        Assert.Equal("connector-disabled-or-owner-denied", (await connector.DispatchAsync(action)).Code);
        auth.Allowed = false;
        Assert.Equal("connector-current-authority-denied", (await connector.DispatchAsync(action)).Code);
    }

    [Fact]
    public async Task Tampered_persisted_payload_is_rejected_and_same_semantic_key_is_not_replaced()
    {
        var connector = new RatelDeskFlowConnector(new Store { Value = State("protected") }, new Authorization());
        var action = (await connector.PrepareAsync(Draft())).Action!;
        Assert.Equal("persisted-incident-payload-invalid", (await connector.DispatchAsync(action with { Fields = action.Fields with { Description = "changed" } })).Code);
        Assert.Equal("persisted-incident-payload-invalid", (await connector.DispatchAsync(action with { Target = action.Target with { CustomerId = "foreign" } })).Code);
    }

    [Fact]
    public async Task Shared_limiter_bounds_same_connector_and_tenant_across_transport_instances_and_restores_slots()
    {
        var limiter = new RatelDeskTransportLimiter();
        using var first = await limiter.TryAcquireAsync(4, ConnectorId, CancellationToken.None);
        Assert.NotNull(first); Assert.Null(await limiter.TryAcquireAsync(4, ConnectorId, CancellationToken.None));
        IDisposable? second = null;
        for (var i = 0; i < 32 && second is null; i++) second = await limiter.TryAcquireAsync(4, Guid.NewGuid(), CancellationToken.None);
        Assert.NotNull(second);
        using (second)
        {
            Assert.Null(await limiter.TryAcquireAsync(4, Guid.NewGuid(), CancellationToken.None));
            first.Dispose();
            using var recovered = await limiter.TryAcquireAsync(4, ConnectorId, CancellationToken.None);
            Assert.NotNull(recovered);
        }
    }

    private static RatelDeskConnectorState State(string? cipher) => new(ConnectorId, 4, 1, 1, "owner", Config, cipher, 1, new(RatelDeskAuthenticationMode.PairedSystem, "00000000-0000-4000-8000-000000000017"));
    private static FlowIncidentActionDraft Draft()
    {
        var input = new FlowEventEnvelope(4, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow,
            new(Guid.NewGuid(), Guid.NewGuid(), "Disk free", "test-client", "C:", "disk.free.percent", "warning", 4, null, DateTimeOffset.UtcNow), new("owner"));
        return new(4, Guid.NewGuid(), Guid.NewGuid(), ConnectorId, 1, Guid.NewGuid(), "stable-action-key", input, new("<b>Disk warning</b>", "Space is low"));
    }
    private sealed class Authorization : IRatelDeskConnectorAuthorization
    {
        public bool Allowed { get; set; } = true;
        public Task<bool> CanManageAsync(ClaimsPrincipal p, int tenant, CancellationToken ct) => Task.FromResult(Allowed && tenant == 4);
        public Task<bool> CanExecuteAsync(string p, string? credential, int tenant, CancellationToken ct) => Task.FromResult(Allowed && p == "owner" && tenant == 4);
    }
    private sealed class Store : IRatelDeskConnectorStore
    {
        public RatelDeskConnectorState? Value { get; set; } public int Reads { get; private set; }
        public Task<RatelDeskConnectorState?> GetAsync(int tenant, Guid id, CancellationToken ct) { Reads++; return Task.FromResult(Value?.TenantId == tenant && Value.Id == id ? Value : null); }
        public Task<IReadOnlyList<RatelDeskConnectorState>> ListAsync(int tenant, CancellationToken ct) => Task.FromResult<IReadOnlyList<RatelDeskConnectorState>>(Value?.TenantId == tenant ? [Value] : []);
        public Task<bool> SaveAsync(RatelDeskConnectorState state, long expected, CancellationToken ct) { if ((Value?.RowVersion ?? 0) != expected) return Task.FromResult(false); Value = state; return Task.FromResult(true); }
    }
}
