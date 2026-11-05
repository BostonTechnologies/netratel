namespace NetRatel.Application.Jobs;

/// <summary>Server-only capture of the current canonical job and all referenced script sources.</summary>
public interface IJobExecutionSnapshotCapture
{
    Task<PinnedJobExecutionSnapshot> CaptureAsync(ulong jobId, int tenantId, CancellationToken cancellationToken);
}
