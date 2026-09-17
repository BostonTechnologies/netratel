namespace NetRatel.Application.Agents;

public interface IEnrollmentService
{
    Task<AgentEnrollResponse> EnrollAsync(AgentEnrollRequest request, CancellationToken ct);
}
