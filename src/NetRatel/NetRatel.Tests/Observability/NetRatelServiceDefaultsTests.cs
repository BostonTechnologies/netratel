using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Http;
using OpenTelemetry.Instrumentation.AspNetCore;
using OpenTelemetry.Instrumentation.Http;
using Xunit;
using Microsoft.Extensions.Logging;
using NetRatel.Shared.Service.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;

namespace NetRatel.Tests.Observability;

public sealed class NetRatelServiceDefaultsTests
{
    [Fact]
    public void DiagnosticDefaults_EmitWarningAndAbove_OnceToConsoleAndToConsumers()
    {
        var output = new StringWriter();
        var original = Console.Out;
        try
        {
            Console.SetOut(output);
            var builder = Host.CreateApplicationBuilder();
            var consumer = new RecordingProvider();
            var otel = new RecordingExporter();
            builder.Logging.AddProvider(consumer);
            builder.AddServiceDefaults();
            builder.Logging.AddOpenTelemetry(options => options.AddProcessor(new SimpleLogRecordExportProcessor(otel)));
            using var host = builder.Build();
            var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Synthetic.Logging.Defaults");
            logger.LogDebug("logging-default-debug");
            logger.LogInformation("logging-default-info");
            logger.LogWarning("logging-default-warning");
            logger.LogError("logging-default-error");
            logger.LogCritical("logging-default-critical");

            Assert.DoesNotContain("logging-default-debug", output.ToString());
            Assert.DoesNotContain("logging-default-info", output.ToString());
            foreach (var marker in new[] { "logging-default-warning", "logging-default-error", "logging-default-critical" })
            {
                Assert.Equal(1, output.ToString().Split(marker).Length - 1);
                Assert.Single(consumer.Messages, value => value == marker);
                Assert.Single(otel.Messages, value => value == marker);
            }
        }
        finally { Console.SetOut(original); }
    }

    [Fact]
    public void EnvironmentAndCategoryOptIns_ControlEmittedEvents_AndCapabilityFiltersRemain()
    {
        var builder = Host.CreateApplicationBuilder();
        // Prefix isolates process environment used by the actual env provider.
        const string prefix = "NETRATEL_LOG_TEST_";
        Environment.SetEnvironmentVariable(prefix + "NetRatel__Logging__MinimumLevel", "Information");
        Environment.SetEnvironmentVariable(prefix + "NetRatel__Logging__Overrides__Synthetic.Diagnostic", "Debug");
        try
        {
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["NetRatel:Logging:MinimumLevel"] = "Error",
                ["Logging:LogLevel:Default"] = "Critical",
                ["Serilog:MinimumLevel:Default"] = "Fatal"
            });
            builder.Configuration.AddEnvironmentVariables(prefix);
            var consumer = new RecordingProvider();
            builder.Logging.AddProvider(consumer);
            builder.AddNetRatelDiagnosticLogging();
            using var host = builder.Build();
            var factory = host.Services.GetRequiredService<ILoggerFactory>();
            factory.CreateLogger("Synthetic.Other").LogInformation("env-information");
            factory.CreateLogger("Synthetic.Other").LogDebug("disabled-debug");
            factory.CreateLogger("Synthetic.Diagnostic.Child").LogDebug("category-debug");
            factory.CreateLogger("Microsoft.AspNetCore.Hosting.Diagnostics").LogInformation("private-capability-path");
            factory.CreateLogger("Yarp.ReverseProxy.Forwarder").LogInformation("private-proxy-path");
            Assert.Contains("env-information", consumer.Messages);
            Assert.Contains("category-debug", consumer.Messages);
            Assert.DoesNotContain("disabled-debug", consumer.Messages);
            Assert.DoesNotContain("private-capability-path", consumer.Messages);
            Assert.DoesNotContain("private-proxy-path", consumer.Messages);
        }
        finally
        {
            Environment.SetEnvironmentVariable(prefix + "NetRatel__Logging__MinimumLevel", null);
            Environment.SetEnvironmentVariable(prefix + "NetRatel__Logging__Overrides__Synthetic.Diagnostic", null);
        }
    }

    [Theory]
    [InlineData("Noisy")]
    [InlineData("99")]
    public void InvalidDiagnosticLevels_FailDuringStartup(string level)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["NetRatel:Logging:MinimumLevel"] = level });
        Assert.Contains("NetRatel:Logging", Assert.Throws<InvalidOperationException>(() => builder.AddNetRatelDiagnosticLogging()).Message);
    }

    [Fact]
    public void NativeDiagnostics_UseTheSameThresholdAndRetainRuntimeConsumer()
    {
        var directory = Path.Combine(Path.GetTempPath(), "netratel-logging-" + Guid.NewGuid().ToString("N"));
        var original = Console.Out;
        var output = new StringWriter();
        try
        {
            Console.SetOut(output);
            NetRatel.Client.Service.Logging.LogManager.Initialize(directory);
            NetRatel.Client.Service.Logging.LogManager.Configure(new());
            NetRatel.Client.Service.Logging.LogManager.WriteLog(LogLevel.Information, "native-information");
            NetRatel.Client.Service.Logging.LogManager.WriteLog(LogLevel.Warning, "native-warning");
            NetRatel.Client.Service.Logging.LogManager.WriteLog(LogLevel.Critical, "native-critical");
            NetRatel.Client.Service.Logging.LogManager.Configure(new() { Overrides = new() { ["NetRatel.Client.Diagnostic"] = LogLevel.Debug } });
            NetRatel.Client.Service.Logging.LogManager.WriteLog(LogLevel.Debug, "native-debug", "NetRatel.Client.Diagnostic");
            var file = File.ReadAllText(NetRatel.Client.Service.Logging.LogManager.LogFilePath);
            Assert.DoesNotContain("native-information", file);
            foreach (var marker in new[] { "native-warning", "native-critical", "native-debug" })
            {
                Assert.Contains(marker, file);
                Assert.Equal(1, output.ToString().Split(marker).Length - 1);
                Assert.Contains(NetRatel.Client.Service.Logging.ClientRuntimeLogBuffer.Snapshot().Records, record => record.Message == marker);
            }
        }
        finally
        {
            NetRatel.Client.Service.Logging.LogManager.Configure(new());
            Console.SetOut(original);
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    private sealed class RecordingProvider : ILoggerProvider
    {
        public List<string> Messages { get; } = [];
        public ILogger CreateLogger(string categoryName) => new RecordingLogger(Messages);
        public void Dispose() { }
        private sealed class RecordingLogger(List<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => messages.Add(formatter(state, exception));
        }
    }

    private sealed class RecordingExporter : BaseExporter<LogRecord>
    {
        public List<string> Messages { get; } = [];
        public override ExportResult Export(in Batch<LogRecord> batch)
        {
            foreach (var record in batch) Messages.Add(record.FormattedMessage ?? string.Empty);
            return ExportResult.Success;
        }
    }

    [Fact]
    public void ResolveServiceAttributes_UsesOtelEnvironment()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OTEL_SERVICE_NAME"] = "netratel-api-dev",
            ["OTEL_SERVICE_VERSION"] = "0.0.91",
            ["OTEL_RESOURCE_ATTRIBUTES"] = "service.namespace=netratel,deployment.environment=development,host.name=test-host,client.name=netratel"
        });

        var attributes = global::Microsoft.Extensions.Hosting.Extensions.ResolveServiceAttributes(builder);

        attributes.ServiceName.Should().Be("netratel-api-dev");
        attributes.ServiceVersion.Should().Be("0.0.91");
        attributes.ServiceNamespace.Should().Be("netratel");
        attributes.DeploymentEnvironment.Should().Be("development");
        attributes.HostName.Should().Be("test-host");
        attributes.ClientName.Should().Be("netratel");
    }

    [Fact]
    public void AddServiceDefaults_WithOtlpEndpoint_RegistersOpenTelemetryServices()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OTEL_SERVICE_NAME"] = "netratel-web-dev",
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "http://otel.example.invalid:4318",
            ["OTEL_EXPORTER_OTLP_PROTOCOL"] = "http/protobuf",
            ["OTEL_RESOURCE_ATTRIBUTES"] = "service.namespace=netratel,deployment.environment=development,host.name=test-host,client.name=netratel"
        });

        builder.AddServiceDefaults();

        builder.Services.Any(descriptor =>
            descriptor.ServiceType.FullName is { } typeName &&
            typeName.Contains("OpenTelemetry", StringComparison.OrdinalIgnoreCase))
            .Should()
            .BeTrue();
    }

    [Fact]
    public void AddServiceDefaults_ExcludesPublicInstallCapabilitiesFromHttpTraces()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.AddServiceDefaults();
        using var host = builder.Build();
        _ = host.Services.GetRequiredService<OpenTelemetry.Trace.TracerProvider>();
        var inbound = host.Services.GetRequiredService<IOptions<AspNetCoreTraceInstrumentationOptions>>().Value;
        var outbound = host.Services.GetRequiredService<IOptions<HttpClientTraceInstrumentationOptions>>().Value;
        var context = new DefaultHttpContext();
        context.Request.Path = "/clients/install/opaque-token.sh";

        Assert.False(inbound.Filter!(context));
        Assert.False(outbound.FilterHttpRequestMessage!(new HttpRequestMessage(
            HttpMethod.Get, "https://api.example.test/clients/install/opaque-token.sh")));
        context.Request.Path = "/api/v1/clients";
        Assert.True(inbound.Filter(context));
        Assert.True(outbound.FilterHttpRequestMessage(new HttpRequestMessage(
            HttpMethod.Get, "https://api.example.test/api/v1/clients")));
    }
}
