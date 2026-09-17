using FluentAssertions;
using Xunit;

namespace NetRatel.Tests.Client;

public sealed class RemoteSupportFirstFrameGateSourceTests
{
    [Fact]
    public void Browser_reports_first_frame_for_the_current_peer()
    {
        var source = ReadRepoFile("NetRatel.Web", "wwwroot", "js", "remote-support-dialog.js");

        source.Should().Contain("sessions.get(session.sessionId) !== session");
        source.Should().Contain("OnFirstVideoFrame");
        source.Should().Contain("firstFrameReported");
        source.Should().Contain("HTMLMediaElement.HAVE_CURRENT_DATA");
        source.Should().Contain("function resetTransition");
        source.Should().Contain("session.pendingMouseMove = null");
    }

    private static string ReadRepoFile(params string[] segments)
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../"));
        return File.ReadAllText(Path.Combine([root, "src", "NetRatel", .. segments]));
    }
}
