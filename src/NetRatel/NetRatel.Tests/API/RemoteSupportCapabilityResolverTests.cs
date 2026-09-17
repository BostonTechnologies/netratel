using FluentAssertions;
using NetRatel.Shared.Contracts.RemoteSupport;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class RemoteSupportCapabilityResolverTests
{
    [Fact]
    public void Resolve_CurrentCapabilities_EnablesConsoleAndExplicitAssist()
    {
        var result = RemoteSupportCapabilityResolver.Resolve("""
            {
              "capabilities": {
                "remoteDesktopAvailable": true,
                "remoteSupportPreLoginSupportLevel": "media_preview",
                "remoteSupportConsoleProviderScaffolded": true,
                "remoteSupportWindowsSessionInventorySupported": true,
                "remoteSupportExplicitTargetingSupported": true
              }
            }
            """);

        result.FallbackDesktopSupported.Should().BeTrue();
        result.ConsoleLoginSupported.Should().BeTrue();
        result.SessionInventorySupported.Should().BeTrue();
        result.ExplicitUserAssistSupported.Should().BeTrue();
        result.LegacyAutomaticSupported.Should().BeFalse();
        result.Source.Should().Be("client_info_capabilities");
    }

    [Fact]
    public void Resolve_LegacyFallbackCapability_KeepsAutomaticSupportVisible()
    {
        var result = RemoteSupportCapabilityResolver.Resolve("""
            { "Capabilities": { "RemoteDesktopAvailable": true } }
            """);

        result.FallbackDesktopSupported.Should().BeTrue();
        result.ConsoleLoginSupported.Should().BeFalse();
        result.ExplicitUserAssistSupported.Should().BeFalse();
        result.LegacyAutomaticSupported.Should().BeTrue();
    }

    [Fact]
    public void Resolve_NewProtocolCapabilities_UsesIndependentFeatureFlags()
    {
        var result = RemoteSupportCapabilityResolver.Resolve("""
            {
              "remoteDesktopAvailable": true,
              "remoteSupportConsoleLoginSupported": true,
              "remoteSupportInteractiveAssistSupported": true,
              "remoteSupportTargetPreflightSupported": true,
              "remoteSupportProtocolRevision": "2"
            }
            """);

        result.ConsoleLoginSupported.Should().BeTrue();
        result.ExplicitUserAssistSupported.Should().BeTrue();
        result.TargetPreflightSupported.Should().BeTrue();
        result.ProtocolRevision.Should().Be(2);
    }

    [Theory]
    [InlineData(null, "client_info_missing")]
    [InlineData("not-json", "client_info_malformed")]
    public void Resolve_MissingOrMalformedJson_DoesNotInventSupport(string? json, string source)
    {
        var result = RemoteSupportCapabilityResolver.Resolve(json);

        result.FallbackDesktopSupported.Should().BeFalse();
        result.ConsoleLoginSupported.Should().BeFalse();
        result.SessionInventorySupported.Should().BeFalse();
        result.ExplicitUserAssistSupported.Should().BeFalse();
        result.LegacyAutomaticSupported.Should().BeFalse();
        result.Source.Should().Be(source);
    }
}
