using System.ComponentModel.DataAnnotations;

namespace NetRatel.Akka.Configuration;

public sealed class NetRatelAkkaMigrationOptions
{
    public const string SectionName = "NetRatelAkkaMigration";

    public bool Enabled { get; set; }

    public bool PresenceEnabled { get; set; }

    public bool GatewayEnabled { get; set; }

    /// <summary>
    /// Enables PostgreSQL-backed update offers on the authoritative presence
    /// acknowledgements. Disabled by default for rolling-upgrade safety.
    /// </summary>
    public bool ClientUpdatesEnabled { get; set; }

    public bool ControlGatewayEnabled { get; set; }

    public bool FileGatewayEnabled { get; set; }

    /// <summary>
    /// Enables the additive, separate log stream. Raw log records are never
    /// routed through the client actor or persisted by the authority layer.
    /// </summary>
    public bool LogGatewayEnabled { get; set; }

    public bool RemoteSupportGatewayEnabled { get; set; }

    /// <summary>
    /// Enables the opt-in V2 WTS inventory and exact-target preparation stream.
    /// It remains independent of browser/media cutover and is safe to disable.
    /// </summary>
    public bool RemoteSupportV2InventoryEnabled { get; set; }

    /// <summary>
    /// Enables the durable, actor-owned V2 lifecycle authority. It is separate
    /// from the existing in-memory signalling bridge and never enables media.
    /// </summary>
    public bool RemoteSupportV2LifecycleAuthorityEnabled { get; set; }

    /// <summary>
    /// Routes V2 lifecycle requests through Akka Cluster Sharding and enables
    /// replica-local, non-durable edge registrations. This is deliberately
    /// separate from the lifecycle flag so rollout and rollback remain
    /// explicit.
    /// </summary>
    public bool RemoteSupportV2ReplicaSafeEdgeEnabled { get; set; }

    /// <summary>
    /// Enables the opt-in RS2-4 direct-media boundary. It requires the V2
    /// lifecycle and replica-safe edge; it never enables the legacy gateway.
    /// </summary>
    public bool RemoteSupportV2MediaEnabled { get; set; }

    /// <summary>
    /// Explicit emergency rollback for the pre-V2 in-memory signalling broker.
    /// It is off by default once the replica-safe V2 edge is enabled.
    /// </summary>
    public bool RemoteSupportLegacyGatewayRollbackEnabled { get; set; }

    /// <summary>
    /// Re-registers a live agent edge before a transient connection becomes
    /// stale. It is configuration rather than a protocol value so test and
    /// deployment environments can use bounded, explicit intervals.
    /// </summary>
    public TimeSpan RemoteSupportV2AgentEdgeRenewalInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Internal Akka remoting topology. These addresses are deployment-only
    /// and are never exposed in a NetRatel browser or agent contract.
    /// </summary>
    public RemoteSupportV2ClusterOptions RemoteSupportV2Cluster { get; set; } = new();

    /// <summary>
    /// Enables mapped gateway read models for the existing primary client-card
    /// experience. Disabled by default until a tenant is explicitly enabled.
    /// </summary>
    public bool PrimaryCardGatewayReadsEnabled { get; set; }

    /// <summary>
    /// Allows non-terminal primary-card actions to route through Agent-ID V2
    /// gateway APIs. This never enables a route without the read cutover.
    /// </summary>
    public bool PrimaryCardGatewayActionsEnabled { get; set; }

    /// <summary>
    /// Enables the additive terminal gRPC transport. It is separate from the
    /// legacy terminal tunnel and does not itself change primary-card routing.
    /// </summary>
    public bool TerminalGatewayEnabled { get; set; }

    /// <summary>
    /// Makes the primary-card terminal launch use the additive terminal
    /// gateway. It remains disabled until the terminal transport is proven.
    /// </summary>
    public bool TerminalGatewayPrimaryCardEnabled { get; set; }

    /// <summary>
    /// Enables the gateway-backed primary presence read model. When disabled,
    /// the read model is unavailable; it never falls back to a retired source.
    /// </summary>
    public bool PresenceReadModelEnabled { get; set; }

    [Range(1, 65535)]
    public int GatewayGrpcPort { get; set; } = 9223;

    public bool TelemetryShadowEnabled { get; set; }

    public bool CommandShadowEnabled { get; set; }

    public bool CommandPersistenceEnabled { get; set; }

    public bool JobShadowEnabled { get; set; }

    public bool TerminalShadowEnabled { get; set; }

    public bool SignalRShadowEnabled { get; set; }

    public bool SignalRShadowLocalCanaryEnabled { get; set; }

    public bool PresenceAuthorityEnabled { get; set; }

    public bool PingAuthorityEnabled { get; set; }

    public bool TelemetryAuthorityEnabled { get; set; }

    public bool FileBrowseAuthorityEnabled { get; set; }

    /// <summary>Requires the log stream to be presence-fenced by Akka authority.</summary>
    public bool LogAuthorityEnabled { get; set; }

    public bool RemoteSupportAuthorityEnabled { get; set; }

    public bool CommandAuthorityEnabled { get; set; }

    public bool JobAuthorityEnabled { get; set; }

    /// <summary>
    /// Enables the fenced terminal lifecycle authority after both the agent
    /// gRPC leg and operator bridge are available. This remains independent
    /// from the additive terminal transport switch so it is rollback-safe.
    /// </summary>
    public bool TerminalAuthorityEnabled { get; set; }

    public bool SignalRAuthorityEnabled { get; set; }

    [Required]
    public string ActorSystemName { get; set; } = "NetRatel";

    [Required]
    public string ProtocolVersion { get; set; } = "1.0";

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

    public int HeartbeatTimeoutSeconds =>
        checked((HeartbeatIntervalSeconds * MissedHeartbeatLimit) + HeartbeatGraceSeconds);

    public TimeSpan HeartbeatTimeout => TimeSpan.FromSeconds(HeartbeatTimeoutSeconds);

    public TimeSpan AskTimeout => TimeSpan.FromSeconds(AskTimeoutSeconds);

    public bool IsPresenceAuthorityActive =>
        Enabled &&
        PresenceAuthorityEnabled;

    public bool IsClientUpdateAuthorityActive =>
        IsPresenceAuthorityActive && GatewayEnabled && ClientUpdatesEnabled;

    public bool IsPingAuthorityActive =>
        IsPresenceAuthorityActive &&
        ControlGatewayEnabled &&
        PingAuthorityEnabled;

    public bool IsTelemetryAuthorityActive =>
        IsPresenceAuthorityActive &&
        TelemetryShadowEnabled &&
        TelemetryAuthorityEnabled;

    public bool IsFileBrowseAuthorityActive =>
        IsPresenceAuthorityActive && FileGatewayEnabled && FileBrowseAuthorityEnabled;

    public bool IsLogAuthorityActive =>
        IsPresenceAuthorityActive && LogGatewayEnabled && LogAuthorityEnabled;

    public bool IsRemoteSupportAuthorityActive =>
        IsPresenceAuthorityActive && RemoteSupportGatewayEnabled && RemoteSupportAuthorityEnabled;

    public bool IsRemoteSupportV2InventoryActive =>
        IsRemoteSupportAuthorityActive && RemoteSupportV2InventoryEnabled;

    public bool IsRemoteSupportV2LifecycleAuthorityActive =>
        IsRemoteSupportAuthorityActive && RemoteSupportV2LifecycleAuthorityEnabled;

    public bool IsRemoteSupportV2ReplicaSafeEdgeActive =>
        IsRemoteSupportV2LifecycleAuthorityActive && RemoteSupportV2ReplicaSafeEdgeEnabled;

    public bool IsRemoteSupportV2MediaActive =>
        IsRemoteSupportV2ReplicaSafeEdgeActive && RemoteSupportV2InventoryEnabled && RemoteSupportV2MediaEnabled;

    public bool IsLegacyRemoteSupportGatewayActive =>
        IsRemoteSupportAuthorityActive &&
        (!IsRemoteSupportV2ReplicaSafeEdgeActive || RemoteSupportLegacyGatewayRollbackEnabled);

    public bool IsCommandAuthorityActive =>
        IsPresenceAuthorityActive && CommandShadowEnabled && CommandAuthorityEnabled;

    /// <summary>
    /// Enables the fenced, durable AgentJobGateway lifecycle. Job definitions
    /// remain in PostgreSQL and the gateway replaces the retired legacy
    /// execution transport when explicitly enabled.
    /// </summary>
    public bool IsJobAuthorityActive =>
        IsPresenceAuthorityActive && JobShadowEnabled && JobAuthorityEnabled;

    public bool IsTerminalAuthorityActive =>
        IsPresenceAuthorityActive && TerminalGatewayEnabled && TerminalAuthorityEnabled;

    public bool IsSignalRAuthorityActive =>
        IsPresenceAuthorityActive && SignalRShadowEnabled && SignalRShadowLocalCanaryEnabled && SignalRAuthorityEnabled;

    public bool IsPrimaryCardGatewayReadActive =>
        IsPresenceAuthorityActive && PrimaryCardGatewayReadsEnabled;

    public bool IsPrimaryCardGatewayActionActive =>
        IsPrimaryCardGatewayReadActive && PrimaryCardGatewayActionsEnabled;

    public bool IsPrimaryCardTerminalGatewayActive =>
        IsPrimaryCardGatewayActionActive && TerminalGatewayEnabled && TerminalGatewayPrimaryCardEnabled;

    public bool IsGatewayPresenceReadModelEnabled =>
        Enabled &&
        PresenceEnabled &&
        GatewayEnabled &&
        PresenceReadModelEnabled;

    public string PresenceAuthority => IsPresenceAuthorityActive
        ? "akka"
        : "unavailable";

    public string OperationalPresenceAuthority => IsPresenceAuthorityActive
        ? "akka"
        : "unavailable";

    public string AuthorityModeName => IsPresenceAuthorityActive
        ? "akka"
        : "unavailable";
}

public sealed class RemoteSupportV2ClusterOptions
{
    [Required]
    public string HostName { get; set; } = "127.0.0.1";

    [Range(0, 65535)]
    public int Port { get; set; }

    [Required]
    public string Role { get; set; } = "remote-support-v2";

    /// <summary>Akka addresses used only for internal cluster membership.</summary>
    public string[] SeedNodes { get; set; } = [];

    /// <summary>
    /// Allows one-node development and test clusters. Production deployment
    /// must set this false and provide normal deployment-managed seed nodes.
    /// </summary>
    public bool AllowSingleNode { get; set; } = true;
}
