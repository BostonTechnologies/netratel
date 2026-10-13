using BlazorMonaco.Editor;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Components;
using NetRatel.Shared.Contracts.Netclaw;
using NetRatel.Web.Components.Pages.AIOps;
using NetRatel.Web.Components.Shared;
using NetRatel.Web.Services;
using Xunit;

namespace NetRatel.Web.ComponentTests;

public sealed class PromptLibraryTests : AsyncBunitContext
{
    private readonly LibraryApi api = new();
    public PromptLibraryTests() { JSInterop.Mode = JSRuntimeMode.Loose; Services.AddSingleton<IPromptLibraryApiService>(api); }

    [Fact]
    public async Task Monaco_roundtrip_preserves_literal_guidance_and_prompt_cancel_confirms_dirty_changes()
    {
        var cut = Render<Prompts>(); await cut.WaitForAssertionAsync(() => Assert.Contains("No tenant-specific default", cut.Markup));
        await cut.Find("[data-testid=prompt-new]").ClickAsync(new());
        cut.Find("[data-testid=prompt-name]").Input("Offline investigation");
        const string guidance = "# Offline client\nRead evidence <script>literal text</script>\n";
        JSInterop.Setup<string>("blazorMonaco.editor.getValue", _ => true).SetResult(guidance);
        var surface = cut.FindComponent<MonacoTextEditor>(); var monaco = surface.FindComponent<StandaloneCodeEditor>();
        await cut.InvokeAsync(() => monaco.Instance.OnDidInit.InvokeAsync());
        await cut.InvokeAsync(() => monaco.Instance.OnDidChangeModelContent.InvokeAsync(new ModelContentChangedEvent()));
        await cut.WaitForAssertionAsync(() => Assert.Contains("Unsaved changes", cut.Markup));
        Assert.Equal("markdown", monaco.Instance.ConstructionOptions(monaco.Instance).Language);
        Assert.Equal(guidance, await surface.Instance.GetValueAsync());
        Assert.Empty(cut.FindAll("script"));
        await cut.Find("[data-testid=prompt-cancel]").ClickAsync(new());
        Assert.Contains("Discard unsaved prompt changes", cut.Markup); Assert.Empty(api.Created);
        await cut.FindAll("button").Single(x => x.TextContent == "Keep editing").ClickAsync(new());
        await cut.Find("[data-testid=prompt-save]").ClickAsync(new());
        await cut.WaitForAssertionAsync(() => Assert.Single(api.Created));
        Assert.Equal(guidance, api.Created[0].Body); Assert.Equal("Offline investigation", api.Created[0].Name);
        await cut.WaitForAssertionAsync(() => Assert.DoesNotContain("Unsaved changes", cut.Markup));
        var viewer = cut.FindComponent<StandaloneCodeEditor>().Instance;
        Assert.True(viewer.ConstructionOptions(viewer).ReadOnly); Assert.Equal(guidance, viewer.ConstructionOptions(viewer).Value);
    }

    [Fact]
    public async Task Read_only_access_preserves_viewing_and_an_inflight_old_tenant_read_cannot_replace_the_new_tenant()
    {
        api.CanManage = false; api.DelayedRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var cut = Render<Prompts>(); await cut.WaitForAssertionAsync(() => Assert.NotEmpty(cut.FindAll("[data-testid=prompt-item]")));
        Assert.True(cut.Find("[data-testid=prompt-new]").HasAttribute("disabled"));
        var opening = cut.Find("[data-testid=prompt-item]").ClickAsync(new());
        await cut.WaitForAssertionAsync(() => Assert.True(api.ReadStarted));
        await cut.Find("[data-testid=prompt-tenant]").ChangeAsync(new() { Value = "18" });
        api.DelayedRead.SetResult(api.Detail);
        await opening;
        await cut.WaitForAssertionAsync(() => Assert.Contains("Other tenant guidance", cut.Markup));
        Assert.DoesNotContain("Original tenant body", cut.Markup);
        Assert.Empty(cut.FindAll("[data-testid=prompt-save]")); Assert.Empty(api.Created);
        Services.GetRequiredService<NavigationManager>().NavigateTo("/ai/prompts?tenantId=17");
        await cut.WaitForAssertionAsync(() => Assert.Contains("Original tenant guidance", cut.Markup));
        Assert.DoesNotContain("Other tenant guidance", cut.Markup);
    }

    [Fact]
    public async Task Cancel_discard_leaves_the_server_prompt_unchanged()
    {
        var cut = Render<Prompts>(); await cut.WaitForAssertionAsync(() => Assert.NotEmpty(cut.FindAll("[data-testid=prompt-item]")));
        await cut.Find("[data-testid=prompt-item]").ClickAsync(new());
        await cut.FindAll("button").Single(x => x.TextContent == "Edit").ClickAsync(new());
        cut.Find("[data-testid=prompt-name]").Input("Unsaved replacement");
        await cut.Find("[data-testid=prompt-cancel]").ClickAsync(new());
        await cut.FindAll("button").Single(x => x.TextContent == "Discard changes").ClickAsync(new());
        Assert.Empty(api.Created); Assert.Empty(api.Updated); Assert.DoesNotContain("Unsaved replacement", cut.Markup);
        Assert.Contains("Original tenant guidance", cut.Markup);
    }

    private sealed class LibraryApi : IPromptLibraryApiService
    {
        private readonly Guid id = Guid.NewGuid();
        public bool CanManage = true, ReadStarted;
        public TaskCompletionSource<AiPromptDetailDto>? DelayedRead;
        public List<AiPromptSaveRequest> Created { get; } = [];
        public List<AiPromptSaveRequest> Updated { get; } = [];
        private AiPromptSummaryDto Summary(string name) => new(id, name, null, 1, false, false, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "author", "author");
        public AiPromptDetailDto Detail => new(Summary("Original tenant guidance"), "Original tenant body");
        public Task<IReadOnlyList<AiPromptTenantDto>> TenantsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<AiPromptTenantDto>>([new(17, "Operations", CanManage), new(18, "Other tenant", CanManage)]);
        public Task<AiPromptListDto> ListAsync(int tenantId, string? search = null, bool includeArchived = false, CancellationToken ct = default) =>
            Task.FromResult(new AiPromptListDto([Summary(tenantId == 17 ? "Original tenant guidance" : "Other tenant guidance")], new(0, null), false));
        public Task<AiPromptDetailDto> GetAsync(int tenantId, Guid promptId, long? revision = null, CancellationToken ct = default)
        { ReadStarted = true; return DelayedRead?.Task ?? Task.FromResult(Detail); }
        public Task<AiPromptDetailDto> CreateAsync(int tenantId, AiPromptSaveRequest request, CancellationToken ct = default)
        { Created.Add(request); return Task.FromResult(new AiPromptDetailDto(Summary(request.Name), request.Body)); }
        public Task<AiPromptDetailDto> UpdateAsync(int tenantId, Guid promptId, AiPromptSaveRequest request, CancellationToken ct = default)
        { Updated.Add(request); return Task.FromResult(new AiPromptDetailDto(Summary(request.Name) with { Revision = 2 }, request.Body)); }
        public Task<AiPromptDetailDto> ArchiveAsync(int tenantId, Guid promptId, AiPromptArchiveRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AiPromptDefaultDto> SetDefaultAsync(int tenantId, AiPromptSetDefaultRequest request, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
