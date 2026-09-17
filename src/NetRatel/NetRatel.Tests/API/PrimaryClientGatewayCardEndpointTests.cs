using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.API.Endpoints.Client;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class PrimaryClientGatewayCardEndpointTests
{
    [Fact]
    public void Routes_Can_Be_Built_Without_The_Optional_Control_Gateway()
    {
        var builder = WebApplication.CreateBuilder();
        var app = builder.Build();

        app.MapPrimaryClientGatewayCardReadEndpoints();

        var endpoints = app.Services.GetRequiredService<EndpointDataSource>();
        var action = () => _ = endpoints.Endpoints;

        action.Should().NotThrow();
    }
}
