using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NetRatel.Shared;
using NetRatel.Shared.Contracts;
using NetRatel.Shared.Contracts.Jobs;
using NetRatel.Shared.Contracts.Tasks;
using NetRatel.Web.Services.Search;
using Xunit;

namespace NetRatel.Tests.Web;

public sealed class GlobalSearchServiceTests
{
    [Fact]
    public async Task Empty_query_returns_high_level_sections_without_api_calls()
    {
        var service = new GlobalSearchService(new FakeHttpClientFactory(new Dictionary<string, object?>()), NullLogger<GlobalSearchService>.Instance);

        var groups = await service.SearchAsync("");

        groups.Select(g => g.Key).Should().Equal("tenants", "clients", "scripts", "jobs", "requests", "tasks");
        groups.Should().OnlyContain(g => g.Results.Count == 0);
    }

    [Fact]
    public async Task Search_matches_across_configured_sections_and_builds_filter_links()
    {
        var factory = new FakeHttpClientFactory(new Dictionary<string, object?>
        {
            ["/api/v1/global-search/tenants?q=Camelot&pageSize=6"] = Page(new[]
            {
                new TenantDto(2, "Camelot-Estate", "Luxury estate", "Cape Town", ["camelot.example"], "Arthur", "arthur@example.com", false, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
            }),
            ["/api/v1/global-search/jobs?q=Camelot&pageSize=6"] = Page(new[]
            {
                new JobDto(12, "Camelot backup", "/estate", "Nightly estate backup", 2, "client-1", 0, 0, "Camelot-Estate", "DESKTOP-UANOJ1P", ClientEnvironment.Dev)
            })
        });
        var service = new GlobalSearchService(factory, NullLogger<GlobalSearchService>.Instance);

        var groups = await service.SearchAsync("Camelot");

        factory.RequestedPaths.Should().Contain("/api/v1/global-search/tenants?q=Camelot&pageSize=6");
        factory.RequestedPaths.Should().Contain("/api/v1/global-search/jobs?q=Camelot&pageSize=6");
        groups.Single(g => g.Key == "tenants").Results.Should().ContainSingle()
            .Which.Href.Should().Be("/tenants?search=Camelot-Estate");
        groups.Single(g => g.Key == "jobs").Results.Should().ContainSingle()
            .Which.Href.Should().Be("/jobs?search=Camelot%20backup");
    }

    [Fact]
    public async Task Search_caps_results_per_group_and_prefers_title_matches()
    {
        var tenants = Enumerable.Range(1, 8)
            .Select(i => new TenantDto(i, i == 8 ? "Acme Primary" : $"Tenant {i}", $"Acme description {i}", null, [], null, null, false, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow))
            .ToArray();

        var service = new GlobalSearchService(new FakeHttpClientFactory(new Dictionary<string, object?>
        {
            ["/api/v1/global-search/tenants?q=Acme&pageSize=6"] = Page(tenants)
        }), NullLogger<GlobalSearchService>.Instance);

        var group = (await service.SearchAsync("Acme")).Single(g => g.Key == "tenants");

        group.Results.Should().HaveCount(5);
        group.Results[0].Title.Should().Be("Acme Primary");
        group.ContinueHref.Should().Be("/tenants?search=Acme");
    }

    [Fact]
    public async Task Clients_use_agent_keyed_search_records_without_legacy_client_identity()
    {
        var agentId = Guid.Parse("5d9e3a1f-310a-4d73-83c5-9a5517746657");
        var service = new GlobalSearchService(new FakeHttpClientFactory(new Dictionary<string, object?>
        {
            ["/api/v1/global-search/clients?q=ops-host&pageSize=6"] = Page(new[]
            {
                new GlobalSearchAgentDto(9, agentId, "Operations host", "ops-host-01", "Acme", "Linux", "1.2.3", true)
            })
        }), NullLogger<GlobalSearchService>.Instance);

        var clients = (await service.SearchAsync("ops-host")).Single(group => group.Key == "clients");

        clients.Results.Should().ContainSingle();
        clients.Results[0].Title.Should().Be("Operations host");
        clients.Results[0].Href.Should().Be("/clients?search=Operations%20host");
    }

    [Fact]
    public async Task Tasks_keep_request_detail_navigation_with_current_agent_target()
    {
        var agentId = Guid.Parse("f8a067e8-0d30-45be-96f4-3b696777f814");
        var service = new GlobalSearchService(new FakeHttpClientFactory(new Dictionary<string, object?>
        {
            ["/api/v1/global-search/tasks?q=req-42&pageSize=6"] = Page(new[]
            {
                new TaskDto(42, "req-42", string.Empty, 9, ClientEnvironment.None, TaskKinds.ExecShellCommand, "Pending", null, null, DateTimeOffset.UtcNow, null, null, null, "Operations host", "ops-host-01", "Operations host", agentId)
            })
        }), NullLogger<GlobalSearchService>.Instance);

        var tasks = (await service.SearchAsync("req-42")).Single(group => group.Key == "tasks");
        tasks.Results.Should().ContainSingle().Which.Href.Should().Be("/tasks/request/req-42");
    }

    [Fact]
    public async Task Incremental_search_returns_fast_groups_before_slow_groups()
    {
        var factory = new FakeHttpClientFactory(
            new Dictionary<string, object?>
            {
                ["/api/v1/global-search/tenants?q=Camelot&pageSize=6"] = Page(new[]
                {
                    new TenantDto(2, "Camelot-Estate", "Luxury estate", "Cape Town", [], null, null, false, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
                }),
                ["/api/v1/global-search/jobs?q=Camelot&pageSize=6"] = Page(new[]
                {
                    new JobDto(12, "Camelot backup", "/estate", "Nightly estate backup", 2, "client-1", 0, 0)
                })
            },
            new Dictionary<string, TimeSpan>
            {
                ["/api/v1/global-search/jobs?q=Camelot&pageSize=6"] = TimeSpan.FromSeconds(5)
            });
        var service = new GlobalSearchService(factory, NullLogger<GlobalSearchService>.Instance);

        GlobalSearchGroup? firstCompleted = null;
        await foreach (var group in service.SearchIncrementalAsync("Camelot"))
        {
            if (!group.IsLoading && group.Results.Count > 0)
            {
                firstCompleted = group;
                break;
            }
        }

        firstCompleted.Should().NotBeNull();
        firstCompleted!.Key.Should().Be("tenants");
        firstCompleted.Results.Should().Contain(x => x.Title == "Camelot-Estate");
    }

    [Fact]
    public async Task Incremental_search_times_out_slow_groups_with_continue_link()
    {
        var factory = new FakeHttpClientFactory(
            new Dictionary<string, object?>
            {
                ["/api/v1/global-search/tenants?q=Slow&pageSize=6"] = Page(Array.Empty<TenantDto>())
            },
            new Dictionary<string, TimeSpan>
            {
                ["/api/v1/global-search/tenants?q=Slow&pageSize=6"] = TimeSpan.FromSeconds(5)
            });
        var service = new GlobalSearchService(factory, NullLogger<GlobalSearchService>.Instance);

        GlobalSearchGroup? timedOut = null;
        await foreach (var group in service.SearchIncrementalAsync("Slow"))
        {
            if (group.Key == "tenants" && group.TimedOut)
            {
                timedOut = group;
                break;
            }
        }

        timedOut.Should().NotBeNull();
        timedOut!.IsLoading.Should().BeFalse();
        timedOut.ContinueHref.Should().Be("/tenants?search=Slow");
    }

    [Fact]
    public void Timeout_overlay_wording_describes_a_cancelled_source()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot, "src/NetRatel/NetRatel.Web/Components/Layout/GlobalSearchOverlay.razor"));

        source.Should().Contain("search timed out");
        source.Should().NotContain("Still searching in");
    }

    private static PagedResult<T> Page<T>(IReadOnlyList<T> items) => new(items, 1, 6, items.Count);

    private static readonly string RepoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../"));

    private sealed class FakeHttpClientFactory : IHttpClientFactory
    {
        private readonly IReadOnlyDictionary<string, object?> _responses;
        private readonly IReadOnlyDictionary<string, TimeSpan> _delays;
        public List<string> RequestedPaths { get; } = [];

        public FakeHttpClientFactory(
            IReadOnlyDictionary<string, object?> responses,
            IReadOnlyDictionary<string, TimeSpan>? delays = null)
        {
            _responses = responses;
            _delays = delays ?? new Dictionary<string, TimeSpan>();
        }

        public HttpClient CreateClient(string name) =>
            new(new FakeHandler(_responses, _delays, RequestedPaths))
            {
                BaseAddress = new Uri("https://netratel.test")
            };
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly IReadOnlyDictionary<string, object?> _responses;
        private readonly IReadOnlyDictionary<string, TimeSpan> _delays;
        private readonly List<string> _requestedPaths;

        public FakeHandler(
            IReadOnlyDictionary<string, object?> responses,
            IReadOnlyDictionary<string, TimeSpan> delays,
            List<string> requestedPaths)
        {
            _responses = responses;
            _delays = delays;
            _requestedPaths = requestedPaths;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var key = request.RequestUri!.PathAndQuery;
            _requestedPaths.Add(key);
            if (_delays.TryGetValue(key, out var delay))
            {
                await Task.Delay(delay, cancellationToken);
            }

            var response = _responses.TryGetValue(key, out var payload)
                ? payload
                : new PagedResult<object>(Array.Empty<object>(), 1, 6, 0);

            var json = JsonSerializer.Serialize(response);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            };
        }
    }
}
