using Microsoft.AspNetCore.Builder;

namespace NetRatel.API.Endpoints;

public static class DocumentationEndpoints
{
    public static WebApplication MapDocumentationEndpoints(this WebApplication app)
    {
        app.MapOpenApi().AllowAnonymous();

        return app;
    }
}
