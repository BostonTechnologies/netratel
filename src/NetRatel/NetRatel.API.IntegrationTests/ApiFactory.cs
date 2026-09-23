using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NetRatel.API.Bootstrap;
using NetRatel.Infrastructure.Identity;
using NetRatel.Infrastructure.Persistence;
using Testcontainers.PostgreSql;
using Xunit;

public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "netratel-api-openapi", Guid.NewGuid().ToString("N"));
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine").Build();
    private IReadOnlyDictionary<string, string?> _settings = new Dictionary<string, string?>();
    private IReadOnlyDictionary<string, string?> _previousEnvironment = new Dictionary<string, string?>();

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync();
        _settings = await CreateReadyLocalFirstSettingsAsync();
        _previousEnvironment = _settings.Keys.ToDictionary(
            EnvironmentKey,
            Environment.GetEnvironmentVariable,
            StringComparer.Ordinal);
        foreach (var setting in _settings)
        {
            Environment.SetEnvironmentVariable(EnvironmentKey(setting.Key), setting.Value);
        }
    }

    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        Dispose();
        await _postgres.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Production");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(_settings));
        builder.ConfigureTestServices(services =>
        {
            services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = "Test";
                options.DefaultChallengeScheme = "Test";
            }).AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(_root))
        {
            foreach (var setting in _previousEnvironment)
            {
                Environment.SetEnvironmentVariable(setting.Key, setting.Value);
            }
            Directory.Delete(_root, recursive: true);
        }
    }

    private async Task<IReadOnlyDictionary<string, string?>> CreateReadyLocalFirstSettingsAsync()
    {
        Directory.CreateDirectory(_root);
        var stateDirectory = Path.Combine(_root, "bootstrap");
        var connectionString = _postgres.GetConnectionString();
        var settings = new Dictionary<string, string?>
        {
            ["Database:Provider"] = "PostgreSql",
            ["ConnectionStrings:NetRatelDb"] = connectionString,
            ["Bootstrap:StateDirectory"] = stateDirectory,
            ["DataProtection:KeysDirectory"] = Path.Combine(_root, "keys"),
            ["StorageOptions:RootPath"] = Path.Combine(_root, "storage"),
            ["ClientArtifacts:StorageRoot"] = Path.Combine(_root, "artifacts"),
            ["AgentAuth:PrivateKeyPath"] = Path.Combine(_root, "agent-private-key.pem"),
            ["NetRatelAkkaMigration:Enabled"] = "false"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var applicationOptions = new DbContextOptionsBuilder<OrchestratorDbContext>().UseNpgsql(connectionString).Options;
        var identityOptions = new DbContextOptionsBuilder<NetRatelIdentityDbContext>().UseNpgsql(connectionString).Options;
        await using (var application = new OrchestratorDbContext(applicationOptions)) await application.Database.MigrateAsync();
        await using (var identity = new NetRatelIdentityDbContext(identityOptions)) await identity.Database.MigrateAsync();

        var bootstrapOptions = new BootstrapOptions { StateDirectory = stateDirectory };
        var store = new BootstrapStateStore(bootstrapOptions);
        await store.LoadOrCreateAsync();
        var proof = await File.ReadAllTextAsync(Path.Combine(stateDirectory, "setup-proof"));
        var claim = await store.ClaimSetupAsync(proof, "PostgreSQL", "ConnectionStrings:NetRatelDb");
        if (!claim.Succeeded || claim.Descriptor?.OperationId is not { } operationId)
        {
            throw new InvalidOperationException("The Release OpenAPI test host could not claim local-first initialization.");
        }

        var initializer = new BootstrapInitializationService(
            store,
            configuration,
            new PasswordHasher<LocalUser>(),
            Options.Create(new IdentityOptions()));
        var initialized = await initializer.InitializeAsync(
            operationId,
            new BootstrapInitializationRequest("OpenAPI Administrator", "openapi@example.test", "A1! local-first passphrase", "OpenAPI tenant"));
        if (!initialized.Succeeded)
        {
            throw new InvalidOperationException("The Release OpenAPI test host could not complete local-first initialization.");
        }

        return settings;
    }

    private static string EnvironmentKey(string configurationKey) => configurationKey.Replace(":", "__", StringComparison.Ordinal);
}

[CollectionDefinition(Name)]
public sealed class ApiIntegrationCollection : ICollectionFixture<ApiFactory>
{
    public const string Name = "API integration host";
}
