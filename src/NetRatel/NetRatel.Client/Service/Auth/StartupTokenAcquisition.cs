using System;
using System.Threading;
using System.Threading.Tasks;
using NetRatel.Application.ClientAuth;

namespace NetRatel.Client.Service.Auth;

/// <summary>
/// Acquires the startup token and permits one same-start enrollment recovery
/// only for the server's structured terminal Agent-not-found response.
/// </summary>
public static class StartupTokenAcquisition
{
    public static async Task<(string AccessToken, DateTimeOffset ExpiresAtUtc)> GetAccessTokenAsync(
        ClientOptions options,
        IAgentTokenService tokenService,
        IAgentEnrollmentService enrollmentService,
        IAgentCredentialStore credentialStore,
        IInjectedEnrollmentBootstrap injectedEnrollmentBootstrap,
        Action<string> log,
        CancellationToken ct)
    {
        try
        {
            return await tokenService.GetAccessTokenAsync(ct).ConfigureAwait(false);
        }
        catch (AgentClientAuthException exception) when (IsAgentNotFound(exception))
        {
            var replacement = await TryRecoverOnceAsync(
                options, enrollmentService, credentialStore, injectedEnrollmentBootstrap, ct).ConfigureAwait(false);
            if (replacement is null)
            {
                throw new AgentClientAuthException(
                    "The stored Agent identity no longer exists. Supply a valid enrollment code or netratel.enroll.json to recover this installation.",
                    exception.StatusCode,
                    shouldClearCredentials: false,
                    code: exception.Code,
                    endpointRole: exception.EndpointRole,
                    failureKind: exception.FailureKind,
                    retryAfter: exception.RetryAfter,
                    retryAfterWasCapped: exception.RetryAfterWasCapped,
                    innerException: exception);
            }

            log($"Structured Agent-not-found recovery enrolled AgentId={replacement.Value.AgentId}.");
            return await tokenService.GetAccessTokenAsync(ct).ConfigureAwait(false);
        }
    }

    private static bool IsAgentNotFound(AgentClientAuthException exception) =>
        exception.StatusCode == 403 &&
        string.Equals(exception.Code, "agent_not_found", StringComparison.OrdinalIgnoreCase);

    private static async Task<(string AgentId, string RefreshToken)?> TryRecoverOnceAsync(
        ClientOptions options,
        IAgentEnrollmentService enrollmentService,
        IAgentCredentialStore credentialStore,
        IInjectedEnrollmentBootstrap injectedEnrollmentBootstrap,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(options.EnrollmentCode))
        {
            var enrolled = await enrollmentService.EnrollAsync(options.EnrollmentCode.Trim(), ct).ConfigureAwait(false);
            await credentialStore.SaveAsync(enrolled.AgentId, enrolled.RefreshToken, ct).ConfigureAwait(false);
        }
        else
        {
            var enrolled = await injectedEnrollmentBootstrap.TryEnrollAsync(
                options, enrollmentService, credentialStore, ct).ConfigureAwait(false);
            if (enrolled is null)
            {
                return null;
            }
        }

        return await credentialStore.LoadAsync(ct).ConfigureAwait(false);
    }
}
