using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NetRatel.Application.ClientAuth;

namespace NetRatel.Client.Service.Auth;

public interface IEnrollmentCliCommand
{
    Task<(bool Handled, int ExitCode)> TryExecuteAsync(
        bool enrollOnly,
        string? enrollmentCode,
        (string AgentId, string RefreshToken)? existingCredentials,
        IAgentEnrollmentService enrollmentService,
        IAgentCredentialStore credentialStore,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken ct);
}

public sealed class EnrollmentCliCommand : IEnrollmentCliCommand
{
    public async Task<(bool Handled, int ExitCode)> TryExecuteAsync(
        bool enrollOnly,
        string? enrollmentCode,
        (string AgentId, string RefreshToken)? existingCredentials,
        IAgentEnrollmentService enrollmentService,
        IAgentCredentialStore credentialStore,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken ct)
    {
        if (!enrollOnly)
        {
            return (false, 0);
        }

        if (existingCredentials is not null)
        {
            await stdout.WriteLineAsync("Already enrolled.");
            return (true, 0);
        }

        if (string.IsNullOrWhiteSpace(enrollmentCode))
        {
            await stderr.WriteLineAsync("Enrollment code is required. Usage: --enroll <code>");
            return (true, 1);
        }

        try
        {
            var enrolled = await enrollmentService.EnrollAsync(enrollmentCode, ct);
            await credentialStore.SaveAsync(enrolled.AgentId, enrolled.RefreshToken);
            await stdout.WriteLineAsync("Enrollment succeeded.");
            return (true, 0);
        }
        catch (AgentClientAuthException ex)
        {
            await stderr.WriteLineAsync($"Enrollment failed: {ex.Message}");
            return (true, 1);
        }
    }
}
