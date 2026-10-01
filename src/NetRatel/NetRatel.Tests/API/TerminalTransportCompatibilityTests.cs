using FluentAssertions;
using NetRatel.Shared.Contracts.Terminals;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class TerminalTransportCompatibilityTests
{
    [Fact]
    public void HistoricalSpacetimeTransportValue_RemainsReadableAtItsPublishedOrdinal()
    {
        ((int)TerminalTransportKind.Spacetime).Should().Be(2);
        Enum.GetName((TerminalTransportKind)2).Should().Be(nameof(TerminalTransportKind.Spacetime));
    }
}
