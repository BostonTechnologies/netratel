using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using NetRatel.Infrastructure.Identity;
using NetRatel.Infrastructure.Persistence;

namespace NetRatel.SqliteMigrations;

public sealed class SqliteOrchestratorDbContextFactory : IDesignTimeDbContextFactory<OrchestratorDbContext>
{
    public OrchestratorDbContext CreateDbContext(string[] args) => new(CreateOptions<OrchestratorDbContext>());

    private static DbContextOptions<TContext> CreateOptions<TContext>() where TContext : DbContext
        => new DbContextOptionsBuilder<TContext>()
            .UseSqlite("Data Source=/tmp/netratel-design.sqlite", sqlite =>
                sqlite.MigrationsAssembly(typeof(SqliteOrchestratorDbContextFactory).Assembly.GetName().Name))
            .Options;
}

public sealed class SqliteIdentityDbContextFactory : IDesignTimeDbContextFactory<NetRatelIdentityDbContext>
{
    public NetRatelIdentityDbContext CreateDbContext(string[] args) => new(CreateOptions());

    private static DbContextOptions<NetRatelIdentityDbContext> CreateOptions() =>
        new DbContextOptionsBuilder<NetRatelIdentityDbContext>()
            .UseSqlite("Data Source=/tmp/netratel-design.sqlite", sqlite =>
                sqlite.MigrationsAssembly(typeof(SqliteIdentityDbContextFactory).Assembly.GetName().Name))
            .Options;
}
