using Microsoft.Extensions.DependencyInjection;

namespace NetRatel.Infrastructure.ServiceLinks;

public sealed partial class ServiceLinkCoordinator
{
    private bool HasDatabaseTransaction => db.Database.CurrentTransaction is not null;

    // Only recipient commands containing local database/protection work use this
    // boundary. Never wrap Progress or a command that performs peer HTTP with it.
    private async Task<T> RetryAbortedDatabaseTransaction<T>(Func<ServiceLinkCoordinator, Task<T>> action, CancellationToken ct)
    {
        var coordinator = this;
        AsyncServiceScope? retryScope = null;
        try
        {
            for (var retry = 0; ; retry++)
            {
                ct.ThrowIfCancellationRequested();
                try { return await action(coordinator); }
                catch (Exception error) when (retry < 3 && scopes is not null && !ct.IsCancellationRequested &&
                    !coordinator.HasDatabaseTransaction && System.Transactions.Transaction.Current is null &&
                    ServiceLinkDatabaseConflict.IsAbortedTransaction(error))
                {
                    // The recipient's await-using transaction has rolled back and
                    // disposed before this catch. Never reuse its failed context or
                    // scoped registry/profile services. Reread and revalidate the
                    // SAME immutable request/operation using a new complete scope.
                    if (retryScope is { } previous)
                    { retryScope = null; await previous.DisposeAsync(); }
                    retryScope = scopes.CreateAsyncScope();
                    coordinator = retryScope.Value.ServiceProvider.GetRequiredService<ServiceLinkCoordinator>();
                }
            }
        }
        finally
        {
            if (retryScope is { } final) await final.DisposeAsync();
        }
    }
}
