using Microsoft.Extensions.Options;

namespace NetRatel.Akka.Configuration;

public sealed class NetRatelAkkaMigrationOptionsValidator : IValidateOptions<NetRatelAkkaMigrationOptions>
{
    public ValidateOptionsResult Validate(string? name, NetRatelAkkaMigrationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!options.Enabled && HasMigrationSurface(options))
        {
            return ValidateOptionsResult.Fail(
                "NetRatelAkkaMigration:Enabled must be true before a migration surface can be enabled.");
        }

        if (options.PresenceAuthorityEnabled && (!options.PresenceEnabled || !options.GatewayEnabled))
        {
            return ValidateOptionsResult.Fail(
                "NetRatelAkkaMigration:PresenceAuthorityEnabled requires PresenceEnabled and GatewayEnabled.");
        }

        if (options.PingAuthorityEnabled &&
            (!options.PresenceAuthorityEnabled || !options.PresenceEnabled || !options.GatewayEnabled || !options.ControlGatewayEnabled))
        {
            return ValidateOptionsResult.Fail(
                "NetRatelAkkaMigration:PingAuthorityEnabled requires PresenceEnabled, GatewayEnabled, ControlGatewayEnabled, and active PresenceAuthorityEnabled.");
        }

        if (options.TelemetryAuthorityEnabled &&
            (!options.PresenceAuthorityEnabled || !options.PresenceEnabled || !options.GatewayEnabled || !options.TelemetryShadowEnabled))
        {
            return ValidateOptionsResult.Fail(
                "NetRatelAkkaMigration:TelemetryAuthorityEnabled requires PresenceEnabled, GatewayEnabled, TelemetryShadowEnabled, and active PresenceAuthorityEnabled.");
        }

        if (options.FileBrowseAuthorityEnabled && (!options.PresenceAuthorityEnabled || !options.PresenceEnabled || !options.GatewayEnabled || !options.FileGatewayEnabled))
            return ValidateOptionsResult.Fail("NetRatelAkkaMigration:FileBrowseAuthorityEnabled requires active presence authority, PresenceEnabled, GatewayEnabled, and FileGatewayEnabled.");

        if (options.LogAuthorityEnabled && (!options.PresenceAuthorityEnabled || !options.PresenceEnabled || !options.GatewayEnabled || !options.LogGatewayEnabled))
            return ValidateOptionsResult.Fail("NetRatelAkkaMigration:LogAuthorityEnabled requires active presence authority, PresenceEnabled, GatewayEnabled, and LogGatewayEnabled.");

        if (options.RemoteSupportAuthorityEnabled && (!options.PresenceAuthorityEnabled || !options.PresenceEnabled || !options.GatewayEnabled || !options.RemoteSupportGatewayEnabled))
            return ValidateOptionsResult.Fail("NetRatelAkkaMigration:RemoteSupportAuthorityEnabled requires active presence authority, PresenceEnabled, GatewayEnabled, and RemoteSupportGatewayEnabled.");

        if (options.RemoteSupportV2LifecycleAuthorityEnabled && !options.IsRemoteSupportAuthorityActive)
            return ValidateOptionsResult.Fail("NetRatelAkkaMigration:RemoteSupportV2LifecycleAuthorityEnabled requires active RemoteSupportAuthorityEnabled.");

        if (options.RemoteSupportV2ReplicaSafeEdgeEnabled && !options.IsRemoteSupportV2LifecycleAuthorityActive)
            return ValidateOptionsResult.Fail("NetRatelAkkaMigration:RemoteSupportV2ReplicaSafeEdgeEnabled requires active RemoteSupportV2LifecycleAuthorityEnabled.");

        if (options.RemoteSupportV2MediaEnabled && !options.IsRemoteSupportV2ReplicaSafeEdgeActive)
            return ValidateOptionsResult.Fail("NetRatelAkkaMigration:RemoteSupportV2MediaEnabled requires the active replica-safe V2 edge.");

        if (options.RemoteSupportV2MediaEnabled && !options.RemoteSupportV2InventoryEnabled)
            return ValidateOptionsResult.Fail("NetRatelAkkaMigration:RemoteSupportV2MediaEnabled requires RemoteSupportV2InventoryEnabled for exact-target preparation.");

        if (options.RemoteSupportV2ReplicaSafeEdgeEnabled && options.RemoteSupportV2AgentEdgeRenewalInterval < TimeSpan.FromSeconds(1))
            return ValidateOptionsResult.Fail("NetRatelAkkaMigration:RemoteSupportV2AgentEdgeRenewalInterval must be at least one second when replica-safe edge routing is enabled.");

        if (options.RemoteSupportV2ReplicaSafeEdgeEnabled &&
            (string.IsNullOrWhiteSpace(options.RemoteSupportV2Cluster.HostName) ||
             string.IsNullOrWhiteSpace(options.RemoteSupportV2Cluster.Role)))
        {
            return ValidateOptionsResult.Fail("NetRatelAkkaMigration:RemoteSupportV2Cluster requires HostName and Role when replica-safe edge routing is enabled.");
        }

        if (options.RemoteSupportV2ReplicaSafeEdgeEnabled &&
            !options.RemoteSupportV2Cluster.AllowSingleNode &&
            options.RemoteSupportV2Cluster.SeedNodes.Length == 0)
        {
            return ValidateOptionsResult.Fail("NetRatelAkkaMigration:RemoteSupportV2Cluster requires deployment-managed SeedNodes when AllowSingleNode is false.");
        }

        if (options.CommandAuthorityEnabled &&
            (!options.PresenceAuthorityEnabled || !options.PresenceEnabled || !options.GatewayEnabled || !options.CommandShadowEnabled))
        {
            return ValidateOptionsResult.Fail(
                "NetRatelAkkaMigration:CommandAuthorityEnabled requires active presence authority, PresenceEnabled, GatewayEnabled, and CommandShadowEnabled.");
        }

        if (options.JobAuthorityEnabled &&
            (!options.PresenceAuthorityEnabled || !options.PresenceEnabled || !options.GatewayEnabled || !options.JobShadowEnabled))
        {
            return ValidateOptionsResult.Fail(
                "NetRatelAkkaMigration:JobAuthorityEnabled requires active presence authority, PresenceEnabled, GatewayEnabled, and JobShadowEnabled.");
        }

        if (options.TerminalAuthorityEnabled &&
            (!options.PresenceAuthorityEnabled || !options.PresenceEnabled || !options.GatewayEnabled || !options.TerminalGatewayEnabled))
        {
            return ValidateOptionsResult.Fail(
                "NetRatelAkkaMigration:TerminalAuthorityEnabled requires active presence authority, PresenceEnabled, GatewayEnabled, and TerminalGatewayEnabled.");
        }

        if (options.SignalRAuthorityEnabled &&
            (!options.PresenceAuthorityEnabled || !options.PresenceEnabled || !options.GatewayEnabled ||
             !options.SignalRShadowEnabled || !options.SignalRShadowLocalCanaryEnabled))
        {
            return ValidateOptionsResult.Fail(
                "NetRatelAkkaMigration:SignalRAuthorityEnabled requires active presence authority, PresenceEnabled, GatewayEnabled, SignalRShadowEnabled, and SignalRShadowLocalCanaryEnabled.");
        }

        if (options.PrimaryCardGatewayReadsEnabled && !options.IsPresenceAuthorityActive)
        {
            return ValidateOptionsResult.Fail(
                "NetRatelAkkaMigration:PrimaryCardGatewayReadsEnabled requires active DEV presence authority.");
        }

        if (options.PrimaryCardGatewayActionsEnabled && !options.PrimaryCardGatewayReadsEnabled)
        {
            return ValidateOptionsResult.Fail(
                "NetRatelAkkaMigration:PrimaryCardGatewayActionsEnabled requires PrimaryCardGatewayReadsEnabled.");
        }

        if (options.TerminalGatewayEnabled && (!options.GatewayEnabled || !options.PresenceEnabled))
        {
            return ValidateOptionsResult.Fail(
                "NetRatelAkkaMigration:TerminalGatewayEnabled requires PresenceEnabled and GatewayEnabled.");
        }

        if (options.LogGatewayEnabled && (!options.GatewayEnabled || !options.PresenceEnabled))
        {
            return ValidateOptionsResult.Fail(
                "NetRatelAkkaMigration:LogGatewayEnabled requires PresenceEnabled and GatewayEnabled.");
        }

        if (options.TerminalGatewayPrimaryCardEnabled &&
            (!options.TerminalGatewayEnabled || !options.PrimaryCardGatewayActionsEnabled))
        {
            return ValidateOptionsResult.Fail(
                "NetRatelAkkaMigration:TerminalGatewayPrimaryCardEnabled requires TerminalGatewayEnabled and PrimaryCardGatewayActionsEnabled.");
        }

        if (options.PresenceReadModelEnabled &&
            (!options.PresenceEnabled || !options.GatewayEnabled))
        {
            return ValidateOptionsResult.Fail(
                "NetRatelAkkaMigration:PresenceReadModelEnabled requires PresenceEnabled and GatewayEnabled.");
        }

        if (options.GatewayEnabled && !options.PresenceEnabled)
        {
            return ValidateOptionsResult.Fail(
                "NetRatelAkkaMigration:GatewayEnabled requires PresenceEnabled.");
        }

        if (options.ControlGatewayEnabled && !options.GatewayEnabled)
        {
            return ValidateOptionsResult.Fail(
                "NetRatelAkkaMigration:ControlGatewayEnabled requires GatewayEnabled.");
        }

        if (options.TelemetryShadowEnabled && !options.GatewayEnabled)
        {
            return ValidateOptionsResult.Fail(
                "NetRatelAkkaMigration:TelemetryShadowEnabled requires GatewayEnabled.");
        }

        if (options.CommandShadowEnabled && !options.GatewayEnabled)
        {
            return ValidateOptionsResult.Fail(
                "NetRatelAkkaMigration:CommandShadowEnabled requires GatewayEnabled.");
        }

        if (options.CommandPersistenceEnabled && !options.CommandShadowEnabled)
        {
            return ValidateOptionsResult.Fail(
                "NetRatelAkkaMigration:CommandPersistenceEnabled requires CommandShadowEnabled.");
        }

        if (options.SignalRShadowLocalCanaryEnabled && !options.SignalRShadowEnabled)
        {
            return ValidateOptionsResult.Fail(
                "NetRatelAkkaMigration:SignalRShadowLocalCanaryEnabled requires SignalRShadowEnabled.");
        }

        if (options.SignalRShadowEnabled && !options.SignalRShadowLocalCanaryEnabled)
        {
            return ValidateOptionsResult.Fail(
                "NetRatelAkkaMigration:SignalRShadowEnabled requires SignalRShadowLocalCanaryEnabled because Phase 9 is a local canary only.");
        }

        return ValidateOptionsResult.Success;
    }

    private static bool HasAuthorityFlag(NetRatelAkkaMigrationOptions options) =>
        options.PresenceAuthorityEnabled |
        options.PingAuthorityEnabled |
        options.TelemetryAuthorityEnabled |
        options.FileBrowseAuthorityEnabled |
        options.LogAuthorityEnabled |
        options.RemoteSupportAuthorityEnabled |
        options.CommandAuthorityEnabled |
        options.JobAuthorityEnabled |
        options.TerminalAuthorityEnabled |
        options.SignalRAuthorityEnabled;

    private static bool HasMigrationSurface(NetRatelAkkaMigrationOptions options) =>
        options.PresenceEnabled |
        options.GatewayEnabled |
        options.ControlGatewayEnabled |
        options.FileGatewayEnabled |
        options.LogGatewayEnabled |
        options.RemoteSupportGatewayEnabled |
        options.RemoteSupportV2InventoryEnabled |
        options.RemoteSupportV2LifecycleAuthorityEnabled |
        options.RemoteSupportV2ReplicaSafeEdgeEnabled |
        options.RemoteSupportV2MediaEnabled |
        options.RemoteSupportLegacyGatewayRollbackEnabled |
        options.PrimaryCardGatewayReadsEnabled |
        options.PrimaryCardGatewayActionsEnabled |
        options.TerminalGatewayEnabled |
        options.TerminalGatewayPrimaryCardEnabled |
        options.TelemetryShadowEnabled |
        options.CommandShadowEnabled |
        options.CommandPersistenceEnabled |
        options.JobShadowEnabled |
        options.TerminalShadowEnabled |
        options.SignalRShadowEnabled |
        options.SignalRShadowLocalCanaryEnabled |
        options.PresenceReadModelEnabled |
        HasAuthorityFlag(options);
}
