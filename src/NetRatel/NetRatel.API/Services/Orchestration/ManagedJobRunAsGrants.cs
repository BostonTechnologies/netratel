using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Jobs;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.ServiceIdentity;
using NetRatel.Infrastructure.SystemPairing;
using NetRatel.Shared.Contracts.Jobs;
using NetRatel.Shared.SystemPairing;

namespace NetRatel.API.Services.Orchestration;

/// <summary>Approves a pairing's exact job and effective accounts without assigning secret-use rights to the peer.</summary>
public sealed class ManagedJobRunAsGrants(OrchestratorDbContext db, PairingAuthority authority, IEffectiveAccessService access,
    IServicePrincipalRegistry registry, TimeProvider clock)
{
    private const string ReviewDestination = "In NetRatel, open System connections → Paired Run as for this connection and job.";
    internal const string OperatorDenied = "Ask an operator with tenant administration, integration management, job management, Run as and secret-use permissions to review this scope.";
    internal const string ConnectionUnavailable = "Review the saved connection, its retained tenant authority and its credentials in NetRatel System connections.";

    internal async Task<ServicePrincipalRegistration> RequireAdministratorAsync(Guid mappingId, ClaimsPrincipal actor, CancellationToken ct)
    {
        try { await authority.RequireAdministratorAsync(actor, ct); }
        catch (PairingException error) when (error.StatusCode == 403)
        {
            throw new PairingException(403, PairedRunAsReasons.OperatorDenied, OperatorDenied);
        }
        var link = mappingId.ToString("D");
        var connection = await db.Set<PairingConnectionRecord>().AsNoTracking()
            .SingleOrDefaultAsync(row => row.Id == mappingId && row.Active && row.DeletedAtUtc == null, ct);
        var principal = connection?.InboundPrincipalId is { } principalId
            ? await db.Set<ServicePrincipalRegistration>().AsNoTracking().SingleOrDefaultAsync(row => row.Id == principalId && row.LinkId == link, ct)
            : null;
        if (principal is null || !await authority.CanManageAsync(actor, principal.TenantId, false, true, ct) ||
            !await access.AuthorizeAsync(actor, NetRatelPermissions.TenantAdministration, principal.TenantId, ct) ||
            !await access.AuthorizeAsync(actor, NetRatelPermissions.RunAs, principal.TenantId, ct) ||
            !await access.AuthorizeAsync(actor, NetRatelPermissions.SecretUse, principal.TenantId, ct))
            throw new PairingException(403, PairedRunAsReasons.OperatorDenied, OperatorDenied);
        if (!await CanInvokeAsync(principal, ct))
            throw new PairingException(409, PairedRunAsReasons.ConnectionUnavailable, ConnectionUnavailable);
        return principal;
    }

    internal async Task<bool> CanInvokeAsync(ServicePrincipalRegistration principal, CancellationToken ct)
    {
        var credential = await db.Set<ServicePrincipalSecret>().AsNoTracking().SingleOrDefaultAsync(row =>
            row.ServicePrincipalId == principal.Id && row.CredentialRevision == principal.CurrentCredentialRevision, ct);
        // The registry evaluates the pairing and mapping owners independently of the current reviewer.
        return credential is not null &&
            await registry.CanIssueScopesAsync(new(principal, credential), [OrchestrationManagedAuthorization.InvokeScope], ct);
    }

    internal async Task<PairedJobExecutionReadiness> ReadinessAsync(ServicePrincipalRegistration principal,
        JobResolvedTargetPlan plan, CancellationToken ct)
    {
        if (!UsesSavedAccount(plan)) return AgentAccountReadiness;
        var row = await ReadAsync(Guid.Parse(principal.LinkId!), plan.JobId, ct);
        return Readiness(MissingApprovalReason(row, FromPrincipal(principal), plan));
    }

    internal static PairedJobExecutionReadiness UnavailableReadiness =>
        new(false, PairedRunAsReasons.ConnectionUnavailable, ConnectionUnavailable) { Reason = PairedRunAsReasons.ConnectionUnavailable };

    internal async Task<PairedJobRunAsReview> ReviewAsync(ServicePrincipalRegistration principal, JobResolvedTargetPlan plan, CancellationToken ct)
    {
        var binding = FromPrincipal(principal);
        var mappingId = Guid.Parse(binding.LinkId!);
        var digest = ScopeDigest(binding, plan);
        var row = await ReadAsync(mappingId, plan.JobId, ct);
        var profileIds = Candidates(plan).Where(node => node.ExecutionAccount is not null)
            .Select(node => node.ExecutionAccount!.ProfileId).Distinct().ToArray();
        var labels = await db.CredentialProfiles.AsNoTracking()
            .Where(profile => profile.TenantId == plan.TenantId && profileIds.Contains(profile.Id))
            .ToDictionaryAsync(profile => profile.Id, profile => profile.DisplayName, ct);
        var targets = Candidates(plan).Select(node => new PairedJobRunAsTarget(node.AgentId, node.DisplayName ?? node.Hostname,
            node.ExecutionAccount?.ProfileId, node.ExecutionAccount?.Mode, node.ExecutionAccount?.MappingRevision)
        {
            ProfileName = node.ExecutionAccount is { } account ? labels.GetValueOrDefault(account.ProfileId) : null
        }).ToArray();
        var saved = UsesSavedAccount(plan);
        var readiness = saved ? Readiness(MissingApprovalReason(row, binding, plan)) : AgentAccountReadiness;
        return new(readiness, principal.LinkRevision, targets, saved ? digest : null,
            row is { RevokedAtUtc: null } && row.TenantId == plan.TenantId ? row.ApprovalId : null);
    }

    internal async Task ApproveAsync(ServicePrincipalRegistration principal, JobResolvedTargetPlan plan,
        string expectedDigest, ClaimsPrincipal actor, CancellationToken ct)
    {
        var binding = FromPrincipal(principal);
        var mappingId = Guid.Parse(binding.LinkId!);
        var digest = ScopeDigest(binding, plan);
        if (expectedDigest != digest || !UsesSavedAccount(plan))
            throw new PairingException(409, "run_as_review_changed", "Review the current targets and saved execution accounts again before approval.");

        var row = await db.Set<ManagedJobRunAsGrant>().SingleOrDefaultAsync(x => x.MappingId == mappingId &&
            x.JobId == checked((long)plan.JobId), ct);
        if (row is null)
        {
            row = new() { MappingId = mappingId, JobId = checked((long)plan.JobId) };
            db.Add(row);
        }
        if (row.TenantId == plan.TenantId && row.ScopeDigest == digest && row.RevokedAtUtc is null) return;
        row.TenantId = plan.TenantId;
        row.ApprovalId = Guid.NewGuid();
        row.ScopeDigest = digest;
        row.ApprovedBy = PairingAuthority.ActorId(actor);
        row.ApprovedAtUtc = clock.GetUtcNow();
        row.RevokedAtUtc = null;
        await db.SaveChangesAsync(ct);
    }

    internal Task<int> RevokeAsync(Guid mappingId, ulong jobId, int tenantId, Guid approvalId, CancellationToken ct) =>
        db.Set<ManagedJobRunAsGrant>().Where(row => row.MappingId == mappingId && row.JobId == checked((long)jobId) &&
            row.TenantId == tenantId && row.ApprovalId == approvalId && row.RevokedAtUtc == null)
            .ExecuteUpdateAsync(update => update.SetProperty(row => row.RevokedAtUtc, clock.GetUtcNow()), ct);

    internal async Task<Guid> RequireAsync(ManagedOrchestrationRequestBinding binding, JobResolvedTargetPlan plan, CancellationToken ct)
    {
        if (!Guid.TryParseExact(binding.LinkId, "D", out var mappingId) || binding.TenantId != plan.TenantId ||
            binding.JobDefinitionId != plan.JobId.ToString(CultureInfo.InvariantCulture))
            throw Denied(PairedRunAsReasons.ConnectionUnavailable);
        var row = await ReadAsync(mappingId, plan.JobId, ct);
        if (MissingApprovalReason(row, binding, plan) is { } reason)
            throw Denied(reason);
        return row!.ApprovalId;
    }

    private static string? MissingApprovalReason(ManagedJobRunAsGrant? row, ManagedOrchestrationRequestBinding binding,
        JobResolvedTargetPlan plan)
    {
        if (row is null || row.TenantId != plan.TenantId) return PairedRunAsReasons.ApprovalRequired;
        if (row.RevokedAtUtc is not null) return PairedRunAsReasons.ApprovalRevoked;
        return (binding.RunAsGrantId is { } admitted && admitted != row.ApprovalId) || row.ScopeDigest != ScopeDigest(binding, plan)
            ? PairedRunAsReasons.ScopeChanged : null;
    }

    private static bool UsesSavedAccount(JobResolvedTargetPlan plan) => Candidates(plan).Any(node => node.ExecutionAccount is not null);

    private static PairedJobExecutionReadiness AgentAccountReadiness => new(true, "agent_account", "The job uses the agent service account.");

    private static PairedJobExecutionReadiness Readiness(string? reason) => reason is null
        ? new(true, "run_as_approved", "Saved execution accounts are approved for this pairing and job.")
        : new(false, "run_as_denied", Describe(reason)) { Reason = reason };

    internal static string Describe(string? reason) => reason switch
    {
        PairedRunAsReasons.ApprovalRevoked => $"The saved execution-account approval was revoked. {ReviewDestination}",
        PairedRunAsReasons.ScopeChanged => $"The approved scope changed. Review and approve the current scope. {ReviewDestination}",
        PairedRunAsReasons.ConnectionUnavailable => ConnectionUnavailable,
        _ => $"The saved execution accounts need approval. {ReviewDestination}"
    };

    internal static JobTargetValidationException Denied(string reason = PairedRunAsReasons.ScopeChanged) =>
        new("run_as_denied", Describe(reason), 403) { Reason = reason };

    internal static IReadOnlyList<JobTargetNode> Candidates(JobResolvedTargetPlan plan) => plan.HaGroupSelection?.Candidates ?? plan.Nodes;

    private Task<ManagedJobRunAsGrant?> ReadAsync(Guid mappingId, ulong jobId, CancellationToken ct)
    {
        var id = checked((long)jobId);
        var rows = db.Set<ManagedJobRunAsGrant>();
        // Hold an existing approval against concurrent update/revocation through the admission commit.
        return (db.Database.IsNpgsql() && db.Database.CurrentTransaction is not null
                ? rows.FromSqlInterpolated($"SELECT * FROM \"ManagedJobRunAsGrants\" WHERE \"MappingId\" = {mappingId} AND \"JobId\" = {id} FOR SHARE")
                : rows.Where(row => row.MappingId == mappingId && row.JobId == id))
            .AsNoTracking().SingleOrDefaultAsync(ct);
    }

    private static ManagedOrchestrationRequestBinding FromPrincipal(ServicePrincipalRegistration principal) => new()
    {
        TenantId = principal.TenantId, ServicePrincipalId = principal.Id, LinkId = principal.LinkId,
        LinkRevision = principal.LinkRevision, GrantHash = principal.GrantHash, PeerInstanceId = principal.PeerInstanceId,
        PeerTenantId = principal.PeerTenantId
    };

    private static string ScopeDigest(ManagedOrchestrationRequestBinding binding, JobResolvedTargetPlan plan) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            Version = 1, binding.ServicePrincipalId, binding.LinkId, binding.LinkRevision, binding.GrantHash,
            binding.PeerInstanceId, binding.PeerTenantId, plan.TenantId, plan.JobId, plan.DefinitionDigest,
            plan.Target, plan.GroupRevision, plan.ExecutionPolicy,
            Targets = Candidates(plan).OrderBy(node => node.AgentId).Select(node => new { node.AgentId, node.ExecutionAccount })
        })));
}
