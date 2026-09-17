using System.Threading.Channels;

namespace NetRatel.API.Application.Requests.Services;

public interface IExecutionQueue
{
    ValueTask EnqueueAsync(QueuedItem item, CancellationToken ct);
    IAsyncEnumerable<QueuedItem> DequeueAsync(CancellationToken ct);
}

public record QueuedItem(Guid RequestId, string Tenant, string TypeKey, Dictionary<string, object?> Inputs, string CallbackUrl, string CorrelationId);

public sealed class ExecutionQueue : IExecutionQueue
{
    private readonly Channel<QueuedItem> _channel = Channel.CreateUnbounded<QueuedItem>();
    public ValueTask EnqueueAsync(QueuedItem item, CancellationToken ct) => _channel.Writer.WriteAsync(item, ct);
    public async IAsyncEnumerable<QueuedItem> DequeueAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        while (await _channel.Reader.WaitToReadAsync(ct))
            while (_channel.Reader.TryRead(out var item))
                yield return item;
    }
}
