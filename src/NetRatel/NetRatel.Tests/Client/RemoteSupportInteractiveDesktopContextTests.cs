using FluentAssertions;
using NetRatel.Client.Service.RemoteSupport;
using Xunit;

namespace NetRatel.Tests.Client;

public sealed class RemoteSupportInteractiveDesktopContextTests
{
    [Fact]
    public void CoordinatorInvalidationRebindsAndClosesOnlyTheDetachedDesktop()
    {
        var native = new FakeDesktopNative();
        native.ProbeResults.Enqueue((true, 0));
        native.ProbeResults.Enqueue((true, 0));
        var coordinator = new RemoteSupportDesktopContextCoordinator();
        using var context = new RemoteSupportInteractiveDesktopContext(native, coordinator, requireInputProbe: true);

        var first = context.EnsureReady();
        coordinator.Invalidate("rdp_reconnected");
        var second = context.EnsureReady();

        first.Ready.Should().BeTrue();
        second.Ready.Should().BeTrue();
        second.Generation.Should().BeGreaterThan(first.Generation);
        native.SetDesktopCalls.Should().ContainInOrder(new IntPtr(101), new IntPtr(102));
        native.CloseDesktopCalls.Should().ContainSingle().Which.Should().Be(new IntPtr(101));
    }

    [Fact]
    public void FailedFreshnessProbeRebindsBeforeReturningReady()
    {
        var now = DateTimeOffset.UtcNow;
        var native = new FakeDesktopNative();
        native.ProbeResults.Enqueue((true, 0));
        native.ProbeResults.Enqueue((false, 5));
        native.ProbeResults.Enqueue((true, 0));
        var context = new RemoteSupportInteractiveDesktopContext(
            native,
            new RemoteSupportDesktopContextCoordinator(),
            requireInputProbe: true,
            () => now);

        context.EnsureReady().Ready.Should().BeTrue();
        now += RemoteSupportInteractiveDesktopContext.ProbeFreshness + TimeSpan.FromMilliseconds(1);
        var recovered = context.EnsureReady();

        recovered.Ready.Should().BeTrue();
        recovered.DesktopName.Should().Be("Desktop102");
        native.CloseDesktopCalls.Should().Contain(new IntPtr(101));
        context.Dispose();
    }

    [Fact]
    public void DisposeSwitchesBackBeforeClosingTheBoundDesktop()
    {
        var native = new FakeDesktopNative();
        native.ProbeResults.Enqueue((true, 0));
        var context = new RemoteSupportInteractiveDesktopContext(
            native,
            new RemoteSupportDesktopContextCoordinator(),
            requireInputProbe: true);
        context.EnsureReady().Ready.Should().BeTrue();

        context.Dispose();

        native.SetDesktopCalls.Last().Should().Be(new IntPtr(7));
        native.CloseDesktopCalls.Last().Should().Be(new IntPtr(101));
        native.Operations.Should().ContainInOrder("set:7", "close:101");
    }

    [Fact]
    public void OpenFailureIsReportedWithoutClosingAnInvalidHandle()
    {
        var native = new FakeDesktopNative { OpenSucceeds = false };
        using var context = new RemoteSupportInteractiveDesktopContext(
            native,
            new RemoteSupportDesktopContextCoordinator(),
            requireInputProbe: true);

        var result = context.EnsureReady();

        result.Ready.Should().BeFalse();
        result.Status.Should().Be("input_desktop_open_failed");
        result.Win32Error.Should().Be(6);
        native.CloseDesktopCalls.Should().BeEmpty();
    }

    private sealed class FakeDesktopNative : IRemoteSupportDesktopContextNative
    {
        private int _nextHandle = 100;

        public bool OpenSucceeds { get; set; } = true;
        public Queue<(bool Result, int Error)> ProbeResults { get; } = new();
        public List<IntPtr> SetDesktopCalls { get; } = new();
        public List<IntPtr> CloseDesktopCalls { get; } = new();
        public List<string> Operations { get; } = new();
        public IntPtr CurrentDesktop { get; private set; } = new(7);

        public IntPtr GetCurrentThreadDesktop() => CurrentDesktop;

        public string? GetCurrentThreadDesktopName() => $"Desktop{CurrentDesktop.ToInt64()}";

        public IntPtr OpenInputDesktop(out string? desktopName, out int error)
        {
            if (!OpenSucceeds)
            {
                desktopName = null;
                error = 6;
                return IntPtr.Zero;
            }

            var handle = new IntPtr(Interlocked.Increment(ref _nextHandle));
            desktopName = $"Desktop{handle.ToInt64()}";
            error = 0;
            return handle;
        }

        public IntPtr OpenDefaultDesktop(out string? desktopName, out int error) =>
            OpenInputDesktop(out desktopName, out error);

        public bool SetThreadDesktop(IntPtr desktop, out int error)
        {
            SetDesktopCalls.Add(desktop);
            Operations.Add($"set:{desktop.ToInt64()}");
            CurrentDesktop = desktop;
            error = 0;
            return true;
        }

        public bool CloseDesktop(IntPtr desktop, out int error)
        {
            CloseDesktopCalls.Add(desktop);
            Operations.Add($"close:{desktop.ToInt64()}");
            error = 0;
            return true;
        }

        public bool ProbeInputDesktop(out int error)
        {
            var result = ProbeResults.Count > 0 ? ProbeResults.Dequeue() : (true, 0);
            error = result.Item2;
            return result.Item1;
        }
    }
}
