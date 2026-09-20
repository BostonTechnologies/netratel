namespace NetRatel.API.Bootstrap;

/// <summary>The only lifecycle states that may control whether the operational runtime starts.</summary>
public enum BootstrapState
{
    Unconfigured,
    Configuring,
    Ready,
    RecoveryRequired
}
