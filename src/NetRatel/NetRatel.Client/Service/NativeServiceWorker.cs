using System;
using System.Threading;
using System.Threading.Tasks;
using NetRatel.Client.Service.Logging;

namespace NetRatel.Client.Service;

internal static class NativeServiceWorker
{
    internal static async Task<int?> ObserveAsync(Func<CancellationToken, Task> run, CancellationToken stopping, Func<int> exitCode)
    {
        try
        {
            await run(stopping).ConfigureAwait(false);
            if (stopping.IsCancellationRequested) return null;
            var failure = exitCode();
            LogManager.WriteLog($"[Service] Unexpected client worker return. exitCode={failure}");
            return failure == 0 ? 1 : failure;
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { return null; }
        catch (Exception exception)
        {
            if (stopping.IsCancellationRequested) return null;
            LogManager.WriteLog($"[Service] Unexpected client worker fault: {exception.GetType().Name}");
            return 1;
        }
    }
}
