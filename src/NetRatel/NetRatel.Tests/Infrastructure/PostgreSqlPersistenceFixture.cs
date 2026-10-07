using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

[CollectionDefinition(Name)]
public sealed class PostgreSqlPersistenceCollection : ICollectionFixture<PostgreSqlPersistenceFixture>
{
    public const string Name = "PostgreSQL persistence";
}

/// <summary>Shares one real PostgreSQL server while giving every regression an isolated database.</summary>
public sealed class PostgreSqlPersistenceFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16-alpine").Build();

    public ValueTask InitializeAsync() => new(_container.StartAsync());

    public ValueTask DisposeAsync() => _container.DisposeAsync();

    public async Task<string> CreateDatabaseAsync(CancellationToken cancellationToken = default)
    {
        var database = "netratel_" + Guid.NewGuid().ToString("N");
        var creationConnection = new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Pooling = false };
        await using var connection = new NpgsqlConnection(creationConnection.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", connection);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        // Each case owns a separate database. Keeping an idle pool for every case
        // exhausts the shared server during the full provider suite.
        var builder = new NpgsqlConnectionStringBuilder(_container.GetConnectionString())
        {
            Database = database,
            Pooling = false
        };
        return builder.ConnectionString;
    }
}
