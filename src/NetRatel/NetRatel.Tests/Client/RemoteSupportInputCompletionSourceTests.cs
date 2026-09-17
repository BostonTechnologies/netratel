using FluentAssertions;
using Xunit;

namespace NetRatel.Tests.Client;

public sealed class RemoteSupportInputCompletionSourceTests
{
    [Fact]
    public void InteractiveProviderCarriesAndValidatesExactTargetSession()
    {
        var contracts = ReadRepoFile("NetRatel.Client", "Service", "RemoteSupport", "RemoteSupportPipeContracts.cs");
        var manager = ReadRepoFile("NetRatel.Client", "Service", "RemoteSupport", "RemoteSupportInteractiveWebRtcManager.cs");

        contracts.Should().Contain("targetWindowsSessionId");
        manager.Should().Contain("processSessionId != _targetWindowsSessionId.Value");
        manager.Should().Contain("interactive_input_target_session_mismatch");
        manager.Should().Contain("RemoteSupportProviderMetadata.InteractiveUserHelper");
        manager.Should().NotContain("if (!_providerMetadata.IsConsoleProvider)\r\n        {\r\n            return;\r\n        }\r\n\r\n        var activeConsoleSessionId");
    }

    [Fact]
    public void InputAcknowledgementAndAccountingUseActualWindowsApiResults()
    {
        var manager = ReadRepoFile("NetRatel.Client", "Service", "RemoteSupport", "RemoteSupportInteractiveWebRtcManager.cs");
        var desktopContext = ReadRepoFile("NetRatel.Client", "Service", "RemoteSupport", "RemoteSupportInteractiveDesktopContext.cs");

        manager.Should().Contain("type = \"input_ack\"");
        manager.Should().Contain("remote_input_helper_received");
        manager.Should().Contain("remote_input_payload_parsed");
        manager.Should().Contain("remote_input_target_validated");
        manager.Should().Contain("remote_input_injection_attempted");
        manager.Should().Contain("remote_input_injection_succeeded");
        manager.Should().Contain("remote_input_injection_failed");
        manager.Should().Contain("remote_input_ack_sent");
        manager.Should().Contain("if (sendInputResult.Succeeded)");
        manager.Should().Contain("resultCount > 0");
        manager.Should().Contain("cursorSucceeded = SetCursorPos");
        manager.Should().Contain("return new SendInputAttemptResult(");
        manager.Should().Contain("context.Invalidate(result.Status)");
        manager.Should().Contain("RetryAttempted = true");
        manager.Should().Contain("send_input_uipi_possible");
        manager.Should().Contain("if (!result.Succeeded && !result.UipiPossible)");
        manager.Should().Contain("key_mapping_failed");
        desktopContext.Should().Contain("input_desktop_attach_failed");
        desktopContext.Should().Contain("_native.SetThreadDesktop(replacement");
        desktopContext.Should().Contain("_native.SetThreadDesktop(_originalDesktop");
    }

    [Fact]
    public void CorrelationLogsUseCategoriesAndNeverLogRawKeyValues()
    {
        var manager = ReadRepoFile("NetRatel.Client", "Service", "RemoteSupport", "RemoteSupportInteractiveWebRtcManager.cs");
        var logMethodStart = manager.IndexOf("private void LogRemoteInput", StringComparison.Ordinal);
        var mouseCategoryStart = manager.IndexOf("private static string CategorizeMouse", logMethodStart, StringComparison.Ordinal);
        var logMethod = manager[logMethodStart..mouseCategoryStart];

        logMethod.Should().Contain("inputEventId={inputEventId}");
        logMethod.Should().Contain("category={category}");
        logMethod.Should().NotContain("input.Key");
        logMethod.Should().NotContain("input.Code");
    }

    private static string ReadRepoFile(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "NetRatel.sln")))
            {
                return File.ReadAllText(Path.Combine(new[] { directory.FullName, "src", "NetRatel" }.Concat(parts).ToArray()));
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root.");
    }
}
