using System.Text;
using AwesomeAssertions;
using NetRatel.Application.Jobs;
using NetRatel.Shared.Contracts.Tasks;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

public sealed class JobLogPageBuilderTests
{
    [Fact]
    public void Forward_pages_keep_earliest_rows_and_continue_across_equal_sequence_streams_without_loss()
    {
        var rows = Enumerable.Range(1, 1201).Select(index => Row(index, index / 2, "line-" + index, index % 2 == 0 ? "stderr" : "stdout")).ToArray();
        var observed = new List<long>(); string? cursor = null; ExecutionLogDto page;
        do
        {
            page = JobLogPageBuilder.Build("synthetic-request", rows, cursor, 501, 65536);
            observed.AddRange(page.Logs.Select(log => log.Id)); cursor = page.NextCursor;
        } while (page.HasMore);
        observed.Should().Equal(rows.Select(row => row.Id));
        observed.Distinct().Should().HaveCount(1201);
    }

    [Fact]
    public void Byte_boundary_never_consumes_unreturned_rows_and_oversized_lines_are_explicit_with_full_output_navigation()
    {
        var rows = new[] { Row(1, 7, new('a', 40000)), Row(2, 7, new('b', 40000), "stderr"),
            Row(3, 8, new('Ω', 40000)), Row(4, 9, "tail") };
        var pages = new List<ExecutionLogDto>(); string? cursor = null;
        do { var page = JobLogPageBuilder.Build("synthetic-request", rows, cursor, 1000, 65536); pages.Add(page); cursor = page.NextCursor; }
        while (pages[^1].HasMore);
        pages.SelectMany(page => page.Logs).Select(log => log.Id).Should().Equal(1, 2, 3, 4);
        pages[0].Logs.Single().Message.Should().HaveLength(40000); pages[1].Logs.Single().Message.Should().HaveLength(40000);
        pages.Should().OnlyContain(page => page.Logs.Sum(log => Encoding.UTF8.GetByteCount(log.Message)) <= 65536);
        pages[2].TruncatedLogIds.Should().Equal(3); pages[2].FullOutputUrl.Should().Be("/tasks/request/synthetic-request");
        pages[2].Truncated.Should().BeTrue(); pages[^1].Logs.Single().Message.Should().Be("tail");
    }

    private static JobTaskLogInfo Row(long id, long sequence, string message, string stream = "stdout") =>
        new(id, "synthetic-request", 1, "synthetic-node", 7, stream, message, sequence, DateTimeOffset.UnixEpoch);
}
