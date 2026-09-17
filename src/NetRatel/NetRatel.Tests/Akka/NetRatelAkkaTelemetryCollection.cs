using Xunit;

namespace NetRatel.Tests.Akka;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class NetRatelAkkaTelemetryCollection
{
    public const string Name = "NetRatel Akka telemetry";
}
