using NetRatel.AgentClient;

namespace NetRatel.Setup;

/// <summary>Keeps a protected review requester's credential outside the destination profile environment.</summary>
internal static class ReviewedAccessRequesterConfiguration
{
    internal const string CredentialEnvironmentVariable = "NETRATEL_MCP_ACCESS_REQUESTER_TOKEN";

    internal static EffectiveAgentClientConfiguration Load(string path, Func<string, string?>? environment = null)
    {
        var token = (environment ?? Environment.GetEnvironmentVariable)(CredentialEnvironmentVariable);
        // The supported launcher supplies this only to its child. Clear the
        // child's copy once selected; subsequent work uses the in-memory value.
        if (environment is null) Environment.SetEnvironmentVariable(CredentialEnvironmentVariable, null);
        if (token is not null && (string.IsNullOrWhiteSpace(token) || token.Length > 16_384 || token.IndexOfAny(['\r', '\n', '\0']) >= 0))
            throw new AgentClientValidationException("The protected reviewed-access requester credential is invalid.");

        var key = NetRatelConfigurationService.EnvironmentNames(ConfigurationSurface.HttpMcpClient, nameof(AgentClientConfiguration.IntegrationCredential))[0];
        // No ambient target, profile, credential-file or primary client setting
        // can redirect this explicit requester configuration or its destination.
        var requesting = new NetRatelConfigurationService().Load(new(ConfigurationSurface.HttpMcpClient, path,
            GetEnvironmentVariable: name => name == key ? token : null));
        if (!requesting.Validate().IsValid)
            throw new AgentClientValidationException("The requesting HTTP MCP profile is invalid.");
        return requesting;
    }
}
