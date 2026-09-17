namespace NetRatel.API.Services.Jobs;

public sealed class NetRatelTargetClientNotFoundException : InvalidOperationException
{
    public NetRatelTargetClientNotFoundException(ulong jobId, string targetClientIdentity)
        : base($"Target client for job {jobId} is no longer registered in NetRatel. Rebind the job to an active client before retrying.")
    {
        JobId = jobId;
        TargetClientIdentity = targetClientIdentity;
    }

    public ulong JobId { get; }
    public string TargetClientIdentity { get; }
}
