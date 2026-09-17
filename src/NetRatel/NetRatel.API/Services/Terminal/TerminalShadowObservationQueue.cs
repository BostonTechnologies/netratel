using System.Threading.Channels;
using NetRatel.API.Realtime.Shadow;
using NetRatel.Application.Terminals;

namespace NetRatel.API.Services.Terminal;

public sealed record TerminalShadowIngressStatus(
    ulong Enqueued,
    ulong Dropped,
    ulong Accepted,
    ulong Rejected);

public interface ITerminalShadowObservationSink
{
    bool TryEnqueue(TerminalShadowEvent observation);

    TerminalShadowIngressStatus GetStatus();
}

public sealed class NullTerminalShadowObservationSink : ITerminalShadowObservationSink
{
    public static NullTerminalShadowObservationSink Instance { get; } = new();

    private NullTerminalShadowObservationSink()
    {
    }

    public bool TryEnqueue(TerminalShadowEvent observation) => false;

    public TerminalShadowIngressStatus GetStatus() => new(0, 0, 0, 0);
}

/// <summary>
/// Removes Akka latency and failure from the live terminal path. Producers use
/// non-blocking TryWrite and drop shadow metadata when the fixed queue is full.
/// </summary>
public sealed class TerminalShadowObservationQueue : BackgroundService, ITerminalShadowObservationSink
{
    internal const int Capacity = 2_048;
    private readonly ITerminalShadowRouter _router;
    private readonly IShadowFanoutSink _fanout;
    private readonly Channel<TerminalShadowEvent> _observations =
        Channel.CreateBounded<TerminalShadowEvent>(new BoundedChannelOptions(Capacity)
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

    public TerminalShadowObservationQueue(ITerminalShadowRouter router)
        : this(router, NullShadowFanoutSink.Instance)
    {
    }

    public TerminalShadowObservationQueue(ITerminalShadowRouter router, IShadowFanoutSink fanout)
    {
        _router = router;
        _fanout = fanout;
    }

    public bool TryEnqueue(TerminalShadowEvent observation)
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

    public TerminalShadowIngressStatus GetStatus() =>
        new(
            ReadCounter(ref _enqueued),
            ReadCounter(ref _dropped),
            ReadCounter(ref _accepted),
            ReadCounter(ref _rejected));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var observation in _observations.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                var result = await _router.RecordAsync(
                        new RecordTerminalShadowEvent(observation),
                        stoppingToken)
                    .ConfigureAwait(false);
                if (result.Disposition == TerminalShadowMessageDisposition.Accepted)
                {
                    IncrementSaturating(ref _accepted);
                    _fanout.TryEnqueueBestEffort(
                        ShadowFanoutEnvelopeFactory.FromTerminal(observation, result));
                }
                else
                {
                    IncrementSaturating(ref _rejected);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                IncrementSaturating(ref _rejected);
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
