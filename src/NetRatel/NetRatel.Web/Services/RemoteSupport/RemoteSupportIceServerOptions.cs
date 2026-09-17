using NetRatel.Shared.Contracts.RemoteSupport;

namespace NetRatel.Web.Services.RemoteSupport;

public sealed class RemoteSupportIceServerOptions
{
    public List<RemoteSupportIceServerDto> IceServers { get; set; } = new();
    public RemoteSupportHandoverOptions Handover { get; set; } = new();
}

public sealed class RemoteSupportHandoverOptions
{
    public bool Enabled { get; set; }
    public bool AutoReconnectEnabled { get; set; }
    public bool ProviderGenerationEnabled { get; set; }
    public bool CoordinatorEnabled { get; set; }

    public bool AnyEnabled =>
        Enabled ||
        AutoReconnectEnabled ||
        ProviderGenerationEnabled ||
        CoordinatorEnabled;
}
