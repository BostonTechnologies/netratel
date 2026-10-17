using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NetRatel.API.Gateway;
using NetRatel.API.Services;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Application.Events;
using NetRatel.Infrastructure.Events;
using Npgsql;
using System.Text.Json;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

public sealed class ClientUpdatePostgresTests(PostgreSqlPersistenceFixture postgres) : IAsyncLifetime, IClassFixture<PostgreSqlPersistenceFixture>
{
    private string _connectionString = null!;
    private DbContextOptions<OrchestratorDbContext> _options = null!;

    public async ValueTask InitializeAsync()
    {
        _connectionString = await postgres.CreateDatabaseAsync();
        _options = new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseNpgsql(_connectionString).Options;
        await using var db = new OrchestratorDbContext(_options);
        await db.Database.MigrateAsync();
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Repeated_update_faults_atomically_retain_one_durable_notification_and_its_read_delivery_state()
    {
        var now = DateTimeOffset.UtcNow;
        var fault = new DomainEvent
        {
            EventType = NetRatelEventTypes.System.UnhandledException,
            Source = "ExceptionNotificationMiddleware", CorrelationId = "first-correlation",
            TenantId = "7", EntityId = Guid.NewGuid().ToString(), Severity = "Critical",
            Message = "Update result failed.", OccurredUtc = now,
            Payload = new ExceptionPayload("/api/v2/agent-updates/attempts/fixture/result", "POST", "first-correlation",
                "database unavailable", null, "IOException", Guid.NewGuid().ToString(),
                new(Guid.NewGuid().ToString(), 1, now, now, "first-correlation", "first-correlation", "first-trace", "first-trace"))
        };
        Guid id;
        await using (var db = new OrchestratorDbContext(_options))
        {
            await new OutboxEventRecorder(db, NullLogger<OutboxEventRecorder>.Instance).RecordAsync(fault);
            var row = Assert.Single(await db.OutboxMessages.ToListAsync());
            id = row.Id;
            row.Status = OutboxStatuses.Published;
            db.OutboxReadReceipts.Add(new OutboxReadReceipt { EventId = id, UserId = "operator", ReadUtc = now });
            await db.SaveChangesAsync();
        }
        var payload = (ExceptionPayload)fault.Payload;
        var retry = fault with
        {
            OccurredUtc = now.AddDays(1), CorrelationId = "retry-correlation",
            Payload = payload with { Occurrence = payload.Occurrence! with
            {
                LastSeenUtc = now.AddDays(1), LastCorrelationId = "retry-correlation", LastTraceId = "retry-trace"
            } }
        };
        await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            await using var db = new OrchestratorDbContext(_options);
            await new OutboxEventRecorder(db, NullLogger<OutboxEventRecorder>.Instance).RecordAsync(retry);
        }));
        // An older request can commit after a newer retry on a different API node.
        // Its timestamp must extend the first-seen evidence without regressing last seen.
        await using (var delayed = new OrchestratorDbContext(_options))
        {
            var older = fault with
            {
                OccurredUtc = now.AddMinutes(-1), CorrelationId = "older-correlation",
                Payload = payload with { CorrelationId = "older-correlation", Occurrence = payload.Occurrence! with
                {
                    FirstSeenUtc = now.AddMinutes(-1), LastSeenUtc = now.AddMinutes(-1),
                    FirstCorrelationId = "older-correlation", LastCorrelationId = "older-correlation",
                    FirstTraceId = "older-trace", LastTraceId = "older-trace"
                } }
            };
            await new OutboxEventRecorder(delayed, NullLogger<OutboxEventRecorder>.Instance).RecordAsync(older);
        }
        await using var observed = new OrchestratorDbContext(_options);
        var aggregate = Assert.Single(await observed.OutboxMessages.ToListAsync());
        Assert.Equal(id, aggregate.Id);
        Assert.Equal(OutboxStatuses.Published, aggregate.Status);
        Assert.True(await observed.OutboxReadReceipts.AnyAsync(row => row.EventId == id && row.UserId == "operator"));
        var occurrence = JsonSerializer.Deserialize<ExceptionPayload>(aggregate.PayloadJson,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!.Occurrence!;
        Assert.Equal(10, occurrence.Count);
        Assert.Equal(now.AddMinutes(-1), occurrence.FirstSeenUtc);
        Assert.Equal(now.AddDays(1), occurrence.LastSeenUtc);
        Assert.Equal("older-correlation", occurrence.FirstCorrelationId);
        Assert.Equal("older-trace", occurrence.FirstTraceId);
        Assert.Equal("retry-correlation", occurrence.LastCorrelationId);
        Assert.Equal("retry-trace", occurrence.LastTraceId);
    }

    [Fact]
    public async Task Migration_Creates_Update_Authority_And_Enforces_Immutable_Versions()
    {
        await using var db = new OrchestratorDbContext(_options);
        var tables = await db.Database.SqlQueryRaw<string>(
            "SELECT table_name AS \"Value\" FROM information_schema.tables WHERE table_schema='public'")
            .ToListAsync();
        tables.Should().Contain(["ClientUpdateReleases", "ClientUpdateAttempts", "AgentClientUpdateStates", "ClientUpdateCatalogRevision"]);

        db.ClientUpdateReleases.Add(CreateRelease(Guid.NewGuid(), 1));
        await db.SaveChangesAsync();
        db.ClientUpdateReleases.Add(CreateRelease(Guid.NewGuid(), 2));
        var save = () => db.SaveChangesAsync();
        await save.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task Activity_invalidation_is_delivered_only_for_a_committed_transaction()
    {
        await using var listener = new NpgsqlConnection(_connectionString);
        await listener.OpenAsync();
        await using (var command = new NpgsqlCommand("LISTEN " + ClientUpdateActivityNotifications.ChannelName, listener))
            await command.ExecuteNonQueryAsync();
        var observed = new List<ClientUpdateActivityNotification>();
        listener.Notification += (_, notification) => observed.Add(JsonSerializer.Deserialize<ClientUpdateActivityNotification>(notification.Payload)!);
        await using var db = new OrchestratorDbContext(_options);
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await ClientUpdateActivityNotifications.NotifyAsync(db, 7, CancellationToken.None);
            await transaction.RollbackAsync();
        }
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await ClientUpdateActivityNotifications.NotifyAsync(db, 8, CancellationToken.None);
            await transaction.CommitAsync();
        }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await listener.WaitAsync(deadline.Token);
        observed.Should().ContainSingle().Which.TenantId.Should().Be(8);
    }

    [Fact]
    public async Task Concurrent_Claims_Are_Idempotent_And_Admission_Is_Connection_Fenced()
    {
        const int tenantId = 73;
        var agentId = Guid.NewGuid();
        var releasePublicId = Guid.NewGuid();
        await using (var seed = new OrchestratorDbContext(_options))
        {
            seed.Tenants.Add(new Tenant { Id = tenantId, Name = "Canary", AutoUpdate = true });
            seed.Agents.Add(new Agent { Id = agentId, TenantId = tenantId });
            seed.ClientUpdateReleases.Add(CreateRelease(releasePublicId, 1));
            await seed.SaveChangesAsync();
        }

        var identity = new AuthenticatedAgentIdentity(tenantId, agentId);
        var nonce = new string('a', 64);
        var claims = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            await using var db = new OrchestratorDbContext(_options);
            return await Authority(db).ClaimAsync(
                identity, releasePublicId, "linux-x64", "0.4.101", "stable", nonce, CancellationToken.None);
        }));

        claims.Should().OnlyContain(x => x != null);
        claims.Select(x => x!.AttemptId).Distinct().Should().ContainSingle();
        await using var authorityDb = new OrchestratorDbContext(_options);
        var authority = Authority(authorityDb);
        var attemptId = claims[0]!.AttemptId;
        await authority.ReportAsync(identity, attemptId, ClientUpdateAttemptState.Downloading, null, null, CancellationToken.None);
        await authority.ReportAsync(identity, attemptId, ClientUpdateAttemptState.Staged, null, null, CancellationToken.None);
        await authority.ReportAsync(identity, attemptId, ClientUpdateAttemptState.Activating, null, null, CancellationToken.None);

        var connectionId = Guid.NewGuid();
        var rejectedReadmission = await authority.MarkReadmittedAsync(identity, attemptId, releasePublicId, new string('b', 64),
            "0.4.102", connectionId, 4, CancellationToken.None);
        rejectedReadmission.Accepted.Should().BeFalse();
        rejectedReadmission.Reason.Should().Be("nonce_mismatch");
        (await authority.MarkReadmittedAsync(identity, attemptId, releasePublicId, nonce,
            "0.4.102", connectionId, 4, CancellationToken.None)).Accepted.Should().BeTrue();
        (await authority.ConfirmAsync(identity, attemptId, Guid.NewGuid(), 4, CancellationToken.None)).Reason
            .Should().Be("presence_connection_mismatch");
        var confirmation = await authority.ConfirmAsync(identity, attemptId, connectionId, 4, CancellationToken.None);
        confirmation.ConfirmationId.Should().NotBeNull();

        var attempt = await authorityDb.ClientUpdateAttempts.AsNoTracking().SingleAsync();
        attempt.State.Should().Be(ClientUpdateAttemptState.Accepted);
        attempt.ConfirmationId.Should().NotBeNull();

        // The response or ready marker may be lost after the authority committed.
        // A restart and concurrent heartbeat retries must return that exact receipt.
        var confirmations = await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
        {
            await using var retryDb = new OrchestratorDbContext(_options);
            return await Authority(retryDb).ConfirmAsync(identity, attemptId, connectionId, 4, CancellationToken.None);
        }));
        confirmations.Should().OnlyContain(result => result.Accepted && result.ConfirmationId == confirmation.ConfirmationId);
        (await authority.MarkReadmittedAsync(identity, attemptId, releasePublicId, nonce,
            "0.4.102", connectionId, 4, CancellationToken.None)).Accepted.Should().BeTrue();

        var replacementConnection = Guid.NewGuid();
        (await authority.MarkReadmittedAsync(new AuthenticatedAgentIdentity(tenantId + 1, agentId),
            attemptId, releasePublicId, nonce, "0.4.102", replacementConnection, 5, CancellationToken.None)).Reason
            .Should().Be("attempt_not_found");
        (await authority.MarkReadmittedAsync(new AuthenticatedAgentIdentity(tenantId, Guid.NewGuid()),
            attemptId, releasePublicId, nonce, "0.4.102", replacementConnection, 5, CancellationToken.None)).Reason
            .Should().Be("attempt_not_found");
        (await authority.MarkReadmittedAsync(identity, attemptId, Guid.NewGuid(), nonce,
            "0.4.102", replacementConnection, 5, CancellationToken.None)).Reason.Should().Be("release_mismatch");
        (await authority.MarkReadmittedAsync(identity, attemptId, releasePublicId, new string('b', 64),
            "0.4.102", replacementConnection, 5, CancellationToken.None)).Reason.Should().Be("nonce_mismatch");
        (await authority.MarkReadmittedAsync(identity, attemptId, releasePublicId, nonce,
            "0.4.101", replacementConnection, 5, CancellationToken.None)).Reason.Should().Be("version_mismatch");
        (await authority.MarkReadmittedAsync(identity, attemptId, releasePublicId, nonce,
            "0.4.102", replacementConnection, 4, CancellationToken.None)).Reason.Should().Be("presence_epoch_mismatch");
        (await authority.MarkReadmittedAsync(identity, attemptId, releasePublicId, nonce,
            "0.4.102", replacementConnection, 5, CancellationToken.None)).Accepted.Should().BeTrue();
        (await authority.ConfirmAsync(identity, attemptId, connectionId, 4, CancellationToken.None)).Reason
            .Should().Be("presence_connection_mismatch");
        (await authority.ConfirmAsync(identity, attemptId, replacementConnection, 4, CancellationToken.None)).Reason
            .Should().Be("presence_epoch_mismatch");
        (await authority.MarkReadmittedAsync(identity, attemptId, releasePublicId, nonce,
            "0.4.102", connectionId, 4, CancellationToken.None)).Reason.Should().Be("presence_epoch_mismatch");
        (await authority.ConfirmAsync(identity, attemptId, replacementConnection, 5, CancellationToken.None))
            .ConfirmationId.Should().Be(confirmation.ConfirmationId);

        var replayedAttempt = await authorityDb.ClientUpdateAttempts.AsNoTracking().SingleAsync();
        replayedAttempt.ConfirmationId.Should().Be(attempt.ConfirmationId);
        replayedAttempt.ReadmittedAtUtc.Should().Be(attempt.ReadmittedAtUtc);
        replayedAttempt.ConfirmedAtUtc.Should().Be(attempt.ConfirmedAtUtc);
        replayedAttempt.UpdatedAtUtc.Should().Be(attempt.UpdatedAtUtc);
    }

    [Fact]
    public async Task StableTenantCannotClaimAPinnedPrereleaseBySupplyingItsReleaseId()
    {
        var releaseId = Guid.NewGuid();
        var stableAgent = Guid.NewGuid();
        var canaryAgent = Guid.NewGuid();
        const string targetVersion = "0.4.103-rc.1";
        await using (var seed = new OrchestratorDbContext(_options))
        {
            seed.Tenants.AddRange(
                new Tenant { Id = 76, Name = "Stable pinned", AutoUpdate = true,
                    AutoUpdateChannel = "stable", AutoUpdateTargetVersion = targetVersion },
                new Tenant { Id = 77, Name = "Canary pinned", AutoUpdate = true,
                    AutoUpdateChannel = "prerelease", AutoUpdateTargetVersion = targetVersion });
            seed.Agents.AddRange(
                new Agent { Id = stableAgent, TenantId = 76 },
                new Agent { Id = canaryAgent, TenantId = 77 });
            var release = CreateRelease(releaseId, 4);
            release.Version = targetVersion;
            release.Channel = "prerelease";
            seed.ClientUpdateReleases.Add(release);
            await seed.SaveChangesAsync();
        }

        await using var db = new OrchestratorDbContext(_options);
        var authority = Authority(db);
        var nonce = new string('c', 64);
        (await authority.ClaimAsync(new AuthenticatedAgentIdentity(76, stableAgent), releaseId,
            "linux-x64", "0.4.102", "prerelease", nonce, CancellationToken.None)).Should().BeNull();
        (await authority.ClaimAsync(new AuthenticatedAgentIdentity(77, canaryAgent), releaseId,
            "linux-x64", "0.4.102", "stable", nonce, CancellationToken.None)).Should().NotBeNull();
    }

    [Fact]
    public async Task Interrupted_Download_Is_Immediately_Reissued_To_A_New_Admission()
    {
        const int tenantId = 74;
        var agentId = Guid.NewGuid();
        var releasePublicId = Guid.NewGuid();
        await using (var seed = new OrchestratorDbContext(_options))
        {
            seed.Tenants.Add(new Tenant { Id = tenantId, Name = "Stale claim", AutoUpdate = true });
            seed.Agents.Add(new Agent { Id = agentId, TenantId = tenantId });
            seed.ClientUpdateReleases.Add(CreateRelease(releasePublicId, 2));
            await seed.SaveChangesAsync();
        }

        var identity = new AuthenticatedAgentIdentity(tenantId, agentId);
        var firstNonce = new string('a', 64);
        var replacementNonce = new string('b', 64);
        Guid attemptId;
        await using (var initialDb = new OrchestratorDbContext(_options))
        {
            var authority = Authority(initialDb);
            var initialClaim = await authority.ClaimAsync(
                identity, releasePublicId, "linux-x64", "0.4.101", "stable", firstNonce, CancellationToken.None);
            initialClaim.Should().NotBeNull();
            attemptId = initialClaim!.AttemptId;
            await authority.ReportAsync(identity, attemptId, ClientUpdateAttemptState.Downloading, null, null, CancellationToken.None);

            var freshReplacementClaim = await authority.ClaimAsync(
                identity, releasePublicId, "linux-x64", "0.4.101", "stable", replacementNonce, CancellationToken.None);
            freshReplacementClaim.Should().NotBeNull("a client restart must be able to resume an interrupted download");
            freshReplacementClaim!.AttemptId.Should().NotBe(attemptId, "a fresh admission must retain the interrupted attempt identity for stale report fencing");
        }

        await using var recoveryDb = new OrchestratorDbContext(_options);
        var retained = await recoveryDb.ClientUpdateAttempts.AsNoTracking().OrderBy(attempt => attempt.Id).ToListAsync();
        retained.Should().HaveCount(2);
        retained[0].State.Should().Be(ClientUpdateAttemptState.FailedPreActivation);
        retained[0].FailureCode.Should().Be("admission_superseded");
        retained[1].State.Should().Be(ClientUpdateAttemptState.Claimed);
        retained[1].AdmissionNonceHash.Should().NotBe(retained[0].AdmissionNonceHash);
    }

    [Fact]
    public async Task Stale_Claimed_Attempt_Is_Reissued_To_A_New_Admission()
    {
        const int tenantId = 75;
        var agentId = Guid.NewGuid();
        var releasePublicId = Guid.NewGuid();
        await using (var seed = new OrchestratorDbContext(_options))
        {
            seed.Tenants.Add(new Tenant { Id = tenantId, Name = "Test", AutoUpdate = true, AutoUpdateChannel = "stable" });
            seed.Agents.Add(new Agent { Id = agentId, TenantId = tenantId });
            seed.ClientUpdateReleases.Add(CreateRelease(releasePublicId, 3));
            await seed.SaveChangesAsync();
        }

        var identity = new AuthenticatedAgentIdentity(tenantId, agentId);
        var firstNonce = new string('a', 64);
        var replacementNonce = new string('b', 64);
        await using (var initialDb = new OrchestratorDbContext(_options))
        {
            var initialClaim = await Authority(initialDb).ClaimAsync(
                identity, releasePublicId, "linux-x64", "0.4.101", "stable", firstNonce, CancellationToken.None);
            initialClaim.Should().NotBeNull();

            var freshReplacementClaim = await Authority(initialDb).ClaimAsync(
                identity, releasePublicId, "linux-x64", "0.4.101", "stable", replacementNonce, CancellationToken.None);
            freshReplacementClaim.Should().BeNull("a concurrent claim without an interrupted download remains fenced");
        }

        await using (var staleDb = new OrchestratorDbContext(_options))
        {
            var staleAttempt = await staleDb.ClientUpdateAttempts.SingleAsync();
            staleAttempt.UpdatedAtUtc = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(10);
            await staleDb.SaveChangesAsync();
        }

        await using var recoveryDb = new OrchestratorDbContext(_options);
        var recoveryClaim = await Authority(recoveryDb).ClaimAsync(
            identity, releasePublicId, "linux-x64", "0.4.101", "stable", replacementNonce, CancellationToken.None);

        recoveryClaim.Should().NotBeNull();
        var recoveredAttempt = await recoveryDb.ClientUpdateAttempts.AsNoTracking().SingleAsync(attempt => attempt.PublicId == recoveryClaim!.AttemptId);
        recoveredAttempt.State.Should().Be(ClientUpdateAttemptState.Claimed);
        (await recoveryDb.ClientUpdateAttempts.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task OperatorResume_ReenablesOnlyAnAuthoritySuspendedAutomaticUpdate()
    {
        const int tenantId = 75;
        var agentId = Guid.NewGuid();
        await using (var seed = new OrchestratorDbContext(_options))
        {
            seed.Tenants.Add(new Tenant { Id = tenantId, Name = "Suspended update target", AutoUpdate = true });
            seed.Agents.Add(new Agent
            {
                Id = agentId,
                TenantId = tenantId,
                IsEnabled = true,
                Status = AgentStatus.Active,
                CreatedAtUtc = DateTimeOffset.UtcNow
            });
            seed.AgentClientUpdateStates.Add(new AgentClientUpdateStateRecord
            {
                AgentId = agentId,
                TenantId = tenantId,
                SuspendedAtUtc = DateTimeOffset.UtcNow,
                SuspensionReason = "rollback",
                PolicyRevision = 4
            });
            await seed.SaveChangesAsync();
        }

        await using var db = new OrchestratorDbContext(_options);
        var authority = Authority(db);
        var eligibility = await authority.GetResumeEligibilityAsync(tenantId, agentId, CancellationToken.None);
        eligibility.Should().Be(new ClientUpdateResumeEligibility(true, null, 4));

        var result = await authority.ResumeAutomaticUpdatesAsync(tenantId, agentId, "operator-1", CancellationToken.None);

        result.Should().Be(new ClientUpdateResumeResult(true, null, 5));
        var state = await db.AgentClientUpdateStates.SingleAsync(candidate => candidate.AgentId == agentId);
        state.SuspendedAtUtc.Should().BeNull();
        state.SuspensionReason.Should().BeNull();
        state.PolicyRevision.Should().Be(5);
        state.ResumedBy.Should().Be("operator-1");
        (await authority.GetResumeEligibilityAsync(tenantId, agentId, CancellationToken.None)).FailureCode
            .Should().Be("client_update_not_suspended");
    }

    [Fact]
    public async Task Committed_Rollback_Replays_Preserve_Details_Revisions_And_Operator_Resume()
    {
        var clock = new UpdateClock();
        var identity = await SeedAgentAsync(81);
        Guid attemptId;
        await using (var db = new OrchestratorDbContext(_options))
        {
            var release = await db.ClientUpdateReleases.SingleAsync();
            var authority = Authority(db, clock);
            var claim = await authority.ClaimAsync(identity, release.PublicId, "linux-x64", "0.4.101", "stable", new string('a', 64), default);
            attemptId = claim!.AttemptId;
            foreach (var state in new[] { ClientUpdateAttemptState.Downloading, ClientUpdateAttemptState.Staged, ClientUpdateAttemptState.Activating })
                (await authority.ReportAsync(identity, attemptId, state, null, null, default)).Outcome.Should().Be(ClientUpdateReportOutcome.Applied);
        }
        var reports = await Task.WhenAll(Enumerable.Range(0, 6).Select(async _ =>
        {
            await using var db = new OrchestratorDbContext(_options);
            return await Authority(db, clock).ReportAsync(identity, attemptId, ClientUpdateAttemptState.RolledBack,
                "activation_timeout", "native rollback complete", default);
        }));
        reports.Count(report => report.Outcome == ClientUpdateReportOutcome.Applied).Should().Be(1);
        reports.Count(report => report.Outcome == ClientUpdateReportOutcome.Duplicate).Should().Be(5);
        ClientUpdateAttemptRecord committed;
        long catalogRevision;
        await using (var db = new OrchestratorDbContext(_options))
        {
            committed = await db.ClientUpdateAttempts.AsNoTracking().SingleAsync();
            (await db.AgentClientUpdateStates.SingleAsync()).PolicyRevision.Should().Be(1);
            clock.Now = clock.Now.AddMinutes(1);
            (await Authority(db, clock).ResumeAutomaticUpdatesAsync(identity.TenantId, identity.AgentId, "operator", default)).Resumed.Should().BeTrue();
            catalogRevision = await db.ClientUpdateCatalogRevisions.Select(revision => revision.Revision).SingleAsync();
        }
        clock.Now = clock.Now.AddHours(1);
        await using (var restarted = new OrchestratorDbContext(_options))
        {
            var authority = Authority(restarted, clock);
            var replay = await authority.ReportAsync(identity, attemptId, ClientUpdateAttemptState.RolledBack,
                "activation_timeout", "native rollback complete", default);
            replay.Outcome.Should().Be(ClientUpdateReportOutcome.Duplicate);
            replay.SuspensionActive.Should().BeFalse();
            replay.PolicyRevision.Should().Be(2);
            (await authority.ReportAsync(identity, attemptId, ClientUpdateAttemptState.RolledBack,
                "different_failure", "rewritten", default)).Outcome.Should().Be(ClientUpdateReportOutcome.Conflict);
            var retained = await restarted.ClientUpdateAttempts.AsNoTracking().SingleAsync();
            retained.UpdatedAtUtc.Should().Be(committed.UpdatedAtUtc);
            retained.FailureCode.Should().Be(committed.FailureCode);
            retained.Message.Should().Be(committed.Message);
            (await restarted.AgentClientUpdateStates.AsNoTracking().SingleAsync()).SuspendedAtUtc.Should().BeNull();
            (await restarted.ClientUpdateCatalogRevisions.Select(revision => revision.Revision).SingleAsync()).Should().Be(catalogRevision);
        }
    }

    [Fact]
    public async Task Fresh_Admission_Fences_Legacy_Stale_Reports_And_Retains_Committed_Terminal_History()
    {
        var identity = await SeedAgentAsync(82);
        await using var db = new OrchestratorDbContext(_options);
        var authority = Authority(db);
        var release = await db.ClientUpdateReleases.SingleAsync();
        var first = await authority.ClaimAsync(identity, release.PublicId, "linux-x64", "0.4.101", "stable", new string('a', 64), default);
        await authority.ReportAsync(identity, first!.AttemptId, ClientUpdateAttemptState.Downloading, null, null, default);
        var next = await authority.ClaimAsync(identity, release.PublicId, "linux-x64", "0.4.101", "stable", new string('b', 64), default);
        next!.AttemptId.Should().NotBe(first.AttemptId);
        (await authority.ReportAsync(identity, first.AttemptId, ClientUpdateAttemptState.FailedPreActivation,
            "late_failure", "delayed old native result without nonce", default)).Code.Should().Be("state_conflict");
        (await authority.ClaimAsync(identity, release.PublicId, "linux-x64", "0.4.101", "stable", new string('a', 64), default)).Should().BeNull();
        await authority.ReportAsync(identity, next.AttemptId, ClientUpdateAttemptState.FailedPreActivation, "checksum_failed", "bad package", default);
        var retry = await authority.ClaimAsync(identity, release.PublicId, "linux-x64", "0.4.101", "stable", new string('c', 64), default);
        retry!.AttemptId.Should().NotBe(next.AttemptId);
        (await authority.ReportAsync(identity, next.AttemptId, ClientUpdateAttemptState.FailedPreActivation,
            "checksum_failed", "bad package", default)).Outcome.Should().Be(ClientUpdateReportOutcome.Duplicate);
        (await db.ClientUpdateAttempts.AsNoTracking().SingleAsync(attempt => attempt.PublicId == retry.AttemptId)).State.Should().Be(ClientUpdateAttemptState.Claimed);
    }

    [Fact]
    public async Task Accepted_Rollback_Observation_Preserves_Receipt_And_Requires_One_Operator_Resume()
    {
        var clock = new UpdateClock();
        var identity = await SeedAgentAsync(83);
        Guid attemptId;
        var confirmationId = Guid.NewGuid();
        var committedAt = clock.Now;
        await using (var seed = new OrchestratorDbContext(_options))
        {
            var release = await seed.ClientUpdateReleases.SingleAsync();
            var claim = await Authority(seed, clock).ClaimAsync(identity, release.PublicId, "linux-x64", "0.4.101", "stable", new string('a', 64), default);
            attemptId = claim!.AttemptId;
            await seed.ClientUpdateAttempts.Where(attempt => attempt.PublicId == attemptId).ExecuteUpdateAsync(setters => setters
                .SetProperty(attempt => attempt.State, ClientUpdateAttemptState.Accepted)
                .SetProperty(attempt => attempt.ConfirmationId, confirmationId)
                .SetProperty(attempt => attempt.ConfirmedAtUtc, committedAt));
        }
        clock.Now = clock.Now.AddMinutes(1);
        await using (var db = new OrchestratorDbContext(_options))
        {
            var report = await Authority(db, clock).ReportAsync(identity, attemptId, ClientUpdateAttemptState.RolledBack,
                "lost_ready_response", "native restored previous target", default);
            report.Code.Should().Be("accepted_authority_conflict");
            report.State.Should().Be("Accepted");
            report.ConfirmationId.Should().Be(confirmationId);
            report.SuspensionActive.Should().BeTrue();
            clock.Now = clock.Now.AddMinutes(1);
            (await Authority(db, clock).ResumeAutomaticUpdatesAsync(identity.TenantId, identity.AgentId, "operator", default)).Resumed.Should().BeTrue();
        }
        clock.Now = clock.Now.AddMinutes(1);
        await using (var restarted = new OrchestratorDbContext(_options))
        {
            var report = await Authority(restarted, clock).ReportAsync(identity, attemptId, ClientUpdateAttemptState.RolledBack,
                "lost_ready_response", "native restored previous target", default);
            report.SuspensionActive.Should().BeFalse();
            report.PolicyRevision.Should().Be(2);
            var retained = await restarted.ClientUpdateAttempts.AsNoTracking().SingleAsync();
            retained.State.Should().Be(ClientUpdateAttemptState.Accepted);
            retained.ConfirmationId.Should().Be(confirmationId);
            retained.ConfirmedAtUtc.Should().Be(committedAt);
            retained.UpdatedAtUtc.Should().Be(committedAt);
            retained.ReportedAtUtc.Should().Be(committedAt.AddMinutes(1));
            retained.ReportedState.Should().Be(ClientUpdateAttemptState.RolledBack);
            retained.ReconciliationState.Should().Be("accepted_authority_conflict");
            (await restarted.ClientUpdateCatalogRevisions.Select(revision => revision.Revision).SingleAsync()).Should().Be(2);
        }
    }

    private async Task<AuthenticatedAgentIdentity> SeedAgentAsync(int tenantId)
    {
        var agentId = Guid.NewGuid();
        await using var db = new OrchestratorDbContext(_options);
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "Result recovery", AutoUpdate = true });
        db.Agents.Add(new Agent { Id = agentId, TenantId = tenantId });
        db.ClientUpdateReleases.Add(CreateRelease(Guid.NewGuid(), 1));
        await db.SaveChangesAsync();
        return new AuthenticatedAgentIdentity(tenantId, agentId);
    }

    private sealed class UpdateClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static ClientUpdateAuthorityService Authority(OrchestratorDbContext db, TimeProvider? clock = null) => new(
        db,
        new NoOpCatalog(),
        clock ?? TimeProvider.System,
        NullLogger<ClientUpdateAuthorityService>.Instance);

    private sealed class NoOpCatalog : IClientUpdateCatalog
    {
        public long Revision => 0;
        public DateTimeOffset RefreshedAtUtc => DateTimeOffset.UtcNow;
        public ClientUpdateOfferSnapshot? GetOffer(int tenantId, Guid agentId, string runtimeId, string currentVersion, string channel) => null;
        public ClientUpdatePolicySnapshot GetPolicy(int tenantId, Guid agentId, long clientPolicyRevision) => new(0, false, false, false, null);
        public Task RefreshAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private static ClientUpdateReleaseRecord CreateRelease(Guid publicId, long revision) => new()
    {
        PublicId = publicId,
        Revision = revision,
        RuntimeId = "linux-x64",
        Version = "0.4.102",
        Channel = "stable",
        ArtifactKey = "linux-x64/0.4.102/NetRatel.Client-linux-x64-0.4.102.zip",
        Sha256 = new string('a', 64),
        SizeBytes = 123,
        ManifestJson = "{}",
        Enabled = true,
        PublishedAtUtc = DateTimeOffset.UtcNow
    };
}
