using System.Net;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using NetRatel.API.Security.M2M;

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
            path.Key.Should().NotContain("/api/integrations/service-link/");
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
        SecuritySchemes(document, "/api/v2/access/self", "get")
            .Should().BeEquivalentTo(["Bearer", "LocalSession"]);
        SecuritySchemes(document, "/api/v1/client-artifacts", "get")
            .Should().BeEquivalentTo(["Bearer", "LocalSession"]);
        SecuritySchemes(document, "/api/v1/client-artifacts/{rid}/{version}/download", "get")
            .Should().BeEquivalentTo(["Agent", "Bearer", "LocalSession", "M2M"]);
        SecuritySchemes(document, "/api/v1/system/version", "get").Should().BeEmpty();
        SecuritySchemes(document, "/api/pairing/v1/metadata", "get").Should().BeEmpty();
        SecuritySchemes(document, "/api/pairing/v1/exchange", "post").Should().BeEmpty();
        schemes["PairingSetup"]!["type"]!.GetValue<string>().Should().Be("apiKey");
        schemes["PairingSetup"]!["in"]!.GetValue<string>().Should().Be("header");
        schemes["PairingSetup"]!["name"]!.GetValue<string>().Should().Be("Authorization");
        foreach (var (path, method) in new[]
        {
            ("/api/pairing/v1/directory", "get"),
            ("/api/pairing/v1/mappings/{id}", "put"),
            ("/api/pairing/v1/mappings/{id}/test", "post"),
            ("/api/pairing/v1/mappings/{id}", "delete"),
            ("/api/pairing/v1/pair", "delete")
        })
        {
            SecuritySchemes(document, path, method).Should().BeEquivalentTo(["PairingSetup"]);
        }
        foreach (var (path, method) in new[]
        {
            ("/api/v1/admin/system-connections", "get"),
            ("/api/v1/admin/system-connections/code", "post"),
            ("/api/v1/admin/system-connections/pair", "post"),
            ("/api/v1/admin/system-connections/{pairId}/directory", "get"),
            ("/api/v1/admin/system-connections/{pairId}/mappings/{id}", "put"),
            ("/api/v1/admin/system-connections/{pairId}/mappings/{id}/test", "post"),
            ("/api/v1/admin/system-connections/{pairId}/mappings/{id}", "delete"),
            ("/api/v1/admin/system-connections/{pairId}", "delete")
        })
        {
            SecuritySchemes(document, path, method).Should().BeEquivalentTo(["Bearer", "LocalSession"]);
        }
        schemes[ServiceIdentityAuthenticationHandler.SchemeName]!["type"]!.GetValue<string>().Should().Be("http");
        schemes[ServiceIdentityAuthenticationHandler.SchemeName]!["scheme"]!.GetValue<string>().Should().Be("Bearer");
        var orchestrationReadPaths = new[] { "/api/v1/system/m2m/ping", "/internal/health", "/internal/catalog/jobs", "/internal/catalog/tenants", "/internal/catalog/request-definitions" };
        foreach (var path in orchestrationReadPaths)
        {
            SecuritySchemes(document, path, "get")
                .Should().BeEquivalentTo([ServiceIdentityAuthenticationHandler.SchemeName]);
        }
        SecuritySchemes(document, "/internal/ingest", "post")
            .Should().BeEquivalentTo([ServiceIdentityAuthenticationHandler.SchemeName]);
        foreach (var path in new[] { "/internal/catalog/request-definitions", "/internal/catalog/request-definitions/{requestDefinitionId}/inputs/sync" })
        {
            document["paths"]![path]?["post"].Should().BeNull("the retired M2M write operation is absent");
        }

        (await client.GetAsync("/api/v1/system/version")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/api/v1/jobs")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await client.GetAsync("/api/v1/admin/system-connections")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await client.GetAsync("/api/pairing/v1/directory")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await client.PostAsync($"/api/pairing/v1/mappings/{Guid.NewGuid()}/test", new StringContent("{}", System.Text.Encoding.UTF8, "application/json")))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        foreach (var path in orchestrationReadPaths)
        {
            using var protectedRead = await client.GetAsync(path);
            protectedRead.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }
        (await client.PostAsync("/internal/ingest", new StringContent("{}", System.Text.Encoding.UTF8, "application/json")))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private static IReadOnlyCollection<string> SecuritySchemes(JsonObject document, string path, string method)
    {
        var requirements = document["paths"]![path]![method]!["security"]!.AsArray();
        if (requirements.Count > 0)
        {
            requirements.Should().OnlyContain(requirement => requirement != null && requirement.AsObject().Count == 1,
                "each supported authentication scheme is an alternative rather than a combined requirement");
        }
        return requirements.SelectMany(requirement => requirement!.AsObject().Select(pair => pair.Key)).ToArray();
    }
}
