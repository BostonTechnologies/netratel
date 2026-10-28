using System.Net;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NetRatel.API.Endpoints;
using NetRatel.API.Endpoints.Client;
using NetRatel.API.Middleware;
using NetRatel.API.Services.ComputerUse;
using NetRatel.Application.Operations;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Shared.Contracts.ComputerUse;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class ComputerUseRequestBodyLimitTests
{
    [Theory]
    [InlineData("/api/v2/mcp/operator/agents/1/58453055-90d9-4347-aa34-4fc4423b8a4a/computer-use/sessions/confirm")]
    [InlineData("/api/v2/agents/1/58453055-90d9-4347-aa34-4fc4423b8a4a/computer-use/session/34c7ca0c-ce54-448a-aa7b-125a559d8324/terminate")]
    public async Task Cua_middleware_replaces_global_upload_limit_before_body_binding(string path)
    {
        using var host = await new HostBuilder().ConfigureWebHost(web => web.UseTestServer()
            .ConfigureServices(services =>
            {
                services.AddRouting();
                services.AddOptions<ComputerUseOptions>();
                services.AddSingleton(TimeProvider.System);
                // Handler resolution must not run before routing establishes the request bound.
                services.AddSingleton<ComputerUseOperatorService>(_ => throw new InvalidOperationException("Endpoint executed before the bound assertion."));
                services.AddSingleton<IEffectiveAccessService>(_ => throw new InvalidOperationException("Endpoint executed before the bound assertion."));
                services.AddSingleton<IComputerUseSessionStore>(_ => throw new InvalidOperationException("Endpoint executed before the bound assertion."));
                services.AddSingleton<IComputerUseGateway>(_ => throw new InvalidOperationException("Endpoint executed before the bound assertion."));
            })
            .Configure(app =>
            {
                app.Use((http, next) =>
                {
                    http.Features.Set<IHttpMaxRequestBodySizeFeature>(new RequestSizeFeature());
                    return next(http);
                });
                app.UseRouting();
                app.UseMiddleware<ComputerUseRequestBodyLimitMiddleware>();
                app.Use((HttpContext http, RequestDelegate _) =>
                {
                    http.GetEndpoint().Should().NotBeNull();
                    http.Features.Get<IHttpMaxRequestBodySizeFeature>()!.MaxRequestBodySize
                        .Should().Be(ComputerUseContract.MaximumRequestBytes);
                    http.Response.StatusCode = StatusCodes.Status204NoContent;
                    return Task.CompletedTask;
                });
                app.UseEndpoints(routes =>
                {
                    routes.MapMcpOperatorComputerUseEndpoints();
                    routes.MapComputerUseOperatorEndpoints();
                });
            })).StartAsync();
        using var client = host.GetTestClient();
        using var response = await client.PostAsync(path, new StringContent("{}"));
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Theory]
    [InlineData("/api/v2/mcp/operator/agents/1/58453055-90d9-4347-aa34-4fc4423b8a4a/computer-use/sessions/confirm", false)]
    [InlineData("/api/v2/mcp/operator/agents/1/58453055-90d9-4347-aa34-4fc4423b8a4a/computer-use/sessions/confirm", true)]
    [InlineData("/api/v2/agents/1/58453055-90d9-4347-aa34-4fc4423b8a4a/computer-use/session/34c7ca0c-ce54-448a-aa7b-125a559d8324/terminate", false)]
    [InlineData("/API/V2/AGENTS/1/58453055-90d9-4347-aa34-4fc4423b8a4a/COMPUTER-USE/session/34c7ca0c-ce54-448a-aa7b-125a559d8324/terminate", true)]
    public async Task Known_length_and_chunked_bodies_are_rejected_before_binding_completes(string path, bool chunked)
    {
        var http = new DefaultHttpContext();
        http.Request.Path = path;
        http.Request.Method = HttpMethods.Post;
        http.Request.ContentType = "application/json";
        var oversized = Encoding.UTF8.GetBytes("{\"idempotencyKey\":\"" + new string('x', ComputerUseContract.MaximumRequestBytes) + "\"}");
        var original = new MemoryStream(oversized);
        http.Request.Body = original;
        if (!chunked) http.Request.ContentLength = oversized.Length;
        http.Response.Body = new MemoryStream();
        var enteredBinding = false;
        var bound = false;
        var middleware = new ComputerUseRequestBodyLimitMiddleware(async context =>
        {
            enteredBinding = true;
            await context.Request.ReadFromJsonAsync<ComputerUseControlRequest>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
            bound = true;
        });

        await middleware.InvokeAsync(http);

        http.Response.StatusCode.Should().Be(StatusCodes.Status413PayloadTooLarge);
        http.Response.Headers.CacheControl.ToString().Should().Be("no-store");
        enteredBinding.Should().Be(chunked);
        bound.Should().BeFalse();
        http.Request.Body.Should().BeSameAs(original);
        original.Position.Should().BeLessThanOrEqualTo(ComputerUseContract.MaximumRequestBytes + 1L);
        http.Response.Body.Position = 0;
        var body = await new StreamReader(http.Response.Body).ReadToEndAsync();
        body.Should().Contain("computer_use_request_too_large").And.NotContain(new string('x', 100));
    }

    private sealed class RequestSizeFeature : IHttpMaxRequestBodySizeFeature
    {
        public bool IsReadOnly => false;
        public long? MaxRequestBodySize { get; set; } = 1_000_000_000;
    }
}
