using Microsoft.AspNetCore.SignalR;
using NetRatel.API.Gateway;
using NetRatel.Shared.Contracts;

namespace NetRatel.API.Realtime.Operations;

/// <summary>
/// Directs admitted batches to authorization-derived SignalR groups without a
/// relay queue. Slow-client handling remains owned by SignalR's bounded
/// transport settings and does not backpressure the agent stream.
/// </summary>
public sealed class OperationsLogFanoutBridge : IHostedService, IDisposable
{
    private readonly IAgentLogGatewaySessionRegistry _logSessions;
    private readonly IHubContext<OperationsHub> _hubContext;

    public OperationsLogFanoutBridge(
        IAgentLogGatewaySessionRegistry logSessions,
        IHubContext<OperationsHub> hubContext)
    {
        _logSessions = logSessions;
        _hubContext = hubContext;
        _logSessions.LogBatchAccepted += Forward;
    }

    private void Forward(GatewayLogBatchEvent batch)
    {
        var payload = new GatewayLogBatchDto(
            batch.Client.TenantId,
            batch.Client.AgentId,
            batch.SessionId,
            batch.Records,
            batch.NextCursor,
            batch.DroppedRecordCount,
            batch.ResyncRequired);
        _ = _hubContext.Clients.Group(OperationsHub.GroupName(batch.Client))
            .SendAsync("LogBatch", payload);
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void Dispose() => _logSessions.LogBatchAccepted -= Forward;
}
