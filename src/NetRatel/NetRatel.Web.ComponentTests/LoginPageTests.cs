using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using System.Net;
using System.Net.Http.Json;
using Xunit;
using NetRatel.Web.Components.Pages;

namespace NetRatel.Web.ComponentTests;

public class LoginPageTests : AsyncBunitContext
{
    [Fact]
    public void LoginPage_DisplaysProductBrandingAndProviderNeutralSignIn()
    {
        var cut = RenderLogin(environmentLabel: "Development system");

        var html = cut.Markup;
        html.Should().Contain("NetRatel");
        html.Should().Contain("Automation Platform");
        html.Should().Contain("Development system");
        html.Should().Contain("organization account");
        html.Should().Contain("Sign in");
        html.Should().NotContain("Azure");
    }

    [Fact]
    public void LoginPage_HidesDevelopmentOperatorInProduction()
    {
        var cut = RenderLogin(
            environmentName: Environments.Production,
            developmentOperatorEnabled: true);

        cut.Markup.Should().NotContain("Sign in as local operator");
    }

    [Fact]
    public void LoginPage_ShowsDevelopmentOperatorOnlyWhenDevelopmentAccessEnabled()
    {
        var cut = RenderLogin(
            environmentName: Environments.Development,
            developmentOperatorEnabled: true);

        cut.Markup.Should().Contain("Sign in as local operator");
    }

    [Fact]
    public void LoginPage_PreservesReturnUrlInAuthLinks()
    {
        var cut = RenderLogin(
            returnUrl: "/clients",
            environmentName: Environments.Development,
            developmentOperatorEnabled: true);

        cut.Markup.Should().Contain("href=\"/auth/oidc?returnUrl=%2Fclients\"");
        cut.Markup.Should().Contain("href=\"/auth/development?returnUrl=%2Fclients\"");
    }

    private IRenderedComponent<Login> RenderLogin(
        string environmentLabel = "Production system",
        string environmentName = "Production",
        bool developmentOperatorEnabled = false,
        string? returnUrl = null)
    {
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DevelopmentOperator:Enabled"] = developmentOperatorEnabled.ToString(),
                ["LoginUi:EnvironmentLabel"] = environmentLabel,
                ["Authentication:Oidc:Authority"] = "https://issuer.example.invalid"
            })
            .Build());
        Services.AddSingleton<IHostEnvironment>(new TestHostEnvironment(environmentName));
        Services.AddSingleton<IHttpClientFactory>(new BootstrapStatusHttpClientFactory());

        AddAuthorization();

        if (!string.IsNullOrWhiteSpace(returnUrl))
        {
            var nav = Services.GetRequiredService<NavigationManager>();
            nav.NavigateTo($"/login?ReturnUrl={Uri.EscapeDataString(returnUrl)}");
        }

        return Render<Login>();
    }

    private sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "NetRatel.Web.ComponentTests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class BootstrapStatusHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new BootstrapStatusHandler())
        {
            BaseAddress = new Uri("https://netratel.test/")
        };

        private sealed class BootstrapStatusHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new { setupRequired = false, isRecoveryRequired = false })
                });
        }
    }
}
