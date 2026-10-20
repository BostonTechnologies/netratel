using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
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

[JsonConverter(typeof(JsonStringEnumConverter<ClientUpdateReportOutcome>))]
public enum ClientUpdateReportOutcome
{
    [JsonStringEnumMemberName("applied")] Applied,
    [JsonStringEnumMemberName("duplicate")] Duplicate,
    [JsonStringEnumMemberName("invalid_state")] InvalidState,
    [JsonStringEnumMemberName("not_found")] NotFound,
    [JsonStringEnumMemberName("conflict")] Conflict
}

public sealed record ClientUpdateReportResult(
    ClientUpdateReportOutcome Outcome, string Code, string? State = null,
    Guid? ConfirmationId = null, bool SuspensionActive = false, long? PolicyRevision = null);
public sealed record ClientPackPublishItem(ClientArtifactSummaryDto Artifact, string ManifestJson);

/// <summary>
/// The gateway's narrow persisted-update admission boundary. Implementations
/// must bind readmission and confirmation to the authenticated identity and
/// the exact presence connection that acknowledged the heartbeat.
/// </summary>
public interface IClientUpdateActivationAuthority
{
    Task<ClientUpdateActivationResult> MarkReadmittedAsync(
        AuthenticatedAgentIdentity identity,
        Guid attemptId,
        Guid releaseId,
        string nonce,
        string agentVersion,
        Guid connectionId,
        long connectionEpoch,
        CancellationToken cancellationToken);

    Task<ClientUpdateActivationResult> ConfirmAsync(
        AuthenticatedAgentIdentity identity,
        Guid attemptId,
        Guid connectionId,
        long connectionEpoch,
        CancellationToken cancellationToken);

    // A committed reconnect can change the observed running version even when
    // the client has no activation context, such as after a native rollback.
    Task NotifyPresenceObservedAsync(AuthenticatedAgentIdentity identity, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}

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
    TimeProvider timeProvider,
    ILogger<ClientUpdateAuthorityService> logger)
    : IClientUpdatePublisher, IClientUpdateOperatorAuthority, IClientUpdateActivationAuthority
{
    // A client restart abandons a download before it can persist a usable continuation token.
    // The same authenticated agent can therefore immediately reclaim Downloading; a fresh
    // Claimed attempt remains fenced briefly, and staged or activating work is never reclaimed.
    private static readonly TimeSpan StaleClaimRecoveryDelay = TimeSpan.FromMinutes(5);

    public async Task PublishImportedPackAsync(Guid operationId, IReadOnlyList<ClientPackPublishItem> items,
        string publishedBy, bool confirmPrerelease, CancellationToken cancellationToken,
        bool automatic = false)
    {
        if (items.Count == 0 || items.Select(x => x.Artifact.Rid).Distinct(StringComparer.Ordinal).Count() != items.Count)
            throw new InvalidOperationException("A complete client pack with unique runtimes is required.");
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await ClientReleaseDeletionService.LockAdmissionsAsync(db, cancellationToken).ConfigureAwait(false);
        // The revision row serializes concurrent publishers before reading operation state.
        var revision = await IncrementRevisionAsync(cancellationToken).ConfigureAwait(false);
        var operation = await db.ClientReleaseImportOperations.Include(x => x.Assets)
            .SingleOrDefaultAsync(x => x.Id == operationId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("Client release import was not found.");
        if (operation.State != ClientReleaseImportState.Imported || operation.Assets.Count != items.Count ||
            operation.Assets.Any(x => x.State != ClientReleaseImportAssetState.Imported) ||
            operation.Assets.Select(x => x.RuntimeId).OrderBy(x => x, StringComparer.Ordinal)
                .SequenceEqual(items.Select(x => x.Artifact.Rid).OrderBy(x => x, StringComparer.Ordinal)) == false)
            throw new InvalidOperationException("The client pack is not completely imported.");
        if (operation.PublishedAtUtc.HasValue) return;
        if (!NuGetVersion.TryParse(operation.Version, out var parsedVersion) ||
            parsedVersion.ToNormalizedString() != operation.Version)
            throw new InvalidOperationException("The imported client pack has an invalid version.");
        if (parsedVersion.IsPrerelease && !confirmPrerelease)
            throw new InvalidOperationException("Prerelease publication requires explicit confirmation.");

        if (await ClientReleaseDeletionService.IsSuppressedAsync(db, operation.Version, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("This local release was deleted; deliberately re-import it before publication.");

        var enabledOffers = Array.Empty<(string RuntimeId, string Version)>();
        if (automatic)
        {
            var policy = (await db.ClientReleaseAutomationSettings
                .FromSqlRaw("SELECT * FROM \"ClientReleaseAutomationSettings\" WHERE \"Id\" = 1 FOR SHARE")
                .AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false)).SingleOrDefault();
            if (policy is null ||
                !policy.PublishAutomatically || policy.CheckEveryHours == 0 ||
                parsedVersion.IsPrerelease &&
                (!policy.DownloadPrerelease || !policy.DeployPrereleaseAutomatically) ||
                !parsedVersion.IsPrerelease && !policy.DownloadStable)
                throw new InvalidOperationException("Automatic client publication is disabled by current policy.");

            var runtimeIds = items.Select(item => item.Artifact.Rid).ToArray();
            var channel = parsedVersion.IsPrerelease ? "prerelease" : "stable";
            enabledOffers = (await db.ClientUpdateReleases.AsNoTracking()
                .Where(x => x.Enabled && runtimeIds.Contains(x.RuntimeId) && x.Channel == channel)
                .Select(x => new { x.RuntimeId, x.Version }).ToListAsync(cancellationToken).ConfigureAwait(false))
                .Select(x => (x.RuntimeId, x.Version)).ToArray();

            // Automatic publication reconciles each runtime independently. A newer
            // offer already covering one runtime is retained and that runtime is
            // skipped; missing runtimes can still complete the pack. The exact
            // version checks below preserve immutable and disabled-record conflicts.
        }

        var existing = await db.ClientUpdateReleases
            .Where(x => x.Version == operation.Version).ToListAsync(cancellationToken).ConfigureAwait(false);
        var added = new List<ClientUpdateReleaseRecord>();
        foreach (var item in items)
        {
            if (item.Artifact.Version != operation.Version)
                throw new InvalidOperationException("Client pack version changed before publication.");
            var asset = operation.Assets.Single(x => x.RuntimeId == item.Artifact.Rid);
            if (asset.LocalSha256 != item.Artifact.Sha256 || asset.LocalSizeBytes != item.Artifact.Size)
                throw new ClientArtifactConflictException(item.Artifact.Rid, operation.Version);
            var prior = existing.SingleOrDefault(x => x.RuntimeId == item.Artifact.Rid);
            if (prior is not null)
            {
                if (prior.Sha256 != item.Artifact.Sha256 || !prior.Enabled ||
                    !string.Equals(prior.Channel, parsedVersion.IsPrerelease ? "prerelease" : "stable",
                        StringComparison.OrdinalIgnoreCase))
                    throw new ClientArtifactConflictException(item.Artifact.Rid, operation.Version);
                continue;
            }

            if (automatic && enabledOffers.Any(offer =>
                    string.Equals(offer.RuntimeId, item.Artifact.Rid, StringComparison.Ordinal) &&
                    NuGetVersion.TryParse(offer.Version, out var priorVersion) && priorVersion > parsedVersion))
                continue;

            var release = new ClientUpdateReleaseRecord
            {
                PublicId = Guid.NewGuid(), RuntimeId = item.Artifact.Rid,
                Version = operation.Version,
                Channel = parsedVersion.IsPrerelease ? "prerelease" : "stable",
                ArtifactKey = $"{item.Artifact.Rid}/{operation.Version}/{item.Artifact.FileName}",
                Sha256 = item.Artifact.Sha256, SizeBytes = item.Artifact.Size,
                ManifestJson = item.ManifestJson, Enabled = true,
                PublishedAtUtc = timeProvider.GetUtcNow(), PublishedBy = publishedBy
            };
            db.ClientUpdateReleases.Add(release);
            added.Add(release);
        }
        var lastRevision = revision;
        for (var index = 0; index < added.Count; index++)
        {
            if (index > 0) lastRevision = await IncrementRevisionAsync(cancellationToken).ConfigureAwait(false);
            added[index].Revision = lastRevision;
        }
        operation.PublishedAtUtc = timeProvider.GetUtcNow();
        operation.PublishedBy = publishedBy;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await NotifyAsync(lastRevision, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        await RefreshCatalogSafelyAsync(cancellationToken).ConfigureAwait(false);
    }

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

        if (await ClientReleaseDeletionService.IsSuppressedAsync(db, artifact.Version, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("This local release was deleted; deliberately re-import it before publication.");

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
        await ClientReleaseDeletionService.LockAdmissionsAsync(db, cancellationToken).ConfigureAwait(false);
        if (await ClientReleaseDeletionService.IsSuppressedAsync(db, artifact.Version, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("This local release was deleted; deliberately re-import it before publication.");
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
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await ClientReleaseDeletionService.LockAdmissionsAsync(db, cancellationToken).ConfigureAwait(false);
        await LockAgentUpdateAsync(identity.TenantId, identity.AgentId, cancellationToken).ConfigureAwait(false);
        var release = await db.ClientUpdateReleases.SingleOrDefaultAsync(
            x => x.PublicId == releasePublicId && x.Enabled,
            cancellationToken).ConfigureAwait(false);
        if (release is not null && await ClientReleaseDeletionService.IsSuppressedAsync(db, release.Version, cancellationToken).ConfigureAwait(false))
            return null;
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
            release.Channel == "prerelease" && !tenantAllowsPrerelease ||
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
        // Admission identities are immutable. A restart that needs a new nonce
        // gets a fresh attempt, retaining every old result for safe replay.
        var attempt = await db.ClientUpdateAttempts.AsNoTracking()
            .Where(candidate => candidate.AgentId == identity.AgentId && candidate.ReleaseId == release.Id &&
                candidate.AdmissionNonceHash == nonceHash)
            .OrderByDescending(candidate => candidate.Id).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (attempt is not null)
        {
            if (attempt.State is ClientUpdateAttemptState.Accepted or ClientUpdateAttemptState.RolledBack or ClientUpdateAttemptState.RollbackUnverified ||
                await db.ClientUpdateAttempts.AsNoTracking().AnyAsync(candidate => candidate.AgentId == identity.AgentId &&
                    candidate.TenantId == identity.TenantId && candidate.Id > attempt.Id, cancellationToken).ConfigureAwait(false))
            {
                ClientUpdateTelemetry.ClaimObserved("ineligible");
                return null;
            }
            if (attempt.State != ClientUpdateAttemptState.FailedPreActivation)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return new ClientUpdateClaimResult(attempt.PublicId, release.PublicId, admissionNonce, release.Version);
            }
            // Older clients retain their nonce after an acknowledged staging
            // failure. Give that explicit retry a new immutable attempt as well.
        }
        var latest = await db.ClientUpdateAttempts.AsNoTracking()
            .Where(candidate => candidate.AgentId == identity.AgentId && candidate.ReleaseId == release.Id)
            .OrderByDescending(candidate => candidate.Id).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (latest is not null && latest.State != ClientUpdateAttemptState.FailedPreActivation &&
            latest.State != ClientUpdateAttemptState.Downloading && !IsStaleClaimedAttempt(latest, now))
        {
            ClientUpdateTelemetry.ClaimObserved("rejected");
            return null;
        }
        if (latest is not null && latest.State is ClientUpdateAttemptState.Claimed or ClientUpdateAttemptState.Downloading)
        {
            // The replaced generation never crossed cutover. Finish its history so
            // abandoned downloads cannot block package purging or remain in progress.
            await db.ClientUpdateAttempts.Where(candidate => candidate.Id == latest.Id)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(candidate => candidate.State, ClientUpdateAttemptState.FailedPreActivation)
                    .SetProperty(candidate => candidate.FailureCode, "admission_superseded")
                    .SetProperty(candidate => candidate.Message, "A fresh authenticated admission replaced the interrupted update attempt.")
                    .SetProperty(candidate => candidate.UpdatedAtUtc, now), cancellationToken).ConfigureAwait(false);
        }
        attempt = new ClientUpdateAttemptRecord
        {
            PublicId = Guid.NewGuid(), ReleaseId = release.Id, ReleaseRecordId = release.Id,
            TenantId = identity.TenantId, AgentId = identity.AgentId,
            FromVersion = current.ToNormalizedString(), TargetVersion = release.Version, RuntimeId = release.RuntimeId,
            State = ClientUpdateAttemptState.Claimed, AdmissionNonceHash = nonceHash,
            CreatedAtUtc = now, UpdatedAtUtc = now
        };
        db.ClientUpdateAttempts.Add(attempt);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await ClientUpdateActivityNotifications.NotifyAsync(db, identity.TenantId, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
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
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await LockAgentUpdateAsync(identity.TenantId, identity.AgentId, cancellationToken).ConfigureAwait(false);
        var attempt = await db.ClientUpdateAttempts.AsNoTracking().Include(x => x.Release).SingleOrDefaultAsync(
            x => x.PublicId == attemptId && x.TenantId == identity.TenantId && x.AgentId == identity.AgentId,
            cancellationToken).ConfigureAwait(false);
        if (attempt is null) return new(false, "attempt_not_found");
        if (await db.ClientUpdateAttempts.AsNoTracking().AnyAsync(candidate =>
                candidate.TenantId == identity.TenantId && candidate.AgentId == identity.AgentId &&
                candidate.Id > attempt.Id, cancellationToken).ConfigureAwait(false))
            return new(false, "newer_attempt_exists");
        if ((attempt.PurgedReleasePublicId ?? attempt.Release?.PublicId) != releaseId) return new(false, "release_mismatch");
        if (attempt.State is not ClientUpdateAttemptState.Activating and not ClientUpdateAttemptState.GatewayReadmitted and
            not ClientUpdateAttemptState.Accepted)
            return new(false, "invalid_attempt_state");
        if (!IsAdmissionNonce(nonce) || !CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(attempt.AdmissionNonceHash),
                Convert.FromHexString(HashNonce(nonce)))) return new(false, "nonce_mismatch");
        if (!string.Equals(attempt.TargetVersion, RemoveBuildMetadata(agentVersion), StringComparison.OrdinalIgnoreCase))
            return new(false, "version_mismatch");
        if (connectionId == Guid.Empty || connectionEpoch <= 0) return new(false, "invalid_presence_connection");
        if (attempt.State == ClientUpdateAttemptState.Accepted && !attempt.ConfirmationId.HasValue)
            return new(false, "confirmation_missing");
        if (attempt.GatewayConnectionId == connectionId && attempt.GatewayConnectionEpoch == connectionEpoch)
            return new(true, "accepted", attempt.ConfirmationId);
        if (attempt.GatewayConnectionEpoch >= connectionEpoch)
            return new(false, "presence_epoch_mismatch");

        // A committed confirmation can be lost before the client writes readiness.
        // Rebind the authenticated target to its newer presence session without
        // changing the accepted outcome or manufacturing a new confirmation.
        if (attempt.State == ClientUpdateAttemptState.Accepted)
        {
            await db.ClientUpdateAttempts.Where(x => x.Id == attempt.Id)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.GatewayConnectionId, connectionId)
                    .SetProperty(x => x.GatewayConnectionEpoch, connectionEpoch), cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new(true, "accepted", attempt.ConfirmationId);
        }

        var readmittedAtUtc = timeProvider.GetUtcNow();
        var affected = await db.ClientUpdateAttempts
            .Where(x => x.Id == attempt.Id &&
                (x.State == ClientUpdateAttemptState.Activating ||
                 x.State == ClientUpdateAttemptState.GatewayReadmitted))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.State, ClientUpdateAttemptState.GatewayReadmitted)
                .SetProperty(x => x.GatewayConnectionId, connectionId)
                .SetProperty(x => x.GatewayConnectionEpoch, connectionEpoch)
                .SetProperty(x => x.ReadmittedAtUtc, attempt.ReadmittedAtUtc ?? readmittedAtUtc)
                .SetProperty(x => x.UpdatedAtUtc, readmittedAtUtc), cancellationToken)
            .ConfigureAwait(false);
        if (affected == 1)
            await ClientUpdateActivityNotifications.NotifyAsync(db, identity.TenantId, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
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

    public async Task<ClientUpdateActivationResult> ConfirmAsync(
        AuthenticatedAgentIdentity identity,
        Guid attemptId,
        Guid connectionId,
        long connectionEpoch,
        CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await LockAgentUpdateAsync(identity.TenantId, identity.AgentId, cancellationToken).ConfigureAwait(false);
        var attempt = await db.ClientUpdateAttempts.AsNoTracking().SingleOrDefaultAsync(
            x => x.PublicId == attemptId && x.TenantId == identity.TenantId && x.AgentId == identity.AgentId,
            cancellationToken).ConfigureAwait(false);
        if (attempt is null) return new(false, "attempt_not_found");
        if (await db.ClientUpdateAttempts.AsNoTracking().AnyAsync(candidate =>
                candidate.TenantId == identity.TenantId && candidate.AgentId == identity.AgentId &&
                candidate.Id > attempt.Id, cancellationToken).ConfigureAwait(false))
            return new(false, "newer_attempt_exists");
        if (attempt.State is not ClientUpdateAttemptState.GatewayReadmitted and not ClientUpdateAttemptState.Accepted)
            return new(false, "invalid_attempt_state");
        if (attempt.GatewayConnectionId != connectionId) return new(false, "presence_connection_mismatch");
        if (attempt.GatewayConnectionEpoch != connectionEpoch) return new(false, "presence_epoch_mismatch");
        if (attempt.State == ClientUpdateAttemptState.Accepted)
            return attempt.ConfirmationId.HasValue
                ? new(true, "accepted", attempt.ConfirmationId)
                : new(false, "confirmation_missing");

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
        await ClientUpdateActivityNotifications.NotifyAsync(db, identity.TenantId, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        if (attempt.ReadmittedAtUtc.HasValue)
            ClientUpdateTelemetry.ActivationCompleted(now - attempt.ReadmittedAtUtc.Value, "accepted");
        ClientUpdateTelemetry.OutcomeObserved(ClientUpdateAttemptState.Accepted);
        return new(true, "accepted", confirmationId);
    }

    public async Task NotifyPresenceObservedAsync(AuthenticatedAgentIdentity identity, CancellationToken cancellationToken)
    {
        if (await db.ClientUpdateAttempts.AsNoTracking().AnyAsync(attempt =>
                attempt.TenantId == identity.TenantId && attempt.AgentId == identity.AgentId,
                cancellationToken).ConfigureAwait(false))
            await ClientUpdateActivityNotifications.NotifyAsync(db, identity.TenantId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ClientUpdateReportResult> ReportAsync(
        AuthenticatedAgentIdentity identity,
        Guid attemptId,
        ClientUpdateAttemptState state,
        string? failureCode,
        string? message,
        CancellationToken cancellationToken,
        string? admissionNonce = null)
    {
        if (!IsClientReportableState(state))
            return new(ClientUpdateReportOutcome.InvalidState, "invalid_report_state");
        if (failureCode?.Length > 64 || message?.Length > 4096)
            return new(ClientUpdateReportOutcome.InvalidState, "invalid_report_details");

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await LockAgentUpdateAsync(identity.TenantId, identity.AgentId, cancellationToken).ConfigureAwait(false);
        var attempt = await (db.Database.IsNpgsql()
                ? db.ClientUpdateAttempts.FromSqlInterpolated(
                    $"SELECT * FROM \"ClientUpdateAttempts\" WHERE \"PublicId\" = {attemptId} AND \"TenantId\" = {identity.TenantId} AND \"AgentId\" = {identity.AgentId} FOR UPDATE")
                : db.ClientUpdateAttempts.Where(candidate =>
                    candidate.PublicId == attemptId && candidate.TenantId == identity.TenantId && candidate.AgentId == identity.AgentId))
            .AsNoTracking().SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (attempt is null) return new(ClientUpdateReportOutcome.NotFound, "attempt_not_found");
        var agentState = await db.AgentClientUpdateStates.AsNoTracking().SingleOrDefaultAsync(
            candidate => candidate.AgentId == identity.AgentId && candidate.TenantId == identity.TenantId,
            cancellationToken).ConfigureAwait(false);
        ClientUpdateReportResult Result(ClientUpdateReportOutcome outcome, string code) => new(
            outcome, code, attempt.State.ToString(), attempt.ConfirmationId,
            agentState?.SuspendedAtUtc.HasValue == true, agentState?.PolicyRevision);

        // New clients bind reports to the admission generation. Older clients retain
        // the existing attempt-only contract, while authority state still fences them.
        if (admissionNonce is not null && (!IsAdmissionNonce(admissionNonce) ||
            !string.Equals(attempt.AdmissionNonceHash, HashNonce(admissionNonce), StringComparison.Ordinal)))
            return Result(ClientUpdateReportOutcome.Conflict, "admission_mismatch");
        if (attempt.State == state)
            return string.Equals(attempt.FailureCode, failureCode, StringComparison.Ordinal) &&
                string.Equals(attempt.Message, message, StringComparison.Ordinal)
                ? Result(ClientUpdateReportOutcome.Duplicate, "report_duplicate")
                : Result(ClientUpdateReportOutcome.Conflict, "state_conflict");

        var now = timeProvider.GetUtcNow();
        async Task<bool> HasNewerAttemptAsync() => await db.ClientUpdateAttempts.AsNoTracking().AnyAsync(candidate =>
            candidate.TenantId == identity.TenantId && candidate.AgentId == identity.AgentId &&
            candidate.Id > attempt.Id, cancellationToken).ConfigureAwait(false);
        async Task SuspendAsync()
        {
            // Resume fences delayed observations from the earlier outcome. A later
            // fresh attempt can suspend normally; replaying the old one cannot.
            var outcomeAtUtc = attempt.ConfirmedAtUtc ?? attempt.UpdatedAtUtc;
            if (agentState?.ResumedAtUtc is not null && agentState.ResumedAtUtc >= outcomeAtUtc) return;
            var releasePublicId = attempt.PurgedReleasePublicId ??
                await db.ClientUpdateReleases.AsNoTracking()
                    .Where(candidate => candidate.Id == attempt.ReleaseRecordId).Select(candidate => candidate.PublicId)
                    .SingleAsync(cancellationToken).ConfigureAwait(false);
            var policyRevision = (agentState?.PolicyRevision ?? 0) + 1;
            if (agentState is null)
            {
                agentState = new AgentClientUpdateStateRecord
                {
                    AgentId = identity.AgentId, TenantId = identity.TenantId, SuspendedAtUtc = now,
                    SuspensionReason = failureCode ?? state.ToString(), SuspensionAttemptId = attempt.PublicId,
                    SuppressedReleaseId = releasePublicId, PolicyRevision = policyRevision
                };
                db.AgentClientUpdateStates.Add(agentState);
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await db.AgentClientUpdateStates.Where(candidate => candidate.AgentId == identity.AgentId && candidate.TenantId == identity.TenantId)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(candidate => candidate.SuspendedAtUtc, now)
                        .SetProperty(candidate => candidate.SuspensionReason, failureCode ?? state.ToString())
                        .SetProperty(candidate => candidate.SuspensionAttemptId, attempt.PublicId)
                        .SetProperty(candidate => candidate.SuppressedReleaseId, releasePublicId)
                        .SetProperty(candidate => candidate.PolicyRevision, policyRevision), cancellationToken).ConfigureAwait(false);
                agentState.SuspendedAtUtc = now;
                agentState.PolicyRevision = policyRevision;
            }
            var revision = await IncrementRevisionAsync(cancellationToken).ConfigureAwait(false);
            await NotifyAsync(revision, cancellationToken).ConfigureAwait(false);
        }
        if (attempt.State == ClientUpdateAttemptState.Accepted &&
            state is ClientUpdateAttemptState.RolledBack or ClientUpdateAttemptState.RollbackUnverified)
        {
            // Confirmation is an immutable authority receipt. Keep the native
            // observation beside it so a lost ready response cannot hide rollback.
            if (attempt.ReportedAtUtc is null)
            {
                await db.ClientUpdateAttempts.Where(candidate => candidate.Id == attempt.Id)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(candidate => candidate.ReconciliationState, "accepted_authority_conflict")
                        .SetProperty(candidate => candidate.ReportedState, state)
                        .SetProperty(candidate => candidate.ReportedFailureCode, failureCode)
                        .SetProperty(candidate => candidate.ReportedMessage, message)
                        .SetProperty(candidate => candidate.ReportedAtUtc, now), cancellationToken).ConfigureAwait(false);
                if (!await HasNewerAttemptAsync().ConfigureAwait(false))
                    await SuspendAsync().ConfigureAwait(false);
                await ClientUpdateActivityNotifications.NotifyAsync(db, identity.TenantId, cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                await RefreshCatalogSafelyAsync(cancellationToken).ConfigureAwait(false);
            }
            else if (attempt.ReportedState != state || attempt.ReportedFailureCode != failureCode || attempt.ReportedMessage != message)
            {
                logger.LogWarning("Distinct rollback observation conflicts with retained update evidence for tenant {TenantId}, agent {AgentId}, attempt {AttemptId}, state {ReportedState}, code {FailureCode}",
                    identity.TenantId, identity.AgentId, attemptId, state, failureCode);
            }
            return Result(ClientUpdateReportOutcome.Conflict, "accepted_authority_conflict");
        }
        if (!IsAllowedTransition(attempt.State, state))
            return Result(ClientUpdateReportOutcome.Conflict, "state_conflict");
        if (await HasNewerAttemptAsync().ConfigureAwait(false))
            return Result(ClientUpdateReportOutcome.Conflict, "newer_attempt_exists");

        await db.ClientUpdateAttempts.Where(candidate => candidate.Id == attempt.Id)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(candidate => candidate.State, state)
                .SetProperty(candidate => candidate.FailureCode, failureCode)
                .SetProperty(candidate => candidate.Message, message)
                .SetProperty(candidate => candidate.UpdatedAtUtc, now), cancellationToken).ConfigureAwait(false);
        attempt.State = state;

        if (state is ClientUpdateAttemptState.RolledBack or ClientUpdateAttemptState.RollbackUnverified)
            await SuspendAsync().ConfigureAwait(false);
        await ClientUpdateActivityNotifications.NotifyAsync(db, identity.TenantId, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        ClientUpdateTelemetry.OutcomeObserved(state);
        if (state is ClientUpdateAttemptState.RolledBack or ClientUpdateAttemptState.RollbackUnverified &&
            attempt.ReadmittedAtUtc.HasValue)
            ClientUpdateTelemetry.ActivationCompleted(now - attempt.ReadmittedAtUtc.Value, "rollback");
        await RefreshCatalogSafelyAsync(cancellationToken).ConfigureAwait(false);
        return Result(ClientUpdateReportOutcome.Applied, "report_applied");
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
        await LockAgentUpdateAsync(tenantId, agentId, cancellationToken).ConfigureAwait(false);
        var state = await db.AgentClientUpdateStates.SingleOrDefaultAsync(
            x => x.TenantId == tenantId && x.AgentId == agentId,
            cancellationToken).ConfigureAwait(false);
        if (state is null)
        {
            // A protected local result can need operator recovery without a
            // prior server suspension. Bind the explicit policy action to an
            // existing tenant agent without creating or changing any attempt.
            if (!await db.Agents.AsNoTracking().AnyAsync(
                    candidate => candidate.Id == agentId && candidate.TenantId == tenantId,
                    cancellationToken).ConfigureAwait(false))
                throw new KeyNotFoundException("Agent was not found.");
            state = new AgentClientUpdateStateRecord { TenantId = tenantId, AgentId = agentId };
            db.AgentClientUpdateStates.Add(state);
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
        await LockAgentUpdateAsync(tenantId, agentId, cancellationToken).ConfigureAwait(false);
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

    private async Task LockAgentUpdateAsync(int tenantId, Guid agentId, CancellationToken cancellationToken)
    {
        if (db.Database.IsNpgsql())
        {
            var key = $"netratel_client_update:{tenantId}:{agentId:D}";
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))", cancellationToken).ConfigureAwait(false);
        }
        // Service calls may share a context in tests or operator flows. Reload authority
        // rows after taking the lock instead of trusting an earlier tracked snapshot.
        foreach (var entry in db.ChangeTracker.Entries().Where(entry => entry.State == EntityState.Unchanged &&
                     (entry.Entity is ClientUpdateAttemptRecord attempt && attempt.AgentId == agentId ||
                      entry.Entity is AgentClientUpdateStateRecord state && state.AgentId == agentId)).ToArray())
            entry.State = EntityState.Detached;
    }

    internal static bool IsClientReportableState(ClientUpdateAttemptState state) => state is
        ClientUpdateAttemptState.Downloading or ClientUpdateAttemptState.Staged or ClientUpdateAttemptState.Activating or
        ClientUpdateAttemptState.FailedPreActivation or ClientUpdateAttemptState.RolledBack or ClientUpdateAttemptState.RollbackUnverified;

    internal static bool TryParseReportState(string? value, out ClientUpdateAttemptState state) =>
        Enum.TryParse(value, true, out state) && IsClientReportableState(state) &&
        string.Equals(Enum.GetName(state), value, StringComparison.OrdinalIgnoreCase);

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
            (ClientUpdateAttemptState.Activating, ClientUpdateAttemptState.FailedPreActivation) => true,
            (ClientUpdateAttemptState.Activating or ClientUpdateAttemptState.GatewayReadmitted,
                ClientUpdateAttemptState.RolledBack or ClientUpdateAttemptState.RollbackUnverified) => true,
            _ when current == next => true,
            _ => false
        };
}
