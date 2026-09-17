using NetRatel.Shared.Contracts;
using NetRatel.Shared.Contracts.RemoteSupport;

namespace NetRatel.Web.Services.RemoteSupport;

public sealed record RemoteSupportClientActionPolicy(
    bool ToolsAvailable,
    bool ConsoleLoginAvailable,
    bool LegacyAutomaticAvailable,
    bool SessionInventoryAvailable,
    bool ExplicitUserAssistAvailable,
    bool TargetPreflightAvailable,
    int ProtocolRevision,
    string CapabilitySource)
{
    public static RemoteSupportClientActionPolicy For(ClientDto client)
    {
        var capabilities = RemoteSupportCapabilityResolver.Resolve(client.ClientInfoJson);
        var toolsAvailable = client.Status.Online &&
            client.Enabled &&
            string.Equals(client.DetectedOs, "Windows", StringComparison.OrdinalIgnoreCase) &&
            capabilities.FallbackDesktopSupported;

        return new RemoteSupportClientActionPolicy(
            toolsAvailable,
            toolsAvailable && capabilities.ConsoleLoginSupported,
            toolsAvailable && capabilities.LegacyAutomaticSupported,
            toolsAvailable && capabilities.SessionInventorySupported,
            toolsAvailable && capabilities.ExplicitUserAssistSupported,
            toolsAvailable && capabilities.TargetPreflightSupported,
            capabilities.ProtocolRevision,
            capabilities.Source);
    }
}
