using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace NetRatel.Infrastructure.Persistence;

/// <summary>
/// Keeps provider-specific relational annotations out of a context model built
/// earlier in the same process for another supported database provider.
/// </summary>
public sealed class ProviderAwareModelCacheKeyFactory : IModelCacheKeyFactory
{
    public object Create(DbContext context, bool designTime) =>
        (context.GetType(), context.Database.ProviderName, designTime);
}
