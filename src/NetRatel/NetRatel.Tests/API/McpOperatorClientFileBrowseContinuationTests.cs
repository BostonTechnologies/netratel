using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.API.Gateway;
using Xunit;

namespace NetRatel.Tests.API;

public sealed partial class McpOperatorClientFileEndpointTests
{
    [Theory]
    [InlineData("/var/log", "/")]
    [InlineData(@"C:\netratel", @"\")]
    public async Task BrowseContinuation_ReturnsEveryEligibleEntryOnceAcrossPagesAndCanReplayAPage(string path, string separator)
    {
        var agentId = Guid.NewGuid();
        using var app = await BuildAppAsync(agentId, includeReadRoot: true);
        app.Services.GetRequiredService<TestAdmission>().ReadRootsOverride = [path];
        var registry = app.Services.GetRequiredService<TestFileRegistry>();
        registry.ListedEntries = [
            new("z-folder", path + separator + "z-folder", true, 0),
            .. new[] { "z.txt", "a.txt", "A.txt", "b.txt", "d.txt", "c.txt" }
                .Select(name => new GatewayFileEntry(name, path + separator + name, false, 1)),
            new("excluded", "/outside/excluded", false, 1)
        ];
        var client = AuthorizedClient(app);
        var route = $"/api/v2/mcp/operator/agents/7/{agentId:D}/files/browse?path={Uri.EscapeDataString(path)}&pageSize=2";
        var names = new List<string>();
        string? cursor = null;
        string? firstCursor = null;
        string[]? secondPage = null;
        var pageCount = 0;
        do
        {
            var response = await client.GetAsync(route + (cursor is null ? string.Empty : "&cursor=" + Uri.EscapeDataString(cursor)));
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var data = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
            var page = data["entries"]!.AsArray().Select(entry => entry!["name"]!.GetValue<string>()).ToArray();
            page.Length.Should().BeInRange(1, 2);
            if (++pageCount == 2) secondPage = page;
            names.AddRange(page);
            cursor = data["nextCursor"]?.GetValue<string>();
            if (pageCount == 1) firstCursor = cursor;
            data["truncated"]!.GetValue<bool>().Should().Be(cursor is not null);
            pageCount.Should().BeLessThanOrEqualTo(4);
        } while (cursor is not null);

        names.Should().Equal("z-folder", "A.txt", "a.txt", "b.txt", "c.txt", "d.txt", "z.txt");
        registry.ListPolicies.Should().HaveCount(4);
        var replay = await client.GetFromJsonAsync<JsonObject>(route + "&cursor=" + Uri.EscapeDataString(firstCursor!));
        replay!["entries"]!.AsArray().Select(entry => entry!["name"]!.GetValue<string>()).Should().Equal(secondPage!);
        app.Services.GetRequiredService<TestAdmission>().Accepted.Should().HaveCount(5);
    }

    [Theory]
    [InlineData("subject")]
    [InlineData("client")]
    [InlineData("path")]
    [InlineData("pageSize")]
    [InlineData("tampered")]
    public async Task BrowseContinuation_RejectsChangedBindingBeforeDispatch(string change)
    {
        var agentId = Guid.NewGuid();
        var changed = false;
        using var app = await BuildAppAsync(agentId, includeReadRoot: true,
            configureDelegation: delegation => !changed ? delegation : change switch
            {
                "subject" => delegation with { Identity = delegation.Identity with { Subject = "other-operator" } },
                "client" => delegation with { Identity = delegation.Identity with { ClientId = "other-client" } },
                _ => delegation
            });
        var registry = app.Services.GetRequiredService<TestFileRegistry>();
        registry.ListedEntries = BrowseEntries();
        var client = AuthorizedClient(app);
        var root = $"/api/v2/mcp/operator/agents/7/{agentId:D}/files/browse";
        var data = (await client.GetFromJsonAsync<JsonObject>(root + "?path=%2Fvar%2Flog&pageSize=1"))!;
        var cursor = data["nextCursor"]!.GetValue<string>();
        changed = true;
        if (change == "tampered") cursor = (cursor[0] == 'x' ? "y" : "x") + cursor[1..];
        var path = change == "path" ? "/var/log/subfolder" : "/var/log";
        var pageSize = change == "pageSize" ? 2 : 1;
        var response = await client.GetAsync($"{root}?path={Uri.EscapeDataString(path)}&pageSize={pageSize}&cursor={Uri.EscapeDataString(cursor)}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("invalid_file_cursor");
        registry.ListPolicies.Should().ContainSingle();
    }

    [Theory]
    [InlineData("added-entry")]
    [InlineData("metadata")]
    [InlineData("metadata-created-null")]
    [InlineData("metadata-modified-null")]
    [InlineData("policy-version")]
    [InlineData("read-roots")]
    [InlineData("expired")]
    [InlineData("expired-during-dispatch")]
    public async Task BrowseContinuation_RejectsAChangedListingPolicyOrExpiredChain(string change)
    {
        var agentId = Guid.NewGuid();
        var clock = new BrowseTestClock();
        using var app = await BuildAppAsync(agentId, includeReadRoot: true, timeProvider: clock);
        var registry = app.Services.GetRequiredService<TestFileRegistry>();
        registry.ListedEntries = BrowseEntries();
        var client = AuthorizedClient(app);
        var route = $"/api/v2/mcp/operator/agents/7/{agentId:D}/files/browse?path=%2Fvar%2Flog&pageSize=1";
        var data = (await client.GetFromJsonAsync<JsonObject>(route))!;
        var cursor = data["nextCursor"]!.GetValue<string>();
        switch (change)
        {
            case "added-entry": registry.ListedEntries = [.. registry.ListedEntries, new("new.log", "/var/log/new.log", false, 0)]; break;
            case "metadata": registry.ListedEntries = [registry.ListedEntries[0] with { SizeBytes = 100 }, registry.ListedEntries[1]]; break;
            case "metadata-created-null": registry.ListedEntries = [registry.ListedEntries[0] with { CreatedUtc = DateTimeOffset.MinValue }, registry.ListedEntries[1]]; break;
            case "metadata-modified-null": registry.ListedEntries = [registry.ListedEntries[0] with { LastModifiedUtc = DateTimeOffset.MinValue }, registry.ListedEntries[1]]; break;
            case "policy-version": app.Services.GetRequiredService<TestAdmission>().PolicyVersion++; break;
            case "read-roots": app.Services.GetRequiredService<TestAdmission>().ReadRootsOverride = ["/var/log", "/tmp"]; break;
            case "expired": clock.Now = clock.Now.AddMinutes(6); break;
            case "expired-during-dispatch": registry.OnList = () => clock.Now = clock.Now.AddMinutes(6); break;
        }

        var response = await client.GetAsync(route + "&cursor=" + Uri.EscapeDataString(cursor));
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync()).Should().Contain(change.StartsWith("expired", StringComparison.Ordinal) ? "file_cursor_expired" : "file_cursor_stale");
        registry.ListPolicies.Should().HaveCount(change.StartsWith("metadata", StringComparison.Ordinal) || change is "added-entry" or "expired-during-dispatch" ? 2 : 1);
    }

    [Fact]
    public async Task BrowseContinuation_RechecksRevocationAndPolicyChangesBeforeReturningEntries()
    {
        var agentId = Guid.NewGuid();
        using var app = await BuildAppAsync(agentId, includeReadRoot: true);
        var registry = app.Services.GetRequiredService<TestFileRegistry>();
        var admission = app.Services.GetRequiredService<TestAdmission>();
        registry.ListedEntries = BrowseEntries();
        var client = AuthorizedClient(app);
        var route = $"/api/v2/mcp/operator/agents/7/{agentId:D}/files/browse?path=%2Fvar%2Flog&pageSize=1";
        var data = (await client.GetFromJsonAsync<JsonObject>(route))!;
        var continuation = route + "&cursor=" + Uri.EscapeDataString(data["nextCursor"]!.GetValue<string>());

        admission.FailureCodeOverride = "target_policy_missing";
        (await client.GetAsync(continuation)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        registry.ListPolicies.Should().ContainSingle();

        admission.FailureCodeOverride = null;
        registry.OnList = () => admission.PolicyVersion++;
        var response = await client.GetAsync(continuation);
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await response.Content.ReadAsStringAsync()).Should().Contain("file_cursor_stale").And.NotContain("a.log");
    }

    [Fact]
    public async Task BrowseContinuation_KeepsTheNativeListingBound()
    {
        var agentId = Guid.NewGuid();
        using var app = await BuildAppAsync(agentId, includeReadRoot: true);
        app.Services.GetRequiredService<TestFileRegistry>().ListedEntries = Enumerable.Range(0, 4097)
            .Select(i => new GatewayFileEntry($"{i}.log", $"/var/log/{i}.log", false, 0)).ToArray();
        var response = await AuthorizedClient(app).GetAsync($"/api/v2/mcp/operator/agents/7/{agentId:D}/files/browse?path=%2Fvar%2Flog");
        response.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
        (await response.Content.ReadAsStringAsync()).Should().Contain("file_listing_too_large");
    }

    private static GatewayFileEntry[] BrowseEntries() => [new("a.log", "/var/log/a.log", false, 1), new("b.log", "/var/log/b.log", false, 2)];

    private sealed class BrowseTestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
