using System.Security.Cryptography;
using System.Text;
using Akka.Actor;
using NetRatel.Akka.Observability;
using NetRatel.Application.Presence;
using NetRatel.Application.Telemetry;

namespace NetRatel.Akka.Telemetry;

/// <summary>
/// Single-node Phase 2 telemetry region. The entity key is retained for a
/// future approved sharding phase, but this actor uses no cluster facilities.
/// </summary>
public sealed class ClientTelemetryRouterActor : ReceiveActor
{
    private readonly DateTimeOffset _startedAtUtc = DateTimeOffset.UtcNow;
    private ulong _acceptedCount;
    private ulong _rejectedCount;
    private DateTimeOffset? _lastUpdateTimestamp;
    private readonly Dictionary<ClientKey, TelemetrySnapshot> _latestByClient = [];

    public ClientTelemetryRouterActor()
    {
        Receive<RecordTelemetrySnapshot>(message =>
            GetClientActor(message).Tell(new RoutedTelemetryRecord(message, Sender)));
        Receive<GetClientTelemetry>(message => GetClientActor(message).Forward(message));
        Receive<GetClientTelemetryReadModel>(_ => Sender.Tell(new ClientTelemetryReadModelSnapshot(
            _latestByClient.Values
                .OrderBy(snapshot => snapshot.Client.TenantId)
                .ThenBy(snapshot => snapshot.Client.AgentId)
                .ToArray(),
            DateTimeOffset.UtcNow)));
        Receive<RoutedTelemetryResult>(HandleResult);
        Receive<ProbeClientTelemetryRoute>(_ => Sender.Tell(CreateStatus()));
    }

    public static Props Props() =>
        global::Akka.Actor.Props.Create(() => new ClientTelemetryRouterActor());

    private IActorRef GetClientActor(IClientTelemetryMessage message)
    {
        var actorName = CreateActorName(message.Client.EntityId);
        var existing = Context.Child(actorName);
        if (!existing.IsNobody())
        {
            return existing;
        }

        using var activity = NetRatelAkkaTelemetry.StartActivity("akka.actor.create", "telemetry");
        var actor = Context.ActorOf(TelemetryActor.Props(message.Client), actorName);
        NetRatelAkkaTelemetry.SetTelemetryActiveClients(Context.GetChildren().Count());
        return actor;
    }

    private void HandleResult(RoutedTelemetryResult message)
    {
        if (message.Result.Disposition == TelemetryMessageDisposition.Accepted)
        {
            _latestByClient[message.Snapshot.Client] = message.Snapshot with
            {
                Disks = message.Snapshot.Disks.ToArray(),
                Networks = message.Snapshot.Networks.ToArray()
            };
            NetRatelAkkaTelemetry.TelemetryAcceptedSnapshot();
            _acceptedCount = IncrementSaturating(_acceptedCount);
            _lastUpdateTimestamp = message.AcceptedAtUtc;
        }
        else
        {
            NetRatelAkkaTelemetry.TelemetryRejectedSnapshot();
            _rejectedCount = IncrementSaturating(_rejectedCount);
        }

        message.ReplyTo.Tell(message.Result);
    }

    private ClientTelemetryRouteStatus CreateStatus()
    {
        var clients = Context.GetChildren().Count();
        NetRatelAkkaTelemetry.SetTelemetryActiveClients(clients);
        return new(
            clients,
            _acceptedCount,
            _rejectedCount,
            _lastUpdateTimestamp,
            _startedAtUtc,
            "local-shadow",
            "unavailable");
    }

    private static ulong IncrementSaturating(ulong value) =>
        value == ulong.MaxValue ? value : value + 1;

    private static string CreateActorName(string entityId)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(entityId));
        return $"telemetry-{Convert.ToHexString(digest).ToLowerInvariant()}";
    }
}

internal sealed record RoutedTelemetryRecord(
    RecordTelemetrySnapshot Message,
    IActorRef ReplyTo);

internal sealed record RoutedTelemetryResult(
    TelemetrySnapshot Snapshot,
    TelemetryMessageResult Result,
    IActorRef ReplyTo,
    DateTimeOffset? AcceptedAtUtc);
