using Microsoft.EntityFrameworkCore;
using NetRatel.Infrastructure.Persistence;
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
    private readonly SemaphoreSlim _templateGate = new(1);
    private string? _orchestratorTemplate;

    public ValueTask InitializeAsync() => new(_container.StartAsync());

    public ValueTask DisposeAsync()
    {
        _templateGate.Dispose();
        return _container.DisposeAsync();
    }

    public Task<string> CreateDatabaseAsync(CancellationToken cancellationToken = default) =>
        CreateDatabaseAsync("template1", cancellationToken);

    public async Task<string> CreateOrchestratorDatabaseAsync(CancellationToken cancellationToken = default)
    {
        // Data-path tests each get a fresh copy of the current migrated schema.
        string template;
        await _templateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_orchestratorTemplate is null)
            {
                var connection = await CreateDatabaseAsync(cancellationToken).ConfigureAwait(false);
                var options = new DbContextOptionsBuilder<OrchestratorDbContext>().UseNpgsql(connection).Options;
                await using (var db = new OrchestratorDbContext(options))
                {
                    await db.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
                }
                _orchestratorTemplate = new NpgsqlConnectionStringBuilder(connection).Database!;
            }
            template = _orchestratorTemplate;
        }
        finally
        {
            _templateGate.Release();
        }

        return await CreateDatabaseAsync(template, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> CreateDatabaseAsync(string template, CancellationToken cancellationToken)
    {
        var database = "netratel_" + Guid.NewGuid().ToString("N");
        var creationConnection = new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Pooling = false };
        await using var connection = new NpgsqlConnection(creationConnection.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand($"CREATE DATABASE \"{database}\" TEMPLATE \"{template}\"", connection);
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
