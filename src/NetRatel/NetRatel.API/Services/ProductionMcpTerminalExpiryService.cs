using NetRatel.API.Gateway;
using NetRatel.Application.Operations;

namespace NetRatel.API.Services;

/// <summary>
/// Turns expired durable Production terminal leases into idempotent agent-side
/// closes. Close-pending records remain durable until the gateway confirms the
/// PTY is gone, so an API restart cannot orphan the remote process.
/// </summary>
public sealed class ProductionMcpTerminalExpiryService(
    IServiceScopeFactory scopes,
    IAgentTerminalSessionRegistry terminals,
    ILogger<ProductionMcpTerminalExpiryService> logger) : BackgroundService
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromSeconds(15);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(SweepInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            await SweepOnceAsync(stoppingToken).ConfigureAwait(false);
    }

    internal async Task SweepOnceAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var sessions = scope.ServiceProvider.GetRequiredService<IMcpOperatorTerminalSessionStore>();
            var leases = await sessions.ClaimDueClosesAsync(DateTimeOffset.UtcNow, 100, stoppingToken).ConfigureAwait(false);
            foreach (var lease in leases)
            {
                try
                {
                    await terminals.CloseAsync(lease.SessionId, lease.Generation, lease.CloseReason ?? "terminal_policy_lease_expired", stoppingToken).ConfigureAwait(false);
                }
                catch (TerminalGatewayActionException exception) when (exception.Code == "terminal_session_not_found")
                {
                    await sessions.MarkTerminalAsync(lease.SessionId, McpOperatorTerminalSessionState.Closed, exception.Code, DateTimeOffset.UtcNow, stoppingToken).ConfigureAwait(false);
                    logger.LogInformation("Durable Production terminal close was already absent and is now closed.");
                }
                catch (TerminalGatewayActionException exception) when (exception.Code == "terminal_transport_unavailable")
                {
                    // Keep the durable closing state until the agent's next
                    // authenticated announcement permits another close frame.
                    logger.LogInformation("Durable Production terminal close remains pending; gateway state={TerminalGatewayCode}", exception.Code);
                }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "Could not dispatch a durable Production terminal close; it will be retried.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Production terminal expiry sweep failed; durable close-pending leases will be retried.");
        }
    }
}
