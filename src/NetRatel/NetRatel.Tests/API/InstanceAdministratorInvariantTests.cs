using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NetRatel.Infrastructure.Identity;
using NetRatel.Infrastructure.Identity.Authorization;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class InstanceAdministratorInvariantTests
{
    [Fact]
    public async Task Disabled_role_assigned_local_user_does_not_keep_the_last_administrator_viable()
    {
        await using var db = CreateDb();
        await SeedAsync(db, enabledRoleAdministrator: false);
        var administrators = new InstanceAdministratorInvariant(db);

        (await administrators.ViableAdministratorCountAsync(TestContext.Current.CancellationToken)).Should().Be(1);
    }

    [Fact]
    public async Task Enabled_role_assigned_local_user_is_a_viable_recovery_administrator()
    {
        await using var db = CreateDb();
        await SeedAsync(db, enabledRoleAdministrator: true);
        var administrators = new InstanceAdministratorInvariant(db);

        (await administrators.ViableAdministratorCountAsync(TestContext.Current.CancellationToken)).Should().Be(2);
    }

    [Fact]
    public async Task Unresolved_role_assignment_does_not_create_a_recovery_path()
    {
        await using var db = CreateDb();
        var role = new AccessRole { Id = "instance-admin", Name = "Instance administrator", IsInstanceAdministratorRole = true };
        db.AccessRoles.Add(role);
        db.PrincipalRoleAssignments.Add(new PrincipalRoleAssignment { PrincipalId = "missing-principal", RoleId = role.Id });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var administrators = new InstanceAdministratorInvariant(db);

        (await administrators.ViableAdministratorCountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
    }

    [Fact]
    public async Task Bound_external_principal_is_a_supported_administrator_recovery_path()
    {
        await using var db = CreateDb();
        var role = new AccessRole { Id = "instance-admin", Name = "Instance administrator", IsInstanceAdministratorRole = true };
        var principal = new ApplicationPrincipal { Id = "external-principal", ExternalIssuer = "https://issuer.example.test", ExternalSubject = "subject-1" };
        db.AddRange(role, principal, new PrincipalRoleAssignment { PrincipalId = principal.Id, RoleId = role.Id });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var administrators = new InstanceAdministratorInvariant(db);

        (await administrators.ViableAdministratorCountAsync(TestContext.Current.CancellationToken)).Should().Be(1);
    }

    private static NetRatelIdentityDbContext CreateDb() => new(new DbContextOptionsBuilder<NetRatelIdentityDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
        .Options);

    private static async Task SeedAsync(NetRatelIdentityDbContext db, bool enabledRoleAdministrator)
    {
        var role = new AccessRole { Id = "instance-admin", Name = "Instance administrator", IsInstanceAdministratorRole = true };
        var bootstrapPrincipal = new ApplicationPrincipal { Id = "bootstrap-principal", LocalUserId = "bootstrap" };
        var assignedPrincipal = new ApplicationPrincipal { Id = "assigned-principal", LocalUserId = "assigned" };
        db.AddRange(
            role,
            bootstrapPrincipal,
            assignedPrincipal,
            new LocalUser { Id = "bootstrap", UserName = "bootstrap@example.test", PrincipalId = bootstrapPrincipal.Id, IsEnabled = true, IsInstanceAdministrator = true },
            new LocalUser { Id = "assigned", UserName = "assigned@example.test", PrincipalId = assignedPrincipal.Id, IsEnabled = enabledRoleAdministrator },
            new PrincipalRoleAssignment { PrincipalId = assignedPrincipal.Id, RoleId = role.Id });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }
}
