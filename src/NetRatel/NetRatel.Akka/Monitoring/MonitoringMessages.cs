using Akka.Actor;
using NetRatel.Application.Monitoring;
using NetRatel.Application.Presence;

namespace NetRatel.Akka.Monitoring;

public interface IMonitoringClientMessage { ClientKey Client { get; } }
public sealed record BeginMonitoringStream(MonitoringEvidenceFence Fence) : IMonitoringClientMessage { public ClientKey Client => Fence.Client; }
public sealed record EndMonitoringStream(MonitoringEvidenceFence Fence) : IMonitoringClientMessage { public ClientKey Client => Fence.Client; }
public sealed record RecordMonitoringTelemetry(MonitoringTelemetryInput Input) : IMonitoringClientMessage { public ClientKey Client => Input.Fence.Client; }
public sealed record RecordMonitoringServices(MonitoringServicesInput Input) : IMonitoringClientMessage { public ClientKey Client => Input.Fence.Client; }
public sealed record GetClientMonitoring(ClientKey Client) : IMonitoringClientMessage;
public sealed record RefreshClientMonitoring(ClientKey Client) : IMonitoringClientMessage;
public sealed record AcknowledgeMonitoringOccurrence(MonitoringOperatorCommand Command) : IMonitoringClientMessage { public ClientKey Client => new(Command.Series.TenantId, Command.Series.AgentId); }
public sealed record ClearMonitoringOccurrence(MonitoringOperatorCommand Command) : IMonitoringClientMessage { public ClientKey Client => new(Command.Series.TenantId, Command.Series.AgentId); }
internal sealed record RoutedMonitoringMessage(IMonitoringClientMessage Message, IActorRef ReplyTo, long Generation);
internal sealed record RoutedMonitoringReply(ClientKey Client, IActorRef ReplyTo, object Result);
internal sealed record RequestMonitoringPassivation(IActorRef Entity, long Generation);
internal sealed record MonitoringRefreshTick : INotInfluenceReceiveTimeout;
public sealed record GetMonitoringRouteDiagnostics;
public sealed record MonitoringRouteDiagnostics(int ActiveClientActors, int OutstandingRequests);
