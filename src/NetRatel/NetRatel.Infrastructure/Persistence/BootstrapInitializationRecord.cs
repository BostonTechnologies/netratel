namespace NetRatel.Infrastructure.Persistence;

/// <summary>
/// Transactionally committed evidence that first-run local initialization completed.
/// It is a singleton so a bootstrap descriptor can only reconcile its own installation.
/// </summary>
public sealed class BootstrapInitializationRecord
{
    public const int SingletonId = 1;

    public int Id { get; init; } = SingletonId;
    public Guid BootstrapInstanceId { get; init; }
    public Guid OperationId { get; init; }
    public int TenantId { get; init; }
    public string AdministratorUserId { get; init; } = string.Empty;
    public DateTimeOffset CompletedAtUtc { get; init; }
}
