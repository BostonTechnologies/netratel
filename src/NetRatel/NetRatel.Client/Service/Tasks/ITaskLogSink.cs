// ITaskLogSink.cs
using System.Collections.Generic;
using System.Threading.Tasks;

namespace NetRatel.Client.Service.Tasks;

public interface ITaskLogSink
{
    Task AppendAsync(string stream, string message, ulong seq);
    Task AppendBatchAsync(IReadOnlyList<(string stream, string message, ulong seq)> batch);
}
