using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NetRatel.Infrastructure.Identity.Branding;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

public sealed class ClientInstallationOptionsTests
{
    [Fact]
    public void Empty_compose_variables_leave_administrator_addresses_editable()
    {
        using var services = Build(new()
        {
            ["Branding:SiteUrl"] = "",
            ["Branding:GatewayUrl"] = " ",
            ["ClientArtifacts:PublicBaseUrl"] = "",
            ["ClientArtifacts:PublicGatewayBaseUrl"] = ""
        });
        var branding = services.GetRequiredService<IOptionsMonitor<DeploymentBrandingOptions>>().CurrentValue;
        branding.SiteUrl.Should().BeNull();
        branding.GatewayUrl.Should().BeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Canonical_and_compatibility_gateway_configuration_are_deployment_owned(bool legacy)
    {
        using var services = Build(new()
        {
            ["Branding:SiteUrl"] = "https://web.example.test",
            [legacy ? "ClientArtifacts:PublicGatewayBaseUrl" : "Branding:GatewayUrl"] = "https://gateway.example.test/"
        });
        var branding = services.GetRequiredService<IOptionsMonitor<DeploymentBrandingOptions>>().CurrentValue;
        branding.SiteUrl.Should().Be("https://web.example.test");
        branding.GatewayUrl.Should().BeOneOf("https://gateway.example.test", "https://gateway.example.test/");
    }

    [Fact]
    public void Conflicting_gateway_aliases_fail_without_disclosing_values()
    {
        using var services = Build(new()
        {
            ["Branding:GatewayUrl"] = "https://one.example.test",
            ["ClientArtifacts:PublicGatewayBaseUrl"] = "https://two.example.test"
        });
        var action = () => services.GetRequiredService<IOptionsMonitor<DeploymentBrandingOptions>>().CurrentValue;
        action.Should().Throw<OptionsValidationException>().WithMessage("*conflicts*");
    }

    [Fact]
    public void Configuration_reload_updates_typed_options()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Branding:GatewayUrl"] = "https://first.example.test"
        }).Build();
        using var services = new ServiceCollection().AddClientInstallationOptions(configuration).BuildServiceProvider();
        var monitor = services.GetRequiredService<IOptionsMonitor<DeploymentBrandingOptions>>();
        monitor.CurrentValue.GatewayUrl.Should().Be("https://first.example.test");
        configuration["Branding:GatewayUrl"] = "https://second.example.test";
        configuration.Reload();
        monitor.CurrentValue.GatewayUrl.Should().Be("https://second.example.test");
    }

    [Theory]
    [InlineData("Branding:GatewayUrl", "http://api:9223")]
    [InlineData("Branding:SiteUrl", "https://localhost")]
    [InlineData("Branding:GatewayUrl", "https://gateway.example.test:9223")]
    [InlineData("Branding:GatewayUrl", "https://gateway.example.test/grpc")]
    [InlineData("ClientArtifacts:PublicBaseUrl", "https://user:secret@api.example.test")]
    [InlineData("ClientArtifacts:PublicGatewayBaseUrl", "https://gateway.example.test?secret=fixture")]
    public async Task Invalid_deployment_addresses_fail_at_startup(string key, string value)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { [key] = value });
        builder.Services.AddClientInstallationOptions(builder.Configuration);
        using var host = builder.Build();
        await FluentActions.Invoking(() => host.StartAsync()).Should().ThrowAsync<OptionsValidationException>();
    }

    private static ServiceProvider Build(Dictionary<string, string?> values) =>
        new ServiceCollection().AddClientInstallationOptions(
            new ConfigurationBuilder().AddInMemoryCollection(values).Build()).BuildServiceProvider();
}
