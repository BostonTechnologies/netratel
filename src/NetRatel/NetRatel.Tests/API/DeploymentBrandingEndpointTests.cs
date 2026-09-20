using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.API.Endpoints.Auth;
using NetRatel.Infrastructure.Identity.Branding;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class DeploymentBrandingEndpointTests
{
    [Fact]
    public void Public_projection_and_asset_reads_are_anonymous_but_every_mutation_requires_instance_administration()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddScoped<IDeploymentBrandingService>(_ => throw new NotSupportedException());
        var app = builder.Build();

        app.MapDeploymentBrandingEndpoints();

        var endpoints = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>().ToArray();
        endpoints.Select(endpoint => endpoint.RoutePattern.RawText).Should().Contain([
            "/api/v2/branding", "/api/v2/branding/assets/{assetId}", "/api/v2/branding/assets"
        ]);
        endpoints.Single(endpoint => endpoint.RoutePattern.RawText == "/api/v2/branding" && endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            .Metadata.GetMetadata<IAllowAnonymous>().Should().NotBeNull();
        endpoints.Where(endpoint => endpoint.RoutePattern.RawText is "/api/v2/branding" or "/api/v2/branding/assets")
            .Where(endpoint => endpoint.Metadata.GetMetadata<IAllowAnonymous>() is null)
            .SelectMany(endpoint => endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>())
            .Select(metadata => metadata.Policy)
            .Should().Contain("InstanceAdministrator");
    }
}
