using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using NetRatel.API.Realtime.Shadow;
using NetRatel.Application.Jobs;

namespace NetRatel.API.Services.Jobs;

public sealed record JobShadowIngressStatus(
    ulong Enqueued,
    ulong Dropped,
    ulong Accepted,
    ulong Rejected,
    ulong PersistenceFailures);

public interface IJobShadowObservationSink
{
    bool TryEnqueue(IJobShadowObservation observation);

    JobShadowIngressStatus GetStatus();
}

/// <summary>
/// Keeps non-authoritative job observations off producer callback paths while
/// preserving a single ordered shadow writer. A full queue drops shadow data
/// only; it cannot delay or fail the production job projection.
/// </summary>
public sealed class JobShadowObservationQueue : BackgroundService, IJobShadowObservationSink
{
    private const int Capacity = 1_024;
    private readonly IJobShadowRouter _router;
    private readonly IShadowFanoutSink _fanout;
    private readonly Channel<IJobShadowObservation> _observations = Channel.CreateBounded<IJobShadowObservation>(
        new BoundedChannelOptions(Capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
    private long _enqueued;
    private long _dropped;
    private long _accepted;
    private long _rejected;
    private long _persistenceFailures;

    public JobShadowObservationQueue(IJobShadowRouter router)
        : this(router, NullShadowFanoutSink.Instance)
    {
    }

    public JobShadowObservationQueue(IJobShadowRouter router, IShadowFanoutSink fanout)
    {
        _router = router;
        _fanout = fanout;
    }

    public bool TryEnqueue(IJobShadowObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (_observations.Writer.TryWrite(observation))
        {
            IncrementSaturating(ref _enqueued);
            return true;
        }

        IncrementSaturating(ref _dropped);
        return false;
    }

    public JobShadowIngressStatus GetStatus() =>
        new(
            ReadCounter(ref _enqueued),
            ReadCounter(ref _dropped),
            ReadCounter(ref _accepted),
            ReadCounter(ref _rejected),
            ReadCounter(ref _persistenceFailures));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var observation in _observations.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                var result = await _router.RecordAsync(
                        new RecordJobShadowObservation(observation),
                        stoppingToken)
                    .ConfigureAwait(false);
                if (result.Disposition == JobShadowMessageDisposition.Accepted)
                {
                    IncrementSaturating(ref _accepted);
                    var envelope = ShadowFanoutEnvelopeFactory.FromJob(observation, result);
                    if (envelope is not null)
                    {
                        _fanout.TryEnqueueBestEffort(envelope);
                    }
                }
                else
                {
                    IncrementSaturating(ref _rejected);
                    if (result.Disposition == JobShadowMessageDisposition.PersistenceUnavailable)
                    {
                        IncrementSaturating(ref _persistenceFailures);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                IncrementSaturating(ref _rejected);
                IncrementSaturating(ref _persistenceFailures);
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _observations.Writer.TryComplete();
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    private static ulong ReadCounter(ref long counter) =>
        checked((ulong)Math.Max(0, Interlocked.Read(ref counter)));

    private static void IncrementSaturating(ref long counter)
    {
        while (true)
        {
            var current = Interlocked.Read(ref counter);
            if (current == long.MaxValue)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref counter, current + 1, current) == current)
            {
                return;
            }
        }
    }
}
