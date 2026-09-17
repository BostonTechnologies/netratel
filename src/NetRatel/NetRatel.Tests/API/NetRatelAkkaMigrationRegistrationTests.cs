using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NetRatel.API.Gateway;
using NetRatel.API.Services.Terminal;
using NetRatel.Application.Commands;
using NetRatel.Application.Jobs;
using NetRatel.Application.Presence;
using NetRatel.Application.Telemetry;
using NetRatel.Application.Terminals;
using NetRatel.Infrastructure.Persistence;
using Xunit;

namespace NetRatel.Tests.API;

[Collection(NetRatel.Tests.Akka.NetRatelAkkaTelemetryCollection.Name)]
public sealed class NetRatelAkkaMigrationRegistrationTests
{
    [Fact]
    public void PresenceAuthority_RequiresGatewayDependencies()
    {
        var validator = new NetRatel.Akka.Configuration.NetRatelAkkaMigrationOptionsValidator();

        var missingGateway = validator.Validate(null, new()
        {
            Enabled = true,
            PresenceEnabled = true,
            PresenceAuthorityEnabled = true
        });
        var valid = validator.Validate(null, new()
        {
            Enabled = true,
            PresenceEnabled = true,
            GatewayEnabled = true,
            PresenceAuthorityEnabled = true
        });

        missingGateway.Failed.Should().BeTrue();
        missingGateway.FailureMessage.Should().Contain("requires PresenceEnabled and GatewayEnabled");
        valid.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void TelemetryAuthority_RequiresItsExplicitGatewayPrerequisites()
    {
        var validator = new NetRatel.Akka.Configuration.NetRatelAkkaMigrationOptionsValidator();
        var options = new NetRatel.Akka.Configuration.NetRatelAkkaMigrationOptions
        {
            Enabled = true,
            TelemetryAuthorityEnabled = true
        };

        var result = validator.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("TelemetryAuthorityEnabled requires PresenceEnabled, GatewayEnabled");
        result.FailureMessage.Should().Contain("TelemetryShadowEnabled");
    }

    [Fact]
    public async Task TelemetryAuthority_RegistersAConcreteHostedAdapterForTheSingletonDemandRegistry()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["NetRatelAkkaMigration:Enabled"] = "true",
                ["NetRatelAkkaMigration:PresenceEnabled"] = "true",
                ["NetRatelAkkaMigration:GatewayEnabled"] = "true",
                ["NetRatelAkkaMigration:TelemetryShadowEnabled"] = "true",
                ["NetRatelAkkaMigration:PresenceAuthorityEnabled"] = "true",
                ["NetRatelAkkaMigration:TelemetryAuthorityEnabled"] = "true",
                ["NetRatelAkkaMigration:ActorSystemName"] = $"NetRatelTests{Guid.NewGuid():N}"
            })
            .Build();

        services.AddSingleton<IConfiguration>(configuration);
        services.AddNetRatelAkkaMigration(configuration);
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IHostedService) &&
            descriptor.ImplementationType == typeof(TelemetryInteractiveDemandHostedService));
        await using var provider = services.BuildServiceProvider();
        var registry = provider.GetRequiredService<TelemetryInteractiveDemandRegistry>();
        var hosted = new TelemetryInteractiveDemandHostedService(registry);

        await hosted.StartAsync(CancellationToken.None);
        registry.GetPolicy(new ClientKey(7, Guid.NewGuid())).Interactive.Should().BeFalse();
        await hosted.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void PingAuthority_RequiresItsControlGatewayPrerequisites()
    {
        var validator = new NetRatel.Akka.Configuration.NetRatelAkkaMigrationOptionsValidator();

        var missingControlGateway = validator.Validate(null, new()
        {
            Enabled = true,
            PresenceEnabled = true,
            GatewayEnabled = true,
            PingAuthorityEnabled = true
        });
        var valid = validator.Validate(null, new()
        {
            Enabled = true,
            PresenceEnabled = true,
            GatewayEnabled = true,
            ControlGatewayEnabled = true,
            PresenceAuthorityEnabled = true,
            PingAuthorityEnabled = true
        });

        missingControlGateway.Failed.Should().BeTrue();
        missingControlGateway.FailureMessage.Should().Contain("requires PresenceEnabled, GatewayEnabled, ControlGatewayEnabled");
        valid.Succeeded.Should().BeTrue();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FileAndRemoteSupportAuthority_RequireActivePresenceAuthority(bool fileBrowse)
    {
        var validator = new NetRatel.Akka.Configuration.NetRatelAkkaMigrationOptionsValidator();
        var options = new NetRatel.Akka.Configuration.NetRatelAkkaMigrationOptions
        {
            Enabled = true,
            PresenceEnabled = true,
            GatewayEnabled = true,
            FileGatewayEnabled = fileBrowse,
            RemoteSupportGatewayEnabled = !fileBrowse,
            FileBrowseAuthorityEnabled = fileBrowse,
            RemoteSupportAuthorityEnabled = !fileBrowse
        };

        var missingPresenceAuthority = validator.Validate(null, options);
        options.PresenceAuthorityEnabled = true;
        var active = validator.Validate(null, options);

        missingPresenceAuthority.Failed.Should().BeTrue();
        missingPresenceAuthority.FailureMessage.Should().Contain("active presence authority");
        active.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void LogAuthority_RequiresTheSeparateLogGatewayAndActivePresenceAuthority()
    {
        var validator = new NetRatel.Akka.Configuration.NetRatelAkkaMigrationOptionsValidator();
        var missingLogGateway = validator.Validate(null, new()
        {
            Enabled = true,
            PresenceEnabled = true,
            GatewayEnabled = true,
            PresenceAuthorityEnabled = true,
            LogAuthorityEnabled = true
        });
        var valid = validator.Validate(null, new()
        {
            Enabled = true,
            PresenceEnabled = true,
            GatewayEnabled = true,
            PresenceAuthorityEnabled = true,
            LogGatewayEnabled = true,
            LogAuthorityEnabled = true
        });

        missingLogGateway.Failed.Should().BeTrue();
        missingLogGateway.FailureMessage.Should().Contain("LogAuthorityEnabled requires active presence authority");
        valid.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void PresenceReadModel_RequiresItsGatewayPrerequisites()
    {
        var validator = new NetRatel.Akka.Configuration.NetRatelAkkaMigrationOptionsValidator();

        var missingGateway = validator.Validate(null, new()
        {
            Enabled = true,
            PresenceEnabled = true,
            PresenceReadModelEnabled = true
        });
        var valid = validator.Validate(null, new()
        {
            Enabled = true,
            PresenceEnabled = true,
            GatewayEnabled = true,
            PresenceReadModelEnabled = true
        });

        missingGateway.Failed.Should().BeTrue();
        missingGateway.FailureMessage.Should().Contain("PresenceReadModelEnabled requires PresenceEnabled and GatewayEnabled");
        valid.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void PrimaryCardGatewayFlags_Require_An_Explicit_Ordered_Cutover()
    {
        var validator = new NetRatel.Akka.Configuration.NetRatelAkkaMigrationOptionsValidator();

        var missingAuthority = validator.Validate(null, new()
        {
            Enabled = true,
            PrimaryCardGatewayReadsEnabled = true
        });
        var actionsWithoutReads = validator.Validate(null, new()
        {
            Enabled = true,
            PresenceEnabled = true,
            GatewayEnabled = true,
            PresenceAuthorityEnabled = true,
            PrimaryCardGatewayActionsEnabled = true
        });
        var terminalWithoutActions = validator.Validate(null, new()
        {
            Enabled = true,
            PresenceEnabled = true,
            GatewayEnabled = true,
            PresenceAuthorityEnabled = true,
            PrimaryCardGatewayReadsEnabled = true,
            TerminalGatewayEnabled = true,
            TerminalGatewayPrimaryCardEnabled = true
        });
        var valid = validator.Validate(null, new()
        {
            Enabled = true,
            PresenceEnabled = true,
            GatewayEnabled = true,
            PresenceAuthorityEnabled = true,
            PrimaryCardGatewayReadsEnabled = true,
            PrimaryCardGatewayActionsEnabled = true,
            TerminalGatewayEnabled = true,
            TerminalGatewayPrimaryCardEnabled = true
        });

        missingAuthority.Failed.Should().BeTrue();
        missingAuthority.FailureMessage.Should().Contain("PrimaryCardGatewayReadsEnabled requires active DEV presence authority");
        actionsWithoutReads.Failed.Should().BeTrue();
        actionsWithoutReads.FailureMessage.Should().Contain("PrimaryCardGatewayActionsEnabled requires PrimaryCardGatewayReadsEnabled");
        terminalWithoutActions.Failed.Should().BeTrue();
        terminalWithoutActions.FailureMessage.Should().Contain("TerminalGatewayPrimaryCardEnabled requires TerminalGatewayEnabled and PrimaryCardGatewayActionsEnabled");
        valid.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void TerminalAuthorityFlag_RequiresTheFencedGatewayPresenceAuthority()
    {
        var validator = new NetRatel.Akka.Configuration.NetRatelAkkaMigrationOptionsValidator();

        var missingGateway = validator.Validate(null, new()
        {
            Enabled = true,
            PresenceEnabled = true,
            GatewayEnabled = true,
            PresenceAuthorityEnabled = true,
            TerminalAuthorityEnabled = true
        });
        var active = new NetRatel.Akka.Configuration.NetRatelAkkaMigrationOptions
        {
            Enabled = true,
            PresenceEnabled = true,
            GatewayEnabled = true,
            PresenceAuthorityEnabled = true,
            TerminalGatewayEnabled = true,
            TerminalAuthorityEnabled = true
        };

        missingGateway.Failed.Should().BeTrue();
        missingGateway.FailureMessage.Should().Contain("TerminalAuthorityEnabled requires active presence authority");
        active.IsTerminalAuthorityActive.Should().BeTrue();
    }

    [Fact]
    public void SignalRAuthorityFlag_RequiresTheFencedPresenceAndFanout()
    {
        var validator = new NetRatel.Akka.Configuration.NetRatelAkkaMigrationOptionsValidator();
        var missingFanout = validator.Validate(null, new()
        {
            Enabled = true,
            PresenceEnabled = true,
            GatewayEnabled = true,
            PresenceAuthorityEnabled = true,
            SignalRAuthorityEnabled = true
        });
        var active = new NetRatel.Akka.Configuration.NetRatelAkkaMigrationOptions
        {
            Enabled = true,
            PresenceEnabled = true,
            GatewayEnabled = true,
            PresenceAuthorityEnabled = true,
            SignalRShadowEnabled = true,
            SignalRShadowLocalCanaryEnabled = true,
            SignalRAuthorityEnabled = true
        };

        missingFanout.Failed.Should().BeTrue();
        missingFanout.FailureMessage.Should().Contain("SignalRAuthorityEnabled requires active presence authority");
        validator.Validate(null, active).Succeeded.Should().BeTrue();
        active.IsSignalRAuthorityActive.Should().BeTrue();
    }

    [Fact]
    public void Registration_IsDisabledByDefault()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();

        services.AddNetRatelAkkaMigration(configuration);

        services.Should().NotContain(descriptor =>
            descriptor.ServiceType == typeof(IClientPresenceRouter));
        services.Should().NotContain(descriptor =>
            descriptor.ServiceType == typeof(IClientTelemetryRouter));
        services.Should().NotContain(descriptor =>
            descriptor.ServiceType == typeof(IClientCommandRouter));
        services.Should().NotContain(descriptor =>
            descriptor.ServiceType == typeof(IJobShadowRouter));
        services.Should().NotContain(descriptor =>
            descriptor.ServiceType == typeof(ITerminalShadowRouter));

        using var provider = services.BuildServiceProvider();
        var healthOptions = provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value;
        healthOptions.Registrations.Should().Contain(registration =>
            registration.Name == "akka-authority-mode");
    }

    [Fact]
    public void Registration_AddsActorRouterAndGatewayHealthWhenEnabled()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["NetRatelAkkaMigration:Enabled"] = "true",
                ["NetRatelAkkaMigration:PresenceEnabled"] = "true",
                ["NetRatelAkkaMigration:GatewayEnabled"] = "true",
                ["NetRatelAkkaMigration:TelemetryShadowEnabled"] = "true",
                ["NetRatelAkkaMigration:CommandShadowEnabled"] = "true",
                ["NetRatelAkkaMigration:ActorSystemName"] = $"NetRatelTests{Guid.NewGuid():N}"
            })
            .Build();

        services.AddNetRatelAkkaMigration(configuration);

        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IClientPresenceRouter));
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IClientTelemetryRouter));
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(IClientCommandRouter));

        using var provider = services.BuildServiceProvider();
        var healthOptions = provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value;
        healthOptions.Registrations.Should().Contain(registration =>
            registration.Name == "akka-authority-mode");
        healthOptions.Registrations.Should().Contain(registration =>
            registration.Name == "akka-presence-shadow");
        healthOptions.Registrations.Should().Contain(registration =>
            registration.Name == "akka-telemetry-shadow");
        healthOptions.Registrations.Should().Contain(registration =>
            registration.Name == "akka-command-shadow");
    }

    [Fact]
    public async Task EnabledRegistration_StartsAndProbesTheLocalActorRoute()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["NetRatelAkkaMigration:Enabled"] = "true",
            ["NetRatelAkkaMigration:PresenceEnabled"] = "true",
            ["NetRatelAkkaMigration:GatewayEnabled"] = "true",
            ["NetRatelAkkaMigration:TelemetryShadowEnabled"] = "true",
            ["NetRatelAkkaMigration:CommandShadowEnabled"] = "true",
            ["NetRatelAkkaMigration:ActorSystemName"] = $"NetRatelTests{Guid.NewGuid():N}"
        });
        builder.Services.AddNetRatelAkkaMigration(builder.Configuration);

        using var host = builder.Build();
        await host.StartAsync();

        var router = host.Services.GetRequiredService<IClientPresenceRouter>();
        var status = await router.ProbeAsync(CancellationToken.None);

        status.Mode.Should().Be("unavailable");
        status.ActiveClientActors.Should().Be(0);

        var telemetryRouter = host.Services.GetRequiredService<IClientTelemetryRouter>();
        var telemetryStatus = await telemetryRouter.ProbeAsync(CancellationToken.None);
        telemetryStatus.Mode.Should().Be("local-shadow");
        telemetryStatus.ActiveTelemetryClients.Should().Be(0);
        telemetryStatus.Authority.Should().Be("unavailable");

        var commandRouter = host.Services.GetRequiredService<IClientCommandRouter>();
        var commandStatus = await commandRouter.ProbeAsync(CancellationToken.None);
        commandStatus.Mode.Should().Be("local-shadow");
        commandStatus.ActiveCommands.Should().Be(0);
        commandStatus.Authority.Should().Be("unavailable");

        await host.StopAsync();
    }

    [Fact]
    public void TelemetryFlag_DoesNotRegisterTelemetryWhenDisabled()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["NetRatelAkkaMigration:Enabled"] = "true",
                ["NetRatelAkkaMigration:PresenceEnabled"] = "true",
                ["NetRatelAkkaMigration:GatewayEnabled"] = "true",
                ["NetRatelAkkaMigration:TelemetryShadowEnabled"] = "false",
                ["NetRatelAkkaMigration:ActorSystemName"] = $"NetRatelTests{Guid.NewGuid():N}"
            })
            .Build();

        services.AddNetRatelAkkaMigration(configuration);

        services.Should().Contain(descriptor => descriptor.ServiceType == typeof(IClientPresenceRouter));
        services.Should().NotContain(descriptor => descriptor.ServiceType == typeof(IClientTelemetryRouter));
    }

    [Fact]
    public void TelemetryFlag_RequiresGatewayFlag()
    {
        var validator = new NetRatel.Akka.Configuration.NetRatelAkkaMigrationOptionsValidator();
        var options = new NetRatel.Akka.Configuration.NetRatelAkkaMigrationOptions
        {
            Enabled = true,
            PresenceEnabled = true,
            GatewayEnabled = false,
            TelemetryShadowEnabled = true
        };

        var result = validator.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("requires GatewayEnabled");
    }

    [Fact]
    public void CommandFlag_DoesNotRegisterCommandRouterWhenDisabled()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["NetRatelAkkaMigration:Enabled"] = "true",
                ["NetRatelAkkaMigration:PresenceEnabled"] = "true",
                ["NetRatelAkkaMigration:GatewayEnabled"] = "true",
                ["NetRatelAkkaMigration:CommandShadowEnabled"] = "false",
                ["NetRatelAkkaMigration:ActorSystemName"] = $"NetRatelTests{Guid.NewGuid():N}"
            })
            .Build();

        services.AddNetRatelAkkaMigration(configuration);

        services.Should().Contain(descriptor => descriptor.ServiceType == typeof(IClientPresenceRouter));
        services.Should().NotContain(descriptor => descriptor.ServiceType == typeof(IClientCommandRouter));
    }

    [Fact]
    public void CommandFlag_RequiresGatewayFlag()
    {
        var validator = new NetRatel.Akka.Configuration.NetRatelAkkaMigrationOptionsValidator();
        var options = new NetRatel.Akka.Configuration.NetRatelAkkaMigrationOptions
        {
            Enabled = true,
            PresenceEnabled = true,
            GatewayEnabled = false,
            CommandShadowEnabled = true
        };

        var result = validator.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("CommandShadowEnabled requires GatewayEnabled");
    }

    [Fact]
    public void CommandAuthority_RequiresTheExplicitPresenceAndGatewayPrerequisites()
    {
        var validator = new NetRatel.Akka.Configuration.NetRatelAkkaMigrationOptionsValidator();
        var invalid = validator.Validate(null, new NetRatel.Akka.Configuration.NetRatelAkkaMigrationOptions
        {
            Enabled = true,
            PresenceEnabled = true,
            GatewayEnabled = true,
            CommandShadowEnabled = true,
            CommandAuthorityEnabled = true
        });
        invalid.Failed.Should().BeTrue();
        invalid.FailureMessage.Should().Contain("requires active presence authority");

        var valid = validator.Validate(null, new NetRatel.Akka.Configuration.NetRatelAkkaMigrationOptions
        {
            Enabled = true,
            PresenceEnabled = true,
            GatewayEnabled = true,
            CommandShadowEnabled = true,
            PresenceAuthorityEnabled = true,
            CommandAuthorityEnabled = true
        });
        valid.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void JobAuthority_RequiresJobShadowAndTheExplicitPresenceGatewayPrerequisites()
    {
        var validator = new NetRatel.Akka.Configuration.NetRatelAkkaMigrationOptionsValidator();
        var invalid = validator.Validate(null, new NetRatel.Akka.Configuration.NetRatelAkkaMigrationOptions
        {
            Enabled = true,
            PresenceEnabled = true,
            GatewayEnabled = true,
            PresenceAuthorityEnabled = true,
            JobAuthorityEnabled = true
        });
        invalid.Failed.Should().BeTrue();
        invalid.FailureMessage.Should().Contain("JobShadowEnabled");

        var valid = validator.Validate(null, new NetRatel.Akka.Configuration.NetRatelAkkaMigrationOptions
        {
            Enabled = true,
            PresenceEnabled = true,
            GatewayEnabled = true,
            JobShadowEnabled = true,
            PresenceAuthorityEnabled = true,
            JobAuthorityEnabled = true
        });
        valid.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void CommandPersistenceFlag_RegistersHealthAndRequiresCommandShadow()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["NetRatelAkkaMigration:Enabled"] = "true",
                ["NetRatelAkkaMigration:PresenceEnabled"] = "true",
                ["NetRatelAkkaMigration:GatewayEnabled"] = "true",
                ["NetRatelAkkaMigration:CommandShadowEnabled"] = "true",
                ["NetRatelAkkaMigration:CommandPersistenceEnabled"] = "true",
                ["NetRatelAkkaMigration:ActorSystemName"] = $"NetRatelTests{Guid.NewGuid():N}"
            })
            .Build();

        services.AddNetRatelCommandPersistence();
        services.AddNetRatelAkkaMigration(configuration);

        using var provider = services.BuildServiceProvider();
        var healthOptions = provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value;
        healthOptions.Registrations.Should().Contain(registration =>
            registration.Name == "akka-command-persistence-shadow");

        var validator = new NetRatel.Akka.Configuration.NetRatelAkkaMigrationOptionsValidator();
        var invalid = validator.Validate(null, new()
        {
            Enabled = true,
            PresenceEnabled = true,
            GatewayEnabled = true,
            CommandShadowEnabled = false,
            CommandPersistenceEnabled = true
        });
        invalid.Failed.Should().BeTrue();
        invalid.FailureMessage.Should().Contain("requires CommandShadowEnabled");
    }

    [Fact]
    public async Task JobShadowFlag_RegistersIndependentLocalObserverAndHealth()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["NetRatelAkkaMigration:Enabled"] = "true",
            ["NetRatelAkkaMigration:PresenceEnabled"] = "false",
            ["NetRatelAkkaMigration:GatewayEnabled"] = "false",
            ["NetRatelAkkaMigration:CommandShadowEnabled"] = "false",
            ["NetRatelAkkaMigration:CommandPersistenceEnabled"] = "false",
            ["NetRatelAkkaMigration:JobShadowEnabled"] = "true",
            ["NetRatelAkkaMigration:ActorSystemName"] = $"NetRatelTests{Guid.NewGuid():N}"
        });
        builder.Services.AddNetRatelJobShadowPersistence();
        builder.Services.AddNetRatelAkkaMigration(builder.Configuration);

        using var host = builder.Build();
        await host.StartAsync();
        var router = host.Services.GetRequiredService<IJobShadowRouter>();
        var status = await router.ProbeAsync(CancellationToken.None);
        var healthOptions = host.Services.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value;

        status.Mode.Should().Be("local-shadow");
        status.ActiveShadowJobs.Should().Be(0);
        status.Authority.Should().Be("unavailable");
        healthOptions.Registrations.Should().Contain(registration =>
            registration.Name == "akka-job-shadow");

        await host.StopAsync();
    }

    [Fact]
    public void JobShadowFlag_RequiresOnlyMigrationMasterFlag()
    {
        var validator = new NetRatel.Akka.Configuration.NetRatelAkkaMigrationOptionsValidator();

        var valid = validator.Validate(null, new()
        {
            Enabled = true,
            JobShadowEnabled = true
        });
        var invalid = validator.Validate(null, new()
        {
            Enabled = false,
            JobShadowEnabled = true
        });

        valid.Succeeded.Should().BeTrue();
        invalid.Failed.Should().BeTrue();
        invalid.FailureMessage.Should().Contain("Enabled must be true");
    }

    [Fact]
    public async Task TerminalShadowFlag_RegistersIndependentLocalObserverAndHealth()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["NetRatelAkkaMigration:Enabled"] = "true",
            ["NetRatelAkkaMigration:TerminalShadowEnabled"] = "true",
            ["NetRatelAkkaMigration:ActorSystemName"] = $"NetRatelTests{Guid.NewGuid():N}"
        });
        builder.Services.AddNetRatelAkkaMigration(builder.Configuration);

        using var host = builder.Build();
        await host.StartAsync();
        var router = host.Services.GetRequiredService<ITerminalShadowRouter>();
        var status = await router.ProbeAsync(CancellationToken.None);
        var sink = host.Services.GetRequiredService<ITerminalShadowObservationSink>();
        var healthOptions = host.Services.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value;

        status.Mode.Should().Be("local-shadow");
        status.ActiveTerminalSessions.Should().Be(0);
        status.Authority.Should().Be("unavailable");
        sink.Should().BeOfType<TerminalShadowObservationQueue>();
        healthOptions.Registrations.Should().Contain(registration =>
            registration.Name == "akka-terminal-shadow");

        await host.StopAsync();
    }

    [Fact]
    public void TerminalShadowFlag_RequiresOnlyMigrationMasterFlag()
    {
        var validator = new NetRatel.Akka.Configuration.NetRatelAkkaMigrationOptionsValidator();

        var valid = validator.Validate(null, new()
        {
            Enabled = true,
            TerminalShadowEnabled = true
        });
        var invalid = validator.Validate(null, new()
        {
            Enabled = false,
            TerminalShadowEnabled = true
        });

        valid.Succeeded.Should().BeTrue();
        invalid.Failed.Should().BeTrue();
        invalid.FailureMessage.Should().Contain("Enabled must be true");
    }

    [Fact]
    public void TerminalShadowFlag_UsesNoOpSinkWhenDisabled()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["NetRatelAkkaMigration:Enabled"] = "true",
                ["NetRatelAkkaMigration:TerminalShadowEnabled"] = "false"
            })
            .Build();

        services.AddNetRatelAkkaMigration(configuration);

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<ITerminalShadowObservationSink>()
            .Should().BeOfType<NullTerminalShadowObservationSink>();
        services.Should().NotContain(descriptor =>
            descriptor.ServiceType == typeof(ITerminalShadowRouter));
    }

    [Theory]
    [InlineData(false, true, false, false, "Enabled must be true")]
    [InlineData(true, false, true, false, "requires PresenceEnabled")]
    [InlineData(true, true, true, true, null)]
    public void TelemetryFeatureFlagRelationships_AreValidated(
        bool enabled,
        bool presenceEnabled,
        bool gatewayEnabled,
        bool telemetryEnabled,
        string? expectedFailure)
    {
        var validator = new NetRatel.Akka.Configuration.NetRatelAkkaMigrationOptionsValidator();
        var options = new NetRatel.Akka.Configuration.NetRatelAkkaMigrationOptions
        {
            Enabled = enabled,
            PresenceEnabled = presenceEnabled,
            GatewayEnabled = gatewayEnabled,
            TelemetryShadowEnabled = telemetryEnabled
        };

        var result = validator.Validate(null, options);

        if (expectedFailure is null)
        {
            result.Succeeded.Should().BeTrue();
        }
        else
        {
            result.Failed.Should().BeTrue();
            result.FailureMessage.Should().Contain(expectedFailure);
        }
    }
}
