using System.Text;
using System.Text.Json;
using NetRatel.Client.Service.Tasks;
using Xunit;

namespace NetRatel.Tests.Client;

public sealed class ShellStatusJsonTests
{
    // Frozen from the original General-default serializers before metadata.
    [Fact]
    public void ProgressPreservesCompactOrderEscapingAndNullBinding()
    {
        var value = new ShellProgressEnvelope(["<&\"é>\n😀"], [], 1, false, null);
        const string expected = """{"stdout":["\u003C\u0026\u0022\u00E9\u003E\n\uD83D\uDE00"],"stderr":[],"outputSequence":1,"outputTruncated":false,"outputProtectionBinding":null}""";
        Assert.Equal(Encoding.UTF8.GetBytes(expected), JsonSerializer.SerializeToUtf8Bytes(value, ClientShellJsonContext.Default.ShellProgressEnvelope));
    }

    [Fact]
    public void TerminalResultPreservesBothStreamsFlagsAndOmittedRawArguments()
    {
        var value = new ShellResultEnvelope(0, ["<&\"é>\n😀"], ["stderr-marker"], false, true, true, false, false, false,
            new ShellExecutionDiagnostics("/synthetic/bash", null, "/synthetic/<&é>", 123.125, false, false));
        const string expected = """{"exitCode":0,"stdout":["\u003C\u0026\u0022\u00E9\u003E\n\uD83D\uDE00"],"stderr":["stderr-marker"],"outputTruncated":false,"outputStreamed":true,"processStarted":true,"cancelled":false,"timedOut":false,"outcomeUnknown":false,"diagnostics":{"shellPath":"/synthetic/bash","arguments":null,"workingDirectory":"/synthetic/\u003C\u0026\u00E9\u003E","durationMs":123.125,"cancelled":false,"timedOut":false}}""";
        Assert.Equal(Encoding.UTF8.GetBytes(expected), JsonSerializer.SerializeToUtf8Bytes(value, ClientShellJsonContext.Default.ShellResultEnvelope));
    }

    [Fact]
    public void FailurePreservesExplicitNullExitAndPayload()
    {
        var value = new ShellFailureEnvelope(null, [], ["synthetic detail <&\"é>\n😀"],
            new ShellFailureDiagnostics("exec-shell-cmd", null, "synthetic failure <&é>"));
        const string expected = """{"exitCode":null,"stdout":[],"stderr":["synthetic detail \u003C\u0026\u0022\u00E9\u003E\n\uD83D\uDE00"],"diagnostics":{"taskType":"exec-shell-cmd","payload":null,"failureMessage":"synthetic failure \u003C\u0026\u00E9\u003E"}}""";
        Assert.Equal(Encoding.UTF8.GetBytes(expected), JsonSerializer.SerializeToUtf8Bytes(value, ClientShellJsonContext.Default.ShellFailureEnvelope));
    }

    [Fact]
    public void CancellationPreservesItsSeparateDiagnosticShape()
    {
        var value = new ShellCancelledEnvelope(null, [], ["Cancelled <&é>"], new ShellCancellationDiagnostics("exec-shell-cmd", true));
        const string expected = """{"exitCode":null,"stdout":[],"stderr":["Cancelled \u003C\u0026\u00E9\u003E"],"diagnostics":{"taskType":"exec-shell-cmd","cancelled":true}}""";
        Assert.Equal(Encoding.UTF8.GetBytes(expected), JsonSerializer.SerializeToUtf8Bytes(value, ClientShellJsonContext.Default.ShellCancelledEnvelope));
    }
}
