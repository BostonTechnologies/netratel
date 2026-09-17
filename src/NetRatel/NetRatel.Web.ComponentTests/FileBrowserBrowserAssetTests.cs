using FluentAssertions;
using Xunit;

namespace NetRatel.Web.ComponentTests;

public sealed class FileBrowserBrowserAssetTests
{
    [Fact]
    public void App_LoadsTheFingerprintedNativeDownloadBridgeBeforeBlazor()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../NetRatel.Web/Components/App.razor"));
        var component = File.ReadAllText(path);

        component.Should().Contain("Assets[\"download.js\"]");
        component.IndexOf("id=\"netratel-downloads-script\"", StringComparison.Ordinal)
            .Should().BeLessThan(component.IndexOf("src=\"_framework/blazor.web.js\"", StringComparison.Ordinal));
    }

    [Fact]
    public void NativeDownloadBridge_PreservesAuthoritativeCancellationStatusUntilItIsReported()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../NetRatel.Web/wwwroot/download.js"));
        var script = File.ReadAllText(path);

        script.Should().Contain("poll: async (id)");
        script.Should().Contain("await window.netratelDownloads.poll(id);");
        script.Should().Contain("'cancelled'");
        script.Should().Contain("'cancel_failed'");
    }

    [Fact]
    public void NativeUploadBridge_RetriesOnlyTheMarkedTransientGatewayFailure()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../NetRatel.Web/wwwroot/download.js"));
        var script = File.ReadAllText(path);

        script.Should().Contain("const maximumAttempts = 5;");
        script.Should().Contain("X-NetRatel-File-Transfer-Retryable");
        script.Should().Contain("OnUploadRetrying");
        script.Should().Contain("transfer.cancelled = true;");
    }

    [Fact]
    public void NativeUpload_DoesNotUseTheDefaultBlazorInteropTimeout()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../NetRatel.Web/Components/Dialogs/ClientFileSystemViewer.razor"));
        var component = File.ReadAllText(path);

        component.Should().Contain("\"netratelFileTransfers.upload\",");
        component.Should().Contain("Timeout.InfiniteTimeSpan,");
    }
}
