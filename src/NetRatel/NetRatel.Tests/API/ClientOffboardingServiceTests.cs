using AwesomeAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.API.Gateway;
using NetRatel.API.Services;
using NetRatel.Application.Presence;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using NetRatel.Shared.Contracts;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class ClientOffboardingServiceTests
{
    private static readonly ClientKey Key = new(7, Guid.Parse("ed9b6001-cf7e-44de-908e-476c25132578"));

    [Theory]
    [InlineData("accepted", "accepted")]
    [InlineData("timeout", "requested")]
    [InlineData("rejected", "requested")]
    public async Task Durable_intent_precedes_dispatch_and_acknowledgement_never_deletes(string dispatch, string state)
    {
        await using var scenario = await Scenario.CreateAsync();
        scenario.Control.Dispatch = dispatch;
        scenario.Control.BeforeDispatch = async command =>
        {
            await using var observer = scenario.OpenDb();
            var durable = await observer.ClientOffboardingOperations.SingleAsync();
            durable.Id.ToString("D").Should().Be(command.OperationId);
            durable.State.Should().Be(ClientOffboardingState.Requested);
            durable.FinishedAtUtc.Should().BeNull();
            durable.CompletionTokenHash.Should().NotBe(command.CompletionToken);
            durable.ProtectedCompletionToken.Should().NotBeNull().And.NotBe(command.CompletionToken);
        };

        var details = await scenario.Service.RequestUninstallAsync(Key, new(Guid.NewGuid(), "Retired"), "operator", default);
        details.Removed.Should().BeFalse();
        details.Operation!.State.Should().Be(state);
        await using var reloaded = scenario.OpenDb();
        var persisted = await reloaded.ClientOffboardingOperations.SingleAsync();
        persisted.FinishedAtUtc.Should().BeNull();
        persisted.ProtectedCompletionToken.Should().NotBeNull();
        (await reloaded.Agents.SingleAsync()).IsEnabled.Should().BeTrue();
        (await reloaded.AgentCredentials.SingleAsync()).RevokedAtUtc.Should().BeNull();
        if (dispatch == "rejected") persisted.FailureCode.Should().Be("helper_launch_rejected");
        var recovered = await scenario.CreateService(reloaded).ReadAsync(Key, default);
        recovered.Operation!.OperationId.Should().Be(persisted.Id);
        recovered.Operation.State.Should().Be(state);

        scenario.Clock.Now = scenario.Clock.Now.AddMinutes(6);
        var late = await scenario.CreateService(reloaded).ReadAsync(Key, default);
        late.Operation!.State.Should().Be("confirmation_pending");
        late.Removed.Should().BeFalse();
        (await reloaded.Agents.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Retry_reuses_operation_token_and_unexpired_start_deadline()
    {
        await using var scenario = await Scenario.CreateAsync();
        scenario.Control.Dispatch = "timeout";
        var operationId = Guid.NewGuid();
        await scenario.Service.RequestUninstallAsync(Key, new(operationId), "operator", default);
        var first = scenario.Control.Commands.Single();
        scenario.Clock.Now = scenario.Clock.Now.AddSeconds(1);
        await scenario.Service.RequestUninstallAsync(Key, new(Guid.NewGuid()), "operator", default);
        var retried = scenario.Control.Commands.Last();
        retried.Should().Be(first, "a pending retry must coalesce with the same command in the control registry");
        (await scenario.Db.ClientOffboardingOperations.CountAsync()).Should().Be(1);

        scenario.Clock.Now = scenario.Clock.Now.AddHours(25);
        var expired = await Assert.ThrowsAsync<ClientOffboardingException>(() =>
            scenario.Service.RequestUninstallAsync(Key, new(operationId), "operator", default));
        expired.Code.Should().Be("uninstall_confirmation_expired");
        (await scenario.Db.Agents.CountAsync()).Should().Be(1);
        (await scenario.Db.ClientOffboardingOperations.SingleAsync()).FinishedAtUtc.Should().BeNull();
    }

    [Theory]
    [InlineData("accepted")]
    [InlineData("rejected")]
    public async Task Only_complete_service_and_file_receipt_removes_record_and_identical_receipt_is_idempotent(string acknowledgement)
    {
        await using var scenario = await Scenario.CreateAsync();
        scenario.Control.Dispatch = acknowledgement;
        await scenario.Service.RequestUninstallAsync(Key, new(Guid.NewGuid()), "operator", default);
        var receipt = Receipt(scenario.Control.Commands.Single());
        (await scenario.Service.CompleteAsync(receipt, default)).Should().BeTrue();

        await using var reloaded = scenario.OpenDb();
        var operation = await reloaded.ClientOffboardingOperations.SingleAsync();
        operation.State.Should().Be(ClientOffboardingState.Completed);
        operation.ServiceRemoved.Should().BeTrue();
        operation.FilesRemoved.Should().BeTrue();
        operation.FinishedAtUtc.Should().NotBeNull();
        operation.ProtectedCompletionToken.Should().BeNull();
        operation.ReceiptHash.Should().NotBeNull();
        (await reloaded.Agents.CountAsync()).Should().Be(0);
        var removed = await reloaded.Agents.IgnoreQueryFilters().SingleAsync();
        removed.DeletedBy.Should().Be("operator");
        (await reloaded.AgentCredentials.SingleAsync()).RevokedAtUtc.Should().NotBeNull();
        var auditCount = await reloaded.OutboxMessages.CountAsync();
        var persistedService = scenario.CreateService(reloaded);
        (await persistedService.ReadAsync(Key, default)).Removed.Should().BeTrue();
        scenario.Clock.Now = scenario.Clock.Now.AddHours(48);
        (await persistedService.CompleteAsync(receipt.Clone(), default)).Should().BeTrue();
        (await reloaded.OutboxMessages.CountAsync()).Should().Be(auditCount);
        (await persistedService.RequestUninstallAsync(Key, new(Guid.Parse(receipt.OperationId)), "operator", default))
            .Removed.Should().BeTrue();
        scenario.Control.Commands.Should().ContainSingle();
        var contradictory = receipt.Clone();
        contradictory.Success = false;
        contradictory.FailureCode = "conflicting_receipt";
        (await persistedService.CompleteAsync(contradictory, default)).Should().BeFalse();
    }

    [Theory]
    [InlineData("tenant")]
    [InlineData("client")]
    [InlineData("operation")]
    [InlineData("token")]
    [InlineData("service")]
    [InlineData("files")]
    [InlineData("failure")]
    public async Task Invalid_or_cross_target_success_receipt_cannot_remove_client(string defect)
    {
        await using var scenario = await Scenario.CreateAsync();
        await scenario.Service.RequestUninstallAsync(Key, new(Guid.NewGuid()), "operator", default);
        var receipt = Receipt(scenario.Control.Commands.Single());
        switch (defect)
        {
            case "tenant": receipt.TenantId++; break;
            case "client": receipt.ClientId = Guid.NewGuid().ToString("D"); break;
            case "operation": receipt.OperationId = Guid.NewGuid().ToString("D"); break;
            case "token": receipt.CompletionToken = new string('Z', 64); break;
            case "service": receipt.ServiceRemoved = false; break;
            case "files": receipt.FilesRemoved = false; break;
            default: receipt.FailureCode = "files_remove_failed"; break;
        }
        (await scenario.Service.CompleteAsync(receipt, default)).Should().BeFalse();
        await using var reloaded = scenario.OpenDb();
        (await reloaded.Agents.SingleAsync()).IsEnabled.Should().BeTrue();
        (await reloaded.AgentCredentials.SingleAsync()).RevokedAtUtc.Should().BeNull();
        var operation = await reloaded.ClientOffboardingOperations.SingleAsync();
        operation.FinishedAtUtc.Should().BeNull();
        operation.ReceiptHash.Should().BeNull();
    }

    [Fact]
    public async Task Helper_failure_retains_record_and_reports_partial_cleanup_without_claiming_completion()
    {
        await using var scenario = await Scenario.CreateAsync();
        await scenario.Service.RequestUninstallAsync(Key, new(Guid.NewGuid()), "operator", default);
        var failure = Receipt(scenario.Control.Commands.Single());
        failure.Success = false;
        failure.FilesRemoved = false;
        failure.FailureCode = "files_remove_failed";
        (await scenario.Service.CompleteAsync(failure, default)).Should().BeTrue();
        await using var reloaded = scenario.OpenDb();
        var state = await scenario.CreateService(reloaded).ReadAsync(Key, default);
        state.Removed.Should().BeFalse();
        state.Operation!.State.Should().Be("failed");
        state.Operation.ServiceRemoved.Should().BeTrue();
        state.Operation.FilesRemoved.Should().BeFalse();
        state.Operation.FailureCode.Should().Be("files_remove_failed");
        (await reloaded.Agents.CountAsync()).Should().Be(1);
        (await reloaded.AgentCredentials.SingleAsync()).RevokedAtUtc.Should().BeNull();
        (await scenario.CreateService(reloaded).CompleteAsync(failure.Clone(), default)).Should().BeTrue();
    }

    [Fact]
    public async Task Helper_receipt_before_launch_acknowledgement_keeps_the_completed_state()
    {
        await using var scenario = await Scenario.CreateAsync();
        scenario.Control.BeforeDispatch = async command =>
        {
            await using var helperContext = scenario.OpenDb();
            (await scenario.CreateService(helperContext).CompleteAsync(Receipt(command), default)).Should().BeTrue();
        };
        var result = await scenario.Service.RequestUninstallAsync(Key, new(Guid.NewGuid()), "operator", default);
        result.Removed.Should().BeTrue();
        result.Operation!.State.Should().Be("completed");
        (await scenario.Db.ClientOffboardingOperations.AsNoTracking().SingleAsync()).State.Should().Be(ClientOffboardingState.Completed);
    }

    [Theory]
    [InlineData("offline", "client_offline")]
    [InlineData("unsupported", "uninstall_not_supported")]
    [InlineData("fence", "control_session_unavailable")]
    public async Task Offline_unsupported_or_replaced_session_cannot_create_an_uninstall_operation(string condition, string expected)
    {
        await using var scenario = await Scenario.CreateAsync();
        if (condition == "offline") scenario.Presence.Snapshot = scenario.Presence.Snapshot with { Status = ClientPresenceStatus.Offline };
        if (condition == "unsupported") scenario.Control.Availability = scenario.Control.Availability! with { Capabilities = [] };
        if (condition == "fence") scenario.Control.Availability = scenario.Control.Availability! with { ConnectionEpoch = 6 };
        var error = await Assert.ThrowsAsync<ClientOffboardingException>(() =>
            scenario.Service.RequestUninstallAsync(Key, new(Guid.NewGuid()), "operator", default));
        error.Code.Should().Be(expected);
        (await scenario.Db.ClientOffboardingOperations.CountAsync()).Should().Be(0);
        scenario.Control.Commands.Should().BeEmpty();
        (await scenario.Db.Agents.CountAsync()).Should().Be(1);
    }

    private static UninstallReceipt Receipt(UninstallRequest request) => new()
    {
        TenantId = Key.TenantId, ClientId = Key.AgentId.ToString("D"), OperationId = request.OperationId,
        CompletionToken = request.CompletionToken, Success = true, ServiceRemoved = true, FilesRemoved = true
    };

    private sealed class Scenario : IAsyncDisposable
    {
        private readonly IDataProtectionProvider _protection = new EphemeralDataProtectionProvider();
        private readonly DbContextOptions<OrchestratorDbContext> _options = new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"), new InMemoryDatabaseRoot()).Options;
        public Clock Clock { get; } = new();
        public Presence Presence { get; } = new();
        public Control Control { get; } = new();
        public OrchestratorDbContext Db { get; }
        public ClientOffboardingService Service { get; }

        private Scenario()
        {
            Db = OpenDb();
            Service = CreateService(Db);
        }

        public static async Task<Scenario> CreateAsync()
        {
            var scenario = new Scenario();
            scenario.Db.Agents.Add(new Agent
            {
                Id = Key.AgentId, TenantId = Key.TenantId, IsEnabled = true, Status = AgentStatus.Active,
                Name = "fixture-host", PublicKeyFingerprint = "fixture-device-key"
            });
            scenario.Db.AgentCredentials.Add(new AgentCredential
            {
                Id = Guid.NewGuid(), AgentId = Key.AgentId, RefreshTokenHash = "fixture-refresh", CreatedAtUtc = scenario.Clock.Now
            });
            await scenario.Db.SaveChangesAsync();
            return scenario;
        }

        public OrchestratorDbContext OpenDb() => new(_options);
        public ClientOffboardingService CreateService(OrchestratorDbContext db) => new(db,
            new AgentManagementService(db, NullLogger<AgentManagementService>.Instance), Presence, Control, _protection, Clock,
            NullLogger<ClientOffboardingService>.Instance);
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 12, 8, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Presence : IClientPresenceRouter
    {
        public ClientPresenceSnapshot Snapshot { get; set; } = new(Key, ClientPresenceStatus.Online, 5, Control.Connection,
            1, null, "fixture", ["uninstall-v1"], null, "akka", true);
        public Task<ClientPresenceSnapshot> GetSnapshotAsync(ClientKey client, CancellationToken cancellationToken) => Task.FromResult(Snapshot);
        public Task<GatewayPresenceSessionStarted> StartSessionAsync(StartGatewayPresenceSession message, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PresenceMessageResult> RecordHeartbeatAsync(RecordGatewayHeartbeat message, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PresenceMessageResult> EndSessionAsync(EndGatewayPresenceSession message, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ClientPresenceRouteStatus> ProbeAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class Control : IAgentControlSessionRegistry
    {
        public static readonly Guid Connection = Guid.Parse("6d5b1cd1-2e9a-4463-8a72-b0cde879ca58");
        public AgentControlAvailability? Availability { get; set; } = new(Connection, 5, ["uninstall-v1"]);
        public List<UninstallRequest> Commands { get; } = [];
        public string Dispatch { get; set; } = "accepted";
        public Func<UninstallRequest, Task>? BeforeDispatch { get; set; }
        public AgentControlAvailability? GetAvailability(ClientKey client) => client == Key ? Availability : null;
        public async Task<UninstallAcknowledgement> RequestUninstallAsync(ClientKey client, Guid connectionId, ulong connectionEpoch,
            UninstallRequest request, TimeSpan timeout, CancellationToken cancellationToken)
        {
            client.Should().Be(Key);
            connectionId.Should().Be(Connection);
            connectionEpoch.Should().Be(5);
            Commands.Add(request.Clone());
            if (BeforeDispatch is not null) await BeforeDispatch(request);
            if (Dispatch == "timeout") throw new TimeoutException("Immediate fixture acknowledgement timeout.");
            return new() { OperationId = request.OperationId, Accepted = Dispatch == "accepted", FailureCode = Dispatch == "rejected" ? "helper_launch_rejected" : string.Empty };
        }
        public AgentControlSessionRegistration Register(ClientKey client, Guid connectionId, ulong connectionEpoch, bool provisional = false) => throw new NotSupportedException();
        public Task<AgentControlPingResult> RequestPingAsync(ClientKey client, TimeSpan timeout, CancellationToken cancellationToken) => throw new NotSupportedException();
        public bool IsAvailable(ClientKey client) => GetAvailability(client) is not null;
        public bool TryGetLatestPing(ClientKey client, out AgentControlPingResult result) { result = default!; return false; }
    }
}
