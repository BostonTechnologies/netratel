using System;
using System.Threading;
using System.Threading.Tasks;
using NetRatel.Application.ClientAuth;

namespace NetRatel.Client.Service.Auth;

internal static class DisabledAgentTokenRetry
{
    internal static async Task<(string AccessToken, DateTimeOffset ExpiresAtUtc)> GetAccessTokenAsync(
        IAgentTokenService tokenService,
        Action<string> log,
        CancellationToken stoppingToken,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        delayAsync ??= Task.Delay;
        var retrySeconds = 1;
        while (true)
        {
            stoppingToken.ThrowIfCancellationRequested();
            try
            {
                return await tokenService.GetAccessTokenAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (AgentClientAuthException exception) when (
                exception.StatusCode == 403 &&
                !exception.ShouldClearCredentials &&
                string.Equals(exception.Code, "agent_disabled", StringComparison.Ordinal))
            {
                // Disabling is reversible. Keep the enrolled identity offline and
                // retain its credentials until the administrator enables it again.
                log($"Agent disabled by administrator. Waiting to retry authentication in {retrySeconds}s.");
                await delayAsync(TimeSpan.FromSeconds(retrySeconds), stoppingToken).ConfigureAwait(false);
                retrySeconds = Math.Min(retrySeconds * 2, 30);
            }
        }
    }
}
