using System.Diagnostics;
using FluentAssertions;
using Npgsql;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

[Collection(PostgreSqlPersistenceCollection.Name)]
public sealed class PostgreSqlPersistenceFixtureTests(PostgreSqlPersistenceFixture postgres)
{
    [Fact]
    public async Task Disposed_isolated_database_connections_release_physical_server_sessions()
    {
        var connectionStrings = new[] { await postgres.CreateDatabaseAsync(), await postgres.CreateDatabaseAsync() };
        var databases = connectionStrings.Select(value => new NpgsqlConnectionStringBuilder(value).Database!).ToArray();
        var connections = connectionStrings.SelectMany(value => Enumerable.Range(0, 2).Select(_ => new NpgsqlConnection(value))).ToArray();
        try
        {
            await using var observer = new NpgsqlConnection(connectionStrings[0]);
            await observer.OpenAsync();
            foreach (var connection in connections) await connection.OpenAsync();
            (await CountSessionsAsync(observer, databases)).Should().Be(4, "each database must support real concurrent connections");

            foreach (var connection in connections) await connection.DisposeAsync();
            var elapsed = Stopwatch.StartNew();
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(25));
            var retainedSessions = await CountSessionsAsync(observer, databases);
            while (retainedSessions != 0 && elapsed.Elapsed < TimeSpan.FromSeconds(5))
            {
                await timer.WaitForNextTickAsync();
                retainedSessions = await CountSessionsAsync(observer, databases);
            }
            retainedSessions.Should().Be(0,
                "disposing an isolated regression must release its physical sessions instead of accumulating idle pools for every database");
        }
        finally
        {
            foreach (var connection in connections) await connection.DisposeAsync();
            // Keep a failing regression from leaking its own sessions into the rest of this collection.
            foreach (var connectionString in connectionStrings)
            {
                await using var ownedPool = new NpgsqlConnection(connectionString);
                NpgsqlConnection.ClearPool(ownedPool);
            }
        }
    }

    private static async Task<long> CountSessionsAsync(NpgsqlConnection observer, string[] databases)
    {
        await using var command = new NpgsqlCommand("""
            SELECT count(*) FROM pg_stat_activity
            WHERE datname = ANY (@databases) AND pid <> pg_backend_pid()
            """, observer);
        command.Parameters.AddWithValue("databases", databases);
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
