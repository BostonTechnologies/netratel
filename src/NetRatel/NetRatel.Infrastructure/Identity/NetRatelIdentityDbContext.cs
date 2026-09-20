using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace NetRatel.Infrastructure.Identity;

/// <summary>
/// Identity state shares the selected application database while retaining a
/// dedicated model and migration history. It never owns agent credentials or
/// existing OIDC signing material.
/// </summary>
public sealed class NetRatelIdentityDbContext(DbContextOptions<NetRatelIdentityDbContext> options)
    : IdentityDbContext<LocalUser, IdentityRole, string>(options)
{
    public DbSet<ApplicationPrincipal> ApplicationPrincipals => Set<ApplicationPrincipal>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        builder.Entity<LocalUser>(entity =>
        {
            entity.ToTable("LocalUsers");
            entity.Property(user => user.DisplayName).HasMaxLength(256);
            entity.Property(user => user.PrincipalId).HasMaxLength(32).IsRequired();
            entity.Property(user => user.IsEnabled).HasDefaultValue(true);
            entity.Property(user => user.IsInstanceAdministrator).HasDefaultValue(false);
            entity.Property(user => user.AuthorizationRevision).HasDefaultValue(0L);
            entity.HasIndex(user => user.PrincipalId).IsUnique();
            entity.HasIndex(user => user.IsEnabled);
        });

        builder.Entity<ApplicationPrincipal>(entity =>
        {
            entity.ToTable("ApplicationPrincipals");
            entity.HasKey(principal => principal.Id);
            entity.Property(principal => principal.Id).HasMaxLength(32);
            entity.Property(principal => principal.LocalUserId).HasMaxLength(450);
            entity.Property(principal => principal.ExternalIssuer).HasMaxLength(2048);
            entity.Property(principal => principal.ExternalSubject).HasMaxLength(512);
            entity.HasIndex(principal => principal.LocalUserId).IsUnique();
            entity.HasIndex(principal => new { principal.ExternalIssuer, principal.ExternalSubject }).IsUnique();
        });
    }
}
