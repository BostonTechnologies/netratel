using System.Net;
using System.Text.Json.Nodes;
using FluentAssertions;

using Xunit;

[Trait("category", "integration")]
[Collection(ApiIntegrationCollection.Name)]
public sealed class ReleaseOpenApiDocumentTests
{
    private readonly ApiFactory _factory;

    public ReleaseOpenApiDocumentTests(ApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Release_document_describes_resolvable_authentication_alternatives_and_runtime_boundaries()
    {
        using var client = _factory.CreateClient();
        using var response = await client.GetAsync("/openapi/v1.json");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var document = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();

        var schemes = document["components"]!["securitySchemes"]!.AsObject();
        var operationIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in document["paths"]!.AsObject())
        {
            path.Key.Should().NotContain("/api/v2/mcp/local-delegation/");
            foreach (var operation in path.Value!.AsObject().Where(pair => pair.Key is "get" or "post" or "put" or "delete" or "patch"))
            {
                var operationId = operation.Value!["operationId"]!.GetValue<string>();
                operationIds.Add(operationId).Should().BeTrue($"{operation.Key.ToUpperInvariant()} {path.Key} needs a unique consumer-facing operation ID");
                foreach (var requirement in operation.Value!["security"]?.AsArray() ?? [])
                {
                    foreach (var securityScheme in requirement!.AsObject())
                    {
                        schemes.ContainsKey(securityScheme.Key).Should().BeTrue($"{operationId} references a declared authentication scheme");
                    }
                }
            }
        }

        SecuritySchemes(document, "/api/v2/account/integration-credentials", "get")
            .Should().BeEquivalentTo(["Bearer", "LocalSession"]);
        SecuritySchemes(document, "/api/v1/client-artifacts", "get")
            .Should().BeEquivalentTo(["Agent", "Bearer", "LocalSession", "M2M"]);
        SecuritySchemes(document, "/api/v1/system/version", "get").Should().BeEmpty();

        (await client.GetAsync("/api/v1/system/version")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/api/v1/jobs")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private static IReadOnlyCollection<string> SecuritySchemes(JsonObject document, string path, string method) => document["paths"]![path]![method]!["security"]!
        .AsArray()
        .SelectMany(requirement => requirement!.AsObject().Select(pair => pair.Key))
        .ToArray();
}
