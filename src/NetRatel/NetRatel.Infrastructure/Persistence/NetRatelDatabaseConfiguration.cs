using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace NetRatel.Infrastructure.Persistence;

public enum NetRatelDatabaseProvider
{
    PostgreSql,
    Sqlite
}

public sealed record NetRatelDatabaseConfiguration(
    NetRatelDatabaseProvider Provider,
    string ConnectionString);

/// <summary>Resolves the one durable application datastore selected at startup.</summary>
public static class NetRatelDatabaseConfigurationResolver
{
    public const string ProviderKey = "Database:Provider";
    public const string InstanceCountKey = "Database:InstanceCount";

    public static NetRatelDatabaseConfiguration Resolve(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var rawProvider = configuration[ProviderKey];
        var provider = string.IsNullOrWhiteSpace(rawProvider) ||
                       string.Equals(rawProvider, "postgres", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(rawProvider, "postgresql", StringComparison.OrdinalIgnoreCase)
            ? NetRatelDatabaseProvider.PostgreSql
            : string.Equals(rawProvider, "sqlite", StringComparison.OrdinalIgnoreCase)
                ? NetRatelDatabaseProvider.Sqlite
                : throw new InvalidOperationException(
                    $"{ProviderKey} must be PostgreSql or Sqlite.");

        var connectionString = configuration.GetConnectionString("NetRatelDb")
            ?? configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException(
                "A database connection string is required. Configure ConnectionStrings:NetRatelDb or ConnectionStrings:Default.");

        if (provider is NetRatelDatabaseProvider.PostgreSql)
        {
            return new(provider, connectionString);
        }

        var instanceCount = configuration.GetValue<int?>(InstanceCountKey) ?? 1;
        if (instanceCount != 1)
        {
            throw new InvalidOperationException(
                "SQLite is supported only for a single NetRatel instance. Set Database:InstanceCount to 1 or use PostgreSQL.");
        }

        var sqlite = new SqliteConnectionStringBuilder(connectionString);
        if (string.IsNullOrWhiteSpace(sqlite.DataSource) || string.Equals(sqlite.DataSource, ":memory:", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("SQLite requires a durable absolute Data Source path.");
        }

        if (!Path.IsPathFullyQualified(sqlite.DataSource))
        {
            throw new InvalidOperationException("SQLite Data Source must be an absolute path.");
        }

        sqlite.ForeignKeys = true;
        sqlite.DefaultTimeout = 5;
        return new(provider, sqlite.ConnectionString);
    }
}
