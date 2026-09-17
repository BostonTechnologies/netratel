using FluentAssertions;
using Xunit;

namespace NetRatel.Web.ComponentTests;

public sealed class TerminalResizeAssetTests
{
    [Fact]
    public void TerminalConsole_ObservesAndDisposesContainerResize()
    {
        var path = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../NetRatel.Web/Components/Pages/Terminal/TerminalConsole.razor"));
        var component = File.ReadAllText(path);

        component.Should().Contain("@implements IAsyncDisposable");
        component.Should().Contain("RegisterResizeObserverAsync");
        component.Should().Contain("[JSInvokable]");
        component.Should().Contain("FitTerminalSurfaceAsync(\"container-resize\")");
        component.Should().Contain("InvokeVoidAsync(\"unobserveResize\"");
    }

    [Fact]
    public void ResizeObserver_AwaitsCallbackAndDisconnects()
    {
        var path = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../NetRatel.Web/wwwroot/js/resizeObserver.js"));
        var script = File.ReadAllText(path);

        script.Should().Contain("new ResizeObserver");
        script.Should().Contain("requestAnimationFrame(async () =>");
        script.Should().Contain("await dotNetObjectReference.invokeMethodAsync(callbackName)");
        script.Should().Contain("callbackName = 'OnTerminalResize'");
        script.Should().Contain("existing.observer.disconnect()");
    }

    [Fact]
    public void ScriptLibrary_UsesANamedResizeCallbackAndDisposesEditorResources()
    {
        var path = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../NetRatel.Web/Components/ScriptLibrary/ScriptLibraryView.razor"));
        var component = File.ReadAllText(path);

        component.Should().Contain("@implements IAsyncDisposable");
        component.Should().Contain("nameof(OnEditorHostResize)");
        component.Should().Contain("await _editor.Layout()");
        component.Should().Contain("await _resizeObserverModule.InvokeVoidAsync(\"unobserveResize\"");
        component.Should().Contain("await _editor.DisposeEditor()");
        component.Should().Contain("data-testid=\"script-library-workspace\"");
        component.Should().Contain("data-testid=\"script-library-tree\"");
        component.Should().Contain("data-testid=\"script-library-editor-frame\"");
        component.Should().Contain("data-testid=\"script-library-editor\"");
    }

    [Fact]
    public void TerminalDialog_MeasuresBeforeOpeningAndUsesMudFullscreen()
    {
        var path = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../NetRatel.Web/Components/Dialogs/ClientTerminalSessionDialog.razor"));
        var component = File.ReadAllText(path);

        component.Should().Contain("SessionId=\"@(_session?.SessionId ?? string.Empty)\"");
        component.Should().Contain("await OpenAndStartAsync();");
        component.Should().Contain("MudDialog.SetOptionsAsync");
        component.Should().Contain("Icons.Material.Filled.FullscreenExit");
        component.Should().NotContain("public int? Cols { get; set; } = 120");
        component.Should().NotContain("public int? Rows { get; set; } = 32");
    }

    [Fact]
    public void TerminalRecoveryAssets_KeepLifecycleIndependentFromOutputAndDisposeBeforeClosing()
    {
        var consolePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../NetRatel.Web/Components/Pages/Terminal/TerminalConsole.razor"));
        var dialogPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "../../../../NetRatel.Web/Components/Dialogs/ClientTerminalSessionDialog.razor"));

        var console = File.ReadAllText(consolePath);
        var dialog = File.ReadAllText(dialogPath);

        console.Should().Contain("_outputQueue.Writer.TryWrite(chunk)");
        console.Should().Contain("RecordDroppedOutput");
        console.Should().Contain("ReconnectInputChannelAsync");
        console.Should().NotContain("await _outputQueue.Writer.WriteAsync");

        dialog.Should().Contain("@implements IAsyncDisposable");
        dialog.Should().Contain("CloseAndExitAsync");
        dialog.Should().Contain("await CloseResourcesAndSessionAsync(reason);");
        dialog.Should().Contain("PeriodicTimer(LifecyclePollInterval)");
        dialog.Should().Contain("GatewayTerminalHandleStorageKey");
        dialog.Should().Contain("sessionStorage.getItem");
        dialog.Should().Contain("TryRestoreGatewaySessionAsync");
        dialog.Should().Contain("GetGatewaySessionAsync");
        dialog.Should().Contain("DisposeResourcesWithoutClosingSessionAsync");
        dialog.Should().NotContain("var deadline = DateTimeOffset.UtcNow.AddSeconds(10);");
    }
}
