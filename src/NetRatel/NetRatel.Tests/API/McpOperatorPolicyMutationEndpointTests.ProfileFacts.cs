using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using NetRatel.API.Endpoints.Client;
using NetRatel.Application.Operations;
using NetRatel.Infrastructure.Services;
using Xunit;

namespace NetRatel.Tests.API;

public sealed partial class McpOperatorPolicyMutationEndpointTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Policy_administration_distinguishes_absent_and_invalid_profile_facts(bool invalid)
    {
        var draft = ExactAgentDraft();
        await using var db = await CreatePolicyDbAsync("create", draft.TargetSelector);
        var parent = await db.McpOperatorPolicies.SingleAsync();
        parent.TargetClassification = null;
        parent.ConstraintsJson = JsonSerializer.Serialize(new McpOperatorConstraints(), McpOperatorJsonContext.Default.McpOperatorConstraints);
        await db.SaveChangesAsync();
        var administration = new RecordingAdministration { CurrentTargetProfile = invalid ? TargetProfile(draft.TargetSelector) with { InvalidFactsDigest = new string('A', 64) } : null };
        using var app = await BuildAppAsync(new McpOperatorAuthorization(db), administration, new McpOperatorConfirmationService(db));
        var client = M2mClient(app);
        SetDelegation(client, app, "preview_create", draft.TargetSelector, ["PolicyAdministrator"], ["netratel.mcp.admin"]);
        var response = await client.PostAsJsonAsync("/api/v2/mcp/operator/policy/create/preview", new PreviewMcpOperatorPolicyCreateRequest(draft));
        response.StatusCode.Should().Be(invalid ? HttpStatusCode.Forbidden : HttpStatusCode.OK);
        if (invalid) (await response.Content.ReadAsStringAsync()).Should().Contain("target_profile_invalid");
        administration.Creates.Should().Be(0);
        (await db.McpOperatorConfirmationPlans.CountAsync()).Should().Be(invalid ? 0 : 1);
    }
}
