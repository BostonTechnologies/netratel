using System.Text.Json;
using NetRatel.Infrastructure.Netclaw;
using NetRatel.Shared.Contracts;
using Xunit;

namespace NetRatel.Tests.Netclaw;

public sealed class NetclawTraceRecoveryTests
{
    [Fact]
    public void Trace_is_allowlisted_redacted_bounded_and_distinguishes_source_time_and_failure()
    {
        var frame = JsonSerializer.SerializeToElement(new {
            type = "tool_result", toolName = "netclaw/server", timestampMs = 1793600000000L, callId = "call-a",
            argumentsJson = "{\"authorization\":\"Bearer credentials\",\"nested\":{\"api_key\":\"credential-value\",\"path\":\"/safe/\"}}",
            result = "{\"password\":\"hide-me\",\"pwd\":\"hide-short-key\",\"body\":\"Bearer abcdefghijklmnop\",\"value\":\"known-device-secret\"}",
            toolFailureCode = "timeout", thinking = "never-retain-this", errorDetail = "never-retain-exception",
            inputTokens = 12, outputTokens = 5
        });
        var message = Guid.NewGuid();
        var trace = NetclawTraceMapper.Map(frame, "saved-session", message, "known-device-secret");
        var json = JsonSerializer.Serialize(trace);
        Assert.DoesNotContain("credential-value", json); Assert.DoesNotContain("hide-me", json);
        Assert.DoesNotContain("abcdefghijklmnop", json); Assert.DoesNotContain("known-device-secret", json);
        Assert.DoesNotContain("hide-short-key", json); Assert.DoesNotContain("never-retain", json); Assert.Contains("/safe/", json);
        Assert.True(trace.Redacted); Assert.Equal("reported failure", trace.Status);
        Assert.Equal("timeout", trace.FailureCode); Assert.Equal(message, trace.TurnMessageId);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1793600000000L), trace.SourceTimestampUtc);
        var large = NetclawTraceMapper.Map(JsonSerializer.SerializeToElement(new { type = "tool_result", result = new string('x', 70000), timestampMs = "bad" }), "saved-session", message);
        Assert.True(large.Truncated); Assert.Null(large.SourceTimestampUtc); Assert.Contains("unavailable", large.Result);
    }

    [Fact]
    public void Historical_terminal_evidence_requires_saved_baseline_exact_latest_marker_and_one_count_advance()
    {
        var id = Guid.NewGuid(); var message = Guid.NewGuid();
        JsonElement Joined(int count, params object[] messages) => JsonSerializer.SerializeToElement(new { type = "session_joined", sessionId = "bound-session", turnCount = count, recentMessages = messages });
        var current = new { role = "user", content = NetclawInvestigationWorker.MessageMarker(id, message) + "question" };
        var reply = new { role = "assistant", content = "A reply alone is not terminal evidence." };
        var ended = Joined(3, current, reply);
        Assert.True(NetclawInvestigationWorker.ProvesHistoricalEnd(ended, id, message, 2));
        Assert.False(NetclawInvestigationWorker.ProvesHistoricalEnd(ended, id, message, null));
        Assert.False(NetclawInvestigationWorker.ProvesHistoricalEnd(Joined(2, current, reply), id, message, 2));
        Assert.False(NetclawInvestigationWorker.ProvesHistoricalEnd(Joined(4, current, reply), id, message, 2));
        Assert.False(NetclawInvestigationWorker.ProvesHistoricalEnd(Joined(3, current, reply, new { role = "user", content = "a later message" }), id, message, 2));
        Assert.False(NetclawInvestigationWorker.ProvesHistoricalEnd(Joined(3, current, current), id, message, 2));
        Assert.False(NetclawInvestigationWorker.ProvesHistoricalEnd(ended, Guid.NewGuid(), message, 2));
    }

    [Fact]
    public void Release_provenance_shows_short_commit_and_actual_utc_build_metadata_or_explicit_unavailable()
    {
        const string sha = "cec2757aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var stamp = ReleaseProvenance.ParseTimestamp("2026-10-31T21:01:00Z");
        Assert.Equal("commit cec2757 · built 2026-10-31 21:01 UTC", new ReleaseProvenance(sha, stamp).Display);
        Assert.Equal("commit unavailable · build date unavailable", new ReleaseProvenance("bad", null).Display);
        Assert.Null(ReleaseProvenance.ParseTimestamp("last night"));
        Assert.Equal(sha, ReleaseProvenance.Read(null, "0.1.2-beta.20+" + sha).Commit);
    }
}
