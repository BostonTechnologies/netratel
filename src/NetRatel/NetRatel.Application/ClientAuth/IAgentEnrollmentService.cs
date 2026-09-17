namespace NetRatel.Application.ClientAuth;

public interface IAgentEnrollmentService
{
    Task<(string AgentId, string RefreshToken)> EnrollAsync(string enrollmentCode, CancellationToken ct);
}
