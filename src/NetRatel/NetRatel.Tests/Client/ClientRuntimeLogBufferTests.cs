using FluentAssertions;
using NetRatel.Client.Service.Logging;
using Xunit;

namespace NetRatel.Tests.Client;

public sealed class ClientRuntimeLogBufferTests
{
    [Fact]
    public void Capture_PreservesRepeatedMessagesBySequenceAndExtractsTheActualPrefix()
    {
        var marker = $"retrying connection {Guid.NewGuid():N}";

        ClientRuntimeLogBuffer.Capture($"[Gateway] {marker}", DateTimeOffset.UtcNow);
        ClientRuntimeLogBuffer.Capture($"[Gateway] {marker}", DateTimeOffset.UtcNow.AddMilliseconds(1));

        var records = ClientRuntimeLogBuffer.Snapshot().Records.Where(record => record.Message == marker).ToArray();
        records.Length.Should().BeGreaterThanOrEqualTo(2);
        records.TakeLast(2).Select(record => record.Message).Should().Equal(marker, marker);
        records.TakeLast(2).Select(record => record.Sequence).Should().BeInAscendingOrder();
        records.TakeLast(2).Should().OnlyContain(record => record.Prefix == "Gateway" && record.Category == "Gateway");
    }

    [Fact]
    public void Capture_TruncatesAnOversizedRecordBeforeItCanReachAGatewayFrame()
    {
        var record = ClientRuntimeLogBuffer.Capture(new string('x', 16 * 1024), DateTimeOffset.UtcNow);

        record.Truncated.Should().BeTrue();
        System.Text.Encoding.UTF8.GetByteCount(record.Message).Should().BeLessThanOrEqualTo(8 * 1024);
    }
}
