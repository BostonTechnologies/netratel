using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace NetRatel.Infrastructure.Persistence;

public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<OrchestratorDbContext>
{
    public OrchestratorDbContext CreateDbContext(string[] args)
    {
        var basePath = Directory.GetCurrentDirectory();
        var startupPath = Path.GetFullPath(Path.Combine(basePath, "..", "NetRatel.API"));
        var effectiveBase = Directory.Exists(startupPath) ? startupPath : basePath;
        var configuration = new ConfigurationBuilder()
            .SetBasePath(effectiveBase)
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .Build();

        var optionsBuilder = new DbContextOptionsBuilder<OrchestratorDbContext>();
        var conn = configuration.GetConnectionString("NetRatelDb")
            ?? throw new InvalidOperationException(
                "A PostgreSQL connection string is required. Configure ConnectionStrings:NetRatelDb.");
        optionsBuilder.UseNpgsql(conn);
        return new OrchestratorDbContext(optionsBuilder.Options);
    }
}
