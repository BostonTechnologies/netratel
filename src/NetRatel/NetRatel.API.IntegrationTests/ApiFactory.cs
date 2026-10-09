using System.Security.Cryptography;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;
using NetRatel.API.Bootstrap;
using NetRatel.Infrastructure;
using NetRatel.Infrastructure.Identity;
using NetRatel.Infrastructure.Persistence;
using Testcontainers.PostgreSql;
using Xunit;

public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string LocalAdministratorEmail = "openapi@example.test";
    public const string LocalAdministratorPassword = "A1! local-first passphrase";
    internal static IReadOnlyList<string> RetiredSelectorConfigurationKeys { get; } = Array.AsReadOnly<string>(
    [
        "NetRatelAkkaMigration:Enabled",
        "NetRatelAkkaMigration:PresenceEnabled",
        "NetRatelAkkaMigration:GatewayEnabled",
        "NetRatelAkkaMigration:ClientUpdatesEnabled",
        "NetRatelAkkaMigration:ControlGatewayEnabled",
        "NetRatelAkkaMigration:FileGatewayEnabled",
        "NetRatelAkkaMigration:LogGatewayEnabled",
        "NetRatelAkkaMigration:RemoteSupportGatewayEnabled",
        "NetRatelAkkaMigration:RemoteSupportV2InventoryEnabled",
        "NetRatelAkkaMigration:RemoteSupportV2LifecycleAuthorityEnabled",
        "NetRatelAkkaMigration:RemoteSupportV2ReplicaSafeEdgeEnabled",
        "NetRatelAkkaMigration:RemoteSupportV2MediaEnabled",
        "NetRatelAkkaMigration:RemoteSupportLegacyGatewayRollbackEnabled",
        "NetRatelAkkaMigration:PrimaryCardGatewayReadsEnabled",
        "NetRatelAkkaMigration:PrimaryCardGatewayActionsEnabled",
        "NetRatelAkkaMigration:TerminalGatewayEnabled",
        "NetRatelAkkaMigration:TerminalGatewayPrimaryCardEnabled",
        "NetRatelAkkaMigration:PresenceReadModelEnabled",
        "NetRatelAkkaMigration:TelemetryShadowEnabled",
        "NetRatelAkkaMigration:CommandShadowEnabled",
        "NetRatelAkkaMigration:CommandPersistenceEnabled",
        "NetRatelAkkaMigration:JobShadowEnabled",
        "NetRatelAkkaMigration:TerminalShadowEnabled",
        "NetRatelAkkaMigration:SignalRShadowEnabled",
        "NetRatelAkkaMigration:SignalRShadowLocalCanaryEnabled",
        "NetRatelAkkaMigration:PresenceAuthorityEnabled",
        "NetRatelAkkaMigration:PingAuthorityEnabled",
        "NetRatelAkkaMigration:TelemetryAuthorityEnabled",
        "NetRatelAkkaMigration:FileBrowseAuthorityEnabled",
        "NetRatelAkkaMigration:LogAuthorityEnabled",
        "NetRatelAkkaMigration:RemoteSupportAuthorityEnabled",
        "NetRatelAkkaMigration:CommandAuthorityEnabled",
        "NetRatelAkkaMigration:JobAuthorityEnabled",
        "NetRatelAkkaMigration:TerminalAuthorityEnabled",
        "NetRatelAkkaMigration:SignalRAuthorityEnabled",
        "NetRatelAkkaMigration:AuthorityMode",
        "NetRatelAkkaMigration:RemoteSupportShadowEnabled",
        "LegacyQueueWorker:Enabled"
    ]);

    private readonly string _root;
    private readonly PostgreSqlContainer _postgres;
    private readonly bool _ownsPostgres;
    private readonly bool _isolatePeerHostSettings;
    private IReadOnlyDictionary<string, string?> _settings = new Dictionary<string, string?>();
    private IReadOnlyDictionary<string, string?> _previousEnvironment = new Dictionary<string, string?>();

    public ApiFactory()
        : this(new PostgreSqlBuilder("postgres:16-alpine").Build(), ownsPostgres: true)
    {
    }

    // Only real service-link peers opt in; the collection fixture keeps its original constructor.
    internal ApiFactory(bool isolatePeerHostSettings)
        : this(new PostgreSqlBuilder("postgres:16-alpine").Build(), ownsPostgres: true,
            isolatePeerHostSettings: isolatePeerHostSettings)
    {
    }

    private ApiFactory(PostgreSqlContainer postgres, bool ownsPostgres, bool isolatePeerHostSettings = false)
    {
        _postgres = postgres;
        _ownsPostgres = ownsPostgres;
        _isolatePeerHostSettings = isolatePeerHostSettings;
        _root = Path.Combine(Path.GetTempPath(), "netratel-api-openapi", Guid.NewGuid().ToString("N"));
    }

    private ApiFactory(PostgreSqlContainer postgres, IReadOnlyDictionary<string, string?> settings,
        bool isolatePeerHostSettings = false)
        : this(postgres, ownsPostgres: false, isolatePeerHostSettings: isolatePeerHostSettings)
    {
        _settings = settings;
    }

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync();
        _settings = await CreateReadyLocalFirstSettingsAsync();
        ApplyEnvironmentSettings(
            _settings.Keys
                .Concat(RetiredSelectorConfigurationKeys)
                .Append("ConnectionStrings:Default"));
    }

    async ValueTask IAsyncDisposable.DisposeAsync()
    {
        try
        {
            Dispose();
        }
        finally
        {
            if (_ownsPostgres)
            {
                await _postgres.DisposeAsync();
            }
        }
    }

    // Physical tests copy only ready disposable configuration into private startup
    // files. Returning this dictionary neither starts an API host nor seeds runtime authority.
    internal IReadOnlyDictionary<string, string?> PhysicalRuntimeSettings() =>
        _settings.ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);

    public ApiFactory CreateRuntimeSibling(IReadOnlyDictionary<string, string?> overrides)
    {
        ArgumentNullException.ThrowIfNull(overrides);

        var settings = _settings.ToDictionary(setting => setting.Key, setting => setting.Value, StringComparer.OrdinalIgnoreCase);
        foreach (var setting in overrides)
        {
            settings[setting.Key] = setting.Value;
        }

        var sibling = new ApiFactory(_postgres, settings,
            isolatePeerHostSettings: _isolatePeerHostSettings);
        sibling.ApplyEnvironmentSettings(settings.Keys.Concat(RetiredSelectorConfigurationKeys));
        return sibling;
    }

    public ApiFactory CreateUnavailableDatabaseSibling(IReadOnlyDictionary<string, string?>? overrides = null)
    {
        var settings = _settings.ToDictionary(setting => setting.Key, setting => setting.Value, StringComparer.OrdinalIgnoreCase);
        if (overrides is not null)
        {
            foreach (var setting in overrides)
            {
                settings[setting.Key] = setting.Value;
            }
        }

        var sibling = new ApiFactory(_postgres, settings);
        var readyBootstrapDirectory = _settings["Bootstrap:StateDirectory"]!;
        var isolatedBootstrapDirectory = Path.Combine(sibling._root, "bootstrap");
        CopyDirectory(readyBootstrapDirectory, isolatedBootstrapDirectory);
        settings["Bootstrap:StateDirectory"] = isolatedBootstrapDirectory;

        var unavailableDatabase = new NpgsqlConnectionStringBuilder(
            settings["ConnectionStrings:NetRatelDb"]
            ?? throw new InvalidOperationException("The integration API host has no PostgreSQL connection string."))
        {
            Host = IPAddress.Loopback.ToString(),
            Port = 1,
            Timeout = 1,
            CommandTimeout = 1
        };
        settings["ConnectionStrings:NetRatelDb"] = unavailableDatabase.ConnectionString;
        sibling._settings = settings;
        sibling.ApplyEnvironmentSettings(settings.Keys.Concat(RetiredSelectorConfigurationKeys));
        return sibling;
    }

    public ApiFactory CreateUnconfiguredSibling(IReadOnlyDictionary<string, string?> overrides)
    {
        ArgumentNullException.ThrowIfNull(overrides);
        var settings = _settings.ToDictionary(setting => setting.Key, setting => setting.Value, StringComparer.OrdinalIgnoreCase);
        foreach (var setting in overrides)
        {
            settings[setting.Key] = setting.Value;
        }

        var sibling = new ApiFactory(_postgres, settings);
        settings.Remove("ConnectionStrings:NetRatelDb");
        settings.Remove("ConnectionStrings:Default");
        settings["Bootstrap:StateDirectory"] = Path.Combine(sibling._root, "bootstrap-unconfigured");
        sibling._settings = settings;
        sibling.ApplyEnvironmentSettings(
            settings.Keys
                .Concat(RetiredSelectorConfigurationKeys)
                .Append("ConnectionStrings:NetRatelDb")
                .Append("ConnectionStrings:Default"));
        return sibling;
    }

    internal async Task<IAsyncDisposable> DenyApplicationDatabaseConnectionsAsync()
    {
        var applicationConnectionString = _settings["ConnectionStrings:NetRatelDb"]
            ?? throw new InvalidOperationException("The integration API host has no PostgreSQL connection string.");
        var applicationDatabase = new NpgsqlConnectionStringBuilder(applicationConnectionString).Database;
        if (string.IsNullOrWhiteSpace(applicationDatabase) ||
            string.Equals(applicationDatabase, "template1", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The integration API host must target an application database outside template1.");
        }

        var controlConnectionString = new NpgsqlConnectionStringBuilder(applicationConnectionString)
        {
            Database = "template1",
            Pooling = false,
            Timeout = 5,
            CommandTimeout = 5
        };
        var controlConnection = new NpgsqlConnection(controlConnectionString.ConnectionString);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var quotedDatabase = new NpgsqlCommandBuilder().QuoteIdentifier(applicationDatabase);
        var blocked = false;
        try
        {
            await controlConnection.OpenAsync(timeout.Token);
            await using var command = controlConnection.CreateCommand();
            command.CommandTimeout = 5;
            blocked = true;
            command.CommandText = $"ALTER DATABASE {quotedDatabase} WITH ALLOW_CONNECTIONS FALSE";
            await command.ExecuteNonQueryAsync(timeout.Token);

            command.CommandText = "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = @databaseName AND pid <> pg_backend_pid()";
            command.Parameters.AddWithValue("databaseName", applicationDatabase);
            await command.ExecuteNonQueryAsync(timeout.Token);
            ClearApplicationConnectionPool(applicationConnectionString);

            return new ApplicationDatabaseConnectionLease(
                controlConnection,
                quotedDatabase,
                applicationConnectionString);
        }
        catch
        {
            if (blocked)
            {
                try
                {
                    await using var restore = controlConnection.CreateCommand();
                    restore.CommandTimeout = 5;
                    restore.CommandText = $"ALTER DATABASE {quotedDatabase} WITH ALLOW_CONNECTIONS TRUE";
                    await restore.ExecuteNonQueryAsync(CancellationToken.None);
                }
                finally
                {
                    try
                    {
                        ClearApplicationConnectionPool(applicationConnectionString);
                    }
                    finally
                    {
                        await controlConnection.DisposeAsync();
                    }
                }
            }
            else
            {
                await controlConnection.DisposeAsync();
            }

            throw;
        }
    }

    public string CreateAgentBearerToken(
        int tenantId,
        Guid agentId,
        bool includeRole = true,
        bool includeTenant = true,
        string? issuer = null,
        string? audience = null,
        bool expired = false,
        bool invalidSignature = false)
    {
        var keyPath = _settings["AgentAuth:PrivateKeyPath"]
            ?? throw new InvalidOperationException("The integration API host has no agent signing key path.");
        using var privateKey = ECDsa.Create();
        privateKey.ImportFromPem(File.ReadAllText(keyPath));
        using var alternateKey = invalidSignature ? ECDsa.Create(ECCurve.NamedCurves.nistP256) : null;
        var signingKey = new ECDsaSecurityKey(alternateKey ?? privateKey)
        {
            KeyId = _settings.GetValueOrDefault("AgentAuth:SigningKeyId") ?? "netratel-agent-es256",
            CryptoProviderFactory = new CryptoProviderFactory { CacheSignatureProviders = false }
        };
        var credentials = new SigningCredentials(signingKey, SecurityAlgorithms.EcdsaSha256);
        var now = DateTime.UtcNow;
        var claims = new List<Claim>
        {
            new("sub", agentId.ToString("D")),
            new("agent_id", agentId.ToString("D")),
            new("scope", "netratel:connect")
        };
        if (includeTenant)
        {
            claims.Add(new Claim("tenant_id", tenantId.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }
        if (includeRole)
        {
            claims.Add(new Claim("role", "agent"));
        }

        var token = new JwtSecurityToken(
            issuer: issuer ?? _settings.GetValueOrDefault("AgentAuth:Issuer") ?? "https://netratel.example.invalid",
            audience: audience ?? _settings.GetValueOrDefault("AgentAuth:Audience") ?? "netratel-agent",
            claims,
            notBefore: expired ? now.AddMinutes(-15) : now.AddSeconds(-5),
            expires: expired ? now.AddMinutes(-10) : now.AddMinutes(5),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        if (_isolatePeerHostSettings)
        {
            var settings = _settings.ToDictionary(setting => setting.Key, setting => setting.Value, StringComparer.OrdinalIgnoreCase);
            // The actual peer always has a current database and non-null settings.
            // Do not generalize the early argument seam to nullable/default or deployment profiles.
            if (!settings.TryGetValue("ConnectionStrings:NetRatelDb", out var connectionString) ||
                string.IsNullOrWhiteSpace(connectionString) || settings.Values.Any(static value => value is null) ||
                settings.ContainsKey("ConnectionStrings:Default") ||
                settings.Keys.Any(static key => key.StartsWith("M2MClients", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Isolated peer host settings require a nonempty database, non-null values and no default or deployment-client overrides.");

            // Default cannot supply authority while this peer's NetRatelDb is present.
            // Retired selectors have no production consumers; keep ambient values suppressed.
            settings["ConnectionStrings:Default"] = string.Empty;
            foreach (var key in RetiredSelectorConfigurationKeys) settings.TryAdd(key, string.Empty);
            // MVC.Testing10 converts this host configuration to entry-point arguments
            // before Program reads bootstrap/database/key paths; app configuration alone is later.
            builder.ConfigureHostConfiguration(configuration => configuration.AddInMemoryCollection(settings));
        }

        return base.CreateHost(builder);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Production");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(_settings));
    }

    public async Task<HttpClient> CreateLocalAdministratorClientAsync(TimeSpan? timeout = null)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        if (timeout is { } requestTimeout)
        {
            client.Timeout = requestTimeout;
        }

        using var login = await client.PostAsJsonAsync("/api/v2/local-auth/login", new
        {
            Email = LocalAdministratorEmail,
            Password = LocalAdministratorPassword,
            RememberMe = false
        });
        if (login.StatusCode != HttpStatusCode.NoContent)
        {
            client.Dispose();
            throw new InvalidOperationException($"Local administrator login failed with HTTP {(int)login.StatusCode}.");
        }

        return client;
    }

    public async Task<HttpClient> CreateLocalUserClientAsync(string email, string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        await using (var scope = Services.CreateAsyncScope())
        {
            var identity = scope.ServiceProvider.GetRequiredService<NetRatelIdentityDbContext>();
            if (!await identity.Users.AnyAsync(user => user.Email == email))
            {
                var principal = new ApplicationPrincipal();
                var user = new LocalUser
                {
                    UserName = email,
                    Email = email,
                    NormalizedUserName = email.ToUpperInvariant(),
                    NormalizedEmail = email.ToUpperInvariant(),
                    EmailConfirmed = true,
                    DisplayName = "Unprivileged Integration User",
                    PrincipalId = principal.Id,
                    IsEnabled = true,
                    IsInstanceAdministrator = false
                };
                user.PasswordHash = new PasswordHasher<LocalUser>().HashPassword(user, password);
                principal.LocalUserId = user.Id;
                identity.ApplicationPrincipals.Add(principal);
                identity.Users.Add(user);
                await identity.SaveChangesAsync();
            }
        }

        var client = CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        using var login = await client.PostAsJsonAsync("/api/v2/local-auth/login", new
        {
            Email = email,
            Password = password,
            RememberMe = false
        });
        if (login.StatusCode != HttpStatusCode.NoContent)
        {
            client.Dispose();
            throw new InvalidOperationException($"Local user login failed with HTTP {(int)login.StatusCode}.");
        }

        return client;
    }

    protected override void Dispose(bool disposing)
    {
        if (!disposing)
        {
            base.Dispose(disposing);
            return;
        }

        try
        {
            base.Dispose(disposing);
        }
        finally
        {
            RestoreEnvironmentSettings();
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
    }

    private void ApplyEnvironmentSettings(IEnumerable<string> configurationKeys)
    {
        if (_isolatePeerHostSettings) return;
        var keys = configurationKeys.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var environmentKeys = keys.Select(EnvironmentKey).Distinct(StringComparer.Ordinal).ToArray();
        _previousEnvironment = environmentKeys.ToDictionary(
            static key => key,
            Environment.GetEnvironmentVariable,
            StringComparer.Ordinal);

        foreach (var configurationKey in keys)
        {
            Environment.SetEnvironmentVariable(
                EnvironmentKey(configurationKey),
                _settings.GetValueOrDefault(configurationKey));
        }
    }

    private void RestoreEnvironmentSettings()
    {
        if (_isolatePeerHostSettings) return;
        foreach (var setting in _previousEnvironment)
        {
            Environment.SetEnvironmentVariable(setting.Key, setting.Value);
        }

        _previousEnvironment = new Dictionary<string, string?>();
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
            ["AgentAuth:Issuer"] = "https://netratel.example.invalid",
            ["AgentAuth:Audience"] = "netratel-agent",
            ["AgentAuth:SigningKeyId"] = "netratel-agent-es256",
            ["Authentication:Mode"] = "Local",
            ["Authentication:Local:AllowInsecureLocalhost"] = "true"
        };
        using (var key = ECDsa.Create(ECCurve.NamedCurves.nistP256))
        {
            await File.WriteAllTextAsync(settings["AgentAuth:PrivateKeyPath"]!, key.ExportPkcs8PrivateKeyPem());
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var migrationServices = new ServiceCollection();
        migrationServices.AddSingleton<IConfiguration>(configuration);
        migrationServices.AddNetRatelInfrastructure(configuration);
        await using (var migrationProvider = migrationServices.BuildServiceProvider())
        {
            await migrationProvider.MigrateNetRatelInfrastructureAsync();
        }

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
            new BootstrapInitializationRequest("OpenAPI Administrator", LocalAdministratorEmail, LocalAdministratorPassword, "OpenAPI tenant"));
        if (!initialized.Succeeded)
        {
            throw new InvalidOperationException("The Release OpenAPI test host could not complete local-first initialization.");
        }

        return settings;
    }

    private static string EnvironmentKey(string configurationKey) => configurationKey.Replace(":", "__", StringComparison.Ordinal);

    private static void ClearApplicationConnectionPool(string connectionString)
    {
        using var connection = new NpgsqlConnection(connectionString);
        NpgsqlConnection.ClearPool(connection);
    }

    private static void CopyDirectory(string sourceDirectory, string targetDirectory)
    {
        Directory.CreateDirectory(targetDirectory);
        foreach (var sourceFile in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, sourceFile);
            var targetFile = Path.Combine(targetDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
            File.Copy(sourceFile, targetFile, overwrite: true);
        }
    }

    private sealed class ApplicationDatabaseConnectionLease(
        NpgsqlConnection controlConnection,
        string quotedDatabase,
        string applicationConnectionString) : IAsyncDisposable
    {
        private NpgsqlConnection? _controlConnection = controlConnection;

        public async ValueTask DisposeAsync()
        {
            var connection = Interlocked.Exchange(ref _controlConnection, null);
            if (connection is null)
            {
                return;
            }

            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await using var command = connection.CreateCommand();
                command.CommandTimeout = 5;
                command.CommandText = $"ALTER DATABASE {quotedDatabase} WITH ALLOW_CONNECTIONS TRUE";
                await command.ExecuteNonQueryAsync(timeout.Token);
            }
            finally
            {
                try
                {
                    ClearApplicationConnectionPool(applicationConnectionString);
                }
                finally
                {
                    await connection.DisposeAsync();
                }
            }
        }
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ApiIntegrationCollection : ICollectionFixture<ApiFactory>
{
    public const string Name = "API integration host";
}
