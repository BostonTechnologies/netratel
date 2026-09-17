using System.Runtime.ExceptionServices;
using Grpc.Core;

namespace NetRatel.API.Gateway;

/// <summary>Joins both halves of a registration-owned RPC, including replacement and writer failure.</summary>
internal static class GatewayDuplexSession
{
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(5);

    public static async Task RunAsync(
        Func<CancellationToken, Task> read,
        Func<CancellationToken, Task> write,
        CancellationToken callCancellation,
        CancellationToken registrationCompletion,
        ILogger logger)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(callCancellation, registrationCompletion);
        var reader = InvokeAsync(read, cancellation.Token);
        var writer = InvokeAsync(write, cancellation.Token);
        Exception? failure = null;
        try
        {
            await (await Task.WhenAny(reader, writer).ConfigureAwait(false)).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsCancellation(exception, cancellation.Token))
        {
            logger.LogDebug("Gateway duplex stream completed through owner cancellation.");
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            await cancellation.CancelAsync().ConfigureAwait(false);
            var joined = Task.WhenAll(reader, writer);
            try
            {
                await joined.WaitAsync(ShutdownTimeout).ConfigureAwait(false);
            }
            catch (TimeoutException exception)
            {
                // Observe a late fault without starting another worker or waiting
                // indefinitely for a dependency that ignores RPC cancellation.
                _ = joined.ContinueWith(static task => _ = task.Exception,
                    CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
                logger.LogWarning(exception, "Gateway duplex shutdown exceeded its bounded join deadline.");
                failure ??= new RpcException(new Status(StatusCode.DeadlineExceeded, "The gateway stream could not finish shutdown within its deadline."));
            }
            catch (Exception exception)
            {
                // WhenAll may surface a cancelled operation before a sibling's
                // protocol fault. Inspect every fault before classifying shutdown.
                var unexpected = joined.Exception?.Flatten().InnerExceptions
                    .FirstOrDefault(error => !IsCancellation(error, cancellation.Token));
                if (unexpected is not null)
                    failure ??= unexpected;
                else if (!IsCancellation(exception, cancellation.Token))
                    failure ??= exception;
                else
                    logger.LogDebug("Gateway duplex sibling joined after cancellation.");
            }
        }

        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static async Task InvokeAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken) =>
        await action(cancellationToken).ConfigureAwait(false);

    private static bool IsCancellation(Exception exception, CancellationToken cancellationToken) =>
        cancellationToken.IsCancellationRequested &&
        (exception is OperationCanceledException || exception is RpcException { StatusCode: StatusCode.Cancelled });
}
