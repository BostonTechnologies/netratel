using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace NetRatel.Infrastructure.Identity;

/// <summary>Design-time factory for the identity migration set; no credentials are persisted by it.</summary>
public sealed class IdentityDesignTimeDbContextFactory : IDesignTimeDbContextFactory<NetRatelIdentityDbContext>
{
    public NetRatelIdentityDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<NetRatelIdentityDbContext>()
            .UseNpgsql("Host=localhost;Database=netratel_design;Username=netratel;Password=design-time-only")
            .Options;
        return new NetRatelIdentityDbContext(options);
    }
}
