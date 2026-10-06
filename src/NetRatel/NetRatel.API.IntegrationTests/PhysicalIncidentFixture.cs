using System.Data;
using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.Application.Agents;
using NetRatel.Application.Flows;
using NetRatel.Application.Jobs;
using NetRatel.Application.Monitoring;
using NetRatel.Application.RatelDesk;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.ServiceIdentity;
using NetRatel.Infrastructure.ServiceLinks;
using NetRatel.Shared.Contracts.Enrollment;
using NetRatel.Shared.Contracts.Flows;
using NetRatel.Shared.Contracts.Monitoring;
using NetRatel.Shared.Contracts.RatelDesk;
using NetRatel.Shared.ServiceLinks;
using Xunit;

namespace NetRatel.API.IntegrationTests.ServiceLinks;

// Real production API/worker OS processes, full Client image and immutable published
// receiver. The only application writes in this class are authenticated owner HTTP
// operations. Its separate service provider contains only read-only probes.
internal sealed partial class PhysicalIncidentFixture : IPhysicalIncidentFixture
{
    private readonly ServiceLinkNativeListener listener = new();
    private readonly JsonSerializerOptions json = new(JsonSerializerDefaults.Web);
    private ServiceLinkPair pair = null!;
    private PhysicalDiskFixtureProcess disk = null!;
    private ServiceLinkNativeArtifacts artifacts = null!;
    private JsonElement provision;
    private JsonElement client;
    private PhysicalRule? selectedRule;
    private FlowGraphDto? selectedGraph;
    private Guid flowId;
    private Guid agent;
    private Guid sourceNamespace;
    private Guid connector;
    private string? command;
    private string? commandMarker;
    private PhysicalLoss? responseLoss;
    private PhysicalRecordedTask? recordedTask;
    private CachedPhysicalAuthorization? cachedAuthorization;
    private long selectedCredentialRevision;
    private PhysicalAllocation? secondAllocation;
    private int allocationCount;
    private bool closed;
    private bool localUnlinkVerified;
    private PhysicalApiProcess[]? terminalProcesses;
    private int Tenant => int.Parse(pair.NetRatel.TenantId, CultureInfo.InvariantCulture);
    public static async Task<PhysicalIncidentFixture> CreateAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux()) throw new InvalidOperationException("The required genuine physical disk lane must run on Linux.");
        var fixture = new PhysicalIncidentFixture();
        try
        {
            fixture.artifacts = await ServiceLinkNativeArtifacts.ResolveAsync(ct);
            fixture.pair = await ServiceLinkPair.CreateAsync(true, nativeListener: fixture.listener,
                rotationPolicy: new(false, false), useSystemTime: true, physicalIncidentMode: true);
            await fixture.pair.NetRatel.AdoptActualPhysicalFlowProducerAsync(ct);
            var repository = FindRepository();
            var helper = Path.Combine(repository, "tools", "testing", "physical-incident", "physical_disk_fixture_process.py");
            var work = Environment.GetEnvironmentVariable("NETRATEL_PHYSICAL_WORK_ROOT")
                ?? throw new InvalidOperationException("The required Linux lane did not select its owned disk-backed fixture work root.");
            fixture.disk = new PhysicalDiskFixtureProcess(helper, work);
            fixture.provision = await fixture.disk.ProvisionAsync(ct);
            return fixture;
        }
        catch { await fixture.DisposeAsync(); throw; }
    }

    public async Task AssertExactCurrentSourcesAndPublishedCompanionAsync(CancellationToken ct)
    {
        var current = await ServiceLinkNativeArtifacts.ResolveAsync(ct);
        Require(current == artifacts, "The physical source/artifact identity changed during setup.");
        var metadata = await GetAsync<ServiceLinkMetadata>(pair.RatelDesk.Anonymous,
            "/api/integrations/service-link/metadata", ct);
        Require(metadata.Product == "rateldesk" && metadata.ProductVersion == ServiceLinkPublishedRatelDeskPeer.PublishedVersion &&
            metadata.InstanceId == pair.RatelDesk.InstanceId.ToString("D"), "The actual published compatible peer identity differs.");
        var candidate = await GetAsync<ServiceLinkMetadata>(pair.NetRatel.Anonymous,
            "/api/integrations/service-link/metadata", ct);
        var expectedVersion = typeof(PhysicalIncidentFixture).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(x => x.Key == "ProductVersion").Value;
        Require(candidate.Product == "netratel" && candidate.ProductVersion == expectedVersion &&
            candidate.InstanceId == pair.NetRatel.InstanceId.ToString("D"), "The actual production API process source/version identity differs.");
    }

    public async Task AssertNoFixtureSeededExecutionAuthorityAsync(CancellationToken ct)
    {
        await using var scope = pair.NetRatel.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        Require(!await db.Agents.AnyAsync(ct) && !await db.Jobs.AnyAsync(ct) && !await db.JobRuns.AnyAsync(ct) &&
            !await db.Set<ClientConnectionOwnerRecord>().AnyAsync(ct) && !await db.MonitoringEvidenceStreams.AnyAsync(ct) &&
            !await db.MonitoringOccurrences.AnyAsync(ct) && !await db.MonitoringFlowOutbox.AnyAsync(ct) &&
            !await db.FlowRuns.AnyAsync(ct) && !await db.FlowActions.AnyAsync(ct) &&
            !await db.Set<FlowReceiverEvidenceRecord>().AnyAsync(ct), "The disposable physical fixture contains seeded runtime authority or effects.");
        Require(await pair.RatelDesk.CountAsync("Incidents") == 0 && await pair.RatelDesk.CountAsync("IncidentCreateReceipts") == 0,
            "The published physical receiver contains a seeded incident or receipt.");
    }

    public async Task StartFullProductionClientAsync(CancellationToken ct)
    {
        var image = Environment.GetEnvironmentVariable("NETRATEL_PHYSICAL_CLIENT_IMAGE_ID")
            ?? throw new InvalidOperationException("The same-job production Client image identity is missing.");
        var executable = Environment.GetEnvironmentVariable("NETRATEL_PHYSICAL_CLIENT_EXECUTABLE_SHA256")
            ?? throw new InvalidOperationException("The actual same-image production Client executable identity is missing.");
        var version = typeof(PhysicalIncidentFixture).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(x => x.Key == "ProductVersion").Value!;
        using var response = await pair.NetRatel.Administrator.PostAsJsonAsync($"/api/v1/tenants/{Tenant}/enrollment-codes",
            new { ValidForMinutes = 5, MaxUses = 1, Note = "isolated full production Client disk acceptance" }, ct);
        Require(response.IsSuccessStatusCode, "The real owner enrollment-code command failed.");
        var issued = await response.Content.ReadFromJsonAsync<IssuedEnrollmentCodeDto>(cancellationToken: ct);
        Require(issued?.EnrollmentCode is { Length: > 0 and <= 4096 }, "The actual owner enrollment-code response was invalid.");
        client = await disk.StartClientAsync(image, artifacts.CandidateSourceSha, version, executable,
            listener.CaPem, pair.NetRatel.BaseUrl, listener.Endpoint, issued!.EnrollmentCode, ct);
        Require(client.GetProperty("trustedTlsAlpnVerified").GetBoolean() && client.GetProperty("unrelatedCaRejected").GetBoolean() &&
            client.GetProperty("daemonVisibleOwnedExt4Verified").GetBoolean() && client.GetProperty("slowCollectionSeconds").GetInt32() == 5,
            "The genuine Client image/owned volume/TLS/effective cadence preconditions were not proven.");
        command = client.GetProperty("command").GetString(); commandMarker = client.GetProperty("commandMarker").GetString();
        Require(command is { Length: > 0 and <= 1024 } && commandMarker is { Length: > 0 and <= 128 }, "The owned bounded command gate is invalid.");
    }

    public async Task<PhysicalAdmission> WaitRealFirstHeartbeatAndTelemetryAdmissionAsync(CancellationToken ct)
    {
        while (true)
        {
            await disk.AssertAliveAsync(ct);
            await using var scope = pair.NetRatel.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            var enrolled = await db.Agents.AsNoTracking().Where(x => x.TenantId == Tenant && x.CreatedBy == "enrollment").ToArrayAsync(ct);
            Require(enrolled.Length <= 1, "The single genuine Client enrolled more than one Agent-ID.");
            if (enrolled is [var value] && value.IsEnabled && value.Status == AgentStatus.Active && !string.IsNullOrWhiteSpace(value.PublicKeyFingerprint))
            {
                agent = value.Id;
                var snapshot = await ReadAcceptedTelemetryAsync(agent, ct);
                return new(agent, snapshot.Owner.ConnectionId!.Value, checked((ulong)snapshot.Owner.ConnectionEpoch),
                    snapshot.Stream.EvidenceStreamId, true, true, "full-production-NetRatel.Client");
            }
            await Task.Delay(200, ct);
        }
    }

    public async Task StartSecondRealApiWorkerReplicaAsync(CancellationToken ct)
    { await pair.NetRatel.StartPhysicalWorkerReplicaAsync(ct); }
    public Task AssertTwoActualWorkerReplicasAsync(CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); pair.NetRatel.AssertPhysicalWorkerProcesses(); return Task.CompletedTask; }
    public Task AssertFullProductionClientWithinFixedLifetimeAsync(CancellationToken ct) => disk.AssertAliveAsync(ct);

    public async Task<PhysicalDiskSample> ReadActualAcceptedSelectedDiskAsync(Guid agentId, CancellationToken ct)
    {
        var accepted = await ReadAcceptedTelemetryAsync(agentId, ct);
        var diskValue = accepted.Snapshot.GetProperty("disks").EnumerateArray()
            .Single(x => x.GetProperty("scope").GetString() == "/netratel-physical-disk");
        Require(diskValue.GetProperty("collectionQuality").GetInt32() == 1,
            "The actual selected disk collection is incomplete, unsupported or failed.");
        return new("/netratel-physical-disk", diskValue.GetProperty("collectionId").GetGuid(),
            diskValue.GetProperty("collectedAtUtc").GetDateTimeOffset(), accepted.Snapshot.GetProperty("observedAtUtc").GetDateTimeOffset(),
            accepted.Snapshot.GetProperty("receivedAtUtc").GetDateTimeOffset(), accepted.Sequence,
            checked((long)diskValue.GetProperty("totalBytes").GetUInt64()), checked((long)diskValue.GetProperty("freeBytes").GetUInt64()), true, true, true);
    }

    private async Task<(JsonElement Snapshot, ulong Sequence, ClientConnectionOwnerRecord Owner, MonitoringEvidenceStreamRecord Stream)>
        ReadAcceptedTelemetryAsync(Guid agentId, CancellationToken ct)
    {
        // A real authorized SSE envelope contains the transport epoch/sequence and
        // the server's actual effective sampling policy. Do not invent a cursor from
        // clock time or a cached sample. Closing each read releases its demand lease.
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v2/agents/{Tenant}/{agentId:D}/telemetry/stream?samplePeriodMs=5000");
        using var response = await pair.NetRatel.Administrator.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        Require(response.StatusCode == HttpStatusCode.OK, "The actual owner telemetry read was denied.");
        await using var body = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(body);
        while (true)
        {
            var line = await reader.ReadLineAsync(ct) ?? throw new InvalidOperationException("The actual accepted telemetry stream closed.");
            Require(line.Length <= 131_072, "The actual accepted telemetry envelope exceeded its bound.");
            if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
            using var message = JsonDocument.Parse(line[6..]);
            var envelope = message.RootElement;
            if (!envelope.TryGetProperty("snapshot", out var sample) || sample.ValueKind == JsonValueKind.Null) continue;
            Require(envelope.GetProperty("effectiveSamplePeriodMilliseconds").GetInt32() == 5000 &&
                sample.GetProperty("tenantId").GetInt32() == Tenant && sample.GetProperty("agentId").GetGuid() == agentId &&
                sample.GetProperty("isAuthoritative").GetBoolean(), "The actual admitted telemetry target/cadence/authority differs.");
            var epoch = envelope.GetProperty("connectionEpoch").GetInt64();
            var sequence = envelope.GetProperty("sequence").GetUInt64();
            await using var scope = pair.NetRatel.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
            await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", ct);
            var owner = await db.Set<ClientConnectionOwnerRecord>().AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == Tenant && x.AgentId == agentId, ct);
            var stream = await db.MonitoringEvidenceStreams.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == Tenant && x.AgentId == agentId, ct);
            var now = await db.Database.SqlQuery<DateTimeOffset>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);
            var accepted = owner is { Active: true, ConnectionId: not null } && owner.ConnectionEpoch == epoch && owner.LastHeartbeatSequence > 0 &&
                owner.PresenceExpiresAtUtc > now && owner.AuthenticationExpiresAtUtc > now && stream is { Active: true } &&
                stream.ConnectionId == owner.ConnectionId && stream.ConnectionEpoch == epoch && stream.CommittedRegistrationOrdinal > 0;
            await transaction.CommitAsync(ct);
            if (!accepted) continue; // The real first heartbeat/registration may still be committing.
            return (sample.Clone(), sequence, owner!, stream!);
        }
    }

    public async Task<PhysicalRule> ConfigureOwnerPublishedFlowAndSelectedDiskRuleAsync(Guid agentId, string scope,
        PhysicalThresholds thresholds, CancellationToken ct)
    {
        Require(agentId == agent && scope == "/netratel-physical-disk", "Only the real admitted selected volume can be configured.");
        var setupBase = $"/api/v2/tenants/{Tenant}/connectors/rateldesk";
        var setup = await GetAsync<RatelDeskConnectorSetupDto>(pair.NetRatel.Administrator, setupBase + "/setup", ct);
        var approved = setup.ManagedLinks.Single(x => x.LinkId == pair.Review.GrantSummary.LinkId &&
            x.OrganizationId == pair.RatelDesk.OrganizationId && x.CustomerId == pair.RatelDesk.CustomerId);
        Require(setup.FlowSourceInstanceId == pair.NetRatel.SourceInstanceId && setup.AdoptedSourceInstanceId == setup.FlowSourceInstanceId.ToString("D"),
            "The managed setup differs from the actual adopted persisted Flow producer.");
        connector = Guid.NewGuid();
        var configuration = new RatelDeskConnectorConfiguration("Physical disk acceptance", approved.ApiBaseUrl,
            approved.OrganizationId, approved.CustomerId, null, [], new(), true);
        var saved = await SendAsync<RatelDeskConnectorDto>(pair.NetRatel.Administrator, HttpMethod.Put, setupBase + $"/{connector:D}",
            new SaveRatelDeskConnectorRequest(0, configuration, new("service_link", approved.LinkId)), ct);
        Require(saved.Id == connector && saved.Authentication?.ManagedLinkId == approved.LinkId && saved.Revision > 0,
            "The ordinary owner save did not bind the exact approved managed link.");
        var connection = await SendAsync<RatelDeskConnectionTestResult>(pair.NetRatel.Administrator, HttpMethod.Post,
            setupBase + $"/{connector:D}/connection-test", new { }, ct);
        Require(connection.AutomaticDeliveryAvailable && connection.Status == RatelDeskConnectionTestStatus.MappingValidated,
            "Actual capability/namespace/targets/current credential did not permit delivery.");
        var readiness = await ReadReadinessAsync(ct);
        sourceNamespace = readiness.Peer.SourceNamespaceId;
        Require(readiness.Peer.SourceInstanceId == pair.NetRatel.SourceInstanceId && readiness.Peer.ReceiverInstanceId == pair.RatelDesk.InstanceId.ToString("D"),
            "The actual readiness pinned another receiver/source tuple.");
        var flowBase = $"/api/v1/tenants/{Tenant}/flows";
        var catalog = await GetAsync<FlowConnectorReferenceDto[]>(pair.NetRatel.Administrator, flowBase + "/connectors", ct);
        Require(catalog.Single(x => x.Id == connector).CanExecute, "The actual current connector catalog did not permit execution.");
        var template = await GetAsync<FlowGraphDto>(pair.NetRatel.Administrator, flowBase + "/template", ct);
        var action = template.Nodes.Single(x => x.Kind == FlowNodeKind.CreateIncident);
        selectedGraph = template with { Nodes = template.Nodes.Select(x => x.Id == action.Id
            ? x with { ConnectorId = connector, ConnectorRevision = saved.Revision } : x).ToArray() };
        var validation = await SendAsync<FlowValidationResultDto>(pair.NetRatel.Administrator, HttpMethod.Post, flowBase + "/validate", selectedGraph, ct);
        Require(validation.Valid, "The ordinary supported Flow graph validation failed.");
        var flow = await SendAsync<FlowDefinitionDto>(pair.NetRatel.Administrator, HttpMethod.Post, flowBase, new FlowCreateRequest("Physical disk incident"), ct);
        flowId = flow.Id;
        flow = await SendAsync<FlowDefinitionDto>(pair.NetRatel.Administrator, HttpMethod.Put, flowBase + $"/{flowId:D}/draft",
            new FlowSaveDraftRequest(flow.Revision, flow.Name, selectedGraph), ct);
        var version = await SendAsync<FlowVersionDto>(pair.NetRatel.Administrator, HttpMethod.Post, flowBase + $"/{flowId:D}/publish", new FlowRevisionRequest(flow.Revision), ct);
        flow = await GetAsync<FlowDefinitionDto>(pair.NetRatel.Administrator, flowBase + $"/{flowId:D}", ct);
        if (!flow.Enabled) flow = await SendAsync<FlowDefinitionDto>(pair.NetRatel.Administrator, HttpMethod.Put,
            flowBase + $"/{flowId:D}/enabled", new FlowEnabledRequest(flow.Revision, true), ct);
        Require(flow.Enabled && flow.PublishedVersionId == version.Id && !string.IsNullOrEmpty(version.PublishedBy),
            "The actual owner did not publish and enable an immutable bounded Flow.");
        var monitoring = $"/api/v2/tenants/{Tenant}/monitoring";
        var current = await GetAsync<MonitoringConfigurationDto>(pair.NetRatel.Administrator, monitoring + "/configuration", ct);
        var ruleId = Guid.NewGuid();
        var targets = new MonitoringTargetSelectionDto(MonitoringTargetMode.Selected, [agentId], []);
        var condition = new MonitoringConditionDto(MonitoringMetricKind.DiskFreeSpace, MonitoringNumericUnit.Bytes,
            thresholds.BreachBytes, thresholds.RecoveryBytes, scope, null, []);
        var preview = await SendAsync<MonitoringTargetPreviewDto>(pair.NetRatel.Administrator, HttpMethod.Post,
            monitoring + "/targets/preview", new MonitoringTargetPreviewRequest(targets, condition), ct);
        Require(preview.ConfigurationRevision == current.Revision && preview.AgentIds.SequenceEqual([agentId]) &&
            preview.Details.Single().Support == MonitoringTargetSupport.Supported, "The ordinary target preview did not authorize the genuine enrolled disk.");
        var rule = new MonitoringRuleDto(Tenant, ruleId, 1, 1, "Physical disk free space", true, MonitoringSeverity.Warning,
            targets, condition, TimeSpan.FromSeconds(thresholds.HoldSeconds), TimeSpan.FromSeconds(thresholds.RecoveryHoldSeconds),
            TimeSpan.FromSeconds(thresholds.FreshnessSeconds), version.Id);
        current = await SendAsync<MonitoringConfigurationDto>(pair.NetRatel.Administrator, HttpMethod.Put,
            monitoring + $"/rules/{ruleId:D}", new MonitoringRuleWriteDto(rule, current.Revision, "Configure owned physical disk proof"), ct);
        rule = current.Rules.Single(x => x.RuleId == ruleId);
        Require(!string.IsNullOrEmpty(rule.ExecutionPrincipalId) && rule.ExecutionCredentialId is null && rule.PublishedFlowVersionId == version.Id,
            "The real rule mutation did not attribute its actual owner authority.");
        selectedRule = new(ruleId, version.Id, version.ConfigurationHash, MonitoringSeriesEvaluator.DiskResourceKey(scope),
            checked((long)current.Revision), connector, saved.Revision);
        return selectedRule;
    }

    public async Task ValidateAndDryRunWithoutEffectsAsync(PhysicalRule rule, CancellationToken ct)
    {
        var before = await ReadScopedDurableProofAsync(rule, ct);
        var sample = await ReadActualAcceptedSelectedDiskAsync(agent, ct);
        var preview = await SendAsync<FlowDryRunResultDto>(pair.NetRatel.Administrator, HttpMethod.Post,
            $"/api/v1/tenants/{Tenant}/flows/dry-run", new FlowDryRunRequest(selectedGraph!,
                new(agent, rule.RuleId, "Physical disk free space", "Disposable genuine Client", sample.Scope,
                    "DiskFreeSpace", "Warning", sample.FreeBytes, null, sample.CollectedAtUtc)), ct);
        Require(preview.Valid && !preview.Skipped && preview.Incident is not null, "The actual dry-run did not render the bounded incident mapping.");
        var after = await ReadScopedDurableProofAsync(rule, ct);
        Assert.Equal(before.Occurrences, after.Occurrences); Assert.Equal(before.Actions, after.Actions);
        Assert.Equal(before.Incidents, after.Incidents); Assert.Equal(before.Receipts, after.Receipts);
        Require(before.Actions.Length == 0 && after.Actions.Length == 0, "Connection test, validation, preview or healthy configuration created effects.");
    }

    private async Task<RatelDeskReadinessObservation> ReadReadinessAsync(CancellationToken ct)
    {
        await using var scope = pair.NetRatel.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var row = await db.Set<RatelDeskConnectorRecord>().AsNoTracking().SingleAsync(x => x.TenantId == Tenant && x.Id == connector, ct);
        return Parse<RatelDeskReadinessObservation>(row.ReadinessJson);
    }

    public async Task<IPhysicalCommittedResponseLoss> ArmOneActualReceiver201AfterCommitLossAsync(PhysicalRule rule, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var probe = new PhysicalIncidentDurableReadProbe(pair.NetRatel.Services, pair.RatelDesk, Tenant, rule.RuleId, agent, rule.ResourceKey, rule.FlowVersionId);
        responseLoss = new(pair.RatelDesk.Proxy.LoseOneCommittedIncidentResponse(pair.NetRatel.SourceInstanceId, sourceNamespace,
            probe.ReadCommittedAsync));
        return responseLoss;
    }

    public async Task<PhysicalAllocation> AllocateOwnedVolumeAsync(long count, CancellationToken ct)
    {
        Require(count == 256L * 1024 * 1024, "The physical stimulus differs from the bounded reviewed allocation.");
        var allocation = Allocation(await disk.AllocateAsync(ct), false);
        if (++allocationCount == 2) secondAllocation = allocation;
        Require(allocationCount <= 2, "The physical fixture attempted an extra allocation cycle.");
        return allocation;
    }
    public async Task<PhysicalAllocation> DeleteOnlyOwnedAllocationAsync(CancellationToken ct) => Allocation(await disk.RecoverAsync(ct), true);
    private static PhysicalAllocation Allocation(JsonElement value, bool recovered)
    {
        var ticks = value.GetProperty("observedAtUtcUnixNanoseconds").GetInt64() / 100;
        return new(DateTimeOffset.UnixEpoch.AddTicks(ticks), recovered ? 0 : value.GetProperty("stimulusBytes").GetInt64(),
            recovered ? 0 : value.GetProperty("actualAllocatedBytes").GetInt64(), value.GetProperty("selectedAvailableBefore").GetInt64(),
            value.GetProperty("selectedAvailableAfter").GetInt64(), value.GetProperty("hostAvailableAfter").GetInt64());
    }

    public async Task RestartRealApiWorkerReplicasWithPersistedStateAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var before = pair.NetRatel.PhysicalProcesses.Where(x => x.ExitedAtUtc is null).Select(x => x.ProcessId).ToArray();
        Require(before.Length == 2, "Two actual owned worker processes must exist before the crash.");
        await pair.NetRatel.RestartAsync(ct);
        var after = pair.NetRatel.PhysicalProcesses;
        Require(before.All(id => after.Single(x => x.ProcessId == id).ExitedAtUtc is not null) &&
            after.Count(x => x.ExitedAtUtc is null) == 2 && !after.Where(x => x.ExitedAtUtc is null).Any(x => before.Contains(x.ProcessId)),
            "The actual persisted-state worker processes did not exit and restart independently.");
        await pair.RatelDesk.RestartAsync();
        responseLoss!.Inner.MarkActualProcessesRestarted();
    }

    private sealed class PhysicalLoss(CommittedIncidentResponseLoss inner) : IPhysicalCommittedResponseLoss
    {
        public CommittedIncidentResponseLoss Inner => inner;
        public async Task<PhysicalCommittedLoss> WaitIndependentCommittedReceiptAsync(CancellationToken ct)
        { var value = await inner.Committed.WaitAsync(ct); return new(201, true, true, value.NamespaceId, value.Key, value.Fingerprint, 1, 1, 1); }
        public async Task ObserveRealSameKeyRecoveryAfterRestartAsync(CancellationToken ct) => _ = await inner.PostRestartRecovery.WaitAsync(ct);
        public void ReleaseIdenticalRetryGate() => inner.Release();
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private async Task<T> GetAsync<T>(HttpClient owner, string path, CancellationToken ct) => await SendAsync<T>(owner, HttpMethod.Get, path, null, ct);
    private async Task<T> SendAsync<T>(HttpClient owner, HttpMethod method, string path, object? value, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path);
        if (method != HttpMethod.Get) request.Headers.Add("X-NetRatel-Account-Request", "1");
        if (value is not null) request.Content = JsonContent.Create(value, value.GetType(), options: json);
        using var response = await owner.SendAsync(request, ct);
        Require(response.IsSuccessStatusCode, $"The actual owner operation {method} {path} returned HTTP {(int)response.StatusCode}.");
        return await response.Content.ReadFromJsonAsync<T>(json, ct) ?? throw new InvalidOperationException("The actual owner operation returned no bounded typed response.");
    }
    private T Parse<T>(string? raw) => raw is not null && Encoding.UTF8.GetByteCount(raw) <= 131_072
        ? JsonSerializer.Deserialize<T>(raw, json) ?? throw new InvalidOperationException("The durable physical DTO was absent.")
        : throw new InvalidOperationException("The durable physical DTO was absent or oversized.");
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static string FindRepository()
    {
        for (var root = new DirectoryInfo(AppContext.BaseDirectory); root is not null; root = root.Parent)
            if (File.Exists(Path.Combine(root.FullName, "NetRatel.sln"))) return root.FullName;
        throw new InvalidOperationException("The physical fixture must execute within its actual committed source checkout.");
    }
}
