using System.Text.Json;
using FluentAssertions;
using NetRatel.Client.Service.Readiness;
using Xunit;

namespace NetRatel.Tests.Client;

public sealed class WindowsStartupReadinessReporterTests
{
    [Fact]
    public void Report_BindsEveryStageToFreshChallengeAndProcessIdentity()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"netratel-readiness-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var requestPath = Path.Combine(directory, "request.json");
        var readyPath = Path.Combine(directory, "ready.json");
        var now = DateTimeOffset.UtcNow;
        var attemptId = Guid.NewGuid();
        var connectionId = Guid.NewGuid();
        var nonce = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        WriteRequest(requestPath, attemptId, nonce, now);
        var agentId = Guid.NewGuid();
        var reporter = WindowsStartupReadinessReporter.CreateForTesting(
            requestPath,
            readyPath,
            new WindowsStartupReadinessReporter.ReadinessProcessIdentity(
                4321, now.AddSeconds(1), 0, WindowsStartupReadinessReporter.SystemSid),
            clock: () => now.AddSeconds(2));

        try
        {
            reporter.Report("service_started");
            reporter.Report("enrolled", agentId);
            reporter.Report("authenticated", agentId, 7);
            reporter.Report("admitted", agentId, 7, 22, connectionId);
            reporter.Report("heartbeat_ready", agentId, 7, 22, connectionId, 2);

            using var document = JsonDocument.Parse(File.ReadAllText(readyPath));
            var ready = document.RootElement;
            ready.GetProperty("schema").GetString().Should().Be(WindowsStartupReadinessReporter.ReadySchema);
            ready.GetProperty("attemptId").GetString().Should().Be(attemptId.ToString("D"));
            ready.GetProperty("nonce").GetString().Should().Be(nonce);
            ready.GetProperty("stage").GetString().Should().Be("heartbeat_ready");
            ready.GetProperty("processId").GetInt32().Should().Be(4321);
            ready.GetProperty("processStartedAtUtc").GetDateTimeOffset().Should().Be(now.AddSeconds(1));
            ready.GetProperty("sessionId").GetInt32().Should().Be(0);
            ready.GetProperty("userSid").GetString().Should().Be(WindowsStartupReadinessReporter.SystemSid);
            ready.GetProperty("agentId").GetString().Should().Be(agentId.ToString("D"));
            ready.GetProperty("tenantId").GetInt32().Should().Be(7);
            ready.GetProperty("connectionEpoch").GetUInt64().Should().Be(22);
            ready.GetProperty("connectionId").GetString().Should().Be(connectionId.ToString("D"));
            ready.GetProperty("heartbeatSequence").GetUInt64().Should().Be(2);
            ready.GetProperty("observedAtUtc").GetDateTimeOffset().Offset.Should().Be(TimeSpan.Zero);

            WriteRequest(requestPath, Guid.NewGuid(), Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)), now);
            reporter.Report("heartbeat_ready", Guid.NewGuid(), 99, 99);
            using var staleDocument = JsonDocument.Parse(File.ReadAllText(readyPath));
            staleDocument.RootElement.GetProperty("attemptId").GetString().Should().Be(attemptId.ToString("D"));
            staleDocument.RootElement.GetProperty("agentId").GetString().Should().Be(agentId.ToString("D"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Report_KeepsHeartbeatSequenceNullableForExistingCallers()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"netratel-readiness-legacy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var requestPath = Path.Combine(directory, "request.json");
        var readyPath = Path.Combine(directory, "ready.json");
        var now = DateTimeOffset.UtcNow;
        WriteRequest(requestPath, Guid.NewGuid(), Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)), now);
        var reporter = WindowsStartupReadinessReporter.CreateForTesting(
            requestPath,
            readyPath,
            new WindowsStartupReadinessReporter.ReadinessProcessIdentity(
                4321, now.AddSeconds(1), 0, WindowsStartupReadinessReporter.SystemSid),
            clock: () => now.AddSeconds(2));

        try
        {
            reporter.Report("heartbeat_ready", Guid.NewGuid(), 7, 22, Guid.NewGuid());

            using var document = JsonDocument.Parse(File.ReadAllText(readyPath));
            document.RootElement.GetProperty("schema").GetString().Should().Be(WindowsStartupReadinessReporter.ReadySchema);
            document.RootElement.GetProperty("heartbeatSequence").ValueKind.Should().Be(JsonValueKind.Null);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("S-1-5-18", 0, 1, true)]
    [InlineData("S-1-5-18", 1, 1, false)]
    [InlineData("S-1-5-32-544", 0, 1, false)]
    [InlineData("S-1-5-18", 0, -1, false)]
    public void CreateForTesting_UsesProductionChallengeAndProcessEligibility(
        string sid,
        int sessionId,
        int startOffsetSeconds,
        bool expectedEligible)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"netratel-readiness-check-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var requestPath = Path.Combine(directory, "request.json");
        var readyPath = Path.Combine(directory, "ready.json");
        var now = DateTimeOffset.UtcNow;
        var attemptId = Guid.NewGuid();
        var nonce = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        WriteRequest(requestPath, attemptId, nonce, now);
        var reporter = WindowsStartupReadinessReporter.CreateForTesting(
            requestPath,
            readyPath,
            new WindowsStartupReadinessReporter.ReadinessProcessIdentity(
                4321, now.AddSeconds(startOffsetSeconds), sessionId, sid),
            clock: () => now.AddSeconds(2));

        try
        {
            reporter.Report("service_started");
            File.Exists(readyPath).Should().Be(expectedEligible);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void CreateForTesting_RejectsExpiredChallengeAndDoesNotWriteReadiness()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"netratel-readiness-expired-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var requestPath = Path.Combine(directory, "request.json");
        var readyPath = Path.Combine(directory, "ready.json");
        var now = DateTimeOffset.UtcNow;
        WriteRequest(requestPath, Guid.NewGuid(), Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)), now);
        var reporter = WindowsStartupReadinessReporter.CreateForTesting(
            requestPath,
            readyPath,
            new WindowsStartupReadinessReporter.ReadinessProcessIdentity(
                4321, now.AddSeconds(1), 0, WindowsStartupReadinessReporter.SystemSid),
            clock: () => now.AddMinutes(6));

        try
        {
            reporter.Report("heartbeat_ready", Guid.NewGuid(), 7, 1, Guid.NewGuid());
            File.Exists(readyPath).Should().BeFalse();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void WriteRequest(string path, Guid attemptId, string nonce, DateTimeOffset requestedAt) =>
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            schema = WindowsStartupReadinessReporter.RequestSchema,
            attemptId,
            nonce,
            requestedAtUtc = requestedAt,
            expiresAtUtc = requestedAt.AddMinutes(5)
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
}
