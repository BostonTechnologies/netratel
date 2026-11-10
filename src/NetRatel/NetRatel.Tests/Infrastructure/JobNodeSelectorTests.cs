using System.Security.Claims;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using NetRatel.API.Services.Jobs;
using NetRatel.Application.Jobs;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using NetRatel.Shared;
using NetRatel.Shared.Contracts.Jobs;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

public sealed class JobNodeSelectorTests
{
    private static readonly Guid[] Ids = Enumerable.Range(1, 5).Select(value => new Guid(value, 0, 0, new byte[8])).ToArray();
    private static readonly JobNodeInventoryDto[] Inventory = Enumerable.Range(1, 5).Select(value =>
        new JobNodeInventoryDto(Ids[value - 1], $"lab-node-{value:00}", $"Friendly {value}", $"registered-{value}",
            value == 5 ? ["TestSystem", "Windows", "Excluded"] : ["TestSystem", "Linux", value % 2 == 0 ? "Staging" : "Odd"],
            value == 5 ? "Windows" : "Linux", "x64", null, value < 5, true, null)).ToArray();

    [Theory]
    [InlineData("lab-node-01 lab-node-02", 2)]
    [InlineData("hostname: lab-node-01,lab-node-02", 2)]
    [InlineData("tags: TestSystem", 5)]
    [InlineData("tags: TestSystem+Linux", 4)]
    [InlineData("tags: Staging,Windows", 3)]
    [InlineData("os: Linux tags: TestSystem !tags: Staging", 2)]
    [InlineData("hostname: /^lab-node-0[1-3]$/", 3)]
    [InlineData("tags: Staging tags: Windows", 3)]
    [InlineData("hostname: /node-01$/,/node-03$/", 2)]
    [InlineData("all: true !tags: Excluded", 4)]
    [InlineData("displayname: \"Friendly 1\"", 1)]
    public void One_grammar_defines_exact_property_tag_regex_exclusion_and_repeat_semantics(string filter, int count)
    {
        var parsed = JobNodeFilterParser.Parse(filter);
        JobNodeFilterEvaluator.Resolve(parsed, Inventory).Should().HaveCount(count);
        var formatted = JobNodeFilterParser.Format(parsed);
        JobNodeFilterEvaluator.Resolve(JobNodeFilterParser.Parse(formatted), Inventory).Select(x => x.AgentId)
            .Should().Equal(JobNodeFilterEvaluator.Resolve(parsed, Inventory).Select(x => x.AgentId));
    }

    [Fact]
    public void Quoted_punctuation_is_literal_and_parameter_values_cannot_create_selector_clauses()
    {
        const string tag = "blue + green: /test/ \"quoted\"";
        var special = Inventory[0] with { Tags = [tag, ".*,!tags: Excluded"] };
        var expression = JobNodeFilterParser.Parse("tags: \"blue + green: /test/ \\\"quoted\\\"\"");
        JobNodeFilterEvaluator.Resolve(expression, [special, Inventory[1]]).Should().ContainSingle(x => x.AgentId == special.AgentId);
        var template = JobNodeFilterParser.Parse("tags: @option.tag@");
        var bound = JobNodeFilterParser.Bind(template, new Dictionary<string, string[]> { ["tag"] = [".*,!tags: Excluded"] });
        bound.Clauses[0].Alternatives[0].Operands[0].Kind.Should().Be(JobNodeFilterOperandKind.Literal);
        JobNodeFilterEvaluator.Resolve(bound, [special, Inventory[1]]).Should().ContainSingle(x => x.AgentId == special.AgentId);
        JobNodeFilterEvaluator.Resolve(JobNodeFilterParser.Bind(template, new Dictionary<string, string[]> { ["TAG"] = ["Staging", "Windows"] }), Inventory)
            .Should().HaveCount(3);
    }

    [Theory]
    [InlineData("", "target_filter_required")]
    [InlineData(" ", "target_filter_required")]
    [InlineData("unknown: value", "target_filter_unknown_property")]
    [InlineData("tags:", "target_filter_operand_required")]
    [InlineData("!tags: Excluded", "target_filter_inclusion_required")]
    [InlineData("hostname: /(?=lab)/", "target_regex_unsupported")]
    [InlineData("hostname: /[/", "target_regex_unsupported")]
    [InlineData("hostname: /unfinished", "target_filter_unterminated")]
    [InlineData("all: false", "target_filter_invalid_all")]
    [InlineData("hostname: a+b", "target_filter_invalid_operator")]
    public void Invalid_selector_is_an_explicit_error_and_never_all_nodes(string filter, string code)
    {
        var error = Assert.Throws<JobTargetValidationException>(() => JobNodeFilterParser.Parse(filter));
        error.Code.Should().Be(code);
    }

    [Fact]
    public void No_matches_missing_properties_and_ambiguous_names_fail_without_partial_results()
    {
        Assert.Throws<JobTargetValidationException>(() => JobNodeFilterEvaluator.Resolve(JobNodeFilterParser.Parse("os: macOS"), Inventory)).Code.Should().Be("target_no_matches");
        Assert.Throws<JobTargetValidationException>(() => JobNodeFilterEvaluator.Resolve(JobNodeFilterParser.Parse("name: missing"), [Inventory[0] with { Name = null }])).Code.Should().Be("target_no_matches");
        Assert.Throws<JobTargetValidationException>(() => JobNodeFilterEvaluator.Resolve(JobNodeFilterParser.Parse("lab-node-01"), [Inventory[0], Inventory[1] with { Hostname = "lab-node-01" }])).Code.Should().Be("target_name_ambiguous");
        Assert.Throws<JobTargetValidationException>(() => JobNodeFilterEvaluator.Resolve(JobNodeFilterParser.Parse("all: true"), Enumerable.Repeat(Inventory[0], 10001).ToArray())).Code.Should().Be("target_inventory_capacity_exceeded");
    }

    [Fact]
    public void Binding_budget_counts_expanded_values_and_trailing_operands_and_cancellation_is_observed()
    {
        var template = JobNodeFilterParser.Parse("tags: @option.tags@ os: Linux");
        var inputs = new Dictionary<string, string[]> { ["tags"] = Enumerable.Range(1, 128).Select(x => x.ToString()).ToArray() };
        Assert.Throws<JobTargetValidationException>(() => JobNodeFilterParser.Bind(template, inputs)).Code.Should().Be("target_filter_too_complex");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => JobNodeFilterEvaluator.Resolve(JobNodeFilterParser.Parse("tags: TestSystem"), Inventory, cancellation.Token));
    }

    [Fact]
    public async Task Complete_resolution_precedes_paging_and_retains_offline_nodes_and_typed_literal_targets()
    {
        await using var db = Db();
        var directory = new Directory(Inventory);
        var resolver = new JobTargetResolver(db, directory);
        var api = new JobTargetApiService(directory, resolver, new Access());
        var page = await api.PreviewAsync(7, new("tags: TestSystem", Page: 2, PageSize: 2), new(), CancellationToken.None);
        page.TotalCount.Should().Be(5); page.Items.Select(x => x.AgentId).Should().Equal(Ids[2], Ids[3]); page.NextPage.Should().Be(3);
        var resolved = await resolver.ResolveAsync(7, new() { Source = JobTargetSource.NodeFilter, Filter = "tags: TestSystem" }, ClientEnvironment.Dev, null, null, null, CancellationToken.None);
        resolved.Nodes.Should().HaveCount(5); resolved.Nodes.Last().IsOnline.Should().BeFalse();
        resolved.Nodes.Should().OnlyContain(x => x.Environment == ClientEnvironment.Dev);
        var runtime = new JobTargetDefinition { Source = JobTargetSource.RuntimeInput, RuntimeInput = new(JobRuntimeTargetKind.Tags, "tags") };
        (await resolver.ResolveAsync(7, runtime, null, null, new(JobRuntimeTargetKind.Tags, ["Staging", "Windows"]), null, CancellationToken.None)).Nodes.Should().HaveCount(3);
        (await Assert.ThrowsAsync<JobTargetValidationException>(() => resolver.ResolveAsync(7,
            new() { Source = JobTargetSource.ExplicitNodes, AgentIds = [Ids[0]] }, null, null,
            new(JobRuntimeTargetKind.ClientId, [Ids[1].ToString()]), null, CancellationToken.None))).Code.Should().Be("target_override_conflict");
    }

    [Fact]
    public async Task Explicit_and_group_targets_fail_whole_set_when_denied_invalid_or_foreign_while_dynamic_discovery_is_scoped()
    {
        await using var db = Db(); var resolver = new JobTargetResolver(db, new Directory(Inventory));
        var authorized = Ids.Take(4).ToHashSet();
        var dynamic = await resolver.ResolveAsync(7, new() { Source = JobTargetSource.NodeFilter, Filter = "tags: TestSystem" }, null, null, null, authorized, CancellationToken.None);
        dynamic.Nodes.Should().HaveCount(4);
        (await Assert.ThrowsAsync<JobTargetValidationException>(() => resolver.ResolveAsync(7,
            new() { Source = JobTargetSource.ExplicitNodes, AgentIds = Ids }, null, null, null, authorized, CancellationToken.None))).Code.Should().Be("target_authority_required");
        var groupId = Guid.NewGuid();
        db.ExecutionGroups.Add(new() { Id = groupId, TenantId = 7, Name = "five", NormalizedName = "FIVE", Members = Ids.Select(id => new ExecutionGroupMemberRecord { TenantId = 7, AgentId = id }).ToList() });
        await db.SaveChangesAsync();
        (await Assert.ThrowsAsync<JobTargetValidationException>(() => resolver.ResolveAsync(7,
            new() { Source = JobTargetSource.ExecutionGroup, ExecutionGroupId = groupId }, null, null, null, authorized, CancellationToken.None))).Code.Should().Be("target_authority_required");
        (await Assert.ThrowsAsync<JobTargetValidationException>(() => resolver.ResolveAsync(8,
            new() { Source = JobTargetSource.ExecutionGroup, ExecutionGroupId = groupId }, null, null, null, null, CancellationToken.None))).Code.Should().Be("target_group_unavailable");
        var invalidResolver = new JobTargetResolver(db, new Directory([Inventory[0] with { IsEnabled = false, UnavailableReason = "agent_revoked" }]));
        (await Assert.ThrowsAsync<JobTargetValidationException>(() => invalidResolver.ResolveAsync(7,
            new() { Source = JobTargetSource.ExplicitNodes, AgentIds = [Ids[0]] }, null, null, null, null, CancellationToken.None))).Code.Should().Be("target_node_unavailable");
    }

    private static OrchestratorDbContext Db() => new(new DbContextOptionsBuilder<OrchestratorDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private sealed class Directory(IReadOnlyList<JobNodeInventoryDto> nodes) : IJobNodeDirectory
    {
        public Task<IReadOnlyList<JobNodeInventoryDto>> ReadAsync(int tenantId, IReadOnlySet<Guid>? ids, bool includeInvalid, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<JobNodeInventoryDto>>(nodes.Where(x => (ids is null || ids.Contains(x.AgentId)) && (includeInvalid || x.IsEnabled)).ToArray());
    }
    private sealed class Access : IEffectiveAccessService
    {
        public Task<bool> AuthorizeAsync(ClaimsPrincipal principal, string permission, int? tenantId, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task<EffectiveAccessSnapshot> GetSnapshotAsync(ClaimsPrincipal principal, int? tenantId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ReconcileBuiltInRolesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
