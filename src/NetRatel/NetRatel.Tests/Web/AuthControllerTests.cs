using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using NetRatel.Web.Configuration;
using NetRatel.Web.Controllers;
using Xunit;

namespace NetRatel.Tests.Web;

public sealed class AuthControllerTests
{
    [Fact]
    public void Oidc_login_challenges_the_provider_neutral_scheme_and_preserves_a_local_return_url()
    {
        var result = CreateController().OidcLogin("/clients").Should().BeOfType<ChallengeResult>().Subject;

        result.AuthenticationSchemes.Should().ContainSingle().Which.Should().Be("Oidc");
        result.Properties.Should().NotBeNull();
        result.Properties!.RedirectUri.Should().Be("/clients");
    }

    [Theory]
    [InlineData("https://attacker.example.invalid")]
    [InlineData("//attacker.example.invalid")]
    public void Oidc_login_rejects_non_local_return_urls(string returnUrl)
    {
        var result = CreateController().OidcLogin(returnUrl).Should().BeOfType<ChallengeResult>().Subject;

        result.Properties.Should().NotBeNull();
        result.Properties!.RedirectUri.Should().Be("/");
    }

    [Fact]
    public void Oidc_login_is_not_exposed_when_the_deployment_has_no_oidc_configuration()
    {
        CreateController(oidcConfigured: false).OidcLogin("/clients").Should().BeOfType<NotFoundResult>();
    }

    private static AuthController CreateController(bool oidcConfigured = true)
    {
        var configurationValues = oidcConfigured
            ? new Dictionary<string, string?> { ["Authentication:Oidc:Authority"] = "https://issuer.example.invalid" }
            : new Dictionary<string, string?>();

        var controller = new AuthController(
            null!,
            new ConfigurationBuilder().AddInMemoryCollection(configurationValues).Build(),
            null!,
            Options.Create(new MachineTokenOptions()),
            null!)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext(),
                RouteData = new RouteData()
            }
        };
        controller.Url = new UrlHelper(controller.ControllerContext);
        return controller;
    }
}
