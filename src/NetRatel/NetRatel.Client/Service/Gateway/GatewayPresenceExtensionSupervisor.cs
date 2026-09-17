using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NetRatel.Client.Service.Gateway;

/// <summary>
/// Owns optional child gateways for exactly one admitted presence fence.
/// A child may fail independently, but it is never allowed to extend its
/// registration authority into a later presence connection.
/// </summary>
internal sealed class GatewayPresenceExtensionSupervisor(Action<string> log, TimeSpan? shutdownTimeout = null)
{
    private static readonly TimeSpan DefaultShutdownTimeout = TimeSpan.FromSeconds(5);
    private readonly TimeSpan _shutdownTimeout = ResolveShutdownTimeout(shutdownTimeout);

    public async Task RunForPresenceSessionAsync(
        GatewayPresenceSession session,
        string accessToken,
        CancellationToken presenceStoppingToken,
        IReadOnlyList<GatewayPresenceExtension> extensions)
    {
        ArgumentNullException.ThrowIfNull(extensions);

        var children = extensions
            .Select(extension => ObserveExtensionAsync(extension, session, accessToken, presenceStoppingToken))
            .ToArray();

        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, presenceStoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (presenceStoppingToken.IsCancellationRequested)
        {
            // The owner has moved to a new immutable presence fence or is stopping.
            log("Gateway presence extension owner was cancelled.");
        }
        finally
        {
            await StopChildrenAsync(children).ConfigureAwait(false);
        }
    }

    private async Task ObserveExtensionAsync(
        GatewayPresenceExtension extension,
        GatewayPresenceSession session,
        string accessToken,
        CancellationToken presenceStoppingToken)
    {
        try
        {
            await extension.RunAsync(session, accessToken, presenceStoppingToken).ConfigureAwait(false);
            if (!presenceStoppingToken.IsCancellationRequested)
            {
                log($"Gateway extension '{extension.Name}' ended unexpectedly; the admitted presence session remains active.");
            }
        }
        catch (OperationCanceledException) when (presenceStoppingToken.IsCancellationRequested)
        {
            // Expected: the parent presence fence owns extension cancellation.
            log($"Gateway extension '{extension.Name}' stopped with its presence owner.");
        }
        catch (Exception exception)
        {
            // Child gateways are non-authoritative. Their own retry policy may
            // recover them; a failure must not collapse healthy presence.
            log($"Gateway extension '{extension.Name}' failed without ending presence: {exception.GetType().Name}: {exception.Message}");
        }
    }

    private async Task StopChildrenAsync(Task[] children)
    {
        if (children.Length == 0)
        {
            return;
        }

        try
        {
            await Task.WhenAll(children).WaitAsync(_shutdownTimeout, CancellationToken.None).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // A late child retains the old session value and cannot register on
            // the next fence. The parent is free to reconnect after this bound.
            log($"Gateway extensions did not stop within {_shutdownTimeout.TotalSeconds:0.###}s; reconnecting presence.");
        }
    }

    private static TimeSpan ResolveShutdownTimeout(TimeSpan? shutdownTimeout)
    {
        var resolved = shutdownTimeout ?? DefaultShutdownTimeout;
        return resolved > TimeSpan.Zero
            ? resolved
            : throw new ArgumentOutOfRangeException(nameof(shutdownTimeout), "Shutdown timeout must be greater than zero.");
    }
}

internal sealed record GatewayPresenceExtension(
    string Name,
    Func<GatewayPresenceSession, string, CancellationToken, Task> RunAsync);
