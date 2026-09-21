using System.Data.Common;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using NetRatel.Infrastructure.Identity;
using NetRatel.Infrastructure.Persistence;
using Npgsql;

namespace NetRatel.API.Bootstrap;

/// <summary>
/// Creates the first durable local administrator and tenant while the API is still in its restricted
/// bootstrap host. It intentionally uses the already-selected datastore; setup never changes a
/// deployment-owned connection string or provider.
/// </summary>
public sealed class BootstrapInitializationService(
    BootstrapStateStore stateStore,
    IConfiguration configuration,
    IPasswordHasher<LocalUser> passwordHasher,
    IOptions<IdentityOptions> identityConfiguration)
{
    public async Task<BootstrapInitializationResult> InitializeAsync(
        Guid operationId,
        BootstrapInitializationRequest request,
        CancellationToken cancellationToken = default)
    {
        var descriptor = await stateStore.LoadOrCreateAsync(cancellationToken).ConfigureAwait(false);
        if (descriptor.State != BootstrapState.Configuring || descriptor.OperationId != operationId)
        {
            return BootstrapInitializationResult.Rejected;
        }

        var displayName = request.DisplayName?.Trim();
        var email = request.Email?.Trim();
        var tenantName = request.TenantName?.Trim();
        if (string.IsNullOrWhiteSpace(displayName) || string.IsNullOrWhiteSpace(email) ||
            string.IsNullOrWhiteSpace(tenantName) || string.IsNullOrWhiteSpace(request.Password))
        {
            return BootstrapInitializationResult.Invalid("Display name, email, password, and tenant name are required.");
        }

        var database = NetRatelDatabaseConfigurationResolver.Resolve(configuration);
        if (!string.Equals(descriptor.SelectedProvider, ProviderName(database.Provider), StringComparison.Ordinal))
        {
            // The descriptor is the source of truth once setup has been claimed. A configuration
            // change at this point is recovery work, never an implicit provider migration.
            return BootstrapInitializationResult.Invalid("The selected database provider changed during setup. Restore the original deployment configuration before continuing.");
        }

        await using var connection = CreateConnection(database);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            var applicationOptions = CreateApplicationOptions(database, connection);
            var identityDbOptions = CreateIdentityOptions(database, connection);
            await using var application = new OrchestratorDbContext(applicationOptions);
            await using var identity = new NetRatelIdentityDbContext(identityDbOptions);
            await using var transaction = await identity.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await application.Database.UseTransactionAsync(transaction.GetDbTransaction(), cancellationToken).ConfigureAwait(false);

            if (await identity.Users.AnyAsync(cancellationToken).ConfigureAwait(false) ||
                await application.Tenants.AnyAsync(cancellationToken).ConfigureAwait(false))
            {
                return BootstrapInitializationResult.Rejected;
            }

            var principal = new ApplicationPrincipal();
            var user = new LocalUser
            {
                UserName = email,
                Email = email,
                NormalizedUserName = email.ToUpperInvariant(),
                NormalizedEmail = email.ToUpperInvariant(),
                EmailConfirmed = true,
                DisplayName = displayName,
                PrincipalId = principal.Id,
                IsInstanceAdministrator = true,
                IsEnabled = true
            };

            var passwordValidation = await new PasswordValidator<LocalUser>()
                .ValidateAsync(new PasswordValidationUserManager(identityConfiguration), user, request.Password)
                .ConfigureAwait(false);
            if (!passwordValidation.Succeeded)
            {
                return BootstrapInitializationResult.Invalid("The administrator password does not meet the configured password policy.");
            }

            user.PasswordHash = passwordHasher.HashPassword(user, request.Password);
            principal.LocalUserId = user.Id;
            var now = DateTimeOffset.UtcNow;
            var tenant = new Tenant
            {
                Name = tenantName,
                ContactPerson = displayName,
                ContactEmail = email,
                CreatedAtUtc = now,
                UpdatedAtUtc = now,
                AutoUpdateChannel = "stable",
                Version = 1
            };

            identity.ApplicationPrincipals.Add(principal);
            identity.Users.Add(user);
            application.Tenants.Add(tenant);
            await identity.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await application.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            application.BootstrapInitializations.Add(new BootstrapInitializationRecord
            {
                BootstrapInstanceId = descriptor.InstanceId,
                OperationId = operationId,
                TenantId = tenant.Id,
                AdministratorUserId = user.Id,
                CompletedAtUtc = now
            });
            await application.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            var completed = await stateStore.CompleteSetupAsync(operationId, cancellationToken).ConfigureAwait(false);
            return completed
                ? BootstrapInitializationResult.Completed(tenant.Id, user.Id)
                : BootstrapInitializationResult.Rejected;
        }
        catch (DbException)
        {
            return BootstrapInitializationResult.Unavailable;
        }
    }

    /// <summary>
    /// Performs deployment-authorized recovery for an existing instance administrator. This is
    /// deliberately separate from first-run setup: it cannot create an account or change scopes.
    /// Rotating both session fences makes every prior local browser session fail revalidation.
    /// </summary>
    public async Task<BootstrapInitializationResult> RecoverAdministratorAsync(
        string? email,
        string? password,
        CancellationToken cancellationToken = default)
    {
        var normalizedEmail = email?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedEmail) || string.IsNullOrWhiteSpace(password))
        {
            return BootstrapInitializationResult.Invalid("The recovery email and password are required.");
        }

        var database = NetRatelDatabaseConfigurationResolver.Resolve(configuration);
        await using var connection = CreateConnection(database);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var identity = new NetRatelIdentityDbContext(CreateIdentityOptions(database, connection));
            var user = await identity.Users.SingleOrDefaultAsync(
                candidate => candidate.NormalizedEmail == normalizedEmail.ToUpperInvariant(), cancellationToken).ConfigureAwait(false);
            if (user is null || !user.IsInstanceAdministrator)
            {
                return BootstrapInitializationResult.Rejected;
            }

            var passwordValidation = await new PasswordValidator<LocalUser>()
                .ValidateAsync(new PasswordValidationUserManager(identityConfiguration), user, password)
                .ConfigureAwait(false);
            if (!passwordValidation.Succeeded)
            {
                return BootstrapInitializationResult.Invalid("The recovery password does not meet the configured password policy.");
            }

            user.PasswordHash = passwordHasher.HashPassword(user, password);
            user.IsEnabled = true;
            user.DisabledAtUtc = null;
            user.TwoFactorEnabled = false;
            user.SecurityStamp = Guid.NewGuid().ToString("N");
            user.AuthorizationRevision++;
            await identity.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return BootstrapInitializationResult.Recovered(user.Id);
        }
        catch (DbException)
        {
            return BootstrapInitializationResult.Unavailable;
        }
    }

    private static DbConnection CreateConnection(NetRatelDatabaseConfiguration database) =>
        database.Provider is NetRatelDatabaseProvider.Sqlite
            ? new SqliteConnection(database.ConnectionString)
            : new NpgsqlConnection(database.ConnectionString);

    private static DbContextOptions<OrchestratorDbContext> CreateApplicationOptions(
        NetRatelDatabaseConfiguration database,
        DbConnection connection)
    {
        var builder = new DbContextOptionsBuilder<OrchestratorDbContext>();
        if (database.Provider is NetRatelDatabaseProvider.Sqlite)
        {
            builder.UseSqlite(connection, sqlite => sqlite.MigrationsAssembly("NetRatel.SqliteMigrations"));
        }
        else
        {
            builder.UseNpgsql(connection, postgres => postgres.MigrationsAssembly("NetRatel.Migrations"));
        }

        return builder.Options;
    }

    private static DbContextOptions<NetRatelIdentityDbContext> CreateIdentityOptions(
        NetRatelDatabaseConfiguration database,
        DbConnection connection)
    {
        var builder = new DbContextOptionsBuilder<NetRatelIdentityDbContext>();
        if (database.Provider is NetRatelDatabaseProvider.Sqlite)
        {
            builder.UseSqlite(connection, sqlite => sqlite.MigrationsAssembly("NetRatel.SqliteMigrations"));
        }
        else
        {
            builder.UseNpgsql(connection, postgres => postgres.MigrationsAssembly("NetRatel.Migrations"));
        }

        return builder.Options;
    }

    private static string ProviderName(NetRatelDatabaseProvider provider) =>
        provider is NetRatelDatabaseProvider.Sqlite ? "SQLite" : "PostgreSQL";

    // Identity's built-in password validators only use the manager's Options for this validation.
    // The bootstrap host does not register a mutable UserManager backed by an operational context.
    private sealed class PasswordValidationUserManager : UserManager<LocalUser>
    {
        public PasswordValidationUserManager(IOptions<IdentityOptions> options)
            : base(new NoopUserStore(), options, new PasswordHasher<LocalUser>(), [], [], null!, null!, null!, null!)
        {
        }
    }

    private sealed class NoopUserStore : IUserPasswordStore<LocalUser>
    {
        public void Dispose() { }
        public Task<string> GetUserIdAsync(LocalUser user, CancellationToken cancellationToken) => Task.FromResult(user.Id);
        public Task<string?> GetUserNameAsync(LocalUser user, CancellationToken cancellationToken) => Task.FromResult(user.UserName);
        public Task SetUserNameAsync(LocalUser user, string? userName, CancellationToken cancellationToken) { user.UserName = userName; return Task.CompletedTask; }
        public Task<string?> GetNormalizedUserNameAsync(LocalUser user, CancellationToken cancellationToken) => Task.FromResult(user.NormalizedUserName);
        public Task SetNormalizedUserNameAsync(LocalUser user, string? normalizedName, CancellationToken cancellationToken) { user.NormalizedUserName = normalizedName; return Task.CompletedTask; }
        public Task<IdentityResult> CreateAsync(LocalUser user, CancellationToken cancellationToken) => Task.FromResult(IdentityResult.Success);
        public Task<IdentityResult> UpdateAsync(LocalUser user, CancellationToken cancellationToken) => Task.FromResult(IdentityResult.Success);
        public Task<IdentityResult> DeleteAsync(LocalUser user, CancellationToken cancellationToken) => Task.FromResult(IdentityResult.Success);
        public Task<LocalUser?> FindByIdAsync(string userId, CancellationToken cancellationToken) => Task.FromResult<LocalUser?>(null);
        public Task<LocalUser?> FindByNameAsync(string normalizedUserName, CancellationToken cancellationToken) => Task.FromResult<LocalUser?>(null);
        public Task SetPasswordHashAsync(LocalUser user, string? passwordHash, CancellationToken cancellationToken) { user.PasswordHash = passwordHash; return Task.CompletedTask; }
        public Task<string?> GetPasswordHashAsync(LocalUser user, CancellationToken cancellationToken) => Task.FromResult(user.PasswordHash);
        public Task<bool> HasPasswordAsync(LocalUser user, CancellationToken cancellationToken) => Task.FromResult(!string.IsNullOrEmpty(user.PasswordHash));
    }
}

public sealed record BootstrapInitializationRequest(string? DisplayName, string? Email, string? Password, string? TenantName);

public sealed record BootstrapInitializationResult(bool Succeeded, bool IsUnavailable, int? TenantId, string? UserId, string? Error)
{
    public static BootstrapInitializationResult Rejected { get; } = new(false, false, null, null, null);
    public static BootstrapInitializationResult Unavailable { get; } = new(false, true, null, null, null);
    public static BootstrapInitializationResult Invalid(string error) => new(false, false, null, null, error);
    public static BootstrapInitializationResult Completed(int tenantId, string userId) => new(true, false, tenantId, userId, null);
    public static BootstrapInitializationResult Recovered(string userId) => new(true, false, null, userId, null);
}
