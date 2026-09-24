using FluentAssertions;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using NetRatel.API.Bootstrap;
using NetRatel.API.Endpoints.Auth;
using NetRatel.API.Security.Authorization;
using NetRatel.API.Security.Local;
using NetRatel.Infrastructure.Identity;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Persistence;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class BootstrapLegacyAdoptionPostgresTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine").Build();
    private readonly string _stateRoot = Path.Combine(Path.GetTempPath(), "netratel-bootstrap-adoption-tests", Guid.NewGuid().ToString("N"));

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync();
        await using var connection = new NpgsqlConnection(_postgres.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            CREATE SCHEMA legacy;
            CREATE TABLE legacy."Tenants" ("Id" integer primary key);
            CREATE TABLE legacy."Agents" ("Id" uuid primary key);
            INSERT INTO legacy."Tenants" ("Id") VALUES (1);
            INSERT INTO legacy."Agents" ("Id") VALUES ('00000000-0000-0000-0000-000000000001');
            CREATE SCHEMA empty_bootstrap;
            """, connection);
        await command.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _postgres.DisposeAsync();
        if (Directory.Exists(_stateRoot))
        {
            Directory.Delete(_stateRoot, true);
        }
    }

    [Fact]
    public async Task Empty_schema_is_not_adopted_but_existing_netratel_evidence_is_adopted()
    {
        var empty = await InitializeAsync("empty_bootstrap", includeOidc: false);
        empty.State.Should().Be(BootstrapState.Unconfigured);

        var configuredOidc = await InitializeAsync("empty_bootstrap", includeOidc: true);
        configuredOidc.State.Should().Be(BootstrapState.Ready);
        configuredOidc.AdoptedExistingInstallation.Should().BeFalse();

        var localWithStaleOidc = await InitializeAsync("empty_bootstrap", includeOidc: true, mode: "Local");
        localWithStaleOidc.State.Should().Be(BootstrapState.Unconfigured);

        var freshHybrid = await InitializeAsync("empty_bootstrap", includeOidc: true, mode: "Hybrid");
        freshHybrid.State.Should().Be(BootstrapState.Unconfigured);

        var adopted = await InitializeAsync("legacy", includeOidc: true);
        adopted.State.Should().Be(BootstrapState.Ready);
        adopted.AdoptedExistingInstallation.Should().BeTrue();
        adopted.SelectedProvider.Should().Be("PostgreSQL");
        adopted.ConnectionReference.Should().Be("ConnectionStrings:NetRatelDb");
    }

    [Fact]
    public async Task Expired_setup_lease_reconciles_only_the_matching_committed_initialization()
    {
        var connectionString = _postgres.GetConnectionString();
        var applicationOptions = new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        var identityOptions = new DbContextOptionsBuilder<NetRatelIdentityDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        await using (var application = new OrchestratorDbContext(applicationOptions)) await application.Database.MigrateAsync();
        await using (var identity = new NetRatelIdentityDbContext(identityOptions)) await identity.Database.MigrateAsync();

        var stateDirectory = Path.Combine(_stateRoot, "transaction-recovery");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:NetRatelDb"] = connectionString
        }).Build();
        var store = new BootstrapStateStore(new BootstrapOptions { StateDirectory = stateDirectory });
        await store.LoadOrCreateAsync();
        var proof = await File.ReadAllTextAsync(Path.Combine(stateDirectory, "setup-proof"));
        var claim = await store.ClaimSetupAsync(proof, "PostgreSQL", "ConnectionStrings:NetRatelDb");
        claim.Succeeded.Should().BeTrue();
        var descriptor = claim.Descriptor!;

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var applicationTransactionContext = new OrchestratorDbContext(new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseNpgsql(connection)
            .Options);
        await using var identityTransactionContext = new NetRatelIdentityDbContext(new DbContextOptionsBuilder<NetRatelIdentityDbContext>()
            .UseNpgsql(connection)
            .Options);
        await using var transaction = await identityTransactionContext.Database.BeginTransactionAsync();
        await applicationTransactionContext.Database.UseTransactionAsync(transaction.GetDbTransaction());

        var principal = new ApplicationPrincipal { Id = "postgres-bootstrap-principal" };
        var administrator = new LocalUser
        {
            Id = "postgres-bootstrap-admin",
            UserName = "admin@example.test",
            NormalizedUserName = "ADMIN@EXAMPLE.TEST",
            Email = "admin@example.test",
            NormalizedEmail = "ADMIN@EXAMPLE.TEST",
            PrincipalId = principal.Id,
            DisplayName = "Initial Administrator",
            IsEnabled = true,
            IsInstanceAdministrator = true
        };
        principal.LocalUserId = administrator.Id;
        var tenant = new Tenant
        {
            Name = "Initial tenant",
            ContactPerson = administrator.DisplayName,
            ContactEmail = administrator.Email,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow,
            AutoUpdateChannel = "stable",
            Version = 1
        };
        identityTransactionContext.ApplicationPrincipals.Add(principal);
        identityTransactionContext.Users.Add(administrator);
        applicationTransactionContext.Tenants.Add(tenant);
        await identityTransactionContext.SaveChangesAsync();
        await applicationTransactionContext.SaveChangesAsync();
        applicationTransactionContext.BootstrapInitializations.Add(new BootstrapInitializationRecord
        {
            BootstrapInstanceId = descriptor.InstanceId,
            OperationId = descriptor.OperationId!.Value,
            TenantId = tenant.Id,
            AdministratorUserId = administrator.Id,
            CompletedAtUtc = DateTimeOffset.UtcNow
        });
        await applicationTransactionContext.SaveChangesAsync();
        await transaction.CommitAsync();

        await store.UpdateAsync(current => current with { OperationId = Guid.NewGuid() }, "test-mismatched-operation");
        var unmatched = await new BootstrapLifecycleService(store, configuration).InitializeAsync();
        unmatched.State.Should().Be(BootstrapState.Configuring);
        await store.UpdateAsync(current => current with { OperationId = descriptor.OperationId }, "test-restore-operation");

        await store.UpdateAsync(current => current with
        {
            State = BootstrapState.RecoveryRequired,
            OperationId = null,
            OperationLeaseExpiresAtUtc = null,
            RecoveryReason = "configuration-lease-expired"
        }, "configuration-lease-expired");

        var reconciled = await new BootstrapLifecycleService(store, configuration).InitializeAsync();

        reconciled.State.Should().Be(BootstrapState.Ready);
        reconciled.OperationId.Should().BeNull();
        reconciled.RecoveryReason.Should().BeNull();
    }

    [Fact]
    public async Task Completed_initialization_retries_the_same_operation_without_creating_new_ownership()
    {
        var connectionString = _postgres.GetConnectionString();
        var applicationOptions = new DbContextOptionsBuilder<OrchestratorDbContext>().UseNpgsql(connectionString).Options;
        var identityOptions = new DbContextOptionsBuilder<NetRatelIdentityDbContext>().UseNpgsql(connectionString).Options;
        await using (var application = new OrchestratorDbContext(applicationOptions)) await application.Database.MigrateAsync();
        await using (var identity = new NetRatelIdentityDbContext(identityOptions)) await identity.Database.MigrateAsync();

        var stateDirectory = Path.Combine(_stateRoot, "idempotent-retry");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:NetRatelDb"] = connectionString
        }).Build();
        var store = new BootstrapStateStore(new BootstrapOptions { StateDirectory = stateDirectory });
        await store.LoadOrCreateAsync();
        var proof = await File.ReadAllTextAsync(Path.Combine(stateDirectory, "setup-proof"));
        var claim = await store.ClaimSetupAsync(proof, "PostgreSQL", "ConnectionStrings:NetRatelDb");
        claim.Succeeded.Should().BeTrue();
        var initializer = CreateInitializer(store, configuration);
        var completed = await initializer.InitializeAsync(claim.Descriptor!.OperationId!.Value,
            new BootstrapInitializationRequest("Initial Administrator", "admin@example.test", "a local-first passphrase", "Initial tenant"));

        var retry = await initializer.InitializeAsync(claim.Descriptor.OperationId.Value,
            new BootstrapInitializationRequest("Other", "other@example.test", "a local-first passphrase", "Other tenant"));

        retry.Succeeded.Should().BeTrue();
        retry.TenantId.Should().Be(completed.TenantId);
        retry.UserId.Should().Be(completed.UserId);
        await using var verificationApplication = new OrchestratorDbContext(applicationOptions);
        await using var verificationIdentity = new NetRatelIdentityDbContext(identityOptions);
        (await verificationApplication.Tenants.CountAsync()).Should().Be(1);
        (await verificationIdentity.Users.CountAsync()).Should().Be(1);
    }

    [Theory]
    [InlineData("Local")]
    [InlineData("Hybrid")]
    public async Task Ready_installation_survives_initial_administrator_handover(string mode)
    {
        var connectionString = _postgres.GetConnectionString();
        var applicationOptions = new DbContextOptionsBuilder<OrchestratorDbContext>().UseNpgsql(connectionString).Options;
        var identityOptions = new DbContextOptionsBuilder<NetRatelIdentityDbContext>().UseNpgsql(connectionString).Options;
        await using (var application = new OrchestratorDbContext(applicationOptions)) await application.Database.MigrateAsync();
        await using (var identity = new NetRatelIdentityDbContext(identityOptions)) await identity.Database.MigrateAsync();

        var stateDirectory = Path.Combine(_stateRoot, $"handover-{mode}");
        var values = new Dictionary<string, string?>
        {
            ["ConnectionStrings:NetRatelDb"] = connectionString,
            ["Authentication:Mode"] = mode,
            ["Authentication:Oidc:Authority"] = "https://issuer.example.test",
            ["Authentication:Oidc:Audience"] = "netratel-api"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var store = new BootstrapStateStore(new BootstrapOptions { StateDirectory = stateDirectory });
        await store.LoadOrCreateAsync();
        var proof = await File.ReadAllTextAsync(Path.Combine(stateDirectory, "setup-proof"));
        var claim = await store.ClaimSetupAsync(proof, "PostgreSQL", "ConnectionStrings:NetRatelDb");
        var result = await CreateInitializer(store, configuration).InitializeAsync(claim.Descriptor!.OperationId!.Value,
            new BootstrapInitializationRequest("Initial Administrator", "admin@example.test", "a local-first passphrase", "Initial tenant"));
        result.Succeeded.Should().BeTrue();

        var firstHost = await BuildAdministrationHostAsync(connectionString, mode);
        try
        {
            using var firstClient = firstHost.GetTestClient();
            await LoginAsync(firstClient, "admin@example.test", "a local-first passphrase");
            (await firstClient.PostAsync($"/api/v2/local-auth/users/{result.UserId}/disable", null)).StatusCode.Should().Be(HttpStatusCode.Conflict);

            var created = await firstClient.PostAsJsonAsync("/api/v2/local-auth/users",
                new LocalAuthenticationEndpoints.CreateLocalAccountRequest("Successor Administrator", "successor@example.test"));
            created.StatusCode.Should().Be(HttpStatusCode.Created);
            var activation = (await created.Content.ReadFromJsonAsync<LocalAuthenticationEndpoints.ActivationResponse>())!;
            (await firstClient.PostAsJsonAsync("/api/v2/local-auth/activate",
                new LocalAuthenticationEndpoints.ActivateLocalAccountRequest(activation.Email, activation.ActivationToken, "a successor passphrase")))
                .StatusCode.Should().Be(HttpStatusCode.NoContent);

            string principalId;
            string roleId;
            await using (var identity = new NetRatelIdentityDbContext(identityOptions))
            {
                principalId = (await identity.Users.SingleAsync(user => user.Id == activation.UserId)).PrincipalId;
                roleId = (await identity.AccessRoles.SingleAsync(role => role.IsInstanceAdministratorRole)).Id;
            }
            (await firstClient.PutAsJsonAsync($"/api/v2/access/principals/{principalId}/assignments",
                new AccessAdministrationEndpoints.AssignRoleRequest(roleId, null))).StatusCode.Should().Be(HttpStatusCode.Created);

            using var successor = firstHost.GetTestClient();
            await LoginAsync(successor, "successor@example.test", "a successor passphrase");
            (await successor.GetAsync("/api/v2/access/self")).StatusCode.Should().Be(HttpStatusCode.OK);
            (await successor.PostAsync($"/api/v2/local-auth/users/{result.UserId}/disable", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);
            (await successor.PostAsync($"/api/v2/local-auth/users/{activation.UserId}/disable", null)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        }
        finally { await firstHost.DisposeAsync(); }

        await using (var application = new OrchestratorDbContext(applicationOptions))
        {
            var originalTenant = await application.Tenants.SingleAsync(tenant => tenant.Id == result.TenantId);
            application.Tenants.Remove(originalTenant);
            await application.SaveChangesAsync();
        }

        var restarted = await new BootstrapLifecycleService(store, configuration).InitializeAsync();
        restarted.State.Should().Be(BootstrapState.Ready);
        var missingStorage = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:NetRatelDb"] = "REPLACE_ME",
            ["Authentication:Mode"] = mode,
            ["Authentication:Oidc:Authority"] = "https://issuer.example.test",
            ["Authentication:Oidc:Audience"] = "netratel-api"
        }).Build();
        var restricted = await new BootstrapLifecycleService(store, missingStorage).InitializeAsync();
        restricted.State.Should().Be(BootstrapState.RecoveryRequired);
        restricted.RecoveryReason.Should().Be("configured-storage-missing");
        (await new BootstrapLifecycleService(store, configuration).InitializeAsync()).State.Should().Be(BootstrapState.Ready);
        var restartedHost = await BuildAdministrationHostAsync(connectionString, mode);
        try
        {
            using var successor = restartedHost.GetTestClient();
            await LoginAsync(successor, "successor@example.test", "a successor passphrase");
            (await successor.GetAsync("/api/v2/access/self")).StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally { await restartedHost.DisposeAsync(); }
    }

    private static async Task<WebApplication> BuildAdministrationHostAsync(string connectionString, string mode)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddDataProtection();
        builder.Services.AddAuthentication(LocalAuthenticationOptions.Scheme)
            .AddCookie(LocalAuthenticationOptions.Scheme, options => options.Cookie.Name = "NetRatel.Local");
        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy(LocalAuthenticationOptions.LocalUserPolicy, policy => policy
                .AddAuthenticationSchemes(LocalAuthenticationOptions.Scheme).RequireAuthenticatedUser());
            options.AddPolicy("InstanceAdministrator", policy => policy
                .RequireAuthenticatedUser()
                .AddRequirements(new EffectiveAccessRequirement(NetRatelPermissions.UserRoleAdministration, instanceScope: true)));
            options.AddPolicy("AccessAdministration", policy => policy.RequireAuthenticatedUser());
        });
        builder.Services.AddRateLimiter(options =>
        {
            options.AddFixedWindowLimiter("local-login", limiter => { limiter.PermitLimit = 20; limiter.Window = TimeSpan.FromMinutes(1); });
            options.AddPolicy("local-security", _ => RateLimitPartition.GetFixedWindowLimiter("security",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1) }));
        });
        builder.Services.AddSingleton(new LocalAuthenticationOptions(mode, true, "NetRatel.Local"));
        builder.Services.AddDbContext<NetRatelIdentityDbContext>(options => options.UseNpgsql(connectionString));
        builder.Services.AddDbContext<OrchestratorDbContext>(options => options.UseNpgsql(connectionString));
        builder.Services.Configure<IdentityOptions>(options =>
        {
            options.Password.RequiredLength = 15;
            options.Password.RequireDigit = false;
            options.Password.RequireLowercase = false;
            options.Password.RequireUppercase = false;
            options.Password.RequireNonAlphanumeric = false;
        });
        builder.Services.AddIdentityCore<LocalUser>()
            .AddEntityFrameworkStores<NetRatelIdentityDbContext>()
            .AddDefaultTokenProviders();
        builder.Services.AddScoped<IEffectiveAccessService, EffectiveAccessService>();
        builder.Services.AddScoped<InstanceAdministratorInvariant>();
        builder.Services.AddScoped<IAuthorizationHandler, EffectiveAccessHandler>();
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseRateLimiter();
        app.MapLocalAuthenticationEndpoints();
        app.MapAccessAdministrationEndpoints();
        await app.StartAsync();
        await using var scope = app.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IEffectiveAccessService>().ReconcileBuiltInRolesAsync();
        return app;
    }

    private static async Task LoginAsync(HttpClient client, string email, string password)
    {
        var login = await client.PostAsJsonAsync("/api/v2/local-auth/login", new LocalAuthenticationEndpoints.LocalLoginRequest(email, password));
        login.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var cookie = login.Headers.GetValues("Set-Cookie")
            .Single(header => header.StartsWith("NetRatel.Local=", StringComparison.Ordinal)).Split(';', 2)[0];
        client.DefaultRequestHeaders.Remove("Cookie");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", cookie).Should().BeTrue();
    }

    [Theory]
    [InlineData("Oidc")]
    [InlineData("Hybrid")]
    public async Task Active_oidc_accepts_plural_audiences_without_singular_audience(string mode)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Authentication:Mode"] = mode,
            ["Authentication:Oidc:Authority"] = "https://issuer.example.test",
            ["Authentication:Oidc:Audiences:0"] = "netratel-api",
            ["ConnectionStrings:NetRatelDb"] = _postgres.GetConnectionString() + ";Search Path=empty_bootstrap"
        }).Build();
        var store = new BootstrapStateStore(new BootstrapOptions { StateDirectory = Path.Combine(_stateRoot, $"plural-{mode}") });

        var descriptor = await new BootstrapLifecycleService(store, configuration).InitializeAsync();

        descriptor.State.Should().Be(mode == "Oidc" ? BootstrapState.Ready : BootstrapState.Unconfigured);
    }

    [Fact]
    public async Task Local_mode_ignores_inactive_placeholder_oidc_settings()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Authentication:Mode"] = "Local",
            ["Authentication:Oidc:Authority"] = "https://issuer.example.invalid",
            ["Authentication:Oidc:Audiences:0"] = "REPLACE_ME",
            ["ConnectionStrings:NetRatelDb"] = _postgres.GetConnectionString() + ";Search Path=empty_bootstrap"
        }).Build();
        var store = new BootstrapStateStore(new BootstrapOptions { StateDirectory = Path.Combine(_stateRoot, "local-stale-oidc") });

        var descriptor = await new BootstrapLifecycleService(store, configuration).InitializeAsync();

        descriptor.State.Should().Be(BootstrapState.Unconfigured);
    }

    [Fact]
    public async Task Ready_installation_rejects_a_wrong_instance_marker()
    {
        var connectionString = _postgres.GetConnectionString();
        var applicationOptions = new DbContextOptionsBuilder<OrchestratorDbContext>().UseNpgsql(connectionString).Options;
        var identityOptions = new DbContextOptionsBuilder<NetRatelIdentityDbContext>().UseNpgsql(connectionString).Options;
        await using (var application = new OrchestratorDbContext(applicationOptions)) await application.Database.MigrateAsync();
        await using (var identity = new NetRatelIdentityDbContext(identityOptions)) await identity.Database.MigrateAsync();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:NetRatelDb"] = connectionString
        }).Build();
        var stateDirectory = Path.Combine(_stateRoot, "wrong-instance-marker");
        var store = new BootstrapStateStore(new BootstrapOptions { StateDirectory = stateDirectory });
        await store.LoadOrCreateAsync();
        var proof = await File.ReadAllTextAsync(Path.Combine(stateDirectory, "setup-proof"));
        var claim = await store.ClaimSetupAsync(proof, "PostgreSQL", "ConnectionStrings:NetRatelDb");
        var initialized = await CreateInitializer(store, configuration).InitializeAsync(claim.Descriptor!.OperationId!.Value,
            new BootstrapInitializationRequest("Initial Administrator", "admin@example.test", "a local-first passphrase", "Initial tenant"));
        initialized.Succeeded.Should().BeTrue();
        await using (var application = new OrchestratorDbContext(applicationOptions))
        {
            var marker = await application.BootstrapInitializations.SingleAsync();
            application.BootstrapInitializations.Remove(marker);
            await application.SaveChangesAsync();
            application.BootstrapInitializations.Add(new BootstrapInitializationRecord
            {
                BootstrapInstanceId = Guid.NewGuid(),
                OperationId = marker.OperationId,
                TenantId = marker.TenantId,
                AdministratorUserId = marker.AdministratorUserId,
                CompletedAtUtc = marker.CompletedAtUtc
            });
            await application.SaveChangesAsync();
        }

        var restarted = await new BootstrapLifecycleService(store, configuration).InitializeAsync();

        restarted.State.Should().Be(BootstrapState.RecoveryRequired);
        restarted.RecoveryReason.Should().Be("ready-continuity-missing");
    }

    [Theory]
    [InlineData("Authentication:Oidc:Audience", "netratel-api")]
    [InlineData("Authentication:Azure:ClientId", "netratel-api")]
    public void Active_oidc_accepts_singular_and_legacy_section_audience(string audienceKey, string audience)
    {
        var authorityKey = audienceKey.Replace(audienceKey.Split(':')[2], "Authority", StringComparison.Ordinal);
        var settings = OidcApiConfiguration.Resolve(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [authorityKey] = "https://issuer.example.test",
            [audienceKey] = audience
        }).Build());

        settings.ValidateActive();
        settings.Audience.Should().Be(audience);
    }

    [Fact]
    public void AzureAd_alias_provides_audience_and_application_id_uri()
    {
        var settings = OidcApiConfiguration.Resolve(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AzureAd:TenantId"] = "00000000-0000-0000-0000-000000000001",
            ["AzureAd:ClientId"] = "netratel-api",
            ["AzureAd:AppIdUri"] = "api://netratel-api"
        }).Build());

        settings.ValidateActive();
        settings.Audiences.Should().Contain("api://netratel-api");
    }

    [Fact]
    public void Signed_oidc_token_accepts_listed_audience_and_rejects_unrelated_audience()
    {
        var settings = OidcApiConfiguration.Resolve(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Authentication:Oidc:Authority"] = "https://issuer.example.test",
            ["Authentication:Oidc:Audiences:0"] = "netratel-api",
            ["Authentication:Oidc:Audiences:1"] = "api://netratel-api"
        }).Build());
        settings.ValidateActive();
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes("synthetic-oidc-fixture-key-with-256-bit-length"));
        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = settings.Authority,
            ValidateAudience = true,
            ValidAudiences = settings.Audiences,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = key,
            ClockSkew = TimeSpan.Zero
        };
        var handler = new JwtSecurityTokenHandler();
        string Token(string audience) => handler.CreateEncodedJwt(new SecurityTokenDescriptor
        {
            Issuer = settings.Authority,
            Audience = audience,
            Subject = new ClaimsIdentity([new Claim("sub", "synthetic-user")]),
            IssuedAt = DateTime.UtcNow.AddMinutes(-1),
            Expires = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256)
        });

        var principal = handler.ValidateToken(Token("api://netratel-api"), parameters, out _);
        principal.Identity!.IsAuthenticated.Should().BeTrue();
        var rejected = () => handler.ValidateToken(Token("unrelated-api"), parameters, out _);
        rejected.Should().Throw<SecurityTokenInvalidAudienceException>();
    }

    private static BootstrapInitializationService CreateInitializer(BootstrapStateStore store, IConfiguration configuration) => new(
        store,
        configuration,
        new PasswordHasher<LocalUser>(),
        Options.Create(new IdentityOptions
        {
            Password =
            {
                RequiredLength = 15,
                RequiredUniqueChars = 1,
                RequireDigit = false,
                RequireLowercase = false,
                RequireUppercase = false,
                RequireNonAlphanumeric = false
            }
        }));

    private Task<BootstrapDescriptor> InitializeAsync(string schema, bool includeOidc, string? mode = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["ConnectionStrings:NetRatelDb"] = _postgres.GetConnectionString() + $";Search Path={schema}"
        };
        if (includeOidc)
        {
            values["Authentication:Oidc:Authority"] = "https://issuer.example.test";
            values["Authentication:Oidc:Audience"] = "netratel-api";
        }

        values["Authentication:Mode"] = mode;

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
        var options = new BootstrapOptions { StateDirectory = Path.Combine(_stateRoot, $"{schema}-{mode ?? "auto"}-{includeOidc}") };
        return new BootstrapLifecycleService(new BootstrapStateStore(options), configuration).InitializeAsync();
    }

    [Fact]
    public async Task Partial_active_oidc_configuration_is_rejected_before_creating_bootstrap_state()
    {
        var stateDirectory = Path.Combine(_stateRoot, "partial-oidc");
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Authentication:Mode"] = "Oidc",
            ["Authentication:Oidc:Authority"] = "https://issuer.example.test",
            ["ConnectionStrings:NetRatelDb"] = _postgres.GetConnectionString() + ";Search Path=empty_bootstrap"
        }).Build();

        var action = () => new BootstrapLifecycleService(
            new BootstrapStateStore(new BootstrapOptions { StateDirectory = stateDirectory }), configuration).InitializeAsync();

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Authentication:Oidc:Audience*");
        Directory.Exists(stateDirectory).Should().BeFalse();
    }
}
