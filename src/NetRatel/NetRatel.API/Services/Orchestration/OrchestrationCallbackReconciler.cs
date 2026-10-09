using System.Globalization;
using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Jobs;
using NetRatel.Application.Requests;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.SystemPairing;
using NetRatel.Shared.SystemPairing;

namespace NetRatel.API.Services.Orchestration;

/// <summary>Reconciles durable job authority with durable callbacks; every invocation has its own DI scope.</summary>
public sealed class OrchestrationCallbackReconciler(OrchestratorDbContext db, IRequestService requests,
    IJobRunService runs, IRequestEventBus changes, INetRatelExternalServiceCallbackClient callbacks,
    PairingBusinessProfileService profiles, TimeProvider clock, ILogger<OrchestrationCallbackReconciler> logger)
{
    public async Task ReconcileAsync(CancellationToken ct)
    {
        _ = await ReconcileBatchAsync(0, ct);
    }

    public async Task<int> ReconcileBatchAsync(int afterRequestId, CancellationToken ct)
    {
        var bindings = await db.Set<ManagedOrchestrationRequestBinding>().AsNoTracking()
            .Where(x => x.LinkId != null && x.CallbackUrl != null && x.RequestId > afterRequestId &&
                !db.Set<OrchestrationCallbackDelivery>().Any(delivery => delivery.RequestId == x.RequestId &&
                    delivery.DeliveredAtUtc != null && (delivery.Phase == "succeeded" || delivery.Phase == "failed")))
            .OrderBy(x => x.RequestId).Take(16).ToArrayAsync(ct);
        foreach (var binding in bindings)
        {
            ct.ThrowIfCancellationRequested();
            try { await ReconcileManagedAsync(binding, ct); }
            catch (DbUpdateConcurrencyException) { db.ChangeTracker.Clear(); }
            catch (PairingException) { db.ChangeTracker.Clear(); }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                db.ChangeTracker.Clear();
                logger.LogWarning("Callback reconciliation failed for local request {RequestId}: {FailureType}.", binding.RequestId, error.GetType().Name);
            }
        }
        return bindings.Length == 0 ? 0 : bindings[^1].RequestId;
    }

    private async Task ReconcileManagedAsync(ManagedOrchestrationRequestBinding binding, CancellationToken ct)
    {
        // Resolve before creating or leasing delivery. A revoked connection cannot acquire a peer token or send.
        var profile = await profiles.ResolveAsync(binding.TenantId, binding.LinkId!, "rateldesk.orchestration.callback", ct);
        if (profile.Revision != binding.LinkRevision || profile.AuthorityHash != binding.GrantHash ||
            profile.Peer.InstallationId != binding.PeerInstanceId || profile.Mapping.RatelDeskOrganizationId != binding.PeerTenantId ||
            profile.Peer.ApiOrigin + "/api/v1/orchestration/provider/callback" != binding.CallbackUrl) return;
        var request = await ManagedOrchestrationRecovery.RecoverAsync(db, requests, runs, binding, ct);
        if (request is null || !ulong.TryParse(binding.ExecutionId, NumberStyles.None, CultureInfo.InvariantCulture, out var runId)) return;
        var details = await runs.GetDetailsAsync(runId, ct);
        if (details is null || !OrchestrationCallbackProjection.Matches(request, details.Run, binding) ||
            !await OrchestrationCallbackProjection.TerminalReadyAsync(db, details, ct)) return;
        var callback = OrchestrationCallbackProjection.Build(request, details, binding);
        var requestStatus = OrchestrationCallbackProjection.RequestStatus(details.Run.Status);
        if (request.Status != requestStatus)
        {
            if (await OrchestrationCallbackProjection.ProjectRequestStatusAsync(db, requests, request.Id, details.Run.Status,
                callback.Message, callback.ResultJson, clock.GetUtcNow(), ct))
                changes.Publish(new RequestChangedEvent(request.Id, "external-service-job-status", clock.GetUtcNow()));
        }
        if (details.Run.Status == JobRunState.Pending) return;
        var now = clock.GetUtcNow();
        var delivery = await db.Set<OrchestrationCallbackDelivery>().SingleOrDefaultAsync(x => x.RequestId == request.Id && x.Phase == callback.Status, ct);
        if (delivery is null)
        {
            delivery = new OrchestrationCallbackDelivery { RequestId = request.Id, Phase = callback.Status, NextAttemptAtUtc = now };
            db.Set<OrchestrationCallbackDelivery>().Add(delivery);
            try { await db.SaveChangesAsync(ct); }
            catch (DbUpdateException) { db.ChangeTracker.Clear(); return; } // Another replica created the phase.
        }
        if (delivery.DeliveredAtUtc is not null || delivery.NextAttemptAtUtc > now || delivery.LeaseExpiresAtUtc is { } leaseExpiry && leaseExpiry > now) return;
        var leaseId = Guid.NewGuid();
        delivery.LeaseId = leaseId;
        delivery.LeaseExpiresAtUtc = now.AddMinutes(3);
        delivery.Attempts++;
        delivery.Revision++;
        await db.SaveChangesAsync(ct); // Optimistic CAS wins one current phase lease.
        var delivered = await callbacks.TrySendStatusAsync(callback, binding.CorrelationId, ct);
        // The stored phase and run remain the authority even if the process crashes before this ack.
        await db.Entry(delivery).ReloadAsync(ct);
        if (delivery.LeaseId != leaseId) return;
        delivery.LeaseId = null;
        delivery.LeaseExpiresAtUtc = null;
        delivery.NextAttemptAtUtc = clock.GetUtcNow().AddSeconds(15);
        if (delivered) delivery.DeliveredAtUtc = clock.GetUtcNow();
        delivery.Revision++;
        await db.SaveChangesAsync(ct);
    }
}

public sealed class OrchestrationCallbackWorker(IServiceScopeFactory scopes, TimeProvider clock,
    ILogger<OrchestrationCallbackWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var afterRequestId = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                afterRequestId = await scope.ServiceProvider.GetRequiredService<OrchestrationCallbackReconciler>()
                    .ReconcileBatchAsync(afterRequestId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error)
            {
                logger.LogWarning("Callback reconciliation tick failed: {FailureType}.", error.GetType().Name);
            }
            try { await Task.Delay(TimeSpan.FromSeconds(2), clock, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
