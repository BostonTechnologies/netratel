using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Http;
using OpenTelemetry.Instrumentation.AspNetCore;
using OpenTelemetry.Instrumentation.Http;
using Xunit;

namespace NetRatel.Tests.Observability;

public sealed class NetRatelServiceDefaultsTests
{
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
