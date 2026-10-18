using System.Text.Json.Nodes;

namespace NetRatel.Mcp.Core;

public static partial class NetRatelMcpToolDefinitions
{
    // Use the same transport/environment selection and input builder as the
    // published SDK definitions. Schema inspection never broadens availability.
    internal static IReadOnlyList<NetRatelMcpOperationDescriptor> SelectedOperations(
        NetRatelMcpToolDescriptor tool, NetRatelMcpHostContext host)
        => tool.Operations.Where(operation => host.Transport == NetRatelMcpTransport.StreamableHttp
            ? tool.AvailableOverHttp && IsAvailableOverHttp(operation, host.Target.Instance, host.OperatorSurfaceEnabled, host)
            : operation.IsAvailableIn(host.Target.Instance)).ToArray();

    internal static bool UsesOperatorSchema(NetRatelMcpHostContext host)
        => host.Transport == NetRatelMcpTransport.StreamableHttp
            ? host.OperatorSurfaceEnabled : string.Equals(host.Target.Instance, "prod", StringComparison.Ordinal);

    internal static JsonObject PublishedInputSchema(NetRatelMcpToolDescriptor tool, NetRatelMcpHostContext host)
        => InputSchema(tool.Name, SelectedOperations(tool, host), host.Target.Instance, UsesOperatorSchema(host));
}
