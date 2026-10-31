using System.Net;
using System.Net.Http.Json;
using BlazorMonaco.Editor;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using NetRatel.Shared.Contracts.Execution;
using NetRatel.Shared.Contracts.Scripts;
using NetRatel.Web.Components.ScriptLibrary;
using NetRatel.Web.Services.ScriptLibrary;
using Xunit;

namespace NetRatel.Web.ComponentTests;

public sealed class ScriptLibraryWorkspaceTests : AsyncBunitContext
{
    private readonly ScriptTransport transport = new();
    public ScriptLibraryWorkspaceTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        JSInterop.SetupModule("./js/resizeObserver.js");
        Services.AddLogging();
        Services.AddMudServices(options => options.PopoverOptions.CheckForPopoverProvider = false);
        Services.AddSingleton(new ScriptLibraryClient(transport));
    }

    [Fact]
    public async Task Selected_folder_defaults_and_successful_creation_expands_selects_and_focuses_without_execution()
    {
        var provider = Render<MudDialogProvider>();
        var cut = Render<ScriptLibraryView>(p => p.Add(view => view.AllowAdding, true).Add(view => view.AllowEditing, true));
        var tree = cut.FindComponent<MudTreeView<ScriptTreeNode>>();
        var folder = tree.Instance.Items!.Single(item => item.Value!.FullPath == "/operations/").Value;
        await cut.InvokeAsync(() => tree.Instance.SelectedValueChanged.InvokeAsync(folder));
        var opening = cut.Find("[data-testid=script-library-new]").ClickAsync(new());
        try
        {
            provider.WaitForElement("[data-testid=new-script-name]");
            var model = provider.FindComponent<NewScriptDialog>().Instance.Model;
            Assert.Equal("/operations/", model.Folder);
            Assert.Contains("/", model.ExistingFolders);
            provider.Find("[data-testid=new-script-name]").Input("Inventory.ps1");
            await provider.Find("[data-testid=new-script-create]").ClickAsync(new());
            await opening.WaitAsync(TimeSpan.FromSeconds(5));
            await cut.WaitForAssertionAsync(() => Assert.NotEmpty(cut.FindAll("[data-testid=script-library-editor]")));
            var editor = cut.FindComponent<StandaloneCodeEditor>().Instance;
            await cut.InvokeAsync(() => editor.OnDidInit.InvokeAsync());
            Assert.Equal("", editor.ConstructionOptions(editor).Value);
            Assert.Equal("powershell", editor.ConstructionOptions(editor).Language);
            var selection = cut.FindComponent<MudTreeView<ScriptTreeNode>>().Instance;
            Assert.Equal("Inventory.ps1", selection.SelectedValue!.Name);
            Assert.True(selection.Items!.Single(item => item.Value!.FullPath == "/operations/").Expanded);
            Assert.Contains(JSInterop.Invocations, invocation => invocation.Identifier == "blazorMonaco.editor.focus");
            Assert.Equal("/operations/", Assert.Single(transport.Created).FolderPath);
            Assert.All(transport.Paths, path => Assert.StartsWith("/api/v1/script-library", path));
        }
        finally
        {
            foreach (var dialog in provider.FindComponents<NewScriptDialog>())
                await provider.InvokeAsync(() => dialog.Instance.MudDialog.Cancel());
            await opening.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task Embedded_read_only_picker_keeps_type_scope_and_cannot_mutate_scripts()
    {
        var cut = Render<ScriptLibraryView>(p => p.Add(view => view.ReadOnly, true).Add(view => view.ShowViewer, false)
            .Add(view => view.AllowAdding, true).Add(view => view.AllowEditing, true).Add(view => view.FilterByType, "PowerShell"));
        Assert.Empty(cut.FindAll("[data-testid=script-library-new]"));
        Assert.Empty(cut.FindAll("[data-testid=script-library-editor]"));
        Assert.Empty(cut.FindAll("[aria-label='Rename Existing.ps1']"));
        Assert.Equal("PowerShell", cut.FindComponent<MudSelect<string>>().Instance.Value);
        var tree = cut.FindComponent<MudTreeView<ScriptTreeNode>>();
        await cut.InvokeAsync(() => tree.Instance.SelectedValueChanged.InvokeAsync(tree.Instance.Items!.Single().Children!.Single().Value));
        Assert.Empty(transport.Created);
        Assert.Empty(transport.Updated);
    }

    [Fact]
    public async Task Failed_create_retains_name_type_and_nested_folder_until_retry_succeeds()
    {
        transport.FailCreate = true;
        var provider = Render<MudDialogProvider>();
        var model = new NewScriptDialogModel { Name = "settings.json", Type = ScriptType.Json, Folder = "/", UseNewFolder = true, NewFolder = "operations\\new" };
        var dialog = await Services.GetRequiredService<IDialogService>().ShowAsync<NewScriptDialog>("New Script", new DialogParameters
        {
            ["Model"] = model,
            ["CreateScript"] = new Func<CreateScriptRequest, Task<ulong>>(request => Services.GetRequiredService<ScriptLibraryClient>().CreateAsync(request, CancellationToken.None))
        });
        provider.WaitForElement("[data-testid=new-script-create]");
        await provider.Find("[data-testid=new-script-create]").ClickAsync(new());
        await provider.WaitForAssertionAsync(() => Assert.Contains("already occupies", provider.Find("[data-testid=new-script-error]").TextContent));
        Assert.False(dialog.Result!.IsCompleted);
        Assert.Equal("settings.json", model.Name);
        Assert.Equal(ScriptType.Json, model.Type);
        Assert.Equal("operations\\new", model.NewFolder);
        Assert.Contains("/operations/new/settings.json", provider.Find("[data-testid=new-script-placement]").TextContent);
        transport.FailCreate = false;
        await provider.Find("[data-testid=new-script-create]").ClickAsync(new());
        var result = await dialog.Result;
        Assert.False(result!.Canceled);
        Assert.Equal(73UL, result.Data);
        Assert.Equal("Json", transport.Created.Last().ScriptType);
        Assert.Equal("/operations/new/", transport.Created.Last().FolderPath);
    }

    [Fact]
    public async Task Late_refresh_snapshot_cannot_clear_the_just_created_script_and_editor()
    {
        var provider = Render<MudDialogProvider>();
        var cut = Render<ScriptLibraryView>(p => p.Add(view => view.AllowAdding, true).Add(view => view.AllowEditing, true));
        transport.PendingList = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var refresh = cut.Find("[aria-label='Refresh script library']").ClickAsync(new());
        Task? opening = null;
        try
        {
            await cut.WaitForAssertionAsync(() => Assert.True(transport.DelayedListStarted));
            opening = cut.Find("[data-testid=script-library-new]").ClickAsync(new());
            provider.WaitForElement("[data-testid=new-script-name]");
            provider.Find("[data-testid=new-script-name]").Input("New draft.ps1");
            await provider.Find("[data-testid=new-script-create]").ClickAsync(new());
            await opening.WaitAsync(TimeSpan.FromSeconds(5));
            await cut.WaitForAssertionAsync(() => Assert.NotEmpty(cut.FindAll("[data-testid=script-library-editor]")));
            transport.PendingList.SetResult(transport.ListResponse());
            await refresh.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("New draft.ps1", cut.FindComponent<MudTreeView<ScriptTreeNode>>().Instance.SelectedValue!.Name);
            Assert.NotEmpty(cut.FindAll("[data-testid=script-library-editor]"));
        }
        finally
        {
            transport.PendingList.TrySetResult(transport.ListResponse());
            foreach (var dialog in provider.FindComponents<NewScriptDialog>())
                await provider.InvokeAsync(() => dialog.Instance.MudDialog.Cancel());
            if (opening is not null) await opening.WaitAsync(TimeSpan.FromSeconds(5));
            await refresh.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task Filtering_and_refresh_keep_dirty_editor_and_source_revision()
    {
        var cut = Render<ScriptLibraryView>(p => p.Add(view => view.AllowEditing, true));
        var tree = cut.FindComponent<MudTreeView<ScriptTreeNode>>();
        var script = tree.Instance.Items!.Single(item => item.Value!.FullPath == "/operations/").Children!.Single().Value;
        await cut.InvokeAsync(() => tree.Instance.SelectedValueChanged.InvokeAsync(script));
        var editor = cut.FindComponent<StandaloneCodeEditor>();
        var editorValue = JSInterop.Setup<string>("blazorMonaco.editor.getValue", _ => true);
        editorValue.SetResult("unsaved draft");
        await cut.InvokeAsync(() => editor.Instance.OnDidInit.InvokeAsync());
        await cut.InvokeAsync(() => editor.Instance.OnDidChangeModelContent.InvokeAsync(new ModelContentChangedEvent()));
        await cut.WaitForAssertionAsync(() => Assert.Contains("unsaved", cut.Markup));
        await cut.InvokeAsync(() => cut.FindComponent<MudSelect<string>>().Instance.ValueChanged.InvokeAsync("Json"));
        await cut.Find("[aria-label='Refresh script library']").ClickAsync(new());
        Assert.Same(editor.Instance, cut.FindComponent<StandaloneCodeEditor>().Instance);
        Assert.Contains("unsaved", cut.Markup);
        Assert.Empty(transport.Created);
        Assert.Empty(transport.Updated);
        transport.PendingUpdate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var save = cut.FindAll("button").Single(button => button.TextContent.Trim() == "Save").ClickAsync(new());
        try
        {
            await cut.WaitForAssertionAsync(() => Assert.Single(transport.Updated));
            editorValue.SetResult("unsaved draft with later edit");
            await cut.InvokeAsync(() => editor.Instance.OnDidChangeModelContent.InvokeAsync(new ModelContentChangedEvent()));
            transport.PendingUpdate.SetResult(new(HttpStatusCode.OK) { Content = JsonContent.Create(new { id = 7UL, sourceRevision = 10 }) });
            await save.WaitAsync(TimeSpan.FromSeconds(5));
            var update = Assert.Single(transport.Updated);
            Assert.Equal("unsaved draft", update.Content);
            Assert.Equal(9, update.ExpectedSourceRevision);
            Assert.Contains("unsaved", cut.Markup);
            transport.PendingUpdate = null;
            await cut.FindAll("button").Single(button => button.TextContent.Trim() == "Save").ClickAsync(new());
            Assert.Equal("unsaved draft with later edit", transport.Updated.Last().Content);
            Assert.Equal(10, transport.Updated.Last().ExpectedSourceRevision);
        }
        finally
        {
            transport.PendingUpdate?.TrySetResult(new(HttpStatusCode.OK) { Content = JsonContent.Create(new { id = 7UL, sourceRevision = 10 }) });
            await save.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public void Folder_counts_include_nested_scripts_and_follow_current_filters()
    {
        var now = DateTimeOffset.UtcNow;
        var scripts = new[]
        {
            new ScriptDto(1, "one.ps1", "/operations/", "", "", "PowerShell", now, now),
            new ScriptDto(2, "two.ps1", "/operations/windows/nested/", "", "", "PowerShell", now, now),
            new ScriptDto(3, "settings.json", "/operations/windows/", "", "{}", "Json", now, now)
        };
        var root = Assert.Single(ScriptTreeBuilder.Build(scripts, null, null));
        Assert.Equal(3, root.ScriptCount);
        Assert.Equal(2, root.Children.Single(node => node.IsFolder).ScriptCount);
        Assert.Equal(2, Assert.Single(ScriptTreeBuilder.Build(scripts, "PowerShell", null)).ScriptCount);
        Assert.Equal(1, Assert.Single(ScriptTreeBuilder.Build(scripts, null, "two.ps1")).ScriptCount);
    }

    [Fact]
    public async Task Inline_name_is_saved_with_content_and_rename_failure_retains_the_draft()
    {
        var cut = Render<ScriptLibraryView>(p => p.Add(view => view.AllowEditing, true));
        await SelectExistingAsync(cut);
        Assert.Empty(cut.FindAll(".script-library-row-actions"));
        Assert.Equal("button", cut.Find("[aria-label='Select script: Existing.ps1']").TagName.ToLowerInvariant());
        Assert.True(cut.Find("[data-testid=script-editor-save]").HasAttribute("disabled"));
        cut.Find("[data-testid=script-editor-name]").Input("  Renamed.ps1  ");
        Assert.False(cut.Find("[data-testid=script-editor-save]").HasAttribute("disabled"));
        transport.FailUpdate = true;
        await cut.Find("[data-testid=script-editor-save]").ClickAsync(new());
        Assert.Contains("already occupies", cut.Find("[data-testid=script-editor-error]").TextContent);
        Assert.Equal("  Renamed.ps1  ", cut.Find("[data-testid=script-editor-name]").GetAttribute("value"));
        Assert.Contains("unsaved", cut.Markup);
        transport.FailUpdate = false;
        await cut.Find("[data-testid=script-editor-save]").ClickAsync(new());
        var update = transport.Updated.Last();
        Assert.Equal("Renamed.ps1", update.Name);
        Assert.Equal("saved content", update.Content);
        Assert.Equal(9, update.ExpectedSourceRevision);
        Assert.Equal("Renamed.ps1", cut.FindComponent<MudTreeView<ScriptTreeNode>>().Instance.SelectedValue!.Name);
        Assert.True(cut.Find("[data-testid=script-editor-save]").HasAttribute("disabled"));
    }

    [Fact]
    public async Task Full_screen_and_inline_editors_share_name_content_and_saved_revision()
    {
        var provider = Render<MudDialogProvider>();
        var cut = Render<ScriptLibraryView>(p => p.Add(view => view.AllowEditing, true));
        await SelectExistingAsync(cut);
        var value = JSInterop.Setup<string>("blazorMonaco.editor.getValue", _ => true);
        value.SetResult("inline draft");
        var inline = cut.FindComponent<StandaloneCodeEditor>().Instance;
        await cut.InvokeAsync(() => inline.OnDidInit.InvokeAsync());
        await cut.InvokeAsync(() => inline.OnDidChangeModelContent.InvokeAsync(new ModelContentChangedEvent()));
        cut.Find("[data-testid=script-editor-name]").Input("Inline.ps1");
        var opening = cut.Find("[aria-label='Edit script full screen']").ClickAsync(new());
        try
        {
            provider.WaitForElement("[aria-label='Exit full screen']");
            Assert.Empty(cut.FindAll("[data-testid=script-library-editor]"));
            var fullscreen = provider.FindComponent<StandaloneCodeEditor>().Instance;
            Assert.Equal("inline draft", fullscreen.ConstructionOptions(fullscreen).Value);
            Assert.Equal("Inline.ps1", provider.Find("[data-testid=script-editor-name]").GetAttribute("value"));
            await provider.InvokeAsync(() => fullscreen.OnDidInit.InvokeAsync());
            value.SetResult("full-screen saved content");
            await provider.InvokeAsync(() => fullscreen.OnDidChangeModelContent.InvokeAsync(new ModelContentChangedEvent()));
            provider.Find("[data-testid=script-editor-name]").Input("Full-screen.ps1");
            await provider.Find("[data-testid=script-editor-save]").ClickAsync(new());
            var saved = Assert.Single(transport.Updated);
            Assert.Equal("Full-screen.ps1", saved.Name);
            Assert.Equal("full-screen saved content", saved.Content);
            Assert.Equal(9, saved.ExpectedSourceRevision);
            provider.Find("[data-testid=script-editor-name]").Input("Later name.ps1");
            value.SetResult("later unsaved content");
            await provider.Find("[aria-label='Exit full screen']").ClickAsync(new());
            await opening.WaitAsync(TimeSpan.FromSeconds(5));
            var restored = cut.FindComponent<StandaloneCodeEditor>().Instance;
            Assert.Equal("later unsaved content", restored.ConstructionOptions(restored).Value);
            Assert.Equal("Later name.ps1", cut.Find("[data-testid=script-editor-name]").GetAttribute("value"));
            Assert.Contains("unsaved", cut.Markup);
            await cut.Find("[data-testid=script-editor-save]").ClickAsync(new());
            Assert.Equal(10, transport.Updated.Last().ExpectedSourceRevision);
            Assert.Equal("later unsaved content", transport.Updated.Last().Content);
            Assert.Equal("Later name.ps1", transport.Updated.Last().Name);
        }
        finally
        {
            foreach (var dialog in provider.FindComponents<ScriptEditorDialog>())
                await provider.InvokeAsync(() => dialog.Instance.MudDialog.Close());
            await opening.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task Full_screen_delete_requires_confirmation_and_uses_selected_revision()
    {
        var provider = Render<MudDialogProvider>();
        var cut = Render<ScriptLibraryView>(p => p.Add(view => view.AllowEditing, true));
        await SelectExistingAsync(cut);
        var opening = cut.Find("[aria-label='Edit script full screen']").ClickAsync(new());
        Task? deleting = null;
        try
        {
            provider.WaitForElement("[aria-label='Delete selected script']");
            deleting = provider.Find("[aria-label='Delete selected script']").ClickAsync(new());
            await provider.WaitForAssertionAsync(() => Assert.Equal("Delete 'Existing.ps1'?", provider.FindComponent<MudMessageBox>().Instance.Message));
            Assert.Empty(transport.Deleted);
            await provider.FindAll("button").Single(button => button.TextContent.Trim() == "Yes").ClickAsync(new());
            await deleting.WaitAsync(TimeSpan.FromSeconds(5));
            await opening.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("/api/v1/script-library/7?expectedSourceRevision=9", Assert.Single(transport.Deleted));
            Assert.Empty(cut.FindAll("[data-testid=script-library-editor]"));
            Assert.Empty(provider.FindComponents<ScriptEditorDialog>());
        }
        finally
        {
            foreach (var box in provider.FindComponents<MudMessageBox>())
                await box.FindAll("button").Single(button => button.TextContent.Trim() == "Cancel").ClickAsync(new());
            foreach (var dialog in provider.FindComponents<ScriptEditorDialog>())
                await provider.InvokeAsync(() => dialog.Instance.MudDialog.Close());
            if (deleting is not null) await deleting.WaitAsync(TimeSpan.FromSeconds(5));
            await opening.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static async Task SelectExistingAsync(IRenderedComponent<ScriptLibraryView> cut)
    {
        var tree = cut.FindComponent<MudTreeView<ScriptTreeNode>>();
        var script = tree.Instance.Items!.Single(item => item.Value!.FullPath == "/operations/").Children!.Single().Value;
        await cut.InvokeAsync(() => tree.Instance.SelectedValueChanged.InvokeAsync(script));
    }

    [Theory]
    [InlineData("/", null, false, "/")]
    [InlineData("/operations/", null, false, "/operations/")]
    [InlineData("/", "operations\\nested//", true, "/operations/nested/")]
    public void Root_existing_and_new_nested_placements_use_the_same_normalization(string folder, string? newFolder, bool useNew, string expected)
    {
        var model = new NewScriptDialogModel { Name = " sample ", Folder = folder, NewFolder = newFolder, UseNewFolder = useNew, Type = ScriptType.Sql };
        var request = model.ToRequest();
        Assert.Equal(expected, request.FolderPath);
        Assert.Equal("sample", request.Name);
        Assert.Equal("Sql", request.ScriptType);
    }

    [Fact]
    public void Catalogue_preserves_existing_identifiers_with_friendly_labels_and_editor_languages()
    {
        Assert.Equal(Enum.GetValues<ScriptType>(), ScriptLibraryTypes.Options.Select(option => option.Value));
        Assert.Equal("SQL", ScriptLibraryTypes.Find("Sql")!.Label);
        Assert.Equal("JSON", ScriptLibraryTypes.Find("Json")!.Label);
        Assert.Equal("shell", ScriptLibraryTypes.Find("Bash")!.Language);
        Assert.Equal(".ts", ScriptLibraryTypes.Find("TypeScript")!.Extension);
        Assert.Throws<ArgumentException>(() => new NewScriptDialogModel { Name = "sample", Folder = "/" }.ToRequest());
    }

    private sealed class ScriptTransport : HttpMessageHandler, IHttpClientFactory
    {
        public List<CreateScriptRequest> Created { get; } = [];
        public List<UpdateScriptRequest> Updated { get; } = [];
        public List<string> Paths { get; } = [];
        public bool FailCreate { get; set; }
        public bool FailUpdate { get; set; }
        public List<string> Deleted { get; } = [];
        public TaskCompletionSource<HttpResponseMessage>? PendingList { get; set; }
        public TaskCompletionSource<HttpResponseMessage>? PendingUpdate { get; set; }
        public bool DelayedListStarted { get; private set; }
        public HttpResponseMessage ListResponse() => new(HttpStatusCode.OK) { Content = JsonContent.Create(new[] { new ScriptDto(7, "Existing.ps1", "/operations/", "", "saved content", "PowerShell", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 9) }) };
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false) { BaseAddress = new Uri("https://api.example.test") };
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);
            if (request.Method == HttpMethod.Get)
            {
                if (PendingList is null) return ListResponse();
                DelayedListStarted = true;
                return await PendingList.Task;
            }
            if (request.Method == HttpMethod.Post)
            {
                Created.Add((await request.Content!.ReadFromJsonAsync<CreateScriptRequest>(cancellationToken))!);
                return FailCreate
                    ? new(HttpStatusCode.Conflict) { Content = JsonContent.Create(new { remediation = "A script already occupies this library path." }) }
                    : new(HttpStatusCode.OK) { Content = JsonContent.Create(new { id = 73UL }) };
            }
            if (request.Method == HttpMethod.Put)
            {
                Updated.Add((await request.Content!.ReadFromJsonAsync<UpdateScriptRequest>(cancellationToken))!);
                if (FailUpdate) return new(HttpStatusCode.Conflict) { Content = JsonContent.Create(new { remediation = "A script already occupies this library path." }) };
                return PendingUpdate is null ? new(HttpStatusCode.OK) { Content = JsonContent.Create(new { id = 7UL, sourceRevision = 9 + Updated.Count }) } : await PendingUpdate.Task;
            }
            if (request.Method == HttpMethod.Delete)
            {
                Deleted.Add(request.RequestUri!.PathAndQuery);
                return new(HttpStatusCode.NoContent);
            }
            throw new InvalidOperationException($"Unexpected script request {request.Method}.");
        }
    }
}
