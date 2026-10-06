using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NetRatel.Infrastructure.ServiceIdentity;
using NetRatel.Infrastructure.ServiceLinks;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

public sealed class ServiceIdentityHttpOptInBoundaryTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void One_deployment_section_opt_in_validates_both_raw_public_identity_profiles(bool identity, bool linking)
    {
        var configuration = Configuration(identity, linking, "http");
        using var services = Services(configuration);
        Assert.True(services.GetRequiredService<IOptionsMonitor<ServiceIdentityOptions>>().CurrentValue.AllowPrivateHttp);
        Assert.True(services.GetRequiredService<IOptionsMonitor<ServiceLinkOptions>>().CurrentValue.AllowPrivateHttp);
    }

    [Fact]
    public void Explicit_http_without_either_deployment_opt_in_is_rejected()
    {
        using var services = Services(Configuration(false, false, "http"));
        Assert.Throws<OptionsValidationException>(() => services.GetRequiredService<IOptionsMonitor<ServiceIdentityOptions>>().CurrentValue);
        Assert.Throws<OptionsValidationException>(() => services.GetRequiredService<IOptionsMonitor<ServiceLinkOptions>>().CurrentValue);
    }

    [Fact]
    public void Configuration_reload_removes_the_opt_in_from_both_monitored_snapshots()
    {
        var configuration = Configuration(false, true, "https");
        using var services = Services(configuration);
        var identity = services.GetRequiredService<IOptionsMonitor<ServiceIdentityOptions>>();
        var linking = services.GetRequiredService<IOptionsMonitor<ServiceLinkOptions>>();
        Assert.True(identity.CurrentValue.AllowPrivateHttp);
        Assert.True(linking.CurrentValue.AllowPrivateHttp);
        configuration["ServiceLinks:AllowPrivateHttp"] = "false";
        configuration.Reload();
        Assert.False(identity.CurrentValue.AllowPrivateHttp);
        Assert.False(linking.CurrentValue.AllowPrivateHttp);
    }

    private static IConfigurationRoot Configuration(bool identity, bool linking, string scheme) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ServiceIdentity:Enabled"] = "true", ["ServiceLinks:Enabled"] = "true",
            ["ServiceIdentity:Issuer"] = $"{scheme}://api.example.test/services",
            ["ServiceIdentity:ApiBaseUrl"] = $"{scheme}://api.example.test",
            ["ServiceIdentity:WebBaseUrl"] = $"{scheme}://web.example.test",
            ["ServiceLinks:ApiBaseUrl"] = $"{scheme}://api.example.test",
            ["ServiceLinks:WebBaseUrl"] = $"{scheme}://web.example.test",
            ["ServiceIdentity:AllowPrivateHttp"] = identity.ToString(),
            ["ServiceLinks:AllowPrivateHttp"] = linking.ToString()
        }).Build();

    private static ServiceProvider Services(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddNetRatelServiceIdentity(configuration);
        services.AddOptions<ServiceLinkOptions>().Bind(configuration.GetSection(ServiceLinkOptions.SectionName));
        services.AddSingleton<IValidateOptions<ServiceLinkOptions>, ServiceLinkOptionsValidator>();
        return services.BuildServiceProvider();
    }
}
