using Microsoft.Extensions.Options;
using Akka.Actor;

namespace NetRatel.Akka.Configuration;

public sealed class NetRatelAkkaOptionsValidator : IValidateOptions<NetRatelAkkaOptions>
{
    public ValidateOptionsResult Validate(string? name, NetRatelAkkaOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.Cluster is null)
        {
            return ValidateOptionsResult.Fail("NetRatelAkka:Cluster is required.");
        }

        if (string.IsNullOrWhiteSpace(options.ActorSystemName))
        {
            return ValidateOptionsResult.Fail("NetRatelAkka:ActorSystemName is required.");
        }

        var cluster = options.Cluster;
        if (string.IsNullOrWhiteSpace(cluster.HostName) || string.IsNullOrWhiteSpace(cluster.Role))
        {
            return ValidateOptionsResult.Fail("NetRatelAkka:Cluster requires a HostName and Role.");
        }

        if (cluster.Port is < 0 or > 65535)
        {
            return ValidateOptionsResult.Fail("NetRatelAkka:Cluster:Port must be between 0 and 65535.");
        }

        if (cluster.SeedNodes is null)
        {
            return ValidateOptionsResult.Fail("NetRatelAkka:Cluster:SeedNodes cannot be null.");
        }

        if (!cluster.IsClustered && cluster.SeedNodes.Length != 0)
        {
            return ValidateOptionsResult.Fail("NetRatelAkka:Cluster:SeedNodes requires a nonzero Port.");
        }

        if (cluster.IsClustered && cluster.SeedNodes.Length == 0)
        {
            return ValidateOptionsResult.Fail("NetRatelAkka:Cluster requires at least one SeedNodes entry when Port is nonzero.");
        }

        foreach (var seedNode in cluster.SeedNodes)
        {
            if (!TryParseSeedNode(seedNode, options.ActorSystemName, out var failure))
            {
                return ValidateOptionsResult.Fail(failure);
            }
        }

        return ValidateOptionsResult.Success;
    }

    private static bool TryParseSeedNode(string? seedNode, string actorSystemName, out string failure)
    {
        const string invalidAddress =
            "NetRatelAkka:Cluster:SeedNodes must contain Akka actor-system addresses with a host, port, and matching actor-system name.";

        if (string.IsNullOrWhiteSpace(seedNode) ||
            !Uri.TryCreate(seedNode, UriKind.Absolute, out var uri) ||
            (uri.Scheme is not "akka" and not "akka.tcp") ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            uri.AbsolutePath is not ("" or "/") ||
            !Address.TryParse(seedNode, out var address))
        {
            failure = invalidAddress;
            return false;
        }

        if (string.IsNullOrWhiteSpace(address.Host) ||
            address.Port is not (>= 1 and <= 65535))
        {
            failure = invalidAddress;
            return false;
        }

        if (!string.Equals(address.System, actorSystemName, StringComparison.Ordinal))
        {
            failure = $"NetRatelAkka:Cluster:SeedNodes must target actor system '{actorSystemName}'.";
            return false;
        }

        failure = string.Empty;
        return true;
    }
}
