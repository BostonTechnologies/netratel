using NetRatel.Application.Operations;

namespace NetRatel.API.Endpoints.Client;

/// <summary>
/// Resolves the operator-policy environment from the API host. This is an
/// infrastructure fact, not caller input: a signed Dev assertion can only be
/// consumed by a Development host and a signed Production assertion can only
/// be consumed by a Production host.
/// </summary>
internal static class McpOperatorRuntimeEnvironment
{
    public static bool TryResolve(
        IHostEnvironment environment,
        out McpOperatorEnvironment operatorEnvironment,
        out string instance)
    {
        ArgumentNullException.ThrowIfNull(environment);

        if (environment.IsDevelopment())
        {
            operatorEnvironment = McpOperatorEnvironment.Development;
            instance = "dev";
            return true;
        }

        if (environment.IsProduction())
        {
            operatorEnvironment = McpOperatorEnvironment.Production;
            instance = "prod";
            return true;
        }

        operatorEnvironment = default;
        instance = string.Empty;
        return false;
    }
}
