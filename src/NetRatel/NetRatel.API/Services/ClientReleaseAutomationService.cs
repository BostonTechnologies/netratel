using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using NetRatel.Infrastructure.Persistence;

namespace NetRatel.API.Services;

public sealed record ClientReleaseAutomationUpdate(
    int CheckEveryHours, bool DownloadStable, bool DownloadPrerelease,
    bool PublishAutomatically, bool DeployPrereleaseAutomatically, long Revision);

public sealed record ClientReleaseAutomationStatus(
    int CheckEveryHours, bool DownloadStable, bool DownloadPrerelease,
    bool PublishAutomatically, bool DeployPrereleaseAutomatically,
    DateTimeOffset? LastAttemptAtUtc, DateTimeOffset? LastSuccessAtUtc,
    DateTimeOffset? NextCheckAtUtc, string? LastError, long Revision,
    string? UpdatedBy, DateTimeOffset? UpdatedAtUtc);

public sealed class ClientReleaseAutomationService(OrchestratorDbContext db, TimeProvider clock)
{
    public async Task<ClientReleaseAutomationStatus> GetAsync(CancellationToken ct)
    {
        var row = await db.ClientReleaseAutomationSettings.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == 1, ct);
        return ToStatus(row);
    }

    public async Task<ClientReleaseAutomationStatus> UpdateAsync(
        ClientReleaseAutomationUpdate request, string actor, CancellationToken ct)
    {
        if (request.CheckEveryHours is not (0 or 12 or 24))
            throw new ArgumentException("Automatic checks must be off, daily, or twice daily.");
        if (request.DeployPrereleaseAutomatically &&
            (!request.DownloadPrerelease || !request.PublishAutomatically))
            throw new ArgumentException("Automatic prerelease deployment requires separate download and publication opt-ins.");
        if (string.IsNullOrWhiteSpace(actor)) throw new ArgumentException("An operator identity is required.");

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var row = (await db.ClientReleaseAutomationSettings
            .FromSqlRaw("SELECT * FROM \"ClientReleaseAutomationSettings\" WHERE \"Id\" = 1 FOR UPDATE")
            .ToListAsync(ct)).SingleOrDefault();
        if (request.Revision != (row?.Revision ?? 0))
            throw new DbUpdateConcurrencyException("Automation settings changed. Refresh before saving.");
        var now = clock.GetUtcNow();
        if (row is null)
        {
            row = new ClientReleaseAutomationSettings { Id = 1 };
            db.ClientReleaseAutomationSettings.Add(row);
        }
        row.CheckEveryHours = request.CheckEveryHours;
        row.DownloadStable = request.DownloadStable;
        row.DownloadPrerelease = request.DownloadPrerelease;
        row.PublishAutomatically = request.PublishAutomatically;
        row.DeployPrereleaseAutomatically = request.DeployPrereleaseAutomatically;
        row.NextCheckAtUtc = request.CheckEveryHours == 0 ? null :
            now.AddHours(request.CheckEveryHours).AddSeconds(RandomNumberGenerator.GetInt32(0, 301));
        row.Revision++;
        row.UpdatedBy = actor;
        row.UpdatedAtUtc = now;
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return ToStatus(row);
    }

    public async Task<ClientReleaseAutomationStatus> CheckNowAsync(string actor, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(actor)) throw new ArgumentException("An operator identity is required.");
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var row = (await db.ClientReleaseAutomationSettings
            .FromSqlRaw("SELECT * FROM \"ClientReleaseAutomationSettings\" WHERE \"Id\" = 1 FOR UPDATE")
            .ToListAsync(ct)).SingleOrDefault();
        if (row is null)
        {
            row = new ClientReleaseAutomationSettings { Id = 1 };
            db.ClientReleaseAutomationSettings.Add(row);
        }
        row.NextCheckAtUtc = clock.GetUtcNow();
        row.Revision++;
        row.UpdatedBy = actor;
        row.UpdatedAtUtc = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return ToStatus(row);
    }

    private static ClientReleaseAutomationStatus ToStatus(ClientReleaseAutomationSettings? row) =>
        row is null
            ? new(0, false, false, false, false, null, null, null, null, 0, null, null)
            : new(row.CheckEveryHours, row.DownloadStable, row.DownloadPrerelease,
                row.PublishAutomatically, row.DeployPrereleaseAutomatically,
                row.LastAttemptAtUtc, row.LastSuccessAtUtc, row.NextCheckAtUtc,
                row.LastError, row.Revision, row.UpdatedBy, row.UpdatedAtUtc);
}
