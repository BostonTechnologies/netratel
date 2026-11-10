using System.Text;
using NetRatel.API.Middleware;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class JobTargetBodyBoundsTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Target_bounds_reject_oversized_declared_and_chunked_body_before_json_binding(bool declaredLength)
    {
        var http = new DefaultHttpContext();
        http.SetEndpoint(new Endpoint(_ => Task.CompletedTask,
            new EndpointMetadataCollection(new MonitoringHttpBounds { MaximumBytes = 64, ErrorCode = "target_payload_capacity_exceeded" }), "target"));
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(new string('x', 65)));
        if (declaredLength) http.Request.ContentLength = 65;
        http.Response.Body = new MemoryStream();
        var bound = new MonitoringHttpBoundsMiddleware(async context =>
        {
            using var buffer = new MemoryStream();
            await context.Request.Body.CopyToAsync(buffer);
            context.Response.StatusCode = StatusCodes.Status200OK;
        });
        await bound.InvokeAsync(http);
        Assert.Equal(StatusCodes.Status413PayloadTooLarge, http.Response.StatusCode);
        http.Response.Body.Position = 0;
        Assert.Contains("target_payload_capacity_exceeded", await new StreamReader(http.Response.Body).ReadToEndAsync());
    }
}
