using System;
using System.Threading.Channels;
using NetRatel.Shared.Contracts.RemoteSupport;

namespace NetRatel.Client.Service.RemoteSupport;

/// <summary>
/// Presence-scoped hand-off from the admitted V2 edge to the canonical
/// preparation stream. The queue contains instructions only; WTS inventory
/// and exact preparation results still use their established gRPC contracts.
/// </summary>
internal sealed class RemoteSupportTransitionEffectQueue : IDisposable
{
    private readonly Channel<RemoteSupportTransitionEffect> _effects = Channel.CreateBounded<RemoteSupportTransitionEffect>(
        new BoundedChannelOptions(32)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
            AllowSynchronousContinuations = false
        });

    public ChannelReader<RemoteSupportTransitionEffect> Reader => _effects.Reader;

    public bool TryEnqueue(RemoteSupportTransitionEffect effect) => _effects.Writer.TryWrite(effect);

    public void Dispose() => _effects.Writer.TryComplete();
}
