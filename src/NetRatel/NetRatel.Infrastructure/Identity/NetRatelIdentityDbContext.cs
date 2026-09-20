using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Identity.Branding;

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
    public DbSet<IntegrationCredential> IntegrationCredentials => Set<IntegrationCredential>();
    public DbSet<IntegrationCredentialGrant> IntegrationCredentialGrants => Set<IntegrationCredentialGrant>();
    public DbSet<DeploymentBrandingOverride> DeploymentBrandingOverrides => Set<DeploymentBrandingOverride>();
    public DbSet<DeploymentBrandingAsset> DeploymentBrandingAssets => Set<DeploymentBrandingAsset>();

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

        builder.Entity<IntegrationCredential>(entity =>
        {
            entity.ToTable("IntegrationCredentials");
            entity.HasKey(credential => credential.Id);
            entity.Property(credential => credential.Id).HasMaxLength(32);
            entity.Property(credential => credential.PublicId).HasMaxLength(24).IsRequired();
            entity.Property(credential => credential.TokenPrefix).HasMaxLength(32).IsRequired();
            entity.Property(credential => credential.SecretHash).HasMaxLength(64).IsRequired();
            entity.Property(credential => credential.OwnerPrincipalId).HasMaxLength(32).IsRequired();
            entity.Property(credential => credential.Resource).HasMaxLength(2048);
            entity.Property(credential => credential.Name).HasMaxLength(128).IsRequired();
            entity.HasIndex(credential => credential.PublicId).IsUnique();
            entity.HasIndex(credential => credential.SecretHash).IsUnique();
            entity.HasIndex(credential => new { credential.OwnerPrincipalId, credential.CreatedAtUtc });
            entity.HasIndex(credential => new { credential.Purpose, credential.ExpiresAtUtc, credential.RevokedAtUtc });
        });

        builder.Entity<IntegrationCredentialGrant>(entity =>
        {
            entity.ToTable("IntegrationCredentialGrants");
            entity.HasKey(grant => new { grant.CredentialId, grant.TenantId, grant.Permission });
            entity.Property(grant => grant.CredentialId).HasMaxLength(32);
            entity.Property(grant => grant.Permission).HasMaxLength(128).IsRequired();
            entity.HasOne(grant => grant.Credential)
                .WithMany(credential => credential.Grants)
                .HasForeignKey(grant => grant.CredentialId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        builder.Entity<DeploymentBrandingOverride>(entity =>
        {
            entity.ToTable("DeploymentBrandingOverrides");
            entity.HasKey(value => value.Id);
            entity.Property(value => value.Id).HasMaxLength(32);
            entity.Property(value => value.ApplicationName).HasMaxLength(96);
            entity.Property(value => value.OrganizationName).HasMaxLength(96);
            entity.Property(value => value.Tagline).HasMaxLength(160);
            entity.Property(value => value.LogoLightAssetId).HasMaxLength(32);
            entity.Property(value => value.LogoDarkAssetId).HasMaxLength(32);
            entity.Property(value => value.CompactLogoAssetId).HasMaxLength(32);
            entity.Property(value => value.FaviconAssetId).HasMaxLength(32);
            entity.Property(value => value.SupportUrl).HasMaxLength(2048);
            entity.Property(value => value.SiteUrl).HasMaxLength(2048);
            entity.Property(value => value.UpdatedByPrincipalId).HasMaxLength(32);
        });

        builder.Entity<DeploymentBrandingAsset>(entity =>
        {
            entity.ToTable("DeploymentBrandingAssets");
            entity.HasKey(value => value.Id);
            entity.Property(value => value.Id).HasMaxLength(32);
            entity.Property(value => value.ContentType).HasMaxLength(64).IsRequired();
            entity.Property(value => value.Sha256).HasMaxLength(64).IsRequired();
            entity.HasIndex(value => value.Sha256).IsUnique();
        });
    }
}
