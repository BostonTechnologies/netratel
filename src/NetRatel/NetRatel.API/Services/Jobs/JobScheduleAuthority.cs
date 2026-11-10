using System.Security.Claims;
using System.Text.Json.Nodes;

namespace NetRatel.API.Services.Jobs;

/// <summary>Server-owned configuring identity for future schedule firings; carries no bearer token or role claim.</summary>
public static class JobScheduleAuthority
{
    public static string Capture(string optionsJson, ClaimsPrincipal principal)
    {
        var root = JsonNode.Parse(optionsJson) as JsonObject ?? new();
        if (JobSchedulePolicy.FromOptionsJson(optionsJson) is null)
        {
            root.Remove("scheduleAuthority");
            return root.ToJsonString();
        }
        var id = principal.FindFirstValue("netratel_principal_id");
        if (string.IsNullOrWhiteSpace(id)) throw new InvalidOperationException("An enabled schedule requires its current configuring principal.");
        root["scheduleAuthority"] = new JsonObject
        {
            ["principalId"] = id,
            ["integrationCredentialId"] = principal.FindFirstValue("netratel_integration_credential_id")
        };
        return root.ToJsonString();
    }

    public static ClaimsPrincipal? Read(string? optionsJson)
    {
        if (JsonNode.Parse(optionsJson ?? "{}") is not JsonObject root || root["scheduleAuthority"] is not JsonObject authority ||
            authority["principalId"]?.GetValue<string>() is not { Length: > 0 } principal) return null;
        var claims = new List<Claim> { new("netratel_principal_id", principal) };
        if (authority["integrationCredentialId"]?.GetValue<string>() is { Length: > 0 } credential)
            claims.Add(new("netratel_integration_credential_id", credential));
        return new(new ClaimsIdentity(claims, "ScheduledJobAdmission"));
    }
}
