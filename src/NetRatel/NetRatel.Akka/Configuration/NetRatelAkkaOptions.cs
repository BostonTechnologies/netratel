using System.ComponentModel.DataAnnotations;

namespace NetRatel.Akka.Configuration;

/// <summary>
/// Runtime tuning for the Akka-backed control plane. Akka is the normal
/// runtime after bootstrap completes; this section contains no feature or
/// authority selectors.
/// </summary>
public sealed class NetRatelAkkaOptions
{
    public const string SectionName = "NetRatelAkka";

    /// <summary>The agent gateway protocol version is a published wire token.</summary>
    public const string ProtocolVersion = "1.0";

    [Required]
    public string ActorSystemName { get; set; } = "NetRatel";

    [Range(1, 65535)]
    public int GatewayGrpcPort { get; set; } = 9223;

    /// <summary>Bounds authenticated lookup and the initial gateway hello before a session is admitted.</summary>
    [Range(1, 300)]
    public int GatewayAdmissionTimeoutSeconds { get; set; } = 30;

    [Range(5, 300)]
    public int HeartbeatIntervalSeconds { get; set; } = 15;

    [Range(1, 10)]
    public int MissedHeartbeatLimit { get; set; } = 3;

    [Range(0, 60)]
    public int HeartbeatGraceSeconds { get; set; } = 5;

    [Range(1, 30)]
    public int AskTimeoutSeconds { get; set; } = 3;

    [Range(1024, 1_048_576)]
    public int MaxInboundMessageBytes { get; set; } = 16 * 1024;

    [Range(1024, 1_048_576)]
    public int MaxOutboundMessageBytes { get; set; } = 16 * 1024;

    [Range(1, 256)]
    public int MaxTelemetryScopesPerFrame { get; set; } = 64;

    [Range(1, 300)]
    public int RemoteSupportAgentEdgeRenewalSeconds { get; set; } = 10;

    /// <summary>
    /// Optional Akka cluster transport settings. Port zero keeps the actor
    /// system local; a nonzero port enables remoting and cluster sharding.
    /// Seed nodes and the port are deployment topology, not feature switches.
    /// </summary>
    public NetRatelAkkaClusterOptions Cluster { get; set; } = new();

    public int HeartbeatTimeoutSeconds =>
        checked((HeartbeatIntervalSeconds * MissedHeartbeatLimit) + HeartbeatGraceSeconds);

    public TimeSpan HeartbeatTimeout => TimeSpan.FromSeconds(HeartbeatTimeoutSeconds);

    public TimeSpan AskTimeout => TimeSpan.FromSeconds(AskTimeoutSeconds);

    public TimeSpan RemoteSupportAgentEdgeRenewalInterval =>
        TimeSpan.FromSeconds(RemoteSupportAgentEdgeRenewalSeconds);
}

public sealed class NetRatelAkkaClusterOptions
{
    [Required]
    public string HostName { get; set; } = "127.0.0.1";

    [Range(0, 65535)]
    public int Port { get; set; }

    [Required]
    public string Role { get; set; } = "remote-support-v2";

    /// <summary>Akka addresses used only for internal cluster membership.</summary>
    public string[] SeedNodes { get; set; } = [];

    public bool IsClustered => Port > 0;
}
