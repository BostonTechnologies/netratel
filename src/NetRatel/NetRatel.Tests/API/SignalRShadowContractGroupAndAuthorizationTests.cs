using System.Reflection;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using NetRatel.API.Realtime.Shadow;
using NetRatel.Application.Fanout;
using NetRatel.Application.Jobs;
using NetRatel.Application.Terminals;
using NetRatel.Shared.Contracts.Terminals;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class SignalRShadowContractGroupAndAuthorizationTests
{
    [Fact]
    public void NamedContracts_ProduceRetainedSafeNonAuthoritativeCategories()
    {
        var timestamp = DateTimeOffset.Parse("2026-08-07T10:00:00Z");
        var updates = new ShadowFanoutUpdated[]
        {
            new PresenceShadowUpdated(Tenant(), ShadowFanoutEventType.Updated, ShadowFanoutStatus.Active, timestamp),
            new TelemetryShadowUpdated(Client(), ShadowFanoutEventType.Updated, ShadowFanoutStatus.Active, timestamp),
            new CommandShadowUpdated(Command(), ShadowFanoutEventType.Completed, ShadowFanoutStatus.Completed, timestamp),
            new JobShadowUpdated(Job(), ShadowFanoutEventType.Updated, ShadowFanoutStatus.Active, timestamp),
            new TerminalShadowUpdated(Terminal(), ShadowFanoutEventType.Updated, ShadowFanoutStatus.Active, timestamp),
            new FileBrowserShadowUpdated(FileBrowser(), ShadowFanoutEventType.Updated, ShadowFanoutStatus.Active, timestamp)
        };

        updates.Select(static update => update.ToEnvelope().Category).Should().Equal(
            ShadowFanoutCategory.Presence,
            ShadowFanoutCategory.Telemetry,
            ShadowFanoutCategory.Command,
            ShadowFanoutCategory.Job,
            ShadowFanoutCategory.Terminal,
            ShadowFanoutCategory.FileBrowser);
        updates.Select(static update => update.ToEnvelope()).Should().OnlyContain(envelope =>
            envelope.IsValid && !envelope.IsAuthoritative);

        var contractTypes = new[]
        {
            typeof(ShadowFanoutEnvelope),
            typeof(ShadowFanoutTarget),
            typeof(ShadowFanoutDiagnosticSummary)
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
            new ShadowFanoutEnvelope(1, ShadowFanoutCategory.Presence, Tenant(), ShadowFanoutEventType.Updated, ShadowFanoutStatus.Active, timestamp),
            new ShadowFanoutEnvelope(1, ShadowFanoutCategory.Telemetry, Client(), ShadowFanoutEventType.Updated, ShadowFanoutStatus.Active, timestamp),
            new ShadowFanoutEnvelope(1, ShadowFanoutCategory.Command, Command(), ShadowFanoutEventType.Updated, ShadowFanoutStatus.Active, timestamp),
            new ShadowFanoutEnvelope(1, ShadowFanoutCategory.Job, Job(), ShadowFanoutEventType.Updated, ShadowFanoutStatus.Active, timestamp),
            new ShadowFanoutEnvelope(1, ShadowFanoutCategory.Terminal, Terminal(), ShadowFanoutEventType.Updated, ShadowFanoutStatus.Active, timestamp),
            new ShadowFanoutEnvelope(1, ShadowFanoutCategory.FileBrowser, FileBrowser(), ShadowFanoutEventType.Updated, ShadowFanoutStatus.Active, timestamp)
        };

        envelopes.Should().OnlyContain(envelope => envelope.IsValid);
        envelopes.Select(envelope => envelope with { EventType = ShadowFanoutEventType.Snapshot })
            .Should().OnlyContain(envelope => envelope.IsValid && !envelope.ResyncRequired);
        envelopes.Select(envelope => envelope with { ResyncRequired = true })
            .Should().OnlyContain(envelope => envelope.IsValid && envelope.ResyncRequired);
    }

    [Fact]
    public void EnvelopeFactory_MapsAcceptedPhaseFiveThroughEightStateToSafeMetadata()
    {
        var timestamp = DateTimeOffset.Parse("2026-08-07T10:00:00Z");
        var terminalKey = new TerminalShadowSessionKey(
            7,
            "client-a",
            "terminal-a",
            TerminalTransportKind.ApiWebSocket);
        var terminal = ShadowFanoutEnvelopeFactory.FromTerminal(
            new TerminalShadowEvent(
                terminalKey,
                TerminalShadowEventKind.OutputObserved,
                TerminalShadowStreamType.StandardOutput,
                timestamp,
                Sequence: 3,
                PayloadLength: 512),
            new TerminalShadowMessageResult(
                terminalKey,
                TerminalShadowMessageDisposition.Accepted,
                TerminalShadowSessionStatus.Opened,
                3,
                4,
                1_024,
                1,
                2));

        var jobObservation = new JobRunShadowObservation(
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
        var job = ShadowFanoutEnvelopeFactory.FromJob(
            jobObservation,
            new JobShadowMessageResult(
                42,
                JobShadowMessageDisposition.Accepted,
                JobRunState.Succeeded,
                6,
                JobCommandCorrelationStatus.NotProvided,
                null));

        new[] { terminal, job! }
            .Should().OnlyContain(envelope => envelope.IsValid && !envelope.IsAuthoritative);
        new[] { terminal, job! }
            .Select(static envelope => envelope.Category)
            .Should().Equal(
                ShadowFanoutCategory.Terminal,
                ShadowFanoutCategory.Job);
        new[] { terminal, job! }
            .Select(static envelope => envelope.Diagnostics?.RepresentedBytes ?? 0)
            .Should().Equal(1_024UL, 0UL);
    }

    [Fact]
    public void GroupNames_AreDeterministicTenantScopedAndHashExternalIdentifiers()
    {
        SignalRShadowGroupName.TryCreate(Terminal(), out var first).Should().BeTrue();
        SignalRShadowGroupName.TryCreate(Terminal(), out var second).Should().BeTrue();
        SignalRShadowGroupName.TryCreate(
            Terminal() with { TenantId = 8 },
            out var otherTenant).Should().BeTrue();

        first.Should().Be(second);
        first.Should().StartWith("akka-shadow:v1:tenant:");
        first.Should().Contain(":client:");
        first.Should().NotContain(":7:");
        first.Should().NotContain("client-a");
        first.Should().NotContain("terminal-a");
        otherTenant.Should().NotBe(first);
        first.Length.Should().BeLessThanOrEqualTo(SignalRShadowGroupName.MaximumLength);
    }

    [Fact]
    public void GroupNames_DomainSeparateEveryScopeAndExposeNoRawIdentifiers()
    {
        var raw = "SameValue42";
        var targets = new[]
        {
            new ShadowFanoutTarget(42, ShadowFanoutTargetScope.Tenant),
            new ShadowFanoutTarget(42, ShadowFanoutTargetScope.Client, ClientId: raw),
            new ShadowFanoutTarget(42, ShadowFanoutTargetScope.Terminal, ClientId: raw, SessionId: raw),
            new ShadowFanoutTarget(42, ShadowFanoutTargetScope.RemoteSupport, ClientId: raw, SessionId: raw),
            new ShadowFanoutTarget(42, ShadowFanoutTargetScope.FileBrowser, ClientId: raw, RequestId: raw),
            new ShadowFanoutTarget(42, ShadowFanoutTargetScope.Job, JobId: 42),
            new ShadowFanoutTarget(42, ShadowFanoutTargetScope.Command, CommandId: raw)
        };

        var groups = targets.Select(target =>
        {
            SignalRShadowGroupName.TryCreate(target, out var group).Should().BeTrue();
            return group;
        }).ToArray();

        groups.Should().OnlyHaveUniqueItems();
        groups.Should().OnlyContain(group =>
            !group.Contains(raw, StringComparison.Ordinal) &&
            !group.Contains(":tenant:42", StringComparison.Ordinal) &&
            !group.Contains(":job:42", StringComparison.Ordinal));

        SignalRShadowGroupName.TryCreate(
            new ShadowFanoutTarget(42, ShadowFanoutTargetScope.Command, CommandId: raw.ToLowerInvariant()),
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
        SignalRShadowGroupName.TryCreate(
            new ShadowFanoutTarget(7, ShadowFanoutTargetScope.Command, CommandId: identifier),
            out var group).Should().BeFalse();
        group.Should().BeEmpty();
    }

    [Fact]
    public void GroupNames_RejectOversizedIdentifiersAndHashInputAmbiguity()
    {
        SignalRShadowGroupName.TryCreate(
            new ShadowFanoutTarget(7, ShadowFanoutTargetScope.Command, CommandId: new string('a', 129)),
            out _).Should().BeFalse();

        SignalRShadowGroupName.TryCreate(
            new ShadowFanoutTarget(7, ShadowFanoutTargetScope.Command, CommandId: "ab"),
            out var first).Should().BeTrue();
        SignalRShadowGroupName.TryCreate(
            new ShadowFanoutTarget(7, ShadowFanoutTargetScope.Command, CommandId: "a-b"),
            out var second).Should().BeTrue();
        first.Should().NotBe(second);
    }

    [Fact]
    public void TenantAuthorization_IsFailClosedAndHubRequiresTheShadowPolicy()
    {
        var authorizer = new ShadowTenantClaimAuthorizer();
        var principal = Principal(tenantId: 7);

        authorizer.IsAuthorized(principal, 7).Should().BeTrue();
        authorizer.IsAuthorized(principal, 8).Should().BeFalse();
        authorizer.IsAuthorized(Principal(tenantId: null), 7).Should().BeFalse();

        typeof(AkkaShadowHub).GetCustomAttribute<AuthorizeAttribute>()
            .Should().Match<AuthorizeAttribute>(attribute => attribute.Policy == "AkkaShadowAccess");
        typeof(AkkaShadowHub).GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Select(static method => method.Name)
            .Should().NotContain("JoinGroup");
    }

    [Fact]
    public void TenantAuthorization_AllowsOperatorAcrossTenants()
    {
        var authorizer = new ShadowTenantClaimAuthorizer();
        var principal = Principal(tenantId: null, operatorRole: true);

        authorizer.IsAuthorized(principal, 7).Should().BeTrue();
        authorizer.IsAuthorized(principal, 8).Should().BeTrue();
    }

    [Fact]
    public void SubscriptionRegistry_EnforcesPerConnectionGroupAndRateBounds()
    {
        var registry = new SignalRShadowSubscriptionRegistry(TimeProvider.System);
        registry.TryRegisterConnection("connection-a", Principal(7)).Should().BeTrue();

        for (var index = 0; index < SignalRShadowSubscriptionRegistry.MaximumGroupsPerConnection; index++)
        {
            SignalRShadowGroupName.TryCreate(
                new ShadowFanoutTarget(7, ShadowFanoutTargetScope.Job, JobId: (ulong)index + 1),
                out var group).Should().BeTrue();
            registry.TryAddSubscription("connection-a", group)
                .Should().BeTrue();
        }

        SignalRShadowGroupName.TryCreate(Command(), out var overflowGroup).Should().BeTrue();
        registry.TryAddSubscription("connection-a", overflowGroup)
            .Should().BeFalse();
        registry.RecordUnauthorizedSubscription();

        var status = registry.GetStatus();
        status.ActiveConnections.Should().Be(1);
        status.ActiveGroups.Should().Be(SignalRShadowSubscriptionRegistry.MaximumGroupsPerConnection);
        status.ActiveMemberships.Should().Be(SignalRShadowSubscriptionRegistry.MaximumGroupsPerConnection);
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

    private static ShadowFanoutTarget Tenant() => new(7, ShadowFanoutTargetScope.Tenant);

    private static ShadowFanoutTarget Client() =>
        new(7, ShadowFanoutTargetScope.Client, ClientId: "client-a");

    private static ShadowFanoutTarget Terminal() =>
        new(7, ShadowFanoutTargetScope.Terminal, ClientId: "client-a", SessionId: "terminal-a");

    private static ShadowFanoutTarget RemoteSupport() =>
        new(7, ShadowFanoutTargetScope.RemoteSupport, ClientId: "client-a", SessionId: "remote-a");

    private static ShadowFanoutTarget FileBrowser() =>
        new(7, ShadowFanoutTargetScope.FileBrowser, ClientId: "client-a", RequestId: "request-a");

    private static ShadowFanoutTarget Job() => new(7, ShadowFanoutTargetScope.Job, JobId: 42);

    private static ShadowFanoutTarget Command() =>
        new(7, ShadowFanoutTargetScope.Command, CommandId: "command-a");
}
