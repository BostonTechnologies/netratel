using NetRatel.Application.Fanout;
using NetRatel.Application.Jobs;
using NetRatel.Application.Terminals;

namespace NetRatel.API.Realtime.Shadow;

internal static class ShadowFanoutEnvelopeFactory
{
    public static ShadowFanoutEnvelope FromTerminal(
        TerminalShadowEvent observation,
        TerminalShadowMessageResult result) =>
        new(
            ShadowFanoutEnvelope.CurrentSchemaVersion,
            ShadowFanoutCategory.Terminal,
            new ShadowFanoutTarget(
                observation.Session.TenantId,
                ShadowFanoutTargetScope.Terminal,
                ClientId: observation.Session.ClientId,
                SessionId: observation.Session.TerminalSessionId),
            ToEventType(result.CurrentStatus),
            ToStatus(result.CurrentStatus),
            observation.Timestamp,
            observation.Sequence,
            Diagnostics: new ShadowFanoutDiagnosticSummary(
                ObservedCount: 1,
                DroppedCount: result.DroppedMetadataEvents,
                RetainedCount: checked((ulong)Math.Max(0, result.RetainedMetadataCount)),
                RepresentedBytes: checked((ulong)Math.Max(0, result.RetainedByteCount))));

    public static ShadowFanoutEnvelope? FromJob(
        IJobShadowObservation observation,
        JobShadowMessageResult result)
    {
        if (observation.TenantId is not > 0)
        {
            return null;
        }

        return new(
            ShadowFanoutEnvelope.CurrentSchemaVersion,
            ShadowFanoutCategory.Job,
            new ShadowFanoutTarget(
                observation.TenantId.Value,
                ShadowFanoutTargetScope.Job,
                JobId: observation.JobId),
            ToEventType(result.CurrentStatus),
            ToStatus(result.CurrentStatus),
            observation.Timestamp,
            Revision: observation.SourceEventId,
            Diagnostics: new ShadowFanoutDiagnosticSummary(ObservedCount: 1));
    }

    private static ShadowFanoutEventType ToEventType(TerminalShadowSessionStatus? status) => status switch
    {
        TerminalShadowSessionStatus.Closed => ShadowFanoutEventType.Completed,
        TerminalShadowSessionStatus.Failed => ShadowFanoutEventType.Failed,
        _ => ShadowFanoutEventType.Updated
    };

    private static ShadowFanoutStatus ToStatus(TerminalShadowSessionStatus? status) => status switch
    {
        TerminalShadowSessionStatus.Requested => ShadowFanoutStatus.Pending,
        TerminalShadowSessionStatus.Opened or TerminalShadowSessionStatus.Closing => ShadowFanoutStatus.Active,
        TerminalShadowSessionStatus.Closed => ShadowFanoutStatus.Completed,
        TerminalShadowSessionStatus.Failed => ShadowFanoutStatus.Failed,
        _ => ShadowFanoutStatus.Unknown
    };

    private static ShadowFanoutEventType ToEventType(JobRunState? status) => status switch
    {
        JobRunState.Succeeded => ShadowFanoutEventType.Completed,
        JobRunState.Failed or JobRunState.TimedOut => ShadowFanoutEventType.Failed,
        JobRunState.Cancelled => ShadowFanoutEventType.Cancelled,
        _ => ShadowFanoutEventType.Updated
    };

    private static ShadowFanoutStatus ToStatus(JobRunState? status) => status switch
    {
        JobRunState.Pending => ShadowFanoutStatus.Pending,
        JobRunState.Running => ShadowFanoutStatus.Active,
        JobRunState.Succeeded => ShadowFanoutStatus.Completed,
        JobRunState.Failed or JobRunState.TimedOut => ShadowFanoutStatus.Failed,
        JobRunState.Cancelled => ShadowFanoutStatus.Cancelled,
        _ => ShadowFanoutStatus.Unknown
    };
}
