using Microsoft.Extensions.Options;

namespace NetRatel.Web.Configuration;

public sealed class MachineTokenOptionsValidator : IValidateOptions<MachineTokenOptions>
{
    public ValidateOptionsResult Validate(string? name, MachineTokenOptions options)
    {
        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        var errors = new List<string>();

        if (!Uri.TryCreate(options.Authority, UriKind.Absolute, out var authorityUri)
            || authorityUri.Scheme != Uri.UriSchemeHttps)
        {
            errors.Add("Authentication:MachineToken:Authority must be an absolute HTTPS URI.");
        }

        if (string.IsNullOrWhiteSpace(options.Audience))
        {
            errors.Add("Authentication:MachineToken:Audience is required when machine-token authentication is enabled.");
        }

        if (options.RequiredGroups.All(string.IsNullOrWhiteSpace))
        {
            errors.Add("Authentication:MachineToken:RequiredGroups must contain at least one group when machine-token authentication is enabled.");
        }

        if (options.AllowedSigningAlgorithms.All(string.IsNullOrWhiteSpace))
        {
            errors.Add("Authentication:MachineToken:AllowedSigningAlgorithms must contain at least one algorithm when machine-token authentication is enabled.");
        }

        return errors.Count > 0
            ? ValidateOptionsResult.Fail(errors)
            : ValidateOptionsResult.Success;
    }
}
