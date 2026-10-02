using NetRatel.API.Gateway;
using NetRatel.Application.Monitoring;
using NetRatel.Application.Presence;
using NetRatel.Application.Services;
using NetRatel.Shared.Contracts.Monitoring;
using NetRatel.Shared.Contracts.Services;

namespace NetRatel.API.Services.Monitoring;

/// <summary>Only enabled, tenant-owned exact service conditions select watches.</summary>
public sealed class MonitoringServiceWatchPolicySource(IMonitoringConfigurationStore configurations,
    IMonitoringClientDirectory directory, IClientServicesRouter services, TimeProvider timeProvider) : IClientServiceWatchPolicySource
{
    private readonly SemaphoreSlim[] _stripes = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();

    public async Task<ClientServiceWatchPolicy> GetPolicyAsync(ClientKey client, CancellationToken cancellationToken)
    {
        var gate = _stripes[(client.GetHashCode() & int.MaxValue) % _stripes.Length];
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var configuration = await configurations.GetAsync(client.TenantId, cancellationToken).ConfigureAwait(false);
            if (configuration.TenantId != client.TenantId) throw new InvalidOperationException("Monitoring configuration returned a different tenant.");
            var eligible = await directory.IsEligibleAsync(client, cancellationToken).ConfigureAwait(false);
            var names = SelectNames(configuration, client.AgentId, eligible);
            var current = await services.GetSnapshotAsync(client, cancellationToken).ConfigureAwait(false);
            if (current.Client != client) throw new InvalidOperationException("Services projection returned a different client.");
            var unchanged = current.MonitoredServiceNames.SequenceEqual(names, StringComparer.Ordinal);
            var revision = unchanged ? Math.Max(1UL, current.WatchPolicyRevision) : checked(current.WatchPolicyRevision + 1);
            var policy = new ClientServiceWatchPolicy(client, new(revision, names,
                ClientServicesLimits.DefaultWatchIntervalSeconds, ClientServicesLimits.DefaultInventoryIntervalSeconds,
                timeProvider.GetUtcNow().Add(ClientServicesLimits.MaximumPolicyLifetime)));
            if (!ClientServicesCoordinator.IsValidPolicy(policy.Policy, timeProvider.GetUtcNow()))
                throw new ArgumentException("service_watch_capacity_exceeded");
            cancellationToken.ThrowIfCancellationRequested();
            // Persist the selected revision inside the same bounded source gate,
            // so concurrent stream admission and renewal cannot assign one
            // revision to two different selections.
            await services.UpdateWatchPolicyAsync(policy, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return policy;
        }
        finally { gate.Release(); }
    }

    public static string[] SelectNames(MonitoringConfigurationSnapshot configuration, Guid agentId, bool eligible)
    {
        if (!eligible) return [];
        var windows = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var linux = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rule in configuration.Rules)
        {
            if (rule.TenantId != configuration.TenantId) throw new InvalidOperationException("Monitoring rule returned a different tenant.");
            if (!rule.Enabled || rule.Condition.Kind != MonitoringMetricKind.ServiceExpectedState || !TargetsClient(rule, configuration, agentId)) continue;
            if (rule.Condition.ServicePlatform == ClientServicePlatform.Windows) windows.Add(rule.Condition.ResourceName!.ToUpperInvariant());
            else if (rule.Condition.ServicePlatform == ClientServicePlatform.LinuxSystemd) linux.Add(rule.Condition.ResourceName!);
        }
        var names = windows.Concat(linux).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (names.Length > ClientServicesLimits.MaximumWatchServices) throw new ArgumentException("service_watch_capacity_exceeded");
        return names;
    }

    private static bool TargetsClient(MonitoringRuleDto rule, MonitoringConfigurationSnapshot configuration, Guid agentId) =>
        rule.Targets.Mode == MonitoringTargetMode.AllEligible || rule.Targets.AgentIds.Contains(agentId) ||
        rule.Targets.GroupIds.Any(groupId => configuration.Groups.Any(group =>
            group.GroupId == groupId && group.TenantId == configuration.TenantId && group.AgentIds.Contains(agentId)));
}

/// <summary>Renews selected-client policies independently of viewers, through the existing one outbound writer.</summary>
public sealed class MonitoringWatchPolicyReconciler(IClientServiceWatchPolicySource source,
    IClientServicesRouter services, IMonitoringClientDirectory directory,
    IAgentTelemetryGatewaySessionRegistry sessions, TimeProvider timeProvider,
    ILogger<MonitoringWatchPolicyReconciler> logger) : BackgroundService
{
    private readonly SemaphoreSlim _batchGate = new(1, 1);
    private ClientKey? _afterClient;

    public async Task<bool> ReconcileTenantAsync(int tenantId, CancellationToken cancellationToken)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5), timeProvider);
        using var foreground = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            var clients = await directory.GetEligibleAgentsAsync(tenantId, foreground.Token).WaitAsync(foreground.Token).ConfigureAwait(false);
            var current = new List<AgentTelemetryGatewayServicesSession>(65);
            foreach (var agentId in clients)
            {
                foreground.Token.ThrowIfCancellationRequested();
                var client = new ClientKey(tenantId, agentId);
                var session = sessions.GetStatus(client);
                if (!session.Connected || !session.SupportsServices || session.RegistrationId is not Guid registrationId) continue;
                current.Add(new(client, registrationId));
                if (current.Count == 65) break;
            }
            var results = await ProcessAsync(current.Take(64), TimeSpan.FromSeconds(2), foreground.Token).ConfigureAwait(false);
            return current.Count <= 64 && results;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return false; }
    }

    private async Task<bool> ReconcileClientAsync(ClientKey client, Guid registrationId, CancellationToken cancellationToken)
    {
        var evidence = await directory.GetCurrentEvidenceAsync(client, cancellationToken).ConfigureAwait(false);
        if (evidence?.EvidenceStreamId != registrationId) return false;
        var policy = await source.GetPolicyAsync(client, cancellationToken).ConfigureAwait(false);
        await services.UpdateWatchPolicyAsync(policy, cancellationToken).ConfigureAwait(false);
        if ((await directory.GetCurrentEvidenceAsync(client, cancellationToken).ConfigureAwait(false))?.EvidenceStreamId != registrationId) return false;
        cancellationToken.ThrowIfCancellationRequested();
        return sessions.TryPublishServicesPolicy(client, policy.Policy, registrationId);
    }

    public async Task<bool> ReconcileNextBatchAsync(CancellationToken cancellationToken)
    {
        await _batchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var page = sessions.GetServicesSessions(128, _afterClient);
            if (page.Items.Count == 0 && _afterClient is not null) page = sessions.GetServicesSessions(128);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15), timeProvider);
            using var batch = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
            var gate = new object();
            ClientKey? attempted = null;
            try
            {
                return await ProcessAsync(page.Items, TimeSpan.FromSeconds(1), batch.Token, client =>
                {
                    lock (gate)
                        if (attempted is null || CompareClients(client, attempted.Value) > 0) attempted = client;
                }).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return false; }
            finally
            {
                if (attempted is { } last)
                    _afterClient = page.NextCursor is null && last == page.Items[^1].Client ? null : last;
                else if (page.Items.Count == 0) _afterClient = null;
            }
        }
        finally { _batchGate.Release(); }
    }

    private static int CompareClients(ClientKey left, ClientKey right) =>
        left.TenantId != right.TenantId ? left.TenantId.CompareTo(right.TenantId) : left.AgentId.CompareTo(right.AgentId);

    private async Task<bool> ProcessAsync(IEnumerable<AgentTelemetryGatewayServicesSession> clients, TimeSpan attemptTimeout,
        CancellationToken cancellationToken, Action<ClientKey>? started = null)
    {
        var failed = 0;
        await Parallel.ForEachAsync(clients, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = cancellationToken },
            async (session, token) =>
            {
                started?.Invoke(session.Client);
                using var deadline = new CancellationTokenSource(attemptTimeout, timeProvider);
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
                try
                {
                    if (!await ReconcileClientAsync(session.Client, session.RegistrationId, attempt.Token).WaitAsync(attempt.Token).ConfigureAwait(false)) Interlocked.Exchange(ref failed, 1);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception exception)
                {
                    Interlocked.Exchange(ref failed, 1);
                    logger.LogWarning(exception, "Monitoring watch policy reconciliation is pending for an authenticated client.");
                }
            }).ConfigureAwait(false);
        return failed == 0;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30), timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try { await ReconcileNextBatchAsync(stoppingToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "Monitoring watch policy renewal is pending.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
