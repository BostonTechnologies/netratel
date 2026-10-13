using BlazorMonaco.Editor;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Components;
using MudBlazor;
using MudBlazor.Services;
using NetRatel.Shared.Contracts.Netclaw;
using NetRatel.Web.Components.Pages.AIOps;
using NetRatel.Web.Components.Shared;
using NetRatel.Web.Services;
using Xunit;

namespace NetRatel.Web.ComponentTests;

public sealed class PromptLibraryTests : AsyncBunitContext
{
    private readonly LibraryApi api = new();
    public PromptLibraryTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices(options => options.PopoverOptions.CheckForPopoverProvider = false);
        Services.AddSingleton<IPromptLibraryApiService>(api);
    }

    [Fact]
    public async Task Monaco_roundtrip_preserves_literal_guidance_and_prompt_cancel_confirms_dirty_changes()
    {
        var cut = Render<Prompts>(); await cut.WaitForAssertionAsync(() => Assert.Contains("Default: Platform baseline", cut.Markup));
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
        await cut.InvokeAsync(() => cut.FindComponent<MudSelect<int>>().Instance.ValueChanged.InvokeAsync(18));
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

    [Fact]
    public async Task New_is_blank_and_selection_and_navigation_preserve_the_draft_until_discard_is_confirmed()
    {
        var cut = Render<Prompts>(); await cut.WaitForAssertionAsync(() => Assert.NotEmpty(cut.FindAll("[data-testid=prompt-item]")));
        await cut.Find("[data-testid=prompt-item]").ClickAsync(new());
        Assert.Equal("true", cut.Find("[data-testid=prompt-item]").GetAttribute("aria-pressed"));
        await cut.Find("[data-testid=prompt-new]").ClickAsync(new());
        Assert.Equal("", cut.Find("[data-testid=prompt-name]").GetAttribute("value"));
        var editor = cut.FindComponent<MonacoTextEditor>();
        Assert.Equal("", editor.Instance.Value); Assert.False(editor.Instance.AutoFocus);
        Assert.NotNull(cut.Find(".prompt-detail .prompt-footer [data-testid=prompt-save]"));
        cut.Find("[data-testid=prompt-name]").Input("Keep this draft");
        await cut.Find("[data-testid=prompt-item]").ClickAsync(new());
        Assert.Contains("Discard unsaved prompt changes", cut.Markup);
        Assert.Equal("Keep this draft", cut.Find("[data-testid=prompt-name]").GetAttribute("value"));
        await Button(cut, "Keep editing").ClickAsync(new());

        var navigation = Services.GetRequiredService<NavigationManager>();
        await cut.InvokeAsync(() => navigation.NavigateTo("/tasks/new"));
        Assert.DoesNotContain("/tasks/new", navigation.Uri);
        Assert.Contains("Discard unsaved prompt changes", cut.Markup);
        Assert.Equal("Keep this draft", cut.Find("[data-testid=prompt-name]").GetAttribute("value"));
        await Button(cut, "Keep editing").ClickAsync(new());
        await cut.Find("[data-testid=prompt-item]").ClickAsync(new());
        await Button(cut, "Discard changes").ClickAsync(new());
        Assert.Empty(cut.FindAll("[data-testid=prompt-name]"));
        Assert.Equal("true", cut.Find("[data-testid=prompt-item]").GetAttribute("aria-pressed"));
        Assert.Empty(api.Created); Assert.Empty(api.Updated);
    }

    [Fact]
    public async Task Failed_edit_save_retains_input_and_revision_and_tenant_switch_requires_confirmation()
    {
        api.FailSave = true;
        JSInterop.Setup<string>("blazorMonaco.editor.getValue", _ => true).SetResult("Original tenant body");
        var cut = Render<Prompts>(); await cut.WaitForAssertionAsync(() => Assert.NotEmpty(cut.FindAll("[data-testid=prompt-item]")));
        await cut.Find("[data-testid=prompt-item]").ClickAsync(new());
        await Button(cut, "Edit").ClickAsync(new());
        cut.Find("[data-testid=prompt-name]").Input("Edited guidance");
        await cut.Find("[data-testid=prompt-save]").ClickAsync(new());
        Assert.Contains("Revision conflict", cut.Markup);
        Assert.Equal("Edited guidance", cut.Find("[data-testid=prompt-name]").GetAttribute("value"));
        Assert.Equal("Original tenant body", cut.FindComponent<MonacoTextEditor>().Instance.Value);
        Assert.Equal(1, Assert.Single(api.Updated).ExpectedRevision);
        await cut.InvokeAsync(() => cut.FindComponent<MudSelect<int>>().Instance.ValueChanged.InvokeAsync(18));
        Assert.Contains("and switch tenant", cut.Markup);
        Assert.Equal("Edited guidance", cut.Find("[data-testid=prompt-name]").GetAttribute("value"));
        await Button(cut, "Keep editing").ClickAsync(new());
        api.FailSave = false;
        await cut.Find("[data-testid=prompt-save]").ClickAsync(new());
        Assert.Equal(2, api.Updated.Count);
        Assert.All(api.Updated, request => Assert.Equal(1, request.ExpectedRevision));
        Assert.Empty(cut.FindAll("[data-testid=prompt-save]"));
    }

    [Fact]
    public async Task Unavailable_editor_content_retains_the_draft_and_never_submits_an_empty_replacement()
    {
        var cut = Render<Prompts>(); await cut.WaitForAssertionAsync(() => Assert.NotEmpty(cut.FindAll("[data-testid=prompt-item]")));
        await cut.Find("[data-testid=prompt-item]").ClickAsync(new());
        await Button(cut, "Edit").ClickAsync(new());
        cut.Find("[data-testid=prompt-name]").Input("Retained draft");
        var monaco = cut.FindComponent<StandaloneCodeEditor>().Instance;
        await cut.InvokeAsync(() => monaco.OnDidInit.InvokeAsync());
        JSInterop.Setup<string>("blazorMonaco.editor.getValue", _ => true).SetResult(null!);
        await cut.InvokeAsync(() => monaco.OnDidChangeModelContent.InvokeAsync(new ModelContentChangedEvent()));
        await cut.Find("[data-testid=prompt-save]").ClickAsync(new());
        Assert.Contains("Editor content is unavailable", cut.Markup);
        Assert.Equal("Retained draft", cut.Find("[data-testid=prompt-name]").GetAttribute("value"));
        Assert.Equal("Original tenant body", cut.FindComponent<MonacoTextEditor>().Instance.Value);
        Assert.Contains("Unsaved changes", cut.Markup);
        Assert.Empty(api.Created); Assert.Empty(api.Updated);
    }

    [Fact]
    public async Task Default_and_archive_actions_require_explicit_default_removal_and_keep_revision_preconditions()
    {
        var cut = Render<Prompts>(); await cut.WaitForAssertionAsync(() => Assert.NotEmpty(cut.FindAll("[data-testid=prompt-item]")));
        await cut.Find("[data-testid=prompt-item]").ClickAsync(new());
        Assert.Contains("Unresolved operator", cut.Find(".prompt-audit summary").TextContent);
        Assert.DoesNotContain("author", cut.Find(".prompt-audit summary").TextContent);
        Assert.Equal("author", cut.Find(".prompt-audit code").TextContent);
        await Button(cut, "Set as default").ClickAsync(new());
        Assert.Contains("Default: Original tenant guidance", cut.Markup);
        var setDefault = Assert.Single(api.DefaultChanges);
        Assert.Equal(api.Detail.Summary.Id, setDefault.PromptId);
        Assert.Equal(0, setDefault.ExpectedRevision); Assert.Equal(1, setDefault.ExpectedPromptRevision);
        await Button(cut, "Archive").ClickAsync(new());
        Assert.True(Button(cut, "Confirm archive").HasAttribute("disabled"));
        var removal = cut.FindComponents<MudCheckBox<bool>>().Single(x => x.Instance.Label!.StartsWith("Remove default and archive", StringComparison.Ordinal));
        await cut.InvokeAsync(() => removal.Instance.ValueChanged.InvokeAsync(true));
        await Button(cut, "Confirm archive").ClickAsync(new());
        var archived = Assert.Single(api.Archived);
        Assert.True(archived.RemoveDefault); Assert.Equal(1, archived.ExpectedRevision); Assert.Equal(1, archived.ExpectedDefaultRevision);
        Assert.Contains("Default: Platform baseline", cut.Markup);
        Assert.Empty(cut.FindAll("[data-testid=prompt-item]"));
        Assert.Empty(cut.FindAll("button").Where(x => x.TextContent.Trim() == "Edit"));
        Assert.NotNull(Button(cut, "Copy"));
    }

    [Fact]
    public async Task Refresh_keeps_the_editable_Monaco_model_and_accepts_draft_changes_while_the_list_loads()
    {
        var cut = Render<Prompts>(); await cut.WaitForAssertionAsync(() => Assert.NotEmpty(cut.FindAll("[data-testid=prompt-item]")));
        await cut.Find("[data-testid=prompt-item]").ClickAsync(new());
        await Button(cut, "Edit").ClickAsync(new());
        var monaco = cut.FindComponent<StandaloneCodeEditor>().Instance;
        await cut.InvokeAsync(() => monaco.OnDidInit.InvokeAsync());
        api.DelayedList = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var refresh = cut.Find("button[aria-label='Search / refresh']").ClickAsync(new());
        try
        {
            await cut.WaitForAssertionAsync(() => Assert.True(api.ListLoading));
            Assert.Same(monaco, cut.FindComponent<StandaloneCodeEditor>().Instance);
            Assert.False(cut.FindComponent<MonacoTextEditor>().Instance.ReadOnly);
            cut.Find("[data-testid=prompt-name]").Input("Draft during refresh");
            JSInterop.Setup<string>("blazorMonaco.editor.getValue", _ => true).SetResult("Guidance typed during refresh");
            await cut.InvokeAsync(() => monaco.OnDidChangeModelContent.InvokeAsync(new ModelContentChangedEvent()));
        }
        finally
        {
            api.DelayedList.TrySetResult(new([api.Detail.Summary], new(0, null), false));
            await refresh;
        }
        Assert.Same(monaco, cut.FindComponent<StandaloneCodeEditor>().Instance);
        Assert.Equal("Draft during refresh", cut.Find("[data-testid=prompt-name]").GetAttribute("value"));
        Assert.Equal("Guidance typed during refresh", cut.FindComponent<MonacoTextEditor>().Instance.Value);
        Assert.Contains("Unsaved changes", cut.Markup); Assert.Empty(api.Updated);
    }

    private static AngleSharp.Dom.IElement Button(IRenderedComponent<Prompts> cut, string label) =>
        cut.FindAll("button").Single(x => x.TextContent.Trim() == label);

    private sealed class LibraryApi : IPromptLibraryApiService
    {
        private readonly Guid id = Guid.NewGuid();
        public bool CanManage = true, ReadStarted, FailSave, ListLoading;
        private bool archived;
        private AiPromptDefaultDto defaultState = new(0, null);
        public List<AiPromptSetDefaultRequest> DefaultChanges { get; } = [];
        public List<AiPromptArchiveRequest> Archived { get; } = [];
        public TaskCompletionSource<AiPromptDetailDto>? DelayedRead;
        public TaskCompletionSource<AiPromptListDto>? DelayedList;
        public List<AiPromptSaveRequest> Created { get; } = [];
        public List<AiPromptSaveRequest> Updated { get; } = [];
        private AiPromptSummaryDto Summary(string name) => new(id, name, null, 1, archived, defaultState.Prompt is not null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "author", "author");
        public AiPromptDetailDto Detail => new(Summary("Original tenant guidance"), "Original tenant body");
        public Task<IReadOnlyList<AiPromptTenantDto>> TenantsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<AiPromptTenantDto>>([new(17, "Operations", CanManage), new(18, "Other tenant", CanManage)]);
        public Task<AiPromptListDto> ListAsync(int tenantId, string? search = null, bool includeArchived = false, CancellationToken ct = default)
        {
            ListLoading = DelayedList is not null;
            return DelayedList?.Task ?? Task.FromResult(new AiPromptListDto(archived && !includeArchived ? [] : [Summary(tenantId == 17 ? "Original tenant guidance" : "Other tenant guidance")], defaultState, false));
        }
        public Task<AiPromptDetailDto> GetAsync(int tenantId, Guid promptId, long? revision = null, CancellationToken ct = default)
        { ReadStarted = true; return DelayedRead?.Task ?? Task.FromResult(Detail); }
        public Task<AiPromptDetailDto> CreateAsync(int tenantId, AiPromptSaveRequest request, CancellationToken ct = default)
        { Created.Add(request); return Task.FromResult(new AiPromptDetailDto(Summary(request.Name), request.Body)); }
        public Task<AiPromptDetailDto> UpdateAsync(int tenantId, Guid promptId, AiPromptSaveRequest request, CancellationToken ct = default)
        { Updated.Add(request); if (FailSave) throw new HttpRequestException("Revision conflict"); return Task.FromResult(new AiPromptDetailDto(Summary(request.Name) with { Revision = 2 }, request.Body)); }
        public Task<AiPromptDetailDto> ArchiveAsync(int tenantId, Guid promptId, AiPromptArchiveRequest request, CancellationToken ct = default)
        {
            Archived.Add(request); archived = true;
            if (request.RemoveDefault) defaultState = new(defaultState.Revision + 1, null);
            return Task.FromResult(Detail with { Summary = Detail.Summary with { Revision = 2 } });
        }
        public Task<AiPromptDefaultDto> SetDefaultAsync(int tenantId, AiPromptSetDefaultRequest request, CancellationToken ct = default)
        {
            DefaultChanges.Add(request);
            defaultState = new(defaultState.Revision + 1, request.PromptId is null ? null : new(id, 1, "Original tenant guidance", null, "Original tenant body", true));
            return Task.FromResult(defaultState);
        }
    }
}
