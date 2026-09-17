using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NetRatel.API.Endpoints.Search;
using NetRatel.API.Services.AgentDirectory;
using NetRatel.Infrastructure.Persistence;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class GlobalSearchQueryShapeTests
{
    private static readonly string RepoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../"));

    [Theory]
    [InlineData("clients")]
    [InlineData("jobs")]
    [InlineData("requests")]
    [InlineData("tasks")]
    public void Expensive_sections_compile_to_one_bounded_postgresql_command(string section)
    {
        using var db = CreateDbContext();

        var sql = section switch
        {
            "clients" => GlobalSearchEndpoints.BuildAgentQuery(db, "linux").Take(6).ToQueryString(),
            "jobs" => GlobalSearchEndpoints.BuildJobQuery(db, "linux").Take(6).ToQueryString(),
            "requests" => GlobalSearchEndpoints.BuildRequestQuery(db, "linux").Take(6).ToQueryString(),
            "tasks" => GlobalSearchEndpoints.BuildTaskQuery(db, "linux").Take(6).ToQueryString(),
            _ => throw new ArgumentOutOfRangeException(nameof(section), section, null)
        };

        sql.Should().Contain("SELECT");
        sql.Should().Contain("LIMIT");
        sql.TrimEnd().Should().NotEndWith(";");
    }

    [Fact]
    public void Associated_queries_preserve_composite_agent_identity_and_database_side_matching()
    {
        using var db = CreateDbContext();

        var jobs = GlobalSearchEndpoints.BuildJobQuery(db, "docker04").ToQueryString();
        var requests = GlobalSearchEndpoints.BuildRequestQuery(db, "docker04").ToQueryString();
        var tasks = GlobalSearchEndpoints.BuildTaskQuery(db, "docker04").ToQueryString();

        foreach (var sql in new[] { jobs, requests, tasks })
        {
            sql.Should().Contain("LEFT JOIN");
            sql.Should().Contain("FROM \"Agents\"");
            sql.Should().Contain("\"TenantId\"");
            sql.Should().Contain("AgentId\"");
            sql.Should().Contain("EXISTS");
            sql.Should().Contain("\"DeviceInfoJson\" ILIKE");
        }
    }

    [Fact]
    public void Request_search_includes_matching_job_association()
    {
        using var db = CreateDbContext();

        var sql = GlobalSearchEndpoints.BuildRequestQuery(db, "linux disk").ToQueryString();

        sql.Should().Contain("FROM \"Jobs\"");
        sql.Should().Contain("\"RundeckJobDefinitionId\"");
        sql.Should().Contain("::text");
    }

    [Fact]
    public void Endpoint_handlers_have_one_materialization_for_each_expensive_section()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot, "src/NetRatel/NetRatel.API/Endpoints/Search/GlobalSearchEndpoints.cs"));

        CountMaterializations(source, "SearchAgentsAsync", "SearchJobsAsync").Should().Be(1);
        CountMaterializations(source, "SearchJobsAsync", "SearchRequestsAsync").Should().Be(1);
        CountMaterializations(source, "SearchRequestsAsync", "SearchTasksAsync").Should().Be(1);
        CountMaterializations(source, "SearchTasksAsync", "BuildAgentQuery").Should().Be(1);
    }

    [Fact]
    public void Agent_search_is_bounded_in_sql_and_does_not_materialize_the_directory()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot, "src/NetRatel/NetRatel.API/Services/Client/AgentDirectoryPresentation.cs"));

        source.Should().Contain("IQueryable<AgentDirectoryRow> Query");
        source.Should().Contain("EF.Functions.ILike(agent.DeviceInfoJson");
        source.Should().NotContain("LoadDirectoryAsync");
        source.Should().NotContain("ToListAsync");
    }

    [Fact]
    public void Missing_agent_search_indexes_are_modelled_and_migration_is_discoverable()
    {
        using var db = CreateDbContext();
        var agent = db.Model.FindEntityType(typeof(Agent));
        var migrations = db.GetService<IMigrationsAssembly>().Migrations.Values;

        agent.Should().NotBeNull();
        agent!.GetIndexes().Select(index => index.GetDatabaseName()).Should().Contain([
            "IX_Agents_GlobalSearch_Name_trgm",
            "IX_Agents_GlobalSearch_DeviceInfoJson_trgm"
        ]);
        migrations.Should().Contain(type => type.Name == "AddAgentGlobalSearchIndexes");
    }

    [Fact]
    public void Api_startup_warms_composed_search_queries_through_the_normal_database_registration()
    {
        var program = File.ReadAllText(Path.Combine(RepoRoot, "src/NetRatel/NetRatel.API/Program.cs"));
        var warmup = File.ReadAllText(Path.Combine(RepoRoot, "src/NetRatel/NetRatel.API/Services/Search/GlobalSearchQueryWarmupService.cs"));

        program.Should().Contain("AddHostedService<GlobalSearchQueryWarmupService>()");
        program.Should().Contain("MigrateNetRatelInfrastructureAsync()");
        warmup.Should().Contain("BuildAgentQuery");
        warmup.Should().Contain("BuildJobQuery");
        warmup.Should().Contain("BuildRequestQuery");
        warmup.Should().Contain("BuildTaskQuery");
    }

    [Fact]
    public void Web_source_timeout_remains_at_the_product_budget()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot, "src/NetRatel/NetRatel.Web/Services/Search/GlobalSearchService.cs"));

        source.Should().Contain("TimeSpan.FromMilliseconds(1200)");
    }

    [Fact]
    public void Realistic_rows_preserve_client_and_orchestration_result_presentation()
    {
        var agentId = Guid.Parse("a80d71e8-2645-467b-9872-e901e230d170");
        const string device = """{"hostName":"example-host-06","os":"Linux","architecture":"x64"}""";
        var now = DateTimeOffset.Parse("2026-08-17T08:00:00+00:00");

        var client = GlobalSearchEndpoints.MapAgent(new AgentDirectoryRow(7, agentId, "oidc", false, device, "STO"));
        var job = GlobalSearchEndpoints.MapJob(new JobSearchRow(42, "Linux Disk Report", "/reports", null, 7, agentId, string.Empty, now, now, "STO", "oidc", false, device));
        var request = GlobalSearchEndpoints.MapRequest(new RequestSearchRow(84, "WebUI", "Complete", "42", null, null, 7, agentId, string.Empty, now, "STO", "oidc", false, device));
        var task = GlobalSearchEndpoints.MapTask(new TaskSearchRow(126, "request-linux-disk", string.Empty, 7, agentId, "RunScript", "Complete", null, now, now, "oidc", false, device));

        client.Should().BeEquivalentTo(new
        {
            TenantId = 7,
            AgentId = agentId,
            DisplayName = "oidc",
            HostName = "example-host-06",
            TenantName = "STO",
            OperatingSystem = "Linux",
            Enabled = false
        });
        job.Name.Should().Be("Linux Disk Report");
        job.ClientDisplayName.Should().Be("oidc");
        job.TenantDisplayName.Should().Be("STO");
        request.JobDefinitionId.Should().Be("42");
        request.AgentDisplayName.Should().Be("oidc");
        request.AgentHostName.Should().Be("example-host-06");
        task.RequestId.Should().Be("request-linux-disk");
        task.ClientDisplayName.Should().Be("oidc");
        task.ClientHostName.Should().Be("example-host-06");
    }

    private static int CountMaterializations(string source, string methodName, string nextMethodName)
    {
        var start = source.IndexOf($"private static async Task<IResult> {methodName}", StringComparison.Ordinal);
        var end = nextMethodName == "BuildAgentQuery"
            ? source.IndexOf("internal static IQueryable<AgentDirectoryRow> BuildAgentQuery", start, StringComparison.Ordinal)
            : source.IndexOf($"private static async Task<IResult> {nextMethodName}", start + methodName.Length, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0);
        end.Should().BeGreaterThan(start);
        return source[start..end].Split(".ToListAsync(", StringSplitOptions.None).Length - 1;
    }

    private static OrchestratorDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseNpgsql("Host=localhost;Database=global_search_query_shape;Username=query_shape;Password=query_shape")
            .Options;
        return new OrchestratorDbContext(options);
    }
}
