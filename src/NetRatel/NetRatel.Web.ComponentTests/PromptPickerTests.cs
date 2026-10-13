using Bunit;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.Shared.Contracts.Netclaw;
using NetRatel.Web.Components.Pages.AIOps;
using NetRatel.Web.Services;
using Xunit;

namespace NetRatel.Web.ComponentTests;

public sealed class PromptPickerTests : AsyncBunitContext
{
    private readonly FakePromptLibraryApi api = new();
    public PromptPickerTests() => Services.AddSingleton<IPromptLibraryApiService>(api);

    [Fact]
    public async Task Guidance_Previews_Historical_Revision_And_Requires_Deliberate_Update_Without_Writing()
    {
        api.Storage = api.Storage with { Revision = 2 };
        IReadOnlyList<AiPromptReference> selected = [new(api.Storage.Id, 1)];
        var cut = Render<PromptPicker>(p => p.Add(picker => picker.TenantId, 17).Add(picker => picker.Selected, selected)
            .Add(picker => picker.SelectedChanged, references => selected = references));
        await cut.WaitForAssertionAsync(() => Assert.Contains("Storage revision 1", cut.Find("[data-testid=prompt-guidance-preview]").TextContent));
        Assert.Contains("Always included: Tenant safety", cut.Find("[data-testid=prompt-default]").TextContent);
        Assert.Contains("Revision 2 is available", cut.Find("[data-testid=prompt-selected]").TextContent);
        Assert.DoesNotContain("<script>", cut.Markup, StringComparison.Ordinal);
        await cut.InvokeAsync(() => cut.Find("[data-testid=prompt-use-current]").ClickAsync(new()));
        Assert.Equal(new AiPromptReference(api.Storage.Id, 2), Assert.Single(selected));
        cut.Render(p => p.Add(picker => picker.Selected, selected));
        await cut.WaitForAssertionAsync(() => Assert.Contains("Storage revision 2", cut.Find("[data-testid=prompt-guidance-preview]").TextContent));
        await cut.InvokeAsync(() => cut.Find("[data-testid=prompt-search]").ChangeAsync(new() { Value = "Windows" }));
        Assert.Equal((17, "Windows"), api.Searches.Last());
        await cut.InvokeAsync(() => cut.Find("[data-testid=prompt-add]").ClickAsync(new()));
        cut.Render(p => p.Add(picker => picker.Selected, selected));
        await cut.WaitForAssertionAsync(() => Assert.Equal(2, cut.FindAll("[data-testid=prompt-selected]").Count));
        await cut.InvokeAsync(() => cut.FindAll("[data-testid=prompt-move-up]")[1].ClickAsync(new()));
        Assert.Equal(new[] { new AiPromptReference(api.Windows.Id, 1), new AiPromptReference(api.Storage.Id, 2) }, selected);
        Assert.Equal(0, api.WriteCalls);
    }

    [Fact]
    public async Task Tenant_Switch_Clears_Visible_Guidance_And_Late_Response_Cannot_Expose_Previous_Tenant()
    {
        var pending = new TaskCompletionSource<AiPromptListDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cut = Render<PromptPicker>(p => p.Add(picker => picker.TenantId, 17));
        await cut.WaitForAssertionAsync(() => Assert.Contains("Tenant safety", cut.Find("[data-testid=prompt-default]").TextContent));
        api.ListLookup = (tenant, _, _) => tenant == 17 ? pending.Task : Task.FromResult(new AiPromptListDto([], new(0, null), false));
        var search = cut.InvokeAsync(() => cut.Find("[data-testid=prompt-search]").ChangeAsync(new() { Value = "Storage" }));
        await cut.WaitForAssertionAsync(() => Assert.Equal((17, "Storage"), api.Searches.Last()));
        cut.Render(p => p.Add(picker => picker.TenantId, 23));
        await cut.WaitForAssertionAsync(() => Assert.Contains("No tenant default", cut.Find("[data-testid=prompt-default]").TextContent));
        pending.SetResult(api.Library());
        await search;
        Assert.DoesNotContain("Tenant safety", cut.Markup);
        Assert.Empty(cut.FindAll("[data-testid=prompt-add]"));
        Assert.Equal(23, cut.Instance.TenantId);
    }

    [Fact]
    public async Task Unavailable_Selected_Revision_Remains_Explicit_And_Is_Not_Silently_Substituted()
    {
        var selected = new AiPromptReference(Guid.NewGuid(), 4);
        var cut = Render<PromptPicker>(p => p.Add(picker => picker.TenantId, 17).Add(picker => picker.Selected, new[] { selected }));
        await cut.WaitForAssertionAsync(() => Assert.Contains("preserved", cut.Find("[data-testid=prompt-picker-error]").TextContent));
        Assert.Equal(selected, Assert.Single(cut.Instance.Selected));
        Assert.Contains("Unavailable selected prompt", cut.Find("[data-testid=prompt-selected]").TextContent);
        Assert.Equal(0, api.WriteCalls);
    }
}

internal sealed class FakePromptLibraryApi : IPromptLibraryApiService
{
    public AiPromptSummaryDto Storage { get; set; } = Summary("Storage checks");
    public AiPromptSummaryDto Windows { get; } = Summary("Windows vendor checks");
    public int WriteCalls;
    public List<(int Tenant, string? Search)> Searches { get; } = [];
    public Func<int, string?, CancellationToken, Task<AiPromptListDto>>? ListLookup;
    private static AiPromptSummaryDto Summary(string name) => new(Guid.NewGuid(), name, "Read-only investigation guidance", 1, false, false,
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "owner", "owner");
    public AiPromptListDto Library() => new([Storage, Windows], new(1, new(Guid.NewGuid(), 1, "Tenant safety", "Default session guidance", "Require approval before changes. <script>untrusted</script>", true)), false);
    public Task<IReadOnlyList<AiPromptTenantDto>> TenantsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<AiPromptTenantDto>>([new(17, "Operations", true)]);
    public Task<AiPromptListDto> ListAsync(int tenantId, string? search = null, bool includeArchived = false, CancellationToken ct = default)
    {
        Searches.Add((tenantId, search));
        if (ListLookup is not null) return ListLookup(tenantId, search, ct);
        var list = Library();
        return Task.FromResult(list with { Items = list.Items.Where(item => search is null || item.Name.Contains(search, StringComparison.OrdinalIgnoreCase)).ToArray() });
    }
    public Task<AiPromptDetailDto> GetAsync(int tenantId, Guid id, long? revision = null, CancellationToken ct = default)
    {
        var summary = id == Storage.Id ? Storage : id == Windows.Id ? Windows : throw new HttpRequestException("Unavailable prompt revision");
        var requested = revision ?? summary.Revision;
        return Task.FromResult(new AiPromptDetailDto(summary with { Revision = requested }, $"{(id == Storage.Id ? "Storage" : "Windows")} revision {requested}: inspect only."));
    }
    public Task<AiPromptDetailDto> CreateAsync(int tenantId, AiPromptSaveRequest request, CancellationToken ct = default) { WriteCalls++; throw new NotSupportedException(); }
    public Task<AiPromptDetailDto> UpdateAsync(int tenantId, Guid id, AiPromptSaveRequest request, CancellationToken ct = default) { WriteCalls++; throw new NotSupportedException(); }
    public Task<AiPromptDetailDto> ArchiveAsync(int tenantId, Guid id, AiPromptArchiveRequest request, CancellationToken ct = default) { WriteCalls++; throw new NotSupportedException(); }
    public Task<AiPromptDefaultDto> SetDefaultAsync(int tenantId, AiPromptSetDefaultRequest request, CancellationToken ct = default) { WriteCalls++; throw new NotSupportedException(); }
}
