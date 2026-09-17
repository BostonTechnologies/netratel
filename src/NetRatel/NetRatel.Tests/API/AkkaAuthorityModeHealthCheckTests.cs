using FluentAssertions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NetRatel.Akka.Configuration;
using NetRatel.API.Gateway;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class AkkaAuthorityModeHealthCheckTests
{
    [Fact]
    public async Task CheckHealthAsync_ReportsNormalAkkaForEveryEnabledAuthorityPath()
    {
        var check = new AkkaAuthorityModeHealthCheck(new NetRatelAkkaMigrationOptions
        {
            Enabled = true,
            PresenceEnabled = true,
            GatewayEnabled = true,
            ControlGatewayEnabled = true,
            FileGatewayEnabled = true,
            RemoteSupportGatewayEnabled = true,
            CommandShadowEnabled = true,
            JobShadowEnabled = true,
            TelemetryShadowEnabled = true,
            TerminalGatewayEnabled = true,
            SignalRShadowEnabled = true,
            SignalRShadowLocalCanaryEnabled = true,
            PresenceAuthorityEnabled = true,
            PingAuthorityEnabled = true,
            TelemetryAuthorityEnabled = true,
            FileBrowseAuthorityEnabled = true,
            RemoteSupportAuthorityEnabled = true,
            CommandAuthorityEnabled = true,
            JobAuthorityEnabled = true,
            TerminalAuthorityEnabled = true,
            SignalRAuthorityEnabled = true
        });

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Data["authorityMode"].Should().Be("akka");
        result.Data["presenceAuthority"].Should().Be("akka");
        result.Data["pingAuthority"].Should().Be("akka");
        result.Data["telemetryAuthority"].Should().Be("akka");
        result.Data["fileBrowseAuthority"].Should().Be("akka");
        result.Data["remoteSupportAuthority"].Should().Be("akka");
        result.Data["commandAuthority"].Should().Be("akka");
        result.Data["jobAuthority"].Should().Be("akka");
        result.Data["terminalAuthority"].Should().Be("akka");
        result.Data["signalRAuthority"].Should().Be("akka");
    }

    [Fact]
    public async Task CheckHealthAsync_ReportsPresenceAsTheOnlyActiveAuthorityPath()
    {
        var check = new AkkaAuthorityModeHealthCheck(new NetRatelAkkaMigrationOptions
        {
            Enabled = true,
            PresenceEnabled = true,
            GatewayEnabled = true,
            PresenceAuthorityEnabled = true
        });

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy);
        result.Data["authorityMode"].Should().Be("akka");
        result.Data["presenceAuthority"].Should().Be("akka");
        result.Data["telemetryAuthority"].Should().Be("unavailable");
        result.Data["commandAuthority"].Should().Be("unavailable");
        result.Data["jobAuthority"].Should().Be("unavailable");
        result.Data["signalRAuthority"].Should().Be("unavailable");
        result.Data["activeAuthorityPaths"].Should().Be("presence");
    }

    [Fact]
    public async Task CheckHealthAsync_ReportsPingOnlyWhenItsControlCanaryIsActive()
    {
        var check = new AkkaAuthorityModeHealthCheck(new NetRatelAkkaMigrationOptions
        {
            Enabled = true,
            PresenceEnabled = true,
            GatewayEnabled = true,
            ControlGatewayEnabled = true,
            PresenceAuthorityEnabled = true,
            PingAuthorityEnabled = true
        });

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy);
        result.Data["pingAuthority"].Should().Be("akka");
        result.Data["activeAuthorityPaths"].Should().Be("presence,ping");
    }

    [Fact]
    public async Task CheckHealthAsync_ReportsTheLiveFileAndRemoteSupportPaths()
    {
        var check = new AkkaAuthorityModeHealthCheck(new NetRatelAkkaMigrationOptions
        {
            Enabled = true,
            PresenceEnabled = true,
            GatewayEnabled = true,
            FileGatewayEnabled = true,
            RemoteSupportGatewayEnabled = true,
            PresenceAuthorityEnabled = true,
            FileBrowseAuthorityEnabled = true,
            RemoteSupportAuthorityEnabled = true
        });

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Data["fileBrowseAuthority"].Should().Be("akka");
        result.Data["remoteSupportAuthority"].Should().Be("akka");
        result.Data["activeAuthorityPaths"].Should().Be("presence,file-browser,remote-support");
    }

    [Fact]
    public async Task CheckHealthAsync_ReportsCommandsWhenTheAuthorityGateIsActive()
    {
        var check = new AkkaAuthorityModeHealthCheck(new NetRatelAkkaMigrationOptions
        {
            Enabled = true,
            PresenceEnabled = true,
            GatewayEnabled = true,
            CommandShadowEnabled = true,
            PresenceAuthorityEnabled = true,
            CommandAuthorityEnabled = true
        });

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy);
        result.Data["commandAuthority"].Should().Be("akka");
        result.Data["activeAuthorityPaths"].Should().Be("presence,commands");
    }

    [Fact]
    public async Task CheckHealthAsync_ReportsJobsWhenTheAuthorityGateIsActive()
    {
        var check = new AkkaAuthorityModeHealthCheck(new NetRatelAkkaMigrationOptions
        {
            Enabled = true,
            PresenceEnabled = true,
            GatewayEnabled = true,
            JobShadowEnabled = true,
            PresenceAuthorityEnabled = true,
            JobAuthorityEnabled = true
        });

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy);
        result.Data["jobAuthority"].Should().Be("akka");
        result.Data["activeAuthorityPaths"].Should().Be("presence,jobs");
    }
}
