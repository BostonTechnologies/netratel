using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using NetRatel.Infrastructure.Identity.Authorization;

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
    public DbSet<AccessRole> AccessRoles => Set<AccessRole>();
    public DbSet<AccessRolePermission> AccessRolePermissions => Set<AccessRolePermission>();
    public DbSet<PrincipalRoleAssignment> PrincipalRoleAssignments => Set<PrincipalRoleAssignment>();

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

        builder.Entity<AccessRole>(entity =>
        {
            entity.ToTable("AccessRoles");
            entity.HasKey(role => role.Id);
            entity.Property(role => role.Id).HasMaxLength(32);
            entity.Property(role => role.Name).HasMaxLength(128).IsRequired();
            entity.Property(role => role.Description).HasMaxLength(512);
            entity.HasIndex(role => role.Name).IsUnique();
            entity.HasIndex(role => new { role.IsBuiltIn, role.DelegationRank });
        });

        builder.Entity<AccessRolePermission>(entity =>
        {
            entity.ToTable("AccessRolePermissions");
            entity.HasKey(permission => new { permission.RoleId, permission.Permission });
            entity.Property(permission => permission.RoleId).HasMaxLength(32);
            entity.Property(permission => permission.Permission).HasMaxLength(128).IsRequired();
            entity.HasOne(permission => permission.Role)
                .WithMany(role => role.Permissions)
                .HasForeignKey(permission => permission.RoleId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<PrincipalRoleAssignment>(entity =>
        {
            entity.ToTable("PrincipalRoleAssignments");
            entity.HasKey(assignment => assignment.Id);
            entity.Property(assignment => assignment.Id).HasMaxLength(32);
            entity.Property(assignment => assignment.PrincipalId).HasMaxLength(32).IsRequired();
            entity.Property(assignment => assignment.RoleId).HasMaxLength(32).IsRequired();
            entity.Property(assignment => assignment.CreatedByPrincipalId).HasMaxLength(32);
            entity.HasIndex(assignment => new { assignment.PrincipalId, assignment.TenantId });
            entity.HasIndex(assignment => new { assignment.RoleId, assignment.TenantId });
            entity.HasOne(assignment => assignment.Role)
                .WithMany()
                .HasForeignKey(assignment => assignment.RoleId)
                .OnDelete(DeleteBehavior.Restrict);
        });
    }
}
