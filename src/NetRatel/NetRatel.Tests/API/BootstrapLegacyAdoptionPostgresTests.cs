using FluentAssertions;
using Microsoft.Extensions.Configuration;
using NetRatel.API.Bootstrap;
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

        var adopted = await InitializeAsync("legacy", includeOidc: true);
        adopted.State.Should().Be(BootstrapState.Ready);
        adopted.AdoptedExistingInstallation.Should().BeTrue();
        adopted.SelectedProvider.Should().Be("PostgreSQL");
        adopted.ConnectionReference.Should().Be("ConnectionStrings:NetRatelDb");
    }

    private Task<BootstrapDescriptor> InitializeAsync(string schema, bool includeOidc)
    {
        var values = new Dictionary<string, string?>
        {
            ["ConnectionStrings:NetRatelDb"] = _postgres.GetConnectionString() + $";Search Path={schema}"
        };
        if (includeOidc)
        {
            values["Authentication:Oidc:Authority"] = "https://issuer.example.test";
        }

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
        var options = new BootstrapOptions { StateDirectory = Path.Combine(_stateRoot, schema) };
        return new BootstrapLifecycleService(new BootstrapStateStore(options), configuration).InitializeAsync();
    }
}
