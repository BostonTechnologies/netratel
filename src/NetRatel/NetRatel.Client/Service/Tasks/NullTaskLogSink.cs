using System.Collections.Generic;
using System.Threading.Tasks;

namespace NetRatel.Client.Service.Tasks;

/// <summary>
/// Gateway command authority currently transports lifecycle state only. This
/// sink drops execution output until the command-log stream has its own fenced
/// gateway contract.
/// </summary>
internal sealed class NullTaskLogSink : ITaskLogSink
{
    public static NullTaskLogSink Instance { get; } = new();

    public Task AppendAsync(string stream, string message, ulong seq) => Task.CompletedTask;

    public Task AppendBatchAsync(IReadOnlyList<(string stream, string message, ulong seq)> batch) => Task.CompletedTask;
}
