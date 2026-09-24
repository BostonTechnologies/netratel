using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.API.Security.Integration;
using NetRatel.API.Security.Local;
using Xunit;

[Collection(ApiIntegrationCollection.Name)]
public sealed class InteractiveAccountPolicyTests(ApiFactory factory)
{
    [Fact]
    public async Task Credential_discovery_requires_an_interactive_identity_even_when_the_bearer_owner_has_roles()
    {
        var endpoint = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Single(candidate => candidate.RoutePattern.RawText?.EndsWith(
                "/api/v2/account/integration-credentials/tenant-scopes", StringComparison.Ordinal) == true);
        endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()
            .Should().Contain(attribute => attribute.Policy == "InteractiveAccount");

        var authorization = factory.Services.GetRequiredService<IAuthorizationService>();
        var bearer = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("netratel_principal_id", "an-owner"), new Claim("roles", "InstanceAdministrator")],
            IntegrationCredentialAuthenticationHandler.SchemeName));
        var localAccount = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("netratel_principal_id", "an-owner")], LocalAuthenticationOptions.Scheme));

        (await authorization.AuthorizeAsync(bearer, "InteractiveAccount")).Succeeded.Should().BeFalse();
        (await authorization.AuthorizeAsync(localAccount, "InteractiveAccount")).Succeeded.Should().BeTrue();
    }
}
