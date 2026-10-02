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

/// <summary>Deterministic normal-create/lookup fixtures reflect reviewed RatelDesk DTOs, not the missing #116 receiver.</summary>
public sealed class RatelDeskConnectorTests
{
    private const string Credential = "rdk_01234567890123456789012345678901_synthetic-token";
    private static readonly Guid ConnectorId = Guid.Parse("55aad20b-12ce-4ba6-9379-a2c93c9e8b99");
    private static readonly RatelDeskConnectorConfiguration Config = new("Helpdesk", "https://rateldesk.example", "org-1", "customer-1", null, [], new(), true);
    private static IRatelDeskOriginPolicy Policy(params string[] origins) => new RatelDeskOriginPolicy(new ConfigurationBuilder()
        .AddInMemoryCollection(origins.Select((origin, index) => new KeyValuePair<string, string?>($"RatelDesk:AllowedOrigins:{index}", origin))).Build());
    private static ClaimsPrincipal Principal => new(new ClaimsIdentity([new Claim("netratel_principal_id", "owner")], "Test"));

    [Theory]
    [InlineData("http://rateldesk.example")]
    [InlineData("https://foreign.example")]
    [InlineData("https://user:secret@rateldesk.example")]
    [InlineData("https://rateldesk.example/path")]
    [InlineData("https://rateldesk.example?token=secret")]
    [InlineData("https://rateldesk.example/#fragment")]
    public void Origin_policy_requires_operator_approved_exact_https_origin(string origin) => Assert.False(Policy(Config.Origin).TryValidate(origin, out _));

    [Fact]
    public void Explicit_self_hosted_origin_is_accepted_and_default_is_closed()
    {
        Assert.False(Policy().TryValidate(Config.Origin, out _));
        Assert.True(Policy("https://helpdesk.internal:9443").TryValidate("https://HELPDESK.internal:9443/", out var uri));
        Assert.Equal("https://helpdesk.internal:9443/", uri!.AbsoluteUri);
    }

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
    public async Task Configuration_and_credential_rotation_preserve_separate_revisions_and_dry_run_has_no_http()
    {
        var store = new Store(); var auth = new Authorization(); var tester = new Tester();
        var service = new RatelDeskConnectorService(store, auth, new RatelDeskCredentialProtector(new EphemeralDataProtectionProvider()), Policy(Config.Origin), tester);
        var configured = await service.SaveAsync(4, ConnectorId, new(0, Config), Principal, CancellationToken.None);
        Assert.Equal(1, configured.Revision); Assert.False(configured.HasCredential);
        var rotated = await service.RotateAsync(4, ConnectorId, new(0, Credential), Principal, CancellationToken.None);
        Assert.Equal(configured.Revision, rotated.Revision); Assert.Equal(1, rotated.CredentialRevision);
        Assert.False(rotated.AutomaticDeliveryAvailable);
        var preview = await service.DryRunAsync(4, ConnectorId, new("<b>Disk warning</b>", "<p>Low space</p>", 1), Principal, CancellationToken.None);
        Assert.Equal("Disk warning", preview.Payload.Title); Assert.Equal("Low space", preview.Payload.Description);
        Assert.Equal("dry-run-only", preview.FakeIncidentId); Assert.False(preview.SendsIncident); Assert.Equal(0, tester.Calls);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RotateAsync(4, ConnectorId, new(0, Credential), Principal, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => service.RotateAsync(4, ConnectorId, new(1, "nrt_ic_agent-enrollment-is-not-a-helpdesk-token"), Principal, CancellationToken.None));
    }

    [Fact]
    public async Task Current_tenant_authorization_precedes_read_and_disabled_or_rotated_configuration_cannot_dispatch()
    {
        var store = new Store { Value = State("protected") }; var auth = new Authorization();
        var connector = new RatelDeskFlowConnector(store, auth, Policy(Config.Origin));
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
        var connector = new RatelDeskFlowConnector(new Store { Value = State("protected") }, new Authorization(), Policy(Config.Origin));
        var action = (await connector.PrepareAsync(Draft())).Action!;
        Assert.Equal("persisted-incident-payload-invalid", (await connector.DispatchAsync(action with { Fields = action.Fields with { Description = "changed" } })).Code);
        Assert.Equal("persisted-incident-payload-invalid", (await connector.DispatchAsync(action with { Target = action.Target with { CustomerId = "foreign" } })).Code);
    }

    [Fact]
    public async Task Connection_test_uses_actual_read_only_organization_customer_routes_and_reports_missing_receiver_capability()
    {
        var requests = new List<string>();
        using var server = Server(async context =>
        {
            requests.Add(context.Request.Method + " " + context.Request.Path + context.Request.QueryString);
            Assert.Equal("Bearer " + Credential, context.Request.Headers.Authorization);
            await context.Response.WriteAsJsonAsync(context.Request.Path.Value!.EndsWith("organizations", StringComparison.Ordinal)
                ? new[] { new { id = "org-1", organizationId = (string?)null } }
                : new[] { new { id = "customer-1", organizationId = "org-1" } });
        });
        var result = await Transport(server).TestAsync(4, ConnectorId, Config, Credential, CancellationToken.None);
        Assert.Equal(RatelDeskConnectionTestStatus.MappingValidated, result.Status); Assert.False(result.AutomaticDeliveryAvailable);
        Assert.Equal(RatelDeskConnectorLimits.ReceiverUnavailableCode, result.Code);
        Assert.Equal(2, requests.Count); Assert.All(requests, request => Assert.StartsWith("GET ", request));
        Assert.Contains("organizationId=org-1", requests[1]);
    }

    [Theory]
    [InlineData(401, RatelDeskConnectionTestStatus.AuthenticationRejected)]
    [InlineData(403, RatelDeskConnectionTestStatus.AuthenticationRejected)]
    [InlineData(429, RatelDeskConnectionTestStatus.Unavailable)]
    public async Task Read_only_test_bounds_auth_failures_and_retry_after(int status, RatelDeskConnectionTestStatus expected)
    {
        using var server = Server(context => { context.Response.StatusCode = status; context.Response.Headers.RetryAfter = "999999"; return Task.CompletedTask; });
        var result = await Transport(server).TestAsync(4, ConnectorId, Config, Credential, CancellationToken.None);
        Assert.Equal(expected, result.Status); if (status == 429) Assert.Equal(300, result.RetryAfterSeconds);
    }

    [Fact]
    public async Task Foreign_customer_mapping_is_rejected_without_incident_creation()
    {
        using var server = Server(context => context.Response.WriteAsJsonAsync(new[] { new { id = "customer-1", organizationId = "foreign" } }));
        Assert.Equal(RatelDeskConnectionTestStatus.MappingRejected, (await Transport(server).TestAsync(4, ConnectorId, Config, Credential, CancellationToken.None)).Status);
    }

    [Fact]
    public async Task Normal_create_uses_reviewed_numeric_priority_and_fields_without_invented_queue_or_idempotency_header()
    {
        var incidentCount = 0;
        using var server = Server(async context =>
        {
            Assert.Equal("POST", context.Request.Method); Assert.Equal("/api/v1/incidents/", context.Request.Path);
            Assert.False(context.Request.Headers.ContainsKey("Idempotency-Key"));
            using var body = await JsonDocument.ParseAsync(context.Request.Body);
            var root = body.RootElement;
            Assert.Equal(JsonValueKind.Number, root.GetProperty("priority").ValueKind); Assert.Equal(2, root.GetProperty("priority").GetInt32());
            Assert.Equal("customer-1", root.GetProperty("customerId").GetString()); Assert.Equal("org-1", root.GetProperty("organizationId").GetString());
            Assert.Equal(7, root.EnumerateObject().Count()); Assert.False(root.TryGetProperty("queueId", out _));
            incidentCount++; context.Response.StatusCode = 201;
            await context.Response.WriteAsJsonAsync(new { id = "incident-1", trackingId = "INC-1", organizationId = "org-1", customerId = "customer-1" });
        });
        var result = await Transport(server).CreateAsync(4, ConnectorId, Config.Origin, Credential, Payload(), CancellationToken.None);
        Assert.Equal(RatelDeskDeliveryStatus.Succeeded, result.Status); Assert.Equal("incident-1", result.Receipt!.IncidentId);
        Assert.Null(result.Receipt.SafeLink); Assert.Equal(1, incidentCount);
    }

    [Theory]
    [InlineData(400, RatelDeskDeliveryStatus.PayloadRejected)]
    [InlineData(401, RatelDeskDeliveryStatus.AuthenticationRejected)]
    [InlineData(403, RatelDeskDeliveryStatus.AuthenticationRejected)]
    [InlineData(429, RatelDeskDeliveryStatus.RateLimited)]
    [InlineData(302, RatelDeskDeliveryStatus.DeliveryUnknown)]
    [InlineData(500, RatelDeskDeliveryStatus.DeliveryUnknown)]
    public async Task Bounded_normal_transport_does_not_retry_terminal_or_uncertain_responses(int status, RatelDeskDeliveryStatus expected)
    {
        var sends = 0;
        using var server = Server(context => { sends++; context.Response.StatusCode = status; context.Response.Headers.Location = "https://foreign.example/incident"; context.Response.Headers.RetryAfter = "30"; return Task.CompletedTask; });
        var result = await Transport(server).CreateAsync(4, ConnectorId, Config.Origin, Credential, Payload(), CancellationToken.None);
        Assert.Equal(expected, result.Status); Assert.Equal(1, sends); if (status == 429) Assert.Equal(30, result.RetryAfterSeconds);
    }

    [Fact]
    public async Task Lost_response_after_remote_commit_is_delivery_unknown_and_never_blindly_retried()
    {
        var incidents = 0; var confirmations = 0;
        using var server = Server(async context =>
        {
            incidents++; confirmations++; context.Response.StatusCode = 201;
            await context.Response.WriteAsync("{\"id\":\"incident-1\"");
        });
        var result = await Transport(server).CreateAsync(4, ConnectorId, Config.Origin, Credential, Payload(), CancellationToken.None);
        Assert.Equal(RatelDeskDeliveryStatus.DeliveryUnknown, result.Status); Assert.Null(result.Receipt);
        Assert.Equal(1, incidents); Assert.Equal(1, confirmations);
    }

    [Fact]
    public async Task Oversized_response_after_create_is_unknown_and_operation_slots_are_released()
    {
        using var server = Server(async context => { context.Response.StatusCode = 201; await context.Response.WriteAsync(new string('x', RatelDeskConnectorLimits.MaximumResponseBytes + 1)); });
        var transport = Transport(server);
        Assert.Equal(RatelDeskDeliveryStatus.DeliveryUnknown, (await transport.CreateAsync(4, ConnectorId, Config.Origin, Credential, Payload(), CancellationToken.None)).Status);
        Assert.Equal(RatelDeskDeliveryStatus.DeliveryUnknown, (await transport.CreateAsync(4, ConnectorId, Config.Origin, Credential, Payload(), CancellationToken.None)).Status);
    }

    [Fact]
    public async Task Far_future_rate_limit_date_is_capped_without_integer_overflow()
    {
        using var server = Server(context => { context.Response.StatusCode = 429; context.Response.Headers.RetryAfter = new DateTimeOffset(2300, 1, 1, 0, 0, 0, TimeSpan.Zero).ToString("r", System.Globalization.CultureInfo.InvariantCulture); return Task.CompletedTask; });
        var result = await Transport(server).CreateAsync(4, ConnectorId, Config.Origin, Credential, Payload(), CancellationToken.None);
        Assert.Equal(RatelDeskDeliveryStatus.RateLimited, result.Status); Assert.Equal(300, result.RetryAfterSeconds);
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

    private static TestServer Server(RequestDelegate request) => new(new WebHostBuilder().Configure(app => app.Run(request)));
    private static RatelDeskHttpTransport Transport(TestServer server) => new(server.CreateClient(), Policy(Config.Origin), TimeProvider.System, new());
    private static RatelDeskCreateIncidentDto Payload() => RatelDeskPayload.Create(Config, "Disk error", "Disk is full", 2);
    private static RatelDeskConnectorState State(string? cipher) => new(ConnectorId, 4, 1, 1, "owner", Config, cipher, 1);
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
    private sealed class Tester : IRatelDeskConnectionTester
    {
        public int Calls { get; private set; }
        public Task<RatelDeskConnectionTestResult> TestAsync(int tenantId, Guid connectorId, RatelDeskConnectorConfiguration config, string credential, CancellationToken ct) { Calls++; return Task.FromResult(new RatelDeskConnectionTestResult(RatelDeskConnectionTestStatus.MappingValidated, RatelDeskConnectorLimits.ReceiverUnavailableCode)); }
    }
}
