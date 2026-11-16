using System.Net;
using System.Net.Http.Json;
using NetRatel.Application.Operations;
using Xunit;

namespace NetRatel.Tests.API;

public sealed partial class McpOperatorOnboardingEndpointTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Enrollment_preview_uses_application_contract_ceiling_and_retains_custom_policy_limits(bool unrestricted)
    {
        var constraints = unrestricted ? McpIntegrationPolicyCatalog.AllAccessLimits.Effective()
            : new McpOperatorConstraints(MaxOnboardingCodeLifetimeSeconds: 600, MaxOnboardingCodeUses: 10);
        using var app = await BuildAppAsync(new RecordingAuthorization(constraints));
        var response = await ClientFor(app, "create_enrollment", 42).PostAsJsonAsync("/api/v2/mcp/operator/tenants/42/onboarding/preview/create-enrollment",
            new { runtime = "linux-x64", validForMinutes = 60, maxUses = 25 });
        Assert.Equal(unrestricted ? HttpStatusCode.OK : HttpStatusCode.BadRequest, response.StatusCode);
    }
}
