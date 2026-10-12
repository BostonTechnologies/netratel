using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using NetRatel.API.Services.Notifications;
using NetRatel.Application.Flows;
using NetRatel.Application.Notifications;
using NetRatel.Application.RatelDesk;
using NetRatel.Infrastructure.Notifications;
using NetRatel.Shared.Contracts.Flows;
using NetRatel.Shared.Contracts.Monitoring;
using NetRatel.Shared.Contracts.Notifications;
using Xunit;

namespace NetRatel.Tests.Notifications;

public sealed class NotificationConnectorServiceTests
{
    private const int TenantId = 4;
    private const string DiscordUrl = "https://discord.com/api/webhooks/123456789/synthetic_discord_secret";
    private const string SlackUrl = "https://hooks.slack.com/services/T123/B456/synthetic_slack_secret";
    private static readonly Guid ConnectorId = Guid.Parse("138c875a-6747-4f90-8297-f6bf77400836");
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 10, 0, 0, TimeSpan.Zero);
    private static readonly NotificationConnectorConfiguration Configuration = new("Factory alerts", NotificationProvider.Discord, true);

    [Fact]
    public void Protection_is_bound_to_tenant_and_connector_and_DTOs_do_not_expose_the_credential()
    {
        var protector = new NotificationCredentialProtector(new EphemeralDataProtectionProvider());
        var cipher = protector.Protect(TenantId, ConnectorId, DiscordUrl);
        Assert.DoesNotContain(DiscordUrl, cipher);
        Assert.Equal(DiscordUrl, protector.Unprotect(TenantId, ConnectorId, cipher));
        Assert.Throws<CryptographicException>(() => protector.Unprotect(TenantId + 1, ConnectorId, cipher));
        Assert.Throws<CryptographicException>(() => protector.Unprotect(TenantId, Guid.NewGuid(), cipher));
        var state = new NotificationConnectorState(ConnectorId, TenantId, 1, "owner", Configuration, cipher);
        var dto = NotificationConnectorService.ToDto(state);
        Assert.True(dto.HasWebhook);
        var json = JsonSerializer.Serialize(dto);
        Assert.DoesNotContain("synthetic_discord_secret", json);
        Assert.DoesNotContain(cipher, json);
        Assert.DoesNotContain("WebhookUrl", json);
        Assert.DoesNotContain(cipher, state.ToString());
        Assert.DoesNotContain(DiscordUrl, new SaveNotificationConnectorRequest(0, Configuration, DiscordUrl).ToString());
    }

    [Fact]
    public async Task Saving_and_browsing_a_target_are_local_until_an_explicit_send_test()
    {
        var fixture = new Fixture();
        var saved = await fixture.Service.SaveAsync(TenantId, ConnectorId,
            new(0, Configuration with { Name = "  Factory alerts  " }, DiscordUrl), Principal());
        Assert.Equal(1, saved.Revision);
        Assert.Equal("Factory alerts", saved.Configuration.Name);
        Assert.True(saved.HasWebhook);
        Assert.Equal(DiscordUrl, fixture.Protector.Unprotect(TenantId, ConnectorId, fixture.Saved.ProtectedWebhookUrl));
        Assert.Single(await fixture.Service.ListAsync(TenantId, Principal()));
        var reference = await fixture.Connector.GetAsync(TenantId, ConnectorId, new("runner", "runner-credential"));
        Assert.True(reference!.CanExecute);
        Assert.Equal(FlowActionKind.Discord, reference.Kind);
        Assert.DoesNotContain("synthetic_discord_secret", JsonSerializer.Serialize(reference));
        Assert.Empty(fixture.Transport.Sends);

        var result = await fixture.Service.SendTestAsync(TenantId, ConnectorId, Principal());
        Assert.Equal(NotificationDeliveryStatus.Succeeded, result.Status);
        var send = Assert.Single(fixture.Transport.Sends);
        Assert.Equal(DiscordUrl, send.Url);
        Assert.Contains("NetRatel test notification", send.Message);
        Assert.Contains("Factory alerts", send.Message);
        Assert.Contains("2026-10-12 10:00:00 UTC", send.Message);
        Assert.DoesNotContain("synthetic_discord_secret", send.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Editing_configuration_without_a_new_URL_preserves_the_saved_ciphertext(string? replacement)
    {
        var fixture = new Fixture();
        await fixture.SaveAsync();
        var cipher = fixture.Saved.ProtectedWebhookUrl;
        var updated = await fixture.Service.SaveAsync(TenantId, ConnectorId,
            new(1, Configuration with { Name = "Production alerts", DiscordUsername = "NetRatel" }, replacement), Principal());
        Assert.Equal(2, updated.Revision);
        Assert.Equal("Production alerts", updated.Configuration.Name);
        Assert.Equal(cipher, fixture.Saved.ProtectedWebhookUrl);
        Assert.Equal(1, fixture.Protector.ProtectCalls);
        Assert.Empty(fixture.Transport.Sends);
    }

    [Fact]
    public async Task Stale_revision_and_a_concurrent_write_cannot_replace_the_saved_secret()
    {
        var fixture = new Fixture();
        await fixture.SaveAsync();
        var previous = fixture.Saved;
        var stale = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.SaveAsync(TenantId, ConnectorId,
            new(0, Configuration, DiscordUrl + "_replacement"), Principal()));
        Assert.Equal("notification-connector-conflict", stale.Message);
        Assert.Equal(previous, fixture.Saved);
        fixture.Store.RejectNextSave = true;
        var raced = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.SaveAsync(TenantId, ConnectorId,
            new(1, Configuration, DiscordUrl + "_replacement"), Principal()));
        Assert.Equal("notification-connector-conflict", raced.Message);
        Assert.Equal(previous, fixture.Saved);
        Assert.Empty(fixture.Transport.Sends);
    }

    [Fact]
    public async Task Changing_provider_requires_a_new_webhook_for_that_provider()
    {
        var fixture = new Fixture();
        await fixture.SaveAsync();
        var previous = fixture.Saved;
        var slack = Configuration with { Provider = NotificationProvider.Slack };
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.SaveAsync(TenantId, ConnectorId, new(1, slack), Principal()));
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.SaveAsync(TenantId, ConnectorId, new(1, slack, DiscordUrl), Principal()));
        Assert.Equal(previous, fixture.Saved);
        var updated = await fixture.Service.SaveAsync(TenantId, ConnectorId, new(1, slack, SlackUrl), Principal());
        Assert.Equal(NotificationProvider.Slack, updated.Configuration.Provider);
        Assert.Equal(SlackUrl, fixture.Protector.Unprotect(TenantId, ConnectorId, fixture.Saved.ProtectedWebhookUrl));
        Assert.Empty(fixture.Transport.Sends);
    }

    [Fact]
    public async Task Delete_clears_the_secret_and_hides_the_tombstone_from_targets_without_allowing_resurrection()
    {
        var fixture = new Fixture();
        await fixture.SaveAsync();
        await fixture.Service.DeleteAsync(TenantId, ConnectorId, 1, Principal());
        Assert.Equal(2, fixture.Saved.Revision);
        Assert.Equal(Now, fixture.Saved.DeletedAtUtc);
        Assert.False(fixture.Saved.Configuration.Enabled);
        Assert.Empty(fixture.Saved.ProtectedWebhookUrl);
        Assert.False(NotificationConnectorService.ToDto(fixture.Saved).HasWebhook);
        Assert.Empty(await fixture.Service.ListAsync(TenantId, Principal()));
        Assert.Null(await fixture.Connector.GetAsync(TenantId, ConnectorId, new("runner")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.SaveAsync(TenantId, ConnectorId,
            new(2, Configuration, DiscordUrl), Principal()));
        Assert.Equal(NotificationDeliveryStatus.Unavailable, (await fixture.Service.SendTestAsync(TenantId, ConnectorId, Principal())).Status);
        Assert.Empty(fixture.Transport.Sends);
    }

    [Fact]
    public async Task Management_authority_is_checked_before_reading_or_mutating_a_tenant()
    {
        var fixture = new Fixture();
        fixture.Authorization.ManageAllowed = false;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.ListAsync(TenantId, Principal()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.SaveAsync(TenantId, ConnectorId, new(0, Configuration, DiscordUrl), Principal()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.DeleteAsync(TenantId, ConnectorId, 1, Principal()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.SendTestAsync(TenantId, ConnectorId, Principal()));
        Assert.Equal(0, fixture.Store.Reads);
        Assert.Empty(fixture.Store.Values);
        Assert.Empty(fixture.Transport.Sends);
    }

    [Fact]
    public async Task Revoked_actor_execution_permission_prevents_save_and_explicit_test()
    {
        var fixture = new Fixture();
        await fixture.SaveAsync();
        fixture.Authorization.DeniedCredentials.Add("operator-credential");
        var actor = Principal("operator", "operator-credential");
        var reads = fixture.Store.Reads;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.SaveAsync(TenantId, ConnectorId,
            new(1, Configuration with { Name = "Changed" }), actor));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.SendTestAsync(TenantId, ConnectorId, actor));
        Assert.Equal(reads, fixture.Store.Reads);
        Assert.Equal(1, fixture.Saved.Revision);
        Assert.Empty(fixture.Transport.Sends);
    }

    [Fact]
    public async Task Catalog_checks_current_authority_before_read_and_exposes_disabled_targets_as_unusable()
    {
        var fixture = new Fixture();
        await fixture.SaveAsync();
        var authority = new FlowExecutionAuthorityDto("runner", "runner-credential");
        fixture.Authorization.DeniedCredentials.Add("runner-credential");
        var reads = fixture.Store.Reads;
        Assert.Null(await fixture.Connector.GetAsync(TenantId, ConnectorId, authority));
        Assert.Empty(await fixture.Connector.ListAsync(TenantId, authority));
        Assert.Equal(reads, fixture.Store.Reads);
        fixture.Authorization.DeniedCredentials.Clear();
        fixture.Replace(fixture.Saved with { Configuration = Configuration with { Enabled = false } });
        var target = Assert.Single(await fixture.Connector.ListAsync(TenantId, authority));
        Assert.False(target.Enabled);
        Assert.False(target.CanExecute);
        Assert.DoesNotContain("synthetic_discord_secret", JsonSerializer.Serialize(target));
        Assert.Equal(0, fixture.Protector.UnprotectCalls);
        Assert.Empty(fixture.Transport.Sends);
    }

    [Theory]
    [InlineData("actor", FlowIncidentActionResultKind.Failed)]
    [InlineData("credential", FlowIncidentActionResultKind.Failed)]
    [InlineData("tenant", FlowIncidentActionResultKind.Failed)]
    [InlineData("event-tenant", FlowIncidentActionResultKind.Failed)]
    [InlineData("owner", FlowIncidentActionResultKind.Unavailable)]
    [InlineData("revision", FlowIncidentActionResultKind.Unavailable)]
    [InlineData("provider", FlowIncidentActionResultKind.Unavailable)]
    [InlineData("disabled", FlowIncidentActionResultKind.Unavailable)]
    [InlineData("deleted", FlowIncidentActionResultKind.Unavailable)]
    public async Task Dispatch_rechecks_authority_scope_provider_and_current_target_before_decrypting(string change, FlowIncidentActionResultKind expected)
    {
        var fixture = new Fixture();
        await fixture.SaveAsync();
        var draft = Draft();
        switch (change)
        {
            case "actor": fixture.Authorization.DeniedPrincipals.Add("runner"); break;
            case "credential": fixture.Authorization.DeniedCredentials.Add("runner-credential"); break;
            case "tenant": draft = draft with { TenantId = TenantId + 1, Event = draft.Event with { TenantId = TenantId + 1 } }; break;
            case "event-tenant": draft = draft with { Event = draft.Event with { TenantId = TenantId + 1 } }; break;
            case "owner": fixture.Authorization.DeniedPrincipals.Add("owner"); break;
            case "revision": fixture.Replace(fixture.Saved with { Revision = 2 }); break;
            case "provider": fixture.Replace(fixture.Saved with { Configuration = Configuration with { Provider = NotificationProvider.Slack } }); break;
            case "disabled": fixture.Replace(fixture.Saved with { Configuration = Configuration with { Enabled = false } }); break;
            case "deleted": fixture.Replace(fixture.Saved with { DeletedAtUtc = Now }); break;
        }
        var result = await fixture.Connector.DispatchAsync(draft);
        Assert.Equal(expected, result.Kind);
        Assert.Equal(0, fixture.Protector.UnprotectCalls);
        Assert.Empty(fixture.Transport.Sends);
    }

    [Theory]
    [InlineData("fields")]
    [InlineData("event")]
    [InlineData("source")]
    [InlineData("run")]
    [InlineData("action")]
    [InlineData("connector")]
    [InlineData("key")]
    public async Task Malformed_persisted_action_drafts_fail_before_store_or_transport_access(string missing)
    {
        var fixture = new Fixture();
        await fixture.SaveAsync();
        var draft = Draft();
        draft = missing switch
        {
            "fields" => draft with { Fields = null! },
            "event" => draft with { Event = null! },
            "source" => draft with { SourceInstanceId = Guid.Empty },
            "run" => draft with { RunId = Guid.Empty },
            "action" => draft with { ActionNodeId = Guid.Empty },
            "connector" => draft with { ConnectorId = Guid.Empty },
            _ => draft with { IdempotencyKey = "invalid\0key" }
        };
        var reads = fixture.Store.Reads;
        var result = await fixture.Connector.DispatchAsync(draft);
        Assert.Equal(FlowIncidentActionResultKind.Failed, result.Kind);
        Assert.Equal("invalid-notification-action", result.Code);
        Assert.Equal(reads, fixture.Store.Reads);
        Assert.Equal(0, fixture.Protector.UnprotectCalls);
        Assert.Empty(fixture.Transport.Sends);
    }

    [Fact]
    public async Task A_credential_copied_from_another_tenant_cannot_be_used_for_a_test_or_flow()
    {
        var fixture = new Fixture();
        await fixture.SaveAsync();
        fixture.Replace(fixture.Saved with { ProtectedWebhookUrl = fixture.Protector.Protect(TenantId + 1, ConnectorId, DiscordUrl) });
        var flow = await fixture.Connector.DispatchAsync(Draft());
        var test = await fixture.Service.SendTestAsync(TenantId, ConnectorId, Principal());
        Assert.Equal(FlowIncidentActionResultKind.Unavailable, flow.Kind);
        Assert.Equal(NotificationDeliveryStatus.Unavailable, test.Status);
        Assert.Equal("notification-credential-unavailable", flow.Code);
        Assert.Equal("notification-credential-unavailable", test.Code);
        Assert.Empty(fixture.Transport.Sends);
    }

    [Theory]
    [InlineData(NotificationDeliveryStatus.Succeeded, FlowIncidentActionResultKind.Succeeded)]
    [InlineData(NotificationDeliveryStatus.Rejected, FlowIncidentActionResultKind.Failed)]
    [InlineData(NotificationDeliveryStatus.RateLimited, FlowIncidentActionResultKind.RetryableSafe)]
    [InlineData(NotificationDeliveryStatus.DeliveryUnknown, FlowIncidentActionResultKind.DeliveryUnknown)]
    [InlineData(NotificationDeliveryStatus.Unavailable, FlowIncidentActionResultKind.Unavailable)]
    public async Task Dispatch_preserves_delivery_certainty_and_provider_retry_delay(NotificationDeliveryStatus status, FlowIncidentActionResultKind expected)
    {
        var fixture = new Fixture();
        await fixture.SaveAsync();
        fixture.Transport.Result = new(status, "safe-provider-code", status == NotificationDeliveryStatus.Succeeded ? "123456789" : null,
            status == NotificationDeliveryStatus.RateLimited ? TimeSpan.FromSeconds(1.25) : null);
        var result = await fixture.Connector.DispatchAsync(Draft());
        Assert.Equal(expected, result.Kind);
        Assert.Equal("safe-provider-code", result.Code);
        Assert.Equal(fixture.Transport.Result.RetryAfter, result.RetryAfter);
        Assert.Equal(status == NotificationDeliveryStatus.Succeeded ? "123456789" : null, result.Receipt?.IncidentId);
        var send = Assert.Single(fixture.Transport.Sends);
        Assert.Equal(NotificationProvider.Discord, send.Provider);
        Assert.Equal(DiscordUrl, send.Url);
        Assert.DoesNotContain("synthetic_discord_secret", send.Message);
    }

    [Fact]
    public void Normal_alert_format_keeps_context_and_valid_unicode_with_a_bounded_description()
    {
        var draft = Draft();
        draft = draft with
        {
            Fields = new("Disk warning", new string('d', 998) + "😀" + new string('d', 3000)),
            Event = draft.Event with { Data = draft.Event.Data with { RuleName = "Factory disk rule", ClientName = "dispatch-server", Resource = "/data" } }
        };
        var text = NotificationMessageFormatter.Format(draft);
        Assert.True(text.Length <= NotificationConnectorLimits.MaximumMessageLength);
        Assert.Contains("Rule: Factory disk rule", text);
        Assert.Contains("Client: dispatch-server", text);
        Assert.Contains("Resource: /data", text);
        Assert.Contains(draft.Event.OccurrenceId.ToString("D"), text);
        Assert.Contains("…", text);
        _ = new UTF8Encoding(false, true).GetBytes(text);
    }

    [Theory]
    [InlineData(2, "hv01, hv02", false, "2 clients are offline: hv01, hv02.")]
    [InlineData(18, "", true, "18 clients are offline.")]
    [InlineData(2, "", true, "2 clients are offline.")]
    public async Task Offline_notifications_use_one_bounded_scope_summary(int count, string names, bool truncated, string summary)
    {
        var fixture = new Fixture();
        await fixture.SaveAsync();
        var draft = Draft();
        draft = draft with
        {
            Fields = new("Factory offline", "Original template description"),
            Event = draft.Event with
            {
                Data = draft.Event.Data with
                {
                    AgentId = Guid.Empty, Resource = MonitoringOfflinePresentation.ResourceKey, Metric = "ClientOffline", NumericValue = count,
                    OfflineSummary = new(count, 20, names, truncated)
                }
            }
        };
        var result = await fixture.Connector.DispatchAsync(draft);
        Assert.Equal(FlowIncidentActionResultKind.Succeeded, result.Kind);
        var message = Assert.Single(fixture.Transport.Sends).Message;
        Assert.Contains(summary, message);
        Assert.True(message.Length <= NotificationConnectorLimits.MaximumMessageLength);
        Assert.DoesNotContain("Client: ", message);
        Assert.DoesNotContain("more than 10", message, StringComparison.OrdinalIgnoreCase);
    }

    private static ClaimsPrincipal Principal(string id = "owner", string? credential = null)
    {
        var claims = new List<Claim> { new("netratel_principal_id", id) };
        if (credential is not null) claims.Add(new("netratel_integration_credential_id", credential));
        return new(new ClaimsIdentity(claims, "Test"));
    }

    private static FlowIncidentActionDraft Draft()
    {
        var data = new FlowEventDataDto(Guid.NewGuid(), Guid.NewGuid(), "Disk free", "test-client", "/data", "disk.free.percent", "warning", 4, null, Now);
        var envelope = new FlowEventEnvelope(TenantId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Now, data, new("runner", "runner-credential"));
        return new(TenantId, Guid.NewGuid(), Guid.NewGuid(), ConnectorId, 1, Guid.NewGuid(), "stable-notification-key", envelope,
            new("Disk warning", "Space is low.")) { Kind = FlowActionKind.Discord };
    }

    private sealed class Fixture
    {
        internal Store Store { get; } = new();
        internal Authorization Authorization { get; } = new();
        internal TrackingProtector Protector { get; } = new();
        internal RecordingTransport Transport { get; } = new();
        internal NotificationConnectorService Service { get; }
        internal NotificationFlowConnector Connector { get; }
        internal NotificationConnectorState Saved => Store.Values[(TenantId, ConnectorId)];
        internal Fixture()
        {
            Service = new(Store, Authorization, Protector, Transport, new FixedClock());
            Connector = new(Store, Authorization, Protector, Transport);
        }
        internal Task<NotificationConnectorDto> SaveAsync() => Service.SaveAsync(TenantId, ConnectorId, new(0, Configuration, DiscordUrl), Principal());
        internal void Replace(NotificationConnectorState state) => Store.Values[(state.TenantId, state.Id)] = state;
    }

    private sealed class Authorization : IRatelDeskConnectorAuthorization
    {
        internal bool ManageAllowed { get; set; } = true;
        internal HashSet<string> DeniedPrincipals { get; } = [];
        internal HashSet<string> DeniedCredentials { get; } = [];
        public Task<bool> CanManageAsync(ClaimsPrincipal principal, int tenantId, CancellationToken ct) =>
            Task.FromResult(ManageAllowed && tenantId == TenantId);
        public Task<bool> CanExecuteAsync(string principalId, string? integrationCredentialId, int tenantId, CancellationToken ct) =>
            Task.FromResult(tenantId == TenantId && !DeniedPrincipals.Contains(principalId) &&
                (integrationCredentialId is null || !DeniedCredentials.Contains(integrationCredentialId)));
    }

    private sealed class Store : INotificationConnectorStore
    {
        internal Dictionary<(int Tenant, Guid Id), NotificationConnectorState> Values { get; } = [];
        internal int Reads { get; private set; }
        internal bool RejectNextSave { get; set; }
        public Task<NotificationConnectorState?> GetAsync(int tenantId, Guid id, CancellationToken ct)
        {
            Reads++;
            return Task.FromResult(Values.GetValueOrDefault((tenantId, id)));
        }
        public Task<IReadOnlyList<NotificationConnectorState>> ListAsync(int tenantId, CancellationToken ct)
        {
            Reads++;
            return Task.FromResult<IReadOnlyList<NotificationConnectorState>>(Values.Values.Where(value => value.TenantId == tenantId && value.DeletedAtUtc is null).ToArray());
        }
        public Task<bool> SaveAsync(NotificationConnectorState state, long expectedRevision, CancellationToken ct)
        {
            var current = Values.GetValueOrDefault((state.TenantId, state.Id));
            if (RejectNextSave) { RejectNextSave = false; return Task.FromResult(false); }
            if ((current?.Revision ?? 0) != expectedRevision || current?.DeletedAtUtc is not null) return Task.FromResult(false);
            Values[(state.TenantId, state.Id)] = state;
            return Task.FromResult(true);
        }
    }

    private sealed class TrackingProtector : INotificationCredentialProtector
    {
        private readonly NotificationCredentialProtector _inner = new(new EphemeralDataProtectionProvider());
        internal int ProtectCalls { get; private set; }
        internal int UnprotectCalls { get; private set; }
        public string Protect(int tenantId, Guid connectorId, string webhookUrl)
        {
            ProtectCalls++;
            return _inner.Protect(tenantId, connectorId, webhookUrl);
        }
        public string Unprotect(int tenantId, Guid connectorId, string ciphertext)
        {
            UnprotectCalls++;
            return _inner.Unprotect(tenantId, connectorId, ciphertext);
        }
    }

    private sealed record SentNotification(NotificationProvider Provider, string Url, string Message);
    private sealed class RecordingTransport : INotificationWebhookTransport
    {
        internal List<SentNotification> Sends { get; } = [];
        internal NotificationDeliveryResult Result { get; set; } = new(NotificationDeliveryStatus.Succeeded, "notification-delivered", "123456789");
        public Task<NotificationDeliveryResult> SendAsync(NotificationProvider provider, string webhookUrl, string message,
            string? discordUsername, string? discordThreadId, CancellationToken ct)
        {
            Sends.Add(new(provider, webhookUrl, message));
            return Task.FromResult(Result);
        }
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
