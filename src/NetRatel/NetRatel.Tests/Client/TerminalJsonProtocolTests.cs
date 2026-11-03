using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using NetRatel.Client.Service.Terminal;
using Xunit;

namespace NetRatel.Tests.Client;

public sealed class TerminalJsonProtocolTests
{
    [Fact]
    public void Diagnostic_contracts_preserve_existing_fields_and_explicit_nulls()
    {
        JsonSerializer.Serialize(new TerminalAvailabilityDiagnostic("availability", "/app", null, null, null, false),
            TerminalJsonContext.Default.TerminalAvailabilityDiagnostic).Should().Be(
            """{"stage":"availability","appBaseDirectory":"/app","processPath":null,"pythonPath":null,"helperPath":null,"isAvailable":false}""");
        JsonSerializer.Serialize(new TerminalResizeDiagnostic("resize", "failed", 77, 17, null, null, "unavailable"),
            TerminalJsonContext.Default.TerminalResizeDiagnostic).Should().Be(
            """{"stage":"resize","result":"failed","requestedCols":77,"requestedRows":17,"appliedCols":null,"appliedRows":null,"error":"unavailable"}""");
        JsonSerializer.Serialize(new TerminalSelectionFailureDiagnostic("failed", true, false, "missing"),
            TerminalJsonContext.Default.TerminalSelectionFailureDiagnostic).Should().Be(
            """{"stage":"failed","forceNative":true,"forcePython":false,"error":"missing"}""");
    }

    [Fact]
    public void Helper_controls_preserve_utf8_base64_resize_correlation_and_nullable_close_reason()
    {
        var input = new UnixPtyInputMessage("input", Convert.ToBase64String(Encoding.UTF8.GetBytes("é\n\u0003")));
        JsonSerializer.Serialize(input, TerminalJsonContext.Default.UnixPtyInputMessage).Should().Be(
            """{"type":"input","data":"w6kKAw=="}""");
        JsonSerializer.Serialize(new UnixPtyResizeMessage("resize", 12345678901, 77, 17),
            TerminalJsonContext.Default.UnixPtyResizeMessage).Should().Be(
            """{"type":"resize","id":12345678901,"cols":77,"rows":17}""");
        JsonSerializer.Serialize(new UnixPtyCloseMessage("close", null), TerminalJsonContext.Default.UnixPtyCloseMessage).Should().Be(
            """{"type":"close","reason":null}""");
    }

    [Fact]
    public void Helper_status_keeps_web_defaults_and_all_existing_fields()
    {
        UnixPtyHelperTerminalHost.TryParseStatus(
            """{"TYPE":"resize","id":"12345678901","result":"applied","error":null,"pid":"42","cols":"77","rows":17,"future":true}""",
            out var status).Should().BeTrue();
        status!.Type.Should().Be("resize");
        status.Id.Should().Be(12345678901);
        status.Result.Should().Be("applied");
        status.Error.Should().BeNull();
        status.Pid.Should().Be(42);
        status.Cols.Should().Be(77);
        status.Rows.Should().Be(17);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"type\":\"resize\",\"id\":\"bad\"}")]
    public void Invalid_helper_status_does_not_become_a_successful_handshake(string? line)
    {
        UnixPtyHelperTerminalHost.TryParseStatus(line, out _).Should().BeFalse();
    }
}
