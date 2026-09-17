using System.Text.Json;
using FluentAssertions;
using NetRatel.Web.Services.RemoteSupport;
using Xunit;

namespace NetRatel.Web.ComponentTests;

public class RemoteSupportSignalPayloadNormalizerTests
{
    [Fact]
    public void Normalize_AcceptsRawAnswerObject()
    {
        var payload = NormalizeAnswer("""{"type":"answer","sdp":"v=0"}""");

        payload.ProviderGeneration.Should().Be(1);
        payload.PayloadShape.Should().Be("raw_object");
        payload.IsValidSessionDescription.Should().BeTrue();
        payload.HasSdp.Should().BeTrue();
    }

    [Fact]
    public void Normalize_AcceptsRawAnswerJsonString()
    {
        var rawString = JsonSerializer.Serialize("""{"type":"answer","sdp":"v=0"}""");

        var payload = NormalizeAnswer(rawString);

        payload.PayloadShape.Should().Be("raw_string");
        payload.IsValidSessionDescription.Should().BeTrue();
    }

    [Fact]
    public void Normalize_AcceptsGenerationWrappedAnswerObject()
    {
        var payload = NormalizeAnswer("""
            {
              "providerGeneration": 2,
              "provider": "console_secure_desktop_helper",
              "fromProvider": "console_secure_desktop_helper",
              "toProvider": "interactive_user_helper",
              "handoverReason": "console_to_interactive_after_login",
              "payload": { "type": "answer", "sdp": "v=0" }
            }
            """);

        payload.ProviderGeneration.Should().Be(2);
        payload.Provider.Should().Be("console_secure_desktop_helper");
        payload.FromProvider.Should().Be("console_secure_desktop_helper");
        payload.ToProvider.Should().Be("interactive_user_helper");
        payload.HandoverReason.Should().Be("console_to_interactive_after_login");
        payload.PayloadShape.Should().Be("wrapped_payload_object");
        payload.IsValidSessionDescription.Should().BeTrue();
    }

    [Fact]
    public void Normalize_AcceptsGenerationWrappedAnswerString()
    {
        var inner = JsonSerializer.Serialize(new { type = "answer", sdp = "v=0" });
        var wrapper = JsonSerializer.Serialize(new { providerGeneration = 2, payload = inner });

        var payload = NormalizeAnswer(wrapper);

        payload.ProviderGeneration.Should().Be(2);
        payload.PayloadShape.Should().Be("wrapped_payload_string");
        payload.IsValidSessionDescription.Should().BeTrue();
    }

    [Fact]
    public void Normalize_AcceptsPayloadJsonAnswerWrapper()
    {
        var inner = JsonSerializer.Serialize(new { type = "answer", sdp = "v=0" });
        var wrapper = JsonSerializer.Serialize(new { providerGeneration = 2, payloadJson = inner });

        var payload = NormalizeAnswer(wrapper);

        payload.ProviderGeneration.Should().Be(2);
        payload.PayloadShape.Should().Be("wrapped_payloadJson_string");
        payload.IsValidSessionDescription.Should().BeTrue();
    }

    [Fact]
    public void Normalize_RejectsMalformedAnswerMissingSdp()
    {
        var payload = NormalizeAnswer("""{"type":"answer"}""");

        payload.IsValidSessionDescription.Should().BeFalse();
        payload.HasType.Should().BeTrue();
        payload.HasSdp.Should().BeFalse();
        payload.ToDiagnosticJson("answer", "remote_support_invalid_sdp_payload_shape")
            .Should().Contain("remote_support_invalid_sdp_payload_shape");
    }

    [Fact]
    public void Normalize_RejectsWholeWrapperAsSessionDescription()
    {
        var payload = NormalizeAnswer("""
            {
              "providerGeneration": 2,
              "payload": { "notType": "answer", "notSdp": "v=0" }
            }
            """);

        payload.IsValidSessionDescription.Should().BeFalse();
        payload.PayloadKeys.Should().Contain("notType");
        payload.PayloadKeys.Should().NotContain("providerGeneration");
    }

    [Fact]
    public void Normalize_AcceptsRawIceObject()
    {
        var payload = NormalizeIce("""{"candidate":"candidate:1 1 udp 1 127.0.0.1 123 typ host","sdpMid":"0","sdpMLineIndex":0}""");

        payload.ProviderGeneration.Should().Be(1);
        payload.PayloadShape.Should().Be("raw_object");
        payload.IsValidIceCandidate.Should().BeTrue();
    }

    [Fact]
    public void Normalize_AcceptsGenerationWrappedIceObject()
    {
        var payload = NormalizeIce("""
            {
              "providerGeneration": 3,
              "payload": { "candidate": "candidate:1 1 udp 1 127.0.0.1 123 typ host", "sdpMid": "0", "sdpMLineIndex": 0 }
            }
            """);

        payload.ProviderGeneration.Should().Be(3);
        payload.PayloadShape.Should().Be("wrapped_payload_object");
        payload.IsValidIceCandidate.Should().BeTrue();
    }

    [Fact]
    public void Normalize_StaleGenerationCanBeIgnoredByCaller()
    {
        var payload = NormalizeAnswer("""{"providerGeneration":2,"payload":{"type":"answer","sdp":"v=0"}}""");

        (payload.ProviderGeneration < 3).Should().BeTrue();
    }

    [Fact]
    public void Normalize_CurrentGenerationCanBeAcceptedByCaller()
    {
        var payload = NormalizeAnswer("""{"providerGeneration":3,"payload":{"type":"answer","sdp":"v=0"}}""");

        payload.ProviderGeneration.Should().Be(3);
        payload.IsValidSessionDescription.Should().BeTrue();
    }

    private static RemoteSupportSignalPayload NormalizeAnswer(string payloadJson) =>
        RemoteSupportSignalPayloadNormalizer.Normalize("answer", payloadJson);

    private static RemoteSupportSignalPayload NormalizeIce(string payloadJson) =>
        RemoteSupportSignalPayloadNormalizer.Normalize("ice", payloadJson);
}
