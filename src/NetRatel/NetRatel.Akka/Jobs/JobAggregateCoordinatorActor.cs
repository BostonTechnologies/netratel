using Akka.Actor;
using Akka.Event;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.Application.Jobs;

namespace NetRatel.Akka.Jobs;

public sealed record WakeAggregateJob(ulong ParentRunId);

/// <summary>
/// One local mailbox per aggregate. PostgreSQL serializes claims across replicas; the mailbox
/// owns bounded turns and never keeps a node's workflow or execution state in memory.
/// </summary>
public sealed class JobAggregateCoordinatorActor : ReceiveActor
{
    public JobAggregateCoordinatorActor(IServiceScopeFactory scopes)
    {
        Receive<WakeAggregateJob>(message =>
        {
            if (message.ParentRunId == 0) return;
            var name = $"aggregate-{message.ParentRunId}";
            var child = Context.Child(name);
            if (child.IsNobody()) child = Context.ActorOf(JobAggregateRunActor.Props(message.ParentRunId, scopes), name);
            child.Forward(message);
        });
    }

    public static Props Props(IServiceScopeFactory scopes) => global::Akka.Actor.Props.Create(() => new JobAggregateCoordinatorActor(scopes));
}

internal sealed class JobAggregateRunActor : ReceiveActor, IWithTimers
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ILoggingAdapter _log = Context.GetLogger();
    public ITimerScheduler Timers { get; set; } = null!;

    public JobAggregateRunActor(ulong parentRunId, IServiceScopeFactory scopes)
    {
        ReceiveAsync<WakeAggregateJob>(async _ =>
        {
            Timers.Cancel("turn");
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var result = await scope.ServiceProvider.GetRequiredService<IJobAggregatePump>()
                    .PumpAsync(parentRunId, _lifetime.Token);
                if (!result.Active) { Context.Stop(Self); return; }
                Timers.StartSingleTimer("turn", new WakeAggregateJob(parentRunId),
                    result.Immediate ? TimeSpan.FromMilliseconds(1) : TimeSpan.FromSeconds(1));
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            catch (Exception error)
            {
                _log.Warning("Aggregate job {ParentRunId} turn failed: {FailureType}.", parentRunId, error.GetType().Name);
                Timers.StartSingleTimer("turn", new WakeAggregateJob(parentRunId), TimeSpan.FromSeconds(2));
            }
        });
    }

    public static Props Props(ulong parentRunId, IServiceScopeFactory scopes) =>
        global::Akka.Actor.Props.Create(() => new JobAggregateRunActor(parentRunId, scopes));

    protected override void PostStop()
    {
        _lifetime.Cancel();
        _lifetime.Dispose();
        base.PostStop();
    }
}
