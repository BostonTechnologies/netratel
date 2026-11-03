using System.Text;
using AwesomeAssertions;
using NetRatel.Client.Service.Auth;
using Xunit;

namespace NetRatel.Tests.Client;

public sealed class OperationalRecoveryJsonTests
{
    // Actual version-1 file bytes captured at ad9eee91d9e0f152b4e2e98359b287fbc51c9af8.
    [Theory]
    [InlineData(false, """{"version":1,"recordedAtUtc":"2026-10-07T12:00:00+00:00","hasOutage":false,"outageElapsedSeconds":0,"failureCount":0,"attemptCount":0,"nextAttemptUtc":null,"retryAfterUntilUtc":null,"consumedUpdateAttemptId":null}""")]
    [InlineData(true, """{"version":1,"recordedAtUtc":"2026-10-07T14:00:00.1234567+02:00","hasOutage":true,"outageElapsedSeconds":123.125,"failureCount":5,"attemptCount":4294967297,"nextAttemptUtc":"2026-10-07T12:05:00.2345678+00:00","retryAfterUntilUtc":"2026-10-07T13:10:00+01:00","consumedUpdateAttemptId":"01234567-89ab-cdef-0123-456789abcdef"}""")]
    public void Advisory_write_preserves_existing_utf8_order_nulls_offsets_and_precision(bool populated, string expectedJson)
    {
        using var fixture = new Fixture();
        var state = populated
            ? new OperationalRecoveryState(1, DateTimeOffset.Parse("2026-10-07T14:00:00.1234567+02:00"), true, 123.125, 5, 4294967297,
                DateTimeOffset.Parse("2026-10-07T12:05:00.2345678Z"), DateTimeOffset.Parse("2026-10-07T13:10:00+01:00"), Guid.Parse("01234567-89ab-cdef-0123-456789abcdef"))
            : new OperationalRecoveryState(1, DateTimeOffset.Parse("2026-10-07T12:00:00Z"), false, 0, 0, 0, null, null, null);
        fixture.Store.Write(state).Should().BeTrue();
        File.ReadAllBytes(fixture.Path).Should().Equal(Encoding.UTF8.GetBytes(expectedJson));
        fixture.Store.Read().Should().Be(state);
        fixture.Diagnostics.Should().BeEmpty();
        if (!OperatingSystem.IsWindows())
            File.GetUnixFileMode(fixture.Path).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [Fact]
    public void Existing_case_insensitive_quoted_number_records_keep_missing_optional_fields_null()
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.Path, """{"VERSION":"1","RECORDEDATUTC":"2026-10-07T12:00:00Z","HASOUTAGE":true,"OUTAGEELAPSEDSECONDS":"2.5","FAILURECOUNT":"2","ATTEMPTCOUNT":"4294967297"}""");
        fixture.Store.Read().Should().Be(new OperationalRecoveryState(1, DateTimeOffset.Parse("2026-10-07T12:00:00Z"), true, 2.5, 2, 4294967297, null, null, null));
        fixture.Diagnostics.Should().BeEmpty();
    }

    [Theory]
    [InlineData("null", "[OperationalRecovery] Ignored invalid operational recovery advisory state.")]
    [InlineData("[]", "[OperationalRecovery] Could not read operational recovery advisory state: JsonException.")]
    public void Invalid_roots_keep_the_existing_null_result_and_once_only_safe_diagnostic(string json, string diagnostic)
    {
        using var fixture = new Fixture();
        File.WriteAllText(fixture.Path, json);
        fixture.Store.Read().Should().BeNull();
        fixture.Store.Read().Should().BeNull();
        fixture.Diagnostics.Should().Equal(diagnostic);
    }

    private sealed class Fixture : IDisposable
    {
        private string Directory { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "netratel-recovery-json-" + Guid.NewGuid().ToString("N"));
        public string Path => System.IO.Path.Combine(Directory, "operational-recovery.json");
        public List<string> Diagnostics { get; } = [];
        public OperationalRecoveryStateStore Store { get; }
        public Fixture()
        {
            System.IO.Directory.CreateDirectory(Directory);
            Store = new OperationalRecoveryStateStore(Path, Diagnostics.Add);
        }
        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }
}
