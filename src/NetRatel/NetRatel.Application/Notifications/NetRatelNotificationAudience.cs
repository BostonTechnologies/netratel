namespace NetRatel.Application.Notifications;

/// <summary>Request-scoped restriction for operators who may read only their own setup failures.</summary>
public sealed class NetRatelNotificationAudience
{
    public const string PairingFailure = "DomainEvent.Pairing.ConnectionFailed";
    public bool PairingFailuresOnly { get; set; }
}
