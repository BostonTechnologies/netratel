using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using NetRatel.API.Gateway;
using NetRatel.API.Models;
using NetRatel.Akka.Configuration;
using NetRatel.Infrastructure.Persistence;
using NuGet.Versioning;
using Npgsql;

namespace NetRatel.API.Services;

public sealed record ClientUpdateClaimResult(Guid AttemptId, Guid ReleaseId, string AdmissionNonce, string TargetVersion);
public sealed record ClientUpdateActivationResult(bool Accepted, string Reason, Guid? ConfirmationId = null);
public sealed record ClientUpdateResumeEligibility(bool IsEligible, string? FailureCode, long? PolicyRevision = null);
public sealed record ClientUpdateResumeResult(bool Resumed, string? FailureCode, long? PolicyRevision = null);

public interface IClientUpdatePublisher
{
    Task<ClientUpdateReleaseRecord> PublishArtifactAsync(ClientArtifactSummaryDto artifact, string manifestJson,
        string? publishedBy, CancellationToken cancellationToken);
    Task<bool> IsArtifactPublishedAsync(string runtimeId, string version, CancellationToken cancellationToken);
}

/// <summary>
/// The narrow operator-facing update authority. It can resume a target that
/// the update authority previously suspended; it cannot choose, upload, or
/// directly push a release.
/// </summary>
public interface IClientUpdateOperatorAuthority
{
    Task<ClientUpdateResumeEligibility> GetResumeEligibilityAsync(int tenantId, Guid agentId, CancellationToken cancellationToken);
    Task<ClientUpdateResumeResult> ResumeAutomaticUpdatesAsync(int tenantId, Guid agentId, string resumedBy, CancellationToken cancellationToken);
}

public sealed class ClientUpdateAuthorityService(
    OrchestratorDbContext db,
    IClientUpdateCatalog catalog,
    NetRatelAkkaMigrationOptions options,
    TimeProvider timeProvider,
    ILogger<ClientUpdateAuthorityService> logger) : IClientUpdatePublisher, IClientUpdateOperatorAuthority
{
    // A client restart abandons a download before it can persist a usable continuation token.
    // The same authenticated agent can therefore immediately reclaim Downloading; a fresh
    // Claimed attempt remains fenced briefly, and staged or activating work is never reclaimed.
    private static readonly TimeSpan StaleClaimRecoveryDelay = TimeSpan.FromMinutes(5);

    public async Task<ClientUpdateReleaseRecord> PublishArtifactAsync(
        ClientArtifactSummaryDto artifact,
        string manifestJson,
        string? publishedBy,
        CancellationToken cancellationToken)
    {
        if (!NuGetVersion.TryParse(artifact.Version, out var version) ||
            !string.Equals(artifact.Version, version.ToNormalizedString(), StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Artifact version '{artifact.Version}' must be normalized SemVer.");
        }

        var existing = await db.ClientUpdateReleases
            .SingleOrDefaultAsync(x => x.RuntimeId == artifact.Rid && x.Version == version.ToNormalizedString(), cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            if (string.Equals(existing.Sha256, artifact.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                return existing;
            }

            throw new ClientArtifactConflictException(artifact.Rid, artifact.Version);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var revision = await IncrementRevisionAsync(cancellationToken).ConfigureAwait(false);
        var release = new ClientUpdateReleaseRecord
        {
            PublicId = Guid.NewGuid(),
            Revision = revision,
            RuntimeId = artifact.Rid,
            Version = version.ToNormalizedString(),
            Channel = version.IsPrerelease ? "prerelease" : "stable",
            ArtifactKey = $"{artifact.Rid}/{artifact.Version}/{artifact.FileName}",
            Sha256 = artifact.Sha256,
            SizeBytes = artifact.Size,
            ManifestJson = manifestJson,
            Enabled = true,
            PublishedAtUtc = timeProvider.GetUtcNow(),
            PublishedBy = publishedBy
        };
        db.ClientUpdateReleases.Add(release);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await NotifyAsync(revision, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        await RefreshCatalogSafelyAsync(cancellationToken).ConfigureAwait(false);
        return release;
    }

    public async Task<ClientUpdateClaimResult?> ClaimAsync(
        AuthenticatedAgentIdentity identity,
        Guid releasePublicId,
        string runtimeId,
        string currentVersion,
        string channel,
        string admissionNonce,
        CancellationToken cancellationToken)
    {
        if (!options.IsClientUpdateAuthorityActive)
        {
            ClientUpdateTelemetry.ClaimObserved("ineligible");
            return null;
        }
        var release = await db.ClientUpdateReleases.SingleOrDefaultAsync(
            x => x.PublicId == releasePublicId && x.Enabled,
            cancellationToken).ConfigureAwait(false);
        var tenantPolicy = await db.Tenants.AsNoTracking()
            .Where(x => x.Id == identity.TenantId)
            .Select(x => new { x.AutoUpdate, x.AutoUpdateChannel, x.AutoUpdateTargetVersion })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        var agentEnabled = await db.Agents.AsNoTracking()
            .AnyAsync(x => x.Id == identity.AgentId && x.TenantId == identity.TenantId &&
                x.IsEnabled && x.Status == AgentStatus.Active && !x.RevokedAtUtc.HasValue,
                cancellationToken)
            .ConfigureAwait(false);
        var state = await db.AgentClientUpdateStates.FindAsync([identity.AgentId], cancellationToken).ConfigureAwait(false);
        var tenantEnabled = tenantPolicy?.AutoUpdate == true;
        var exactTenantTarget = string.Equals(tenantPolicy?.AutoUpdateTargetVersion, release?.Version, StringComparison.Ordinal);
        var tenantAllowsPrerelease = string.Equals(tenantPolicy?.AutoUpdateChannel, "prerelease", StringComparison.OrdinalIgnoreCase);
        if (release is null || !tenantEnabled || !agentEnabled || state?.SuspendedAtUtc.HasValue == true ||
            state?.SuppressedReleaseId == releasePublicId ||
            !string.Equals(release.RuntimeId, runtimeId, StringComparison.OrdinalIgnoreCase) ||
            release.Channel == "prerelease" &&
                !string.Equals(channel, "prerelease", StringComparison.OrdinalIgnoreCase) && !tenantAllowsPrerelease && !exactTenantTarget ||
            !string.IsNullOrWhiteSpace(tenantPolicy?.AutoUpdateTargetVersion) && !exactTenantTarget ||
            !NuGetVersion.TryParse(currentVersion, out var current) ||
            !NuGetVersion.TryParse(release.Version, out var target) || target <= current)
        {
            ClientUpdateTelemetry.ClaimObserved("ineligible");
            return null;
        }

        if (!IsAdmissionNonce(admissionNonce))
        {
            ClientUpdateTelemetry.ClaimObserved("rejected");
            return null;
        }
        var nonceHash = HashNonce(admissionNonce);
        var now = timeProvider.GetUtcNow();
        var attempt = await db.ClientUpdateAttempts.SingleOrDefaultAsync(
            x => x.AgentId == identity.AgentId && x.ReleaseId == release.Id,
            cancellationToken).ConfigureAwait(false);
        if (attempt is null)
        {
            attempt = new ClientUpdateAttemptRecord
            {
                PublicId = Guid.NewGuid(),
                ReleaseId = release.Id,
                TenantId = identity.TenantId,
                AgentId = identity.AgentId,
                FromVersion = current.ToNormalizedString(),
                TargetVersion = release.Version,
                RuntimeId = release.RuntimeId,
                State = ClientUpdateAttemptState.Claimed,
                AdmissionNonceHash = nonceHash,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };
            db.ClientUpdateAttempts.Add(attempt);
        }
        else if (attempt.State == ClientUpdateAttemptState.FailedPreActivation)
        {
            ReissueClaim(attempt, nonceHash, now);
        }
        else if (attempt.State == ClientUpdateAttemptState.Downloading)
        {
            logger.LogInformation(
                "Reissuing interrupted downloading client update claim {AttemptId} for agent {AgentId}",
                attempt.PublicId,
                identity.AgentId);
            ReissueClaim(attempt, nonceHash, now);
        }
        else if (IsStaleClaimedAttempt(attempt, now))
        {
            logger.LogWarning(
                "Recovering stale claimed client update claim {AttemptId} for agent {AgentId} after {Age}",
                attempt.PublicId,
                identity.AgentId,
                now - attempt.UpdatedAtUtc);
            ReissueClaim(attempt, nonceHash, now);
        }
        else if (attempt.State is not ClientUpdateAttemptState.Accepted and not ClientUpdateAttemptState.RolledBack and not ClientUpdateAttemptState.RollbackUnverified)
        {
            if (!string.Equals(attempt.AdmissionNonceHash, nonceHash, StringComparison.Ordinal))
            {
                ClientUpdateTelemetry.ClaimObserved("rejected");
                return null;
            }
        }
        else
        {
            ClientUpdateTelemetry.ClaimObserved("ineligible");
            return null;
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException exception) when (
            exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            db.ChangeTracker.Clear();
            attempt = await db.ClientUpdateAttempts.SingleAsync(
                x => x.AgentId == identity.AgentId && x.ReleaseId == release.Id,
                cancellationToken).ConfigureAwait(false);
            if (attempt.State is ClientUpdateAttemptState.Accepted or ClientUpdateAttemptState.RolledBack or ClientUpdateAttemptState.RollbackUnverified)
            {
                ClientUpdateTelemetry.ClaimObserved("ineligible");
                return null;
            }
            if (attempt.State == ClientUpdateAttemptState.FailedPreActivation)
            {
                ReissueClaim(attempt, nonceHash, now);
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            else if (attempt.State == ClientUpdateAttemptState.Downloading)
            {
                logger.LogInformation(
                    "Reissuing interrupted downloading client update claim {AttemptId} for agent {AgentId}",
                    attempt.PublicId,
                    identity.AgentId);
                ReissueClaim(attempt, nonceHash, now);
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            else if (IsStaleClaimedAttempt(attempt, now))
            {
                logger.LogWarning(
                    "Recovering stale claimed client update claim {AttemptId} for agent {AgentId} after {Age}",
                    attempt.PublicId,
                    identity.AgentId,
                    now - attempt.UpdatedAtUtc);
                ReissueClaim(attempt, nonceHash, now);
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            else if (!string.Equals(attempt.AdmissionNonceHash, nonceHash, StringComparison.Ordinal))
            {
                ClientUpdateTelemetry.ClaimObserved("rejected");
                return null;
            }
        }
        ClientUpdateTelemetry.ClaimObserved("eligible");
        return new ClientUpdateClaimResult(attempt.PublicId, release.PublicId, admissionNonce, release.Version);
    }

    public async Task<ClientUpdateActivationResult> MarkReadmittedAsync(
        AuthenticatedAgentIdentity identity,
        Guid attemptId,
        Guid releaseId,
        string nonce,
        string agentVersion,
        Guid connectionId,
        long connectionEpoch,
        CancellationToken cancellationToken)
    {
        var attempt = await db.ClientUpdateAttempts.AsNoTracking().Include(x => x.Release).SingleOrDefaultAsync(
            x => x.PublicId == attemptId,
            cancellationToken).ConfigureAwait(false);
        if (attempt is null) return new(false, "attempt_not_found");
        if (attempt.Release.PublicId != releaseId) return new(false, "release_mismatch");
        if (attempt.TenantId != identity.TenantId || attempt.AgentId != identity.AgentId) return new(false, "agent_mismatch");
        if (attempt.State is not ClientUpdateAttemptState.Activating and not ClientUpdateAttemptState.GatewayReadmitted)
            return new(false, "invalid_attempt_state");
        if (!IsAdmissionNonce(nonce) || !CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(attempt.AdmissionNonceHash),
                Convert.FromHexString(HashNonce(nonce)))) return new(false, "nonce_mismatch");
        if (!string.Equals(attempt.TargetVersion, RemoveBuildMetadata(agentVersion), StringComparison.OrdinalIgnoreCase))
            return new(false, "version_mismatch");

        var readmittedAtUtc = timeProvider.GetUtcNow();
        var affected = await db.ClientUpdateAttempts
            .Where(x => x.Id == attempt.Id &&
                (x.State == ClientUpdateAttemptState.Activating ||
                 x.State == ClientUpdateAttemptState.GatewayReadmitted))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.State, ClientUpdateAttemptState.GatewayReadmitted)
                .SetProperty(x => x.GatewayConnectionId, connectionId)
                .SetProperty(x => x.GatewayConnectionEpoch, connectionEpoch)
                .SetProperty(x => x.ReadmittedAtUtc, readmittedAtUtc)
                .SetProperty(x => x.UpdatedAtUtc, readmittedAtUtc), cancellationToken)
            .ConfigureAwait(false);
        return affected == 1
            ? new(true, "accepted")
            : new(false, "invalid_attempt_state");
    }

    private static string RemoveBuildMetadata(string version)
    {
        var separator = version.IndexOf('+');
        return separator >= 0 ? version[..separator] : version;
    }

    private static bool IsStaleClaimedAttempt(ClientUpdateAttemptRecord attempt, DateTimeOffset now) =>
        attempt.State == ClientUpdateAttemptState.Claimed &&
        now - attempt.UpdatedAtUtc >= StaleClaimRecoveryDelay;

    private static void ReissueClaim(ClientUpdateAttemptRecord attempt, string nonceHash, DateTimeOffset now)
    {
        attempt.State = ClientUpdateAttemptState.Claimed;
        attempt.AdmissionNonceHash = nonceHash;
        attempt.FailureCode = null;
        attempt.Message = null;
        attempt.UpdatedAtUtc = now;
    }

    public async Task<ClientUpdateActivationResult> ConfirmAsync(
        AuthenticatedAgentIdentity identity,
        Guid attemptId,
        Guid connectionId,
        long connectionEpoch,
        CancellationToken cancellationToken)
    {
        var attempt = await db.ClientUpdateAttempts.AsNoTracking().SingleOrDefaultAsync(
            x => x.PublicId == attemptId && x.TenantId == identity.TenantId && x.AgentId == identity.AgentId,
            cancellationToken).ConfigureAwait(false);
        if (attempt is null) return new(false, "attempt_not_found");
        if (attempt.State != ClientUpdateAttemptState.GatewayReadmitted) return new(false, "invalid_attempt_state");
        if (attempt.GatewayConnectionId != connectionId) return new(false, "presence_connection_mismatch");
        if (attempt.GatewayConnectionEpoch != connectionEpoch) return new(false, "presence_epoch_mismatch");

        var now = timeProvider.GetUtcNow();
        var confirmationId = Guid.NewGuid();
        var affected = await db.ClientUpdateAttempts
            .Where(x => x.Id == attempt.Id && x.State == ClientUpdateAttemptState.GatewayReadmitted &&
                x.GatewayConnectionId == connectionId && x.GatewayConnectionEpoch == connectionEpoch)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.State, ClientUpdateAttemptState.Accepted)
                .SetProperty(x => x.ConfirmationId, confirmationId)
                .SetProperty(x => x.ConfirmedAtUtc, now)
                .SetProperty(x => x.UpdatedAtUtc, now), cancellationToken)
            .ConfigureAwait(false);
        if (affected != 1) return new(false, "confirmation_write_failed");
        if (attempt.ReadmittedAtUtc.HasValue)
            ClientUpdateTelemetry.ActivationCompleted(now - attempt.ReadmittedAtUtc.Value, "accepted");
        ClientUpdateTelemetry.OutcomeObserved(ClientUpdateAttemptState.Accepted);
        return new(true, "accepted", confirmationId);
    }

    public async Task ReportAsync(
        AuthenticatedAgentIdentity identity,
        Guid attemptId,
        ClientUpdateAttemptState state,
        string? failureCode,
        string? message,
        CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var attempt = await db.ClientUpdateAttempts.FromSqlInterpolated(
                $"SELECT * FROM \"ClientUpdateAttempts\" WHERE \"PublicId\" = {attemptId} AND \"TenantId\" = {identity.TenantId} AND \"AgentId\" = {identity.AgentId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Update attempt was not found.");
        if (!IsAllowedTransition(attempt.State, state))
            throw new InvalidOperationException($"Update attempt transition {attempt.State} -> {state} is not allowed.");
        attempt.State = state;
        attempt.FailureCode = failureCode;
        attempt.Message = message;
        attempt.UpdatedAtUtc = timeProvider.GetUtcNow();

        if (state is ClientUpdateAttemptState.RolledBack or ClientUpdateAttemptState.RollbackUnverified)
        {
            var agentState = await db.AgentClientUpdateStates.FindAsync([identity.AgentId], cancellationToken).ConfigureAwait(false);
            if (agentState is null)
            {
                agentState = new AgentClientUpdateStateRecord { AgentId = identity.AgentId, TenantId = identity.TenantId };
                db.AgentClientUpdateStates.Add(agentState);
            }
            agentState.SuspendedAtUtc = timeProvider.GetUtcNow();
            agentState.SuspensionReason = failureCode ?? state.ToString();
            agentState.SuspensionAttemptId = attempt.PublicId;
            agentState.SuppressedReleaseId = await db.ClientUpdateReleases.AsNoTracking()
                .Where(x => x.Id == attempt.ReleaseId).Select(x => x.PublicId)
                .SingleAsync(cancellationToken).ConfigureAwait(false);
            agentState.PolicyRevision++;
            var revision = await IncrementRevisionAsync(cancellationToken).ConfigureAwait(false);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await NotifyAsync(revision, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        ClientUpdateTelemetry.OutcomeObserved(state);
        if (state is ClientUpdateAttemptState.RolledBack or ClientUpdateAttemptState.RollbackUnverified &&
            attempt.ReadmittedAtUtc.HasValue)
            ClientUpdateTelemetry.ActivationCompleted(timeProvider.GetUtcNow() - attempt.ReadmittedAtUtc.Value, "rollback");
        await RefreshCatalogSafelyAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task DisableReleaseAsync(Guid releaseId, string? disabledBy, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var release = await db.ClientUpdateReleases.SingleOrDefaultAsync(x => x.PublicId == releaseId, cancellationToken)
            .ConfigureAwait(false) ?? throw new KeyNotFoundException("Release was not found.");
        release.Enabled = false;
        release.DisabledAtUtc = timeProvider.GetUtcNow();
        release.DisabledBy = disabledBy;
        var revision = await IncrementRevisionAsync(cancellationToken).ConfigureAwait(false);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await NotifyAsync(revision, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        await RefreshCatalogSafelyAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ResumeAsync(int tenantId, Guid agentId, string? resumedBy, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var state = await db.AgentClientUpdateStates.SingleOrDefaultAsync(
            x => x.TenantId == tenantId && x.AgentId == agentId,
            cancellationToken).ConfigureAwait(false) ?? throw new KeyNotFoundException("Agent update state was not found.");
        state.SuspendedAtUtc = null;
        state.SuspensionReason = null;
        state.SuspensionAttemptId = null;
        state.ResumedAtUtc = timeProvider.GetUtcNow();
        state.ResumedBy = resumedBy;
        state.PolicyRevision++;
        var revision = await IncrementRevisionAsync(cancellationToken).ConfigureAwait(false);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await NotifyAsync(revision, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        await RefreshCatalogSafelyAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<ClientUpdateResumeEligibility> GetResumeEligibilityAsync(
        int tenantId,
        Guid agentId,
        CancellationToken cancellationToken)
    {
        var tenant = await db.Tenants.AsNoTracking()
            .Where(candidate => candidate.Id == tenantId)
            .Select(candidate => new { candidate.AutoUpdate })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (tenant is null) return new(false, "target_not_found");
        if (!tenant.AutoUpdate) return new(false, "client_auto_update_disabled");

        var agent = await db.Agents.AsNoTracking()
            .Where(candidate => candidate.Id == agentId && candidate.TenantId == tenantId)
            .Select(candidate => new { candidate.IsEnabled, candidate.Status, candidate.RevokedAtUtc })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (agent is null) return new(false, "target_not_found");
        if (!agent.IsEnabled || agent.Status != AgentStatus.Active || agent.RevokedAtUtc.HasValue)
            return new(false, "target_disabled");

        var state = await db.AgentClientUpdateStates.AsNoTracking()
            .Where(candidate => candidate.TenantId == tenantId && candidate.AgentId == agentId)
            .Select(candidate => new { candidate.SuspendedAtUtc, candidate.PolicyRevision })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return state is { SuspendedAtUtc: not null }
            ? new(true, null, state.PolicyRevision)
            : new(false, "client_update_not_suspended", state?.PolicyRevision);
    }

    public async Task<ClientUpdateResumeResult> ResumeAutomaticUpdatesAsync(
        int tenantId,
        Guid agentId,
        string resumedBy,
        CancellationToken cancellationToken)
    {
        var eligibility = await GetResumeEligibilityAsync(tenantId, agentId, cancellationToken).ConfigureAwait(false);
        if (!eligibility.IsEligible) return new(false, eligibility.FailureCode, eligibility.PolicyRevision);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var state = await db.AgentClientUpdateStates.SingleOrDefaultAsync(
            candidate => candidate.TenantId == tenantId && candidate.AgentId == agentId,
            cancellationToken).ConfigureAwait(false);
        if (state?.SuspendedAtUtc is null)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return new(false, "client_update_not_suspended", state?.PolicyRevision);
        }

        state.SuspendedAtUtc = null;
        state.SuspensionReason = null;
        state.SuspensionAttemptId = null;
        state.ResumedAtUtc = timeProvider.GetUtcNow();
        state.ResumedBy = resumedBy;
        state.PolicyRevision++;
        var revision = await IncrementRevisionAsync(cancellationToken).ConfigureAwait(false);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await NotifyAsync(revision, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        await RefreshCatalogSafelyAsync(cancellationToken).ConfigureAwait(false);
        return new(true, null, state.PolicyRevision);
    }

    public Task<bool> IsArtifactPublishedAsync(string runtimeId, string version, CancellationToken cancellationToken) =>
        db.ClientUpdateReleases.AnyAsync(x => x.RuntimeId == runtimeId && x.Version == version, cancellationToken);

    private async Task<long> IncrementRevisionAsync(CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO \"ClientUpdateCatalogRevision\" (\"Id\", \"Revision\", \"UpdatedAtUtc\") VALUES (1, 1, now()) " +
            "ON CONFLICT (\"Id\") DO UPDATE SET \"Revision\" = \"ClientUpdateCatalogRevision\".\"Revision\" + 1, \"UpdatedAtUtc\" = now()",
            cancellationToken).ConfigureAwait(false);
        return await db.ClientUpdateCatalogRevisions.AsNoTracking()
            .Where(x => x.Id == 1).Select(x => x.Revision).SingleAsync(cancellationToken).ConfigureAwait(false);
    }

    private Task NotifyAsync(long revision, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_notify('netratel_client_updates', {revision.ToString()})", cancellationToken);

    private async Task RefreshCatalogSafelyAsync(CancellationToken cancellationToken)
    {
        try { await catalog.RefreshAsync(cancellationToken).ConfigureAwait(false); }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Client update catalog refresh failed after a committed authority change.");
        }
    }

    private static string HashNonce(string nonce) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(nonce))).ToLowerInvariant();

    private static bool IsAdmissionNonce(string nonce)
    {
        if (nonce.Length != 64) return false;
        try { return Convert.FromHexString(nonce).Length == 32; }
        catch (FormatException) { return false; }
    }

    internal static bool IsAllowedTransition(ClientUpdateAttemptState current, ClientUpdateAttemptState next) =>
        (current, next) switch
        {
            (ClientUpdateAttemptState.Claimed, ClientUpdateAttemptState.Downloading or ClientUpdateAttemptState.FailedPreActivation) => true,
            (ClientUpdateAttemptState.Downloading, ClientUpdateAttemptState.Staged or ClientUpdateAttemptState.FailedPreActivation) => true,
            (ClientUpdateAttemptState.Staged, ClientUpdateAttemptState.Activating or ClientUpdateAttemptState.FailedPreActivation) => true,
            (ClientUpdateAttemptState.Activating or ClientUpdateAttemptState.GatewayReadmitted,
                ClientUpdateAttemptState.RolledBack or ClientUpdateAttemptState.RollbackUnverified) => true,
            _ when current == next => true,
            _ => false
        };
}
