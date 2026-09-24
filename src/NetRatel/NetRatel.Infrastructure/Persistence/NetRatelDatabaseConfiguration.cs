using Microsoft.Extensions.Configuration;
using Npgsql;

namespace NetRatel.Infrastructure.Persistence;

public sealed record NetRatelDatabaseConfiguration(string ConnectionString);

/// <summary>Resolves the one durable application datastore selected at startup.</summary>
public static class NetRatelDatabaseConfigurationResolver
{
    public const string ProviderKey = "Database:Provider";
    public static void ValidateProvider(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var rawProvider = configuration[ProviderKey]?.Trim();
        if (string.IsNullOrWhiteSpace(rawProvider) ||
            string.Equals(rawProvider, "postgres", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(rawProvider, "postgresql", StringComparison.OrdinalIgnoreCase))
            return;

        throw new InvalidOperationException(string.Equals(rawProvider, "sqlite", StringComparison.OrdinalIgnoreCase)
            ? "SQLite application storage is retired. Preserve the existing installation and configure a PostgreSQL database explicitly; NetRatel will not convert or replace the SQLite data file."
            : $"{ProviderKey} must be PostgreSql. The configured value '{rawProvider}' is unsupported.");
    }

    public static NetRatelDatabaseConfiguration Resolve(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        ValidateProvider(configuration);

        var connectionString = configuration.GetConnectionString("NetRatelDb")
            ?? configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException(
                "A database connection string is required. Configure ConnectionStrings:NetRatelDb or ConnectionStrings:Default.");

        try
        {
            var postgres = new NpgsqlConnectionStringBuilder(connectionString);
            if (string.IsNullOrWhiteSpace(postgres.Host) || string.IsNullOrWhiteSpace(postgres.Database))
                throw new InvalidOperationException("PostgreSQL requires Host and Database in ConnectionStrings:NetRatelDb (or the compatibility Default alias).");
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException("ConnectionStrings:NetRatelDb must be a PostgreSQL connection string with Host and Database; SQLite Data Source values are not accepted.", exception);
        }

        return new(connectionString);
    }
}
