using System.Security.Cryptography;
using System.Text;

namespace NetRatel.Application.Operations;

/// <summary>
/// A server-verified instance account, not a delegated MCP policy. Only the
/// interactive policy adapter may construct this after current effective-access
/// evaluation. Confirmation binds the account in its distinct resource; it does
/// not create a policy or confer authority on an integration credential.
/// </summary>
public sealed record McpOperatorAccountAuthority(string PrincipalId)
{
    public string Resource => "netratel-web-instance-policy-administration:" +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(PrincipalId)));

    /// <summary>Validates the confirmation binding, not the account's permission.</summary>
    public bool Matches(McpOperatorAccessRequest request) =>
        PrincipalId is { Length: > 0 and <= 256 } && !PrincipalId.Any(char.IsControl) &&
        request.McpResource == Resource && request.Principal.ServicePrincipal == "netratel-web" &&
        request.Tool == "netratel_policy" &&
        request.OperationFamily == McpOperatorOperationFamily.PolicyAdministration &&
        request.ConfirmationClass == McpOperatorConfirmationClass.PolicyAdministration &&
        request.Operation is "netratel_policy/create" or "netratel_policy/replace" or
            "netratel_policy/disable" or "netratel_policy/revoke" or "netratel_policy/delete";
}
