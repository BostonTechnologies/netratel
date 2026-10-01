using NetRatel.Application.Fanout;
using NetRatel.Application.Jobs;

namespace NetRatel.API.Realtime;

internal static class RealtimeFanoutEnvelopeFactory
{
    public static RealtimeFanoutEnvelope? FromJob(
        IJobObservation observation,
        JobMessageResult result)
    {
        if (observation.TenantId is not > 0)
        {
            return null;
        }

        return new(
            RealtimeFanoutEnvelope.CurrentSchemaVersion,
            RealtimeFanoutCategory.Job,
            new RealtimeFanoutTarget(
                observation.TenantId.Value,
                RealtimeFanoutTargetScope.Job,
                JobId: observation.JobId),
            ToEventType(result.CurrentStatus),
            ToStatus(result.CurrentStatus),
            observation.Timestamp,
            Revision: observation.SourceEventId,
            Diagnostics: new RealtimeFanoutDiagnosticSummary(ObservedCount: 1));
    }

    private static RealtimeFanoutEventType ToEventType(JobRunState? status) => status switch
    {
        JobRunState.Succeeded => RealtimeFanoutEventType.Completed,
        JobRunState.Failed or JobRunState.TimedOut => RealtimeFanoutEventType.Failed,
        JobRunState.Cancelled => RealtimeFanoutEventType.Cancelled,
        _ => RealtimeFanoutEventType.Updated
    };

    private static RealtimeFanoutStatus ToStatus(JobRunState? status) => status switch
    {
        JobRunState.Pending => RealtimeFanoutStatus.Pending,
        JobRunState.Running => RealtimeFanoutStatus.Active,
        JobRunState.Succeeded => RealtimeFanoutStatus.Completed,
        JobRunState.Failed or JobRunState.TimedOut => RealtimeFanoutStatus.Failed,
        JobRunState.Cancelled => RealtimeFanoutStatus.Cancelled,
        _ => RealtimeFanoutStatus.Unknown
    };
}
