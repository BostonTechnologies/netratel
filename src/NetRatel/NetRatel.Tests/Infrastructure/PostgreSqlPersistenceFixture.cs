using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

[CollectionDefinition(Name)]
public sealed class PostgreSqlPersistenceCollection : ICollectionFixture<PostgreSqlPersistenceFixture>
{
    public const string Name = "PostgreSQL persistence";
}

[CollectionDefinition(Name)]
public sealed class FlowPostgreSqlPersistenceCollection : ICollectionFixture<PostgreSqlPersistenceFixture>
{
    public const string Name = "Flow PostgreSQL persistence";
}

/// <summary>Shares one real PostgreSQL server while giving every regression an isolated database.</summary>
public sealed class PostgreSqlPersistenceFixture : IAsyncLifetime
{
    // These databases live only for this running fixture. Avoid the Docker
    // writable layer's per-migration I/O without changing PostgreSQL's commit,
    // WAL or fsync settings, or reusing data between regressions.
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16-alpine")
        .WithTmpfsMount("/var/lib/postgresql/data")
        .Build();

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
