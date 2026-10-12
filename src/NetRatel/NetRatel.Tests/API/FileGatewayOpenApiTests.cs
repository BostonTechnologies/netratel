using System.Net;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.OpenApi;
using NetRatel.API.Endpoints.Client;
using NetRatel.API.OpenApi;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class FileGatewayOpenApiTests
{
    [Fact]
    public async Task Generated_file_document_preserves_exact_endpoint_policies_and_account_authentication_alternatives()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Production,
            ApplicationName = typeof(AgentFileGatewayEndpoints).Assembly.FullName
        });
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthorization();
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddOpenApi(options =>
        {
            options.AddDocumentTransformer((document, _, _) =>
            {
                document.Components ??= new OpenApiComponents();
                document.Components.SecuritySchemes = new Dictionary<string, IOpenApiSecurityScheme>
                {
                    ["Bearer"] = new OpenApiSecurityScheme { Type = SecuritySchemeType.Http, Scheme = "Bearer" },
                    ["IntegrationCredential"] = new OpenApiSecurityScheme { Type = SecuritySchemeType.Http, Scheme = "Bearer" },
                    ["LocalSession"] = new OpenApiSecurityScheme { Type = SecuritySchemeType.ApiKey, In = ParameterLocation.Cookie, Name = "session" }
                };
                return Task.CompletedTask;
            });
            options.AddOperationTransformer(NetRatelOpenApiCatalog.TransformOperationAsync);
        });

        await using var app = builder.Build();
        app.MapAgentFileGatewayEndpoints();
        app.MapOpenApi().AllowAnonymous();
        await app.StartAsync();
        using var client = app.GetTestClient();
        using var response = await client.GetAsync("/openapi/v1.json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var document = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
        var endpoints = app.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToArray();
        foreach (var (method, suffix, policy) in new[]
        {
            ("get", "/", "FileReader"),
            ("get", "/file", "FileReader"),
            ("put", "/file", "FileWriter"),
            ("post", "/directory", "FileWriter"),
            ("delete", "/directory", "FileDeleter")
        })
        {
            var route = "/api/v2/agents/{tenantId:int}/{agentId:guid}/filesystem" + suffix;
            var endpoint = endpoints.Single(endpoint => endpoint.RoutePattern.RawText == route &&
                endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Contains(method.ToUpperInvariant()));
            endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(data => data.Policy)
                .Should().Equal(policy);

            var path = ("/api/v2/agents/{tenantId}/{agentId}/filesystem" + suffix).TrimEnd('/');
            document["paths"]!.AsObject().Select(pair => pair.Key).Should().Contain(path);
            var requirements = document["paths"]![path]![method]!["security"]!.AsArray();
            requirements.Should().OnlyContain(requirement => requirement != null && requirement.AsObject().Count == 1,
                "supported authentication schemes are independent alternatives");
            requirements.SelectMany(requirement => requirement!.AsObject().Select(pair => pair.Key))
                .Should().BeEquivalentTo(["Bearer", "LocalSession", "IntegrationCredential"]);
        }
    }
}
