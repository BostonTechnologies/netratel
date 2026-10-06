using System;
using System.Collections.Generic;
using System.Linq;
using Grpc.Core;
using System.Threading;
using System.Threading.Tasks;

namespace NetRatel.Client.Service.Gateway;

/// <summary>
/// Owns optional child gateways for exactly one admitted presence fence.
/// A child may fail independently, but it is never allowed to extend its
/// registration authority into a later presence connection.
/// </summary>
internal sealed class GatewayPresenceExtensionSupervisor(Action<string> log, TimeSpan? shutdownTimeout = null,
    TimeProvider? timeProvider = null, Func<double>? nextRandom = null)
{
    private static readonly TimeSpan DefaultShutdownTimeout = TimeSpan.FromSeconds(5);
    private readonly TimeSpan _shutdownTimeout = ResolveShutdownTimeout(shutdownTimeout);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

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
            log($"Gateway presence extension owner was cancelled. {OwnerSummary(session)}");
        }
        finally
        {
            await StopChildrenAsync(children, session).ConfigureAwait(false);
        }
    }

    private async Task ObserveExtensionAsync(
        GatewayPresenceExtension extension,
        GatewayPresenceSession session,
        string accessToken,
        CancellationToken presenceStoppingToken)
    {
        var retry = new GatewayReconnectPolicy(_timeProvider, nextRandom ?? Random.Shared.NextDouble, TimeSpan.FromSeconds(120));
        while (!presenceStoppingToken.IsCancellationRequested)
        {
            try
            {
                await extension.RunAsync(session, session.GetAccessToken(accessToken), presenceStoppingToken).ConfigureAwait(false);
                if (!presenceStoppingToken.IsCancellationRequested)
                    log($"Gateway extension '{extension.Name}' ended unexpectedly; the admitted presence session remains active and this capability is unavailable. {OwnerSummary(session)}");
                return;
            }
            catch (OperationCanceledException) when (presenceStoppingToken.IsCancellationRequested)
            {
                log($"Gateway extension '{extension.Name}' stopped with its presence owner. {OwnerSummary(session)}");
                return;
            }
            catch (RpcException exception) when (presenceStoppingToken.IsCancellationRequested && exception.StatusCode == StatusCode.Cancelled)
            {
                return;
            }
            catch (Exception exception) when (GatewayReconnectPolicy.IsRecoverable(exception))
            {
                // Child clients normally own transport recovery. This covers a
                // classified fault escaping their loop, under the same parent fence.
                if (presenceStoppingToken.IsCancellationRequested) return;
                var delay = retry.FailureDelay();
                log($"Gateway extension '{extension.Name}' transport failed without ending presence: {exception.GetType().Name}; retrying in {delay.TotalSeconds:0.###}s. {OwnerSummary(session)}");
                try { await Task.Delay(delay, _timeProvider, presenceStoppingToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (presenceStoppingToken.IsCancellationRequested) { return; }
            }
            catch (Exception exception)
            {
                // Programming and unsafe-cleanup faults remain visible rather
                // than being retried against potentially unfinished operations.
                log($"Gateway extension '{extension.Name}' failed without ending presence: {exception.GetType().Name}; this capability is unavailable. {OwnerSummary(session)}");
                return;
            }
        }
    }

    private async Task StopChildrenAsync(Task[] children, GatewayPresenceSession session)
    {
        if (children.Length == 0)
        {
            return;
        }

        try
        {
            await Task.WhenAll(children).WaitAsync(_shutdownTimeout, _timeProvider, CancellationToken.None).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // A late child retains the old session value and cannot register on
            // the next fence. The parent is free to reconnect after this bound.
            log($"Gateway extensions did not stop within {_shutdownTimeout.TotalSeconds:0.###}s; reconnecting presence. {OwnerSummary(session)}");
        }
    }

    private static string OwnerSummary(GatewayPresenceSession session)
        => $"serverConnection={session.ConnectionId:D}, connectionEpoch={session.ConnectionEpoch}.";

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
