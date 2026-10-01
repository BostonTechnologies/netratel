using System.Reflection;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using NetRatel.API.Realtime;
using NetRatel.Application.Fanout;
using NetRatel.Application.Jobs;
using NetRatel.Infrastructure.Identity.Authorization;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class RealtimeContractGroupAndAuthorizationTests
{
    [Fact]
    public void NamedContracts_ProduceRetainedSafeNonAuthoritativeCategories()
    {
        var timestamp = DateTimeOffset.Parse("2026-08-07T10:00:00Z");
        var updates = new RealtimeFanoutUpdated[]
        {
            new PresenceFanoutUpdated(Tenant(), RealtimeFanoutEventType.Updated, RealtimeFanoutStatus.Active, timestamp),
            new TelemetryFanoutUpdated(Client(), RealtimeFanoutEventType.Updated, RealtimeFanoutStatus.Active, timestamp),
            new CommandFanoutUpdated(Command(), RealtimeFanoutEventType.Completed, RealtimeFanoutStatus.Completed, timestamp),
            new JobFanoutUpdated(Job(), RealtimeFanoutEventType.Updated, RealtimeFanoutStatus.Active, timestamp),
            new TerminalFanoutUpdated(Terminal(), RealtimeFanoutEventType.Updated, RealtimeFanoutStatus.Active, timestamp),
            new FileBrowserFanoutUpdated(FileBrowser(), RealtimeFanoutEventType.Updated, RealtimeFanoutStatus.Active, timestamp)
        };

        updates.Select(static update => update.ToEnvelope().Category).Should().Equal(
            RealtimeFanoutCategory.Presence,
            RealtimeFanoutCategory.Telemetry,
            RealtimeFanoutCategory.Command,
            RealtimeFanoutCategory.Job,
            RealtimeFanoutCategory.Terminal,
            RealtimeFanoutCategory.FileBrowser);
        updates.Select(static update => update.ToEnvelope()).Should().OnlyContain(envelope =>
            envelope.IsValid && !envelope.IsAuthoritative);

        var contractTypes = new[]
        {
            typeof(RealtimeFanoutEnvelope),
            typeof(RealtimeFanoutTarget),
            typeof(RealtimeFanoutDiagnosticSummary)
        };
        contractTypes.SelectMany(static type => type.GetProperties())
            .Select(static property => property.Name)
            .Should().NotContain(new[]
            {
                "Payload", "Content", "FileContent", "TerminalInput", "TerminalOutput",
                "Sdp", "Ice", "Media", "Secret", "Error", "Path"
            });
        contractTypes.SelectMany(static type => type.GetProperties())
            .Select(static property => property.PropertyType)
            .Should().NotContain(new[] { typeof(byte[]), typeof(Memory<byte>), typeof(ReadOnlyMemory<byte>) });
    }

    [Fact]
    public void RetainedCategories_HaveDistinctSnapshotUpdateAndResyncVariants()
    {
        var timestamp = DateTimeOffset.Parse("2026-08-07T10:00:00Z");
        var envelopes = new[]
        {
            new RealtimeFanoutEnvelope(1, RealtimeFanoutCategory.Presence, Tenant(), RealtimeFanoutEventType.Updated, RealtimeFanoutStatus.Active, timestamp),
            new RealtimeFanoutEnvelope(1, RealtimeFanoutCategory.Telemetry, Client(), RealtimeFanoutEventType.Updated, RealtimeFanoutStatus.Active, timestamp),
            new RealtimeFanoutEnvelope(1, RealtimeFanoutCategory.Command, Command(), RealtimeFanoutEventType.Updated, RealtimeFanoutStatus.Active, timestamp),
            new RealtimeFanoutEnvelope(1, RealtimeFanoutCategory.Job, Job(), RealtimeFanoutEventType.Updated, RealtimeFanoutStatus.Active, timestamp),
            new RealtimeFanoutEnvelope(1, RealtimeFanoutCategory.Terminal, Terminal(), RealtimeFanoutEventType.Updated, RealtimeFanoutStatus.Active, timestamp),
            new RealtimeFanoutEnvelope(1, RealtimeFanoutCategory.FileBrowser, FileBrowser(), RealtimeFanoutEventType.Updated, RealtimeFanoutStatus.Active, timestamp)
        };

        envelopes.Should().OnlyContain(envelope => envelope.IsValid);
        envelopes.Select(envelope => envelope with { EventType = RealtimeFanoutEventType.Snapshot })
            .Should().OnlyContain(envelope => envelope.IsValid && !envelope.ResyncRequired);
        envelopes.Select(envelope => envelope with { ResyncRequired = true })
            .Should().OnlyContain(envelope => envelope.IsValid && envelope.ResyncRequired);
    }

    [Fact]
    public void RuntimeFanoutEnvelopeAndJobMapper_ExposeSafeMetadataOnly()
    {
        var timestamp = DateTimeOffset.Parse("2026-08-07T10:00:00Z");
        var terminal = new RealtimeFanoutEnvelope(
            RealtimeFanoutEnvelope.CurrentSchemaVersion,
            RealtimeFanoutCategory.Terminal,
            Terminal(),
            RealtimeFanoutEventType.Updated,
            RealtimeFanoutStatus.Active,
            timestamp,
            Sequence: 3,
            Diagnostics: new RealtimeFanoutDiagnosticSummary(ObservedCount: 1, RepresentedBytes: 1_024));

        var jobObservation = new JobRunObservation(
            SourceEventId: 6,
            JobRunId: 42,
            JobId: 24,
            TenantId: 7,
            ClientIdentity: "client-a",
            StartedBy: "operator-a",
            Status: JobRunState.Succeeded,
            CurrentStepOrdinal: 2,
            CreatedAtUtc: timestamp,
            StartedAtUtc: timestamp,
            CompletedAtUtc: timestamp,
            Timestamp: timestamp);
        var job = RealtimeFanoutEnvelopeFactory.FromJob(
            jobObservation,
            new JobMessageResult(
                42,
                JobMessageDisposition.Accepted,
                JobRunState.Succeeded,
                6,
                JobCommandCorrelationStatus.NotProvided,
                null));

        new[] { terminal, job! }
            .Should().OnlyContain(envelope => envelope.IsValid && !envelope.IsAuthoritative);
        new[] { terminal, job! }
            .Select(static envelope => envelope.Category)
            .Should().Equal(
                RealtimeFanoutCategory.Terminal,
                RealtimeFanoutCategory.Job);
        new[] { terminal, job! }
            .Select(static envelope => envelope.Diagnostics?.RepresentedBytes ?? 0)
            .Should().Equal(1_024UL, 0UL);
    }

    [Fact]
    public void GroupNames_AreDeterministicTenantScopedAndHashExternalIdentifiers()
    {
        RealtimeGroupName.TryCreate(Terminal(), out var first).Should().BeTrue();
        RealtimeGroupName.TryCreate(Terminal(), out var second).Should().BeTrue();
        RealtimeGroupName.TryCreate(
            Terminal() with { TenantId = 8 },
            out var otherTenant).Should().BeTrue();

        first.Should().Be(second);
        first.Should().StartWith("akka-shadow:v1:tenant:");
        first.Should().Contain(":client:");
        first.Should().NotContain(":7:");
        first.Should().NotContain("client-a");
        first.Should().NotContain("terminal-a");
        otherTenant.Should().NotBe(first);
        first.Length.Should().BeLessThanOrEqualTo(RealtimeGroupName.MaximumLength);
    }

    [Fact]
    public void GroupNames_DomainSeparateEveryScopeAndExposeNoRawIdentifiers()
    {
        var raw = "SameValue42";
        var targets = new[]
        {
            new RealtimeFanoutTarget(42, RealtimeFanoutTargetScope.Tenant),
            new RealtimeFanoutTarget(42, RealtimeFanoutTargetScope.Client, ClientId: raw),
            new RealtimeFanoutTarget(42, RealtimeFanoutTargetScope.Terminal, ClientId: raw, SessionId: raw),
            new RealtimeFanoutTarget(42, RealtimeFanoutTargetScope.RemoteSupport, ClientId: raw, SessionId: raw),
            new RealtimeFanoutTarget(42, RealtimeFanoutTargetScope.FileBrowser, ClientId: raw, RequestId: raw),
            new RealtimeFanoutTarget(42, RealtimeFanoutTargetScope.Job, JobId: 42),
            new RealtimeFanoutTarget(42, RealtimeFanoutTargetScope.Command, CommandId: raw)
        };

        var groups = targets.Select(target =>
        {
            RealtimeGroupName.TryCreate(target, out var group).Should().BeTrue();
            return group;
        }).ToArray();

        groups.Should().OnlyHaveUniqueItems();
        groups.Should().OnlyContain(group =>
            !group.Contains(raw, StringComparison.Ordinal) &&
            !group.Contains(":tenant:42", StringComparison.Ordinal) &&
            !group.Contains(":job:42", StringComparison.Ordinal));

        RealtimeGroupName.TryCreate(
            new RealtimeFanoutTarget(42, RealtimeFanoutTargetScope.Command, CommandId: raw.ToLowerInvariant()),
            out var caseVariant).Should().BeTrue();
        caseVariant.Should().NotBe(groups[^1], "identifier canonicalization is explicitly case-sensitive");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("contains:delimiter")]
    public void GroupNames_RejectEmptyOrMalformedIdentifiers(string? identifier)
    {
        RealtimeGroupName.TryCreate(
            new RealtimeFanoutTarget(7, RealtimeFanoutTargetScope.Command, CommandId: identifier),
            out var group).Should().BeFalse();
        group.Should().BeEmpty();
    }

    [Fact]
    public void GroupNames_RejectOversizedIdentifiersAndHashInputAmbiguity()
    {
        RealtimeGroupName.TryCreate(
            new RealtimeFanoutTarget(7, RealtimeFanoutTargetScope.Command, CommandId: new string('a', 129)),
            out _).Should().BeFalse();

        RealtimeGroupName.TryCreate(
            new RealtimeFanoutTarget(7, RealtimeFanoutTargetScope.Command, CommandId: "ab"),
            out var first).Should().BeTrue();
        RealtimeGroupName.TryCreate(
            new RealtimeFanoutTarget(7, RealtimeFanoutTargetScope.Command, CommandId: "a-b"),
            out var second).Should().BeTrue();
        first.Should().NotBe(second);
    }

    [Fact]
    public async Task TenantAuthorization_UsesEffectiveTenantPermissionAndHubRequiresTheRuntimePolicy()
    {
        var effectiveAccess = new TestEffectiveAccessService(authorized: false);
        var authorizer = new RealtimeTenantAccessAuthorizer(effectiveAccess);
        var principal = Principal(tenantId: 7);

        (await authorizer.IsAuthorizedAsync(principal, 7)).Should().BeFalse(
            "a tenant claim alone must not replace effective permission evaluation");
        effectiveAccess.LastPermission.Should().Be(NetRatelPermissions.ClientManagement);
        effectiveAccess.LastTenantId.Should().Be(7);
        effectiveAccess.AuthorizationCalls.Should().Be(1);

        (await authorizer.IsAuthorizedAsync(Principal(tenantId: null), 0)).Should().BeFalse();
        effectiveAccess.AuthorizationCalls.Should().Be(1, "invalid tenant identifiers must fail before data access");

        typeof(RealtimeHub).GetCustomAttribute<AuthorizeAttribute>()
            .Should().Match<AuthorizeAttribute>(attribute => attribute.Policy == "RealtimeAccess");
        typeof(RealtimeHub).GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Select(static method => method.Name)
            .Should().NotContain("JoinGroup");
    }

    [Fact]
    public async Task TenantAuthorization_UsesEffectiveAccessResultForOperatorAndAdministrator()
    {
        var effectiveAccess = new TestEffectiveAccessService(authorized: true);
        var authorizer = new RealtimeTenantAccessAuthorizer(effectiveAccess);
        var principal = Principal(tenantId: null, operatorRole: true);

        (await authorizer.IsAuthorizedAsync(principal, 7)).Should().BeTrue();
        (await authorizer.IsAuthorizedAsync(principal, 8)).Should().BeTrue();
        effectiveAccess.AuthorizationCalls.Should().Be(2);
    }

    [Fact]
    public void SubscriptionRegistry_EnforcesPerConnectionGroupAndRateBounds()
    {
        var registry = new RealtimeSubscriptionRegistry(TimeProvider.System);
        registry.TryRegisterConnection("connection-a", Principal(7)).Should().BeTrue();

        for (var index = 0; index < RealtimeSubscriptionRegistry.MaximumGroupsPerConnection; index++)
        {
            RealtimeGroupName.TryCreate(
                new RealtimeFanoutTarget(7, RealtimeFanoutTargetScope.Job, JobId: (ulong)index + 1),
                out var group).Should().BeTrue();
            registry.TryAddSubscription("connection-a", group)
                .Should().BeTrue();
        }

        RealtimeGroupName.TryCreate(Command(), out var overflowGroup).Should().BeTrue();
        registry.TryAddSubscription("connection-a", overflowGroup)
            .Should().BeFalse();
        registry.RecordUnauthorizedSubscription();

        var status = registry.GetStatus();
        status.ActiveConnections.Should().Be(1);
        status.ActiveGroups.Should().Be(RealtimeSubscriptionRegistry.MaximumGroupsPerConnection);
        status.ActiveMemberships.Should().Be(RealtimeSubscriptionRegistry.MaximumGroupsPerConnection);
        status.RejectedSubscriptions.Should().Be(2);
        status.UnauthorizedSubscriptions.Should().Be(1);
    }

    private static ClaimsPrincipal Principal(int? tenantId, bool operatorRole = false)
    {
        var claims = new List<Claim> { new("sub", "operator-a") };
        if (tenantId is not null)
        {
            claims.Add(new Claim("tenant_id", tenantId.Value.ToString()));
        }

        if (operatorRole)
        {
            claims.Add(new Claim("roles", "Operator"));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    private static RealtimeFanoutTarget Tenant() => new(7, RealtimeFanoutTargetScope.Tenant);

    private static RealtimeFanoutTarget Client() =>
        new(7, RealtimeFanoutTargetScope.Client, ClientId: "client-a");

    private static RealtimeFanoutTarget Terminal() =>
        new(7, RealtimeFanoutTargetScope.Terminal, ClientId: "client-a", SessionId: "terminal-a");

    private static RealtimeFanoutTarget RemoteSupport() =>
        new(7, RealtimeFanoutTargetScope.RemoteSupport, ClientId: "client-a", SessionId: "remote-a");

    private static RealtimeFanoutTarget FileBrowser() =>
        new(7, RealtimeFanoutTargetScope.FileBrowser, ClientId: "client-a", RequestId: "request-a");

    private static RealtimeFanoutTarget Job() => new(7, RealtimeFanoutTargetScope.Job, JobId: 42);

    private static RealtimeFanoutTarget Command() =>
        new(7, RealtimeFanoutTargetScope.Command, CommandId: "command-a");

    private sealed class TestEffectiveAccessService(bool authorized) : IEffectiveAccessService
    {
        public string? LastPermission { get; private set; }
        public int? LastTenantId { get; private set; }
        public int AuthorizationCalls { get; private set; }

        public Task<bool> AuthorizeAsync(
            ClaimsPrincipal principal,
            string permission,
            int? tenantId,
            CancellationToken cancellationToken = default)
        {
            LastPermission = permission;
            LastTenantId = tenantId;
            AuthorizationCalls++;
            return Task.FromResult(authorized);
        }

        public Task<EffectiveAccessSnapshot> GetSnapshotAsync(
            ClaimsPrincipal principal,
            int? tenantId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<int[]?> GetAuthorizedTenantIdsAsync(
            ClaimsPrincipal principal,
            string permission,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> HasInstancePermissionAsync(
            ClaimsPrincipal principal,
            string permission,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task ReconcileBuiltInRolesAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
