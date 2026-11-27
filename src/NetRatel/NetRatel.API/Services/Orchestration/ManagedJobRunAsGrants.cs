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
    internal const string ApprovalRequired = "Approve the paired job's saved execution accounts in NetRatel.";

    internal async Task<ServicePrincipalRegistration> RequireAdministratorAsync(Guid mappingId, ClaimsPrincipal actor, CancellationToken ct)
    {
        await authority.RequireAdministratorAsync(actor, ct);
        var link = mappingId.ToString("D");
        var connection = await db.Set<PairingConnectionRecord>().AsNoTracking()
            .SingleOrDefaultAsync(row => row.Id == mappingId && row.Active && row.DeletedAtUtc == null, ct);
        var principal = connection?.InboundPrincipalId is { } principalId
            ? await db.Set<ServicePrincipalRegistration>().AsNoTracking().SingleOrDefaultAsync(row => row.Id == principalId && row.LinkId == link, ct)
            : null;
        if (principal is null || !await authority.CanManageAsync(PairingAuthority.ActorId(actor), principal.TenantId, false, true, ct) ||
            !await access.AuthorizeAsync(actor, NetRatelPermissions.TenantAdministration, principal.TenantId, ct) ||
            !await access.AuthorizeAsync(actor, NetRatelPermissions.RunAs, principal.TenantId, ct) ||
            !await access.AuthorizeAsync(actor, NetRatelPermissions.SecretUse, principal.TenantId, ct))
            throw new PairingException(403, "run_as_approval_denied", "Current tenant administration, integration, jobs, Run as and secret-use permissions are required.");
        var credential = await db.Set<ServicePrincipalSecret>().AsNoTracking().SingleOrDefaultAsync(row =>
            row.ServicePrincipalId == principal.Id && row.CredentialRevision == principal.CurrentCredentialRevision, ct);
        if (credential is null || !await registry.CanIssueScopesAsync(new(principal, credential), [OrchestrationManagedAuthorization.InvokeScope], ct))
            throw new PairingException(409, "connection_authority_unavailable", "The selected pairing cannot currently invoke jobs.");
        return principal;
    }

    internal async Task<PairedJobRunAsReview> ReviewAsync(ServicePrincipalRegistration principal, JobResolvedTargetPlan plan, CancellationToken ct)
    {
        var binding = FromPrincipal(principal);
        var mappingId = Guid.Parse(binding.LinkId!);
        var digest = ScopeDigest(binding, plan);
        var row = await ReadAsync(mappingId, plan.JobId, ct);
        var targets = Candidates(plan).Select(node => new PairedJobRunAsTarget(node.AgentId, node.DisplayName ?? node.Hostname,
            node.ExecutionAccount?.ProfileId, node.ExecutionAccount?.Mode, node.ExecutionAccount?.MappingRevision)).ToArray();
        var saved = targets.Any(node => node.ProfileId is not null);
        var approved = row is { RevokedAtUtc: null } && row.TenantId == plan.TenantId && row.ScopeDigest == digest;
        PairedJobExecutionReadiness readiness = !saved ? new(true, "agent_account", "The job uses the agent service account.") :
            approved ? new(true, "run_as_approved", "Saved execution accounts are approved for this pairing and job.") :
            new(false, "run_as_denied", ApprovalRequired);
        return new(readiness, principal.LinkRevision, targets, saved ? digest : null,
            row is { RevokedAtUtc: null } && row.TenantId == plan.TenantId ? row.ApprovalId : null);
    }

    internal async Task ApproveAsync(ServicePrincipalRegistration principal, JobResolvedTargetPlan plan,
        string expectedDigest, ClaimsPrincipal actor, CancellationToken ct)
    {
        var binding = FromPrincipal(principal);
        var mappingId = Guid.Parse(binding.LinkId!);
        var digest = ScopeDigest(binding, plan);
        if (expectedDigest != digest || Candidates(plan).All(node => node.ExecutionAccount is null))
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
            throw Denied();
        var row = await ReadAsync(mappingId, plan.JobId, ct);
        if (row is null || row.RevokedAtUtc is not null || row.TenantId != plan.TenantId ||
            (binding.RunAsGrantId is { } admitted && admitted != row.ApprovalId) || row.ScopeDigest != ScopeDigest(binding, plan))
            throw Denied();
        return row.ApprovalId;
    }

    internal static JobTargetValidationException Denied() => new("run_as_denied", ApprovalRequired, 403);

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
