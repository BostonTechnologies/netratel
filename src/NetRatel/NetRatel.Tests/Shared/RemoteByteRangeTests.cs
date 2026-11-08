using AwesomeAssertions;
using NetRatel.Shared.Contracts.FileSystem;
using Xunit;

namespace NetRatel.Tests.Shared;

public sealed class RemoteByteRangeTests
{
    [Theory]
    [InlineData(null, 100, 0, 100, false)]
    [InlineData("bytes=20-29", 100, 20, 10, true)]
    [InlineData("bytes=90-", 100, 90, 10, true)]
    [InlineData("bytes=-12", 100, 88, 12, true)]
    [InlineData("bytes=-200", 100, 0, 100, true)]
    [InlineData("bytes=99-200", 100, 99, 1, true)]
    [InlineData("bytes=0-1,10-11", 100, 0, 100, false)]
    public void ResolvesSingleRangesWithoutPrefixReads(string? header, long size, long offset, long length, bool partial)
    {
        RemoteByteRange.TryResolve(header, size, false, out var range).Should().BeTrue();
        range.Should().Be(new RemoteByteRange(offset, length, partial));
    }

    [Theory]
    [InlineData("bytes=100-", 100)]
    [InlineData("bytes=0-0", 0)]
    [InlineData("bytes=-0", 100)]
    [InlineData("bytes=20-19", 100)]
    [InlineData("bytes=999999999999999999999-", 100)]
    public void RejectsUnsatisfiableOrOverflowedRanges(string header, long size) =>
        RemoteByteRange.TryResolve(header, size, false, out _).Should().BeFalse();

    [Fact]
    public void WeakValidatorCannotSatisfyIfRange()
    {
        RemoteByteRange.TryResolve("bytes=5-8", 100, true, out var range).Should().BeTrue();
        range.Should().Be(new RemoteByteRange(0, 100, false));
    }
}
