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
using Xunit;

public sealed class ApiFactory : WebApplicationFactory<Program>
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "netratel-api-openapi", Guid.NewGuid().ToString("N"));
    private readonly IReadOnlyDictionary<string, string?> _settings;
    private readonly IReadOnlyDictionary<string, string?> _previousEnvironment;

    public ApiFactory()
    {
        _settings = CreateReadyLocalFirstSettings();
        _previousEnvironment = _settings.Keys.ToDictionary(
            EnvironmentKey,
            Environment.GetEnvironmentVariable,
            StringComparer.Ordinal);
        foreach (var setting in _settings)
        {
            Environment.SetEnvironmentVariable(EnvironmentKey(setting.Key), setting.Value);
        }
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

    private IReadOnlyDictionary<string, string?> CreateReadyLocalFirstSettings()
    {
        Directory.CreateDirectory(_root);
        var databasePath = Path.Combine(_root, "netratel.db");
        var stateDirectory = Path.Combine(_root, "bootstrap");
        var connectionString = $"Data Source={databasePath};Foreign Keys=True";
        var settings = new Dictionary<string, string?>
        {
            ["Database:Provider"] = "Sqlite",
            ["Database:InstanceCount"] = "1",
            ["ConnectionStrings:NetRatelDb"] = connectionString,
            ["Bootstrap:StateDirectory"] = stateDirectory,
            ["DataProtection:KeysDirectory"] = Path.Combine(_root, "keys"),
            ["StorageOptions:RootPath"] = Path.Combine(_root, "storage"),
            ["ClientArtifacts:StorageRoot"] = Path.Combine(_root, "artifacts"),
            ["AgentAuth:PrivateKeyPath"] = Path.Combine(_root, "agent-private-key.pem"),
            ["NetRatelAkkaMigration:Enabled"] = "false"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var applicationOptions = new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseSqlite(connectionString, sqlite => sqlite.MigrationsAssembly("NetRatel.SqliteMigrations"))
            .Options;
        var identityOptions = new DbContextOptionsBuilder<NetRatelIdentityDbContext>()
            .UseSqlite(connectionString, sqlite => sqlite.MigrationsAssembly("NetRatel.SqliteMigrations"))
            .Options;
        using (var application = new OrchestratorDbContext(applicationOptions)) application.Database.Migrate();
        using (var identity = new NetRatelIdentityDbContext(identityOptions)) identity.Database.Migrate();

        var bootstrapOptions = new BootstrapOptions { StateDirectory = stateDirectory };
        var store = new BootstrapStateStore(bootstrapOptions);
        store.LoadOrCreateAsync().GetAwaiter().GetResult();
        var proof = File.ReadAllText(Path.Combine(stateDirectory, "setup-proof"));
        var claim = store.ClaimSetupAsync(proof, "SQLite", "ConnectionStrings:NetRatelDb").GetAwaiter().GetResult();
        if (!claim.Succeeded || claim.Descriptor?.OperationId is not { } operationId)
        {
            throw new InvalidOperationException("The Release OpenAPI test host could not claim local-first initialization.");
        }

        var initializer = new BootstrapInitializationService(
            store,
            configuration,
            new PasswordHasher<LocalUser>(),
            Options.Create(new IdentityOptions()));
        var initialized = initializer.InitializeAsync(
            operationId,
            new BootstrapInitializationRequest("OpenAPI Administrator", "openapi@example.test", "A1! local-first passphrase", "OpenAPI tenant"))
            .GetAwaiter().GetResult();
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
