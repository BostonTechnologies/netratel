using Akka.Actor;
using NetRatel.Application.Presence;
using NetRatel.Application.Telemetry;

namespace NetRatel.Akka.Telemetry;

/// <summary>
/// Owns the latest non-durable telemetry snapshot for one authenticated client.
/// The authenticated gateway supplies the snapshot; this actor intentionally
/// does not claim durable telemetry authority.
/// </summary>
public sealed class TelemetryActor : ReceiveActor
{
    private readonly ClientKey _client;
    private TelemetrySnapshot? _latest;

    public TelemetryActor(ClientKey client)
    {
        if (!client.IsValid)
        {
            throw new ArgumentException("A positive tenant ID and non-empty agent ID are required.", nameof(client));
        }

        _client = client;
        Receive<RecordTelemetrySnapshot>(message => Sender.Tell(Record(message)));
        Receive<RoutedTelemetryRecord>(message =>
        {
            var result = Record(message.Message);
            Context.Parent.Tell(new RoutedTelemetryResult(
                message.Message.Snapshot,
                result,
                message.ReplyTo,
                result.Disposition == TelemetryMessageDisposition.Accepted
                    ? message.Message.Snapshot.ReceivedAtUtc
                    : null));
        });
        Receive<GetClientTelemetry>(message =>
        {
            EnsureClient(message.Client);
            Sender.Tell(new ClientTelemetryState(_client, _latest));
        });
    }

    public static Props Props(ClientKey client) =>
        global::Akka.Actor.Props.Create(() => new TelemetryActor(client));

    private TelemetryMessageResult Record(RecordTelemetrySnapshot message)
    {
        EnsureClient(message.Client);
        var snapshot = message.Snapshot;
        var disposition = GetDisposition(snapshot);
        if (disposition == TelemetryMessageDisposition.Accepted)
        {
            _latest = snapshot with
            {
                Disks = snapshot.Disks.ToArray(),
                Networks = snapshot.Networks.ToArray()
            };
        }

        return new TelemetryMessageResult(
            _client,
            disposition,
            _latest?.Sequence ?? 0);
    }

    private TelemetryMessageDisposition GetDisposition(TelemetrySnapshot snapshot)
    {
        if (_latest is null || snapshot.ConnectionEpoch > _latest.ConnectionEpoch)
        {
            return TelemetryMessageDisposition.Accepted;
        }

        if (snapshot.ConnectionEpoch < _latest.ConnectionEpoch)
        {
            return TelemetryMessageDisposition.StaleConnectionEpoch;
        }

        if (snapshot.Sequence == _latest.Sequence)
        {
            return TelemetryMessageDisposition.Duplicate;
        }

        return snapshot.Sequence < _latest.Sequence
            ? TelemetryMessageDisposition.StaleSequence
            : TelemetryMessageDisposition.Accepted;
    }

    private void EnsureClient(ClientKey client)
    {
        if (client != _client)
        {
            throw new InvalidOperationException($"Telemetry message for {client} reached actor for {_client}.");
        }
    }
}
