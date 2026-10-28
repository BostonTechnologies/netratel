using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NetRatel.Shared.Contracts.ComputerUse;

namespace NetRatel.Client.Service.ComputerUse;

/// <summary>Small native fallback for standard Win32 controls. It never injects global keyboard/mouse input.</summary>
[SupportedOSPlatform("windows")]
internal sealed class NativeWindowsComputerUseProvider : IComputerUseProvider
{
    private readonly Dictionary<string, NativeElement> _elements = new(StringComparer.Ordinal);
    private WindowsComputerUseDesktop.WindowRect _observedBounds;
    private static readonly string[] Actions = [ComputerUseActionKinds.Invoke, ComputerUseActionKinds.Type];

    public Task<ComputerUseResult> ExecuteAsync(ComputerUseCommand command, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = command.Operation switch
        {
            ComputerUseOperations.Capabilities => Result(command, ComputerUseStatuses.Completed) with
            {
                Capabilities = new(command.Provider, true,
                    [ComputerUseOperations.ListWindows, ComputerUseOperations.Open, ComputerUseOperations.Observe, ComputerUseOperations.Act],
                    Actions, "win32-v1", UnavailableReason: null)
            },
            ComputerUseOperations.ListWindows => Result(command, ComputerUseStatuses.Observed) with { Windows = WindowsComputerUseDesktop.ListWindows(command.Target) },
            ComputerUseOperations.Open or ComputerUseOperations.Observe => Result(command, ComputerUseStatuses.Observed) with { Observation = Observe(command.Target, command.IncludeScreenshot, cancellationToken) },
            ComputerUseOperations.Act => Act(command, cancellationToken),
            _ => ComputerUseResult.Refused(command, "unsupported_operation")
        };
        return Task.FromResult(result);
    }

    private ComputerUseResult Act(ComputerUseCommand command, CancellationToken cancellationToken)
    {
        var action = command.Action!;
        if (action.Kind is not (ComputerUseActionKinds.Invoke or ComputerUseActionKinds.Type))
            return ComputerUseResult.Refused(command, "unsupported_action", "The native fallback supports only standard Win32 Button invoke and Edit value writes.");
        if (action.ElementId is null || !_elements.TryGetValue(action.ElementId, out var element))
            return ComputerUseResult.Refused(command, "stale_element", "Select an element from a fresh observation.");
        var root = WindowsComputerUseDesktop.WindowHandle(command.Target);
        if (!WindowsComputerUseDesktop.IsWindow(element.Handle) || WindowsComputerUseDesktop.GetAncestor(element.Handle, 2) != root)
            return ComputerUseResult.Refused(command, "wrong_window", "The observed control no longer belongs to the selected window.");
        WindowsComputerUseDesktop.GetWindowThreadProcessId(element.Handle, out var pid);
        if (pid != command.Target.ProcessId || !SameBounds(_observedBounds, WindowsComputerUseDesktop.Bounds(root)))
            return ComputerUseResult.Refused(command, "stale_observation", "The target's process or geometry changed; observe again.");
        var className = new StringBuilder(128);
        WindowsComputerUseDesktop.GetClassName(element.Handle, className, className.Capacity);
        if (!string.Equals(className.ToString(), element.ClassName, StringComparison.Ordinal))
            return ComputerUseResult.Refused(command, "stale_element");
        cancellationToken.ThrowIfCancellationRequested();
        IntPtr delivered;
        if (action.Kind == ComputerUseActionKinds.Invoke && element.ClassName == "Button")
        {
            delivered = SendMessageTimeout(element.Handle, 0x00F5, UIntPtr.Zero, IntPtr.Zero, 0x0002, 1500, out _); // BM_CLICK
        }
        else if (action.Kind == ComputerUseActionKinds.Type && element.ClassName == "Edit" && !element.Password &&
            (GetWindowLong(element.Handle, -16) & 0x0020) == 0)
        {
            if (action.Text is null || action.Text.Length > ComputerUseContract.MaximumTextLength)
                return ComputerUseResult.Refused(command, "invalid_text");
            delivered = SendTextTimeout(element.Handle, 0x000C, UIntPtr.Zero, action.Text, 0x0002, 1500, out _); // WM_SETTEXT
        }
        else return ComputerUseResult.Refused(command, "unsupported_element", "This action is unsupported for the selected native control.");

        // Timeout is an unknown outcome: the target may process a delivered window message later.
        if (delivered == IntPtr.Zero)
            return Result(command, ComputerUseStatuses.Unknown, "native_delivery_unknown", true);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var verified = action.Kind == ComputerUseActionKinds.Type &&
                (GetWindowLong(element.Handle, -16) & 0x0020) == 0 &&
                string.Equals(ReadText(element.Handle), action.Text, StringComparison.Ordinal);
            var observation = Observe(command.Target, command.IncludeScreenshot, cancellationToken);
            return Result(command, verified ? ComputerUseStatuses.Verified : ComputerUseStatuses.AppliedUnverified,
                verified ? "value_read_back" : "effect_requires_verification", true) with { Observation = observation };
        }
        catch (Exception exception) when (exception is ComputerUseRefusalException or OperationCanceledException or System.ComponentModel.Win32Exception)
        {
            return Result(command, ComputerUseStatuses.AppliedUnverified, "readback_unavailable", true);
        }
    }

    private ComputerUseObservation Observe(ComputerUseTarget target, bool includeScreenshot, CancellationToken cancellationToken)
    {
        var hwnd = WindowsComputerUseDesktop.WindowHandle(target);
        var bounds = WindowsComputerUseDesktop.Bounds(hwnd);
        var id = Guid.NewGuid().ToString("N");
        List<ComputerUseElement> elements = [];
        _elements.Clear();
        WindowsComputerUseDesktop.EnumChildWindows(hwnd, (child, _) =>
        {
            if (elements.Count >= 128 || cancellationToken.IsCancellationRequested) return false;
            if (!WindowsComputerUseDesktop.IsWindowVisible(child)) return true;
            WindowsComputerUseDesktop.GetWindowThreadProcessId(child, out var pid);
            if (pid != target.ProcessId) return true;
            var className = new StringBuilder(128);
            WindowsComputerUseDesktop.GetClassName(child, className, className.Capacity);
            var type = className.ToString();
            if (type is not ("Button" or "Edit" or "Static")) return true;
            var password = type == "Edit" && (GetWindowLong(child, -16) & 0x0020) != 0;
            var elementId = id + ":" + elements.Count.ToString(CultureInfo.InvariantCulture);
            var value = password ? null : ReadText(child, 512);
            _elements.Add(elementId, new(child, type, password));
            elements.Add(new(elementId, type, type == "Edit" ? null : value, type == "Edit" ? value : null));
            return true;
        }, IntPtr.Zero);
        cancellationToken.ThrowIfCancellationRequested();
        _observedBounds = bounds;
        var title = new StringBuilder(257);
        WindowsComputerUseDesktop.GetWindowText(hwnd, title, title.Capacity);
        var state = JsonSerializer.Serialize(new
        {
            title = title.ToString(),
            bounds = new { x = bounds.Left, y = bounds.Top, width = bounds.Width, height = bounds.Height },
            supportedControls = "Standard Win32 Button, Edit and Static child windows; custom, WPF, WinUI and browser accessibility require Cua Driver.",
            elements
        }, ComputerUseContract.JsonOptions);
        return new(id, DateTimeOffset.UtcNow, target, state, elements,
            includeScreenshot ? CaptureWindow(hwnd, bounds) : null);
    }

    private static ComputerUseScreenshot CaptureWindow(IntPtr hwnd, WindowsComputerUseDesktop.WindowRect bounds)
    {
        if (bounds.Width > 4096 || bounds.Height > 4096)
            throw new ComputerUseRefusalException("capture_too_large", "The window exceeds the native capture size limit.");
        using var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            var dc = graphics.GetHdc();
            try
            {
                if (SendMessageTimeout(hwnd, 0x0317, new UIntPtr(unchecked((ulong)dc.ToInt64())), new IntPtr(30), 0x0002, 1500, out _) == IntPtr.Zero)
                    throw new ComputerUseRefusalException("capture_unavailable", "The application cannot render a bounded native window capture.");
            }
            finally { graphics.ReleaseHdc(dc); }
        }
        if (!SameBounds(bounds, WindowsComputerUseDesktop.Bounds(hwnd)))
            throw new ComputerUseRefusalException("stale_capture", "Window geometry changed during capture.");
        var scale = Math.Min(1d, 1280d / Math.Max(bounds.Width, bounds.Height));
        using var resized = new Bitmap(bitmap, Math.Max(1, (int)(bounds.Width * scale)), Math.Max(1, (int)(bounds.Height * scale)));
        using var stream = new MemoryStream();
        resized.Save(stream, ImageFormat.Png);
        if (stream.Length > 1500 * 1024)
            throw new ComputerUseRefusalException("capture_too_large", "The encoded screenshot exceeds the response budget.");
        return new("image/png", Convert.ToBase64String(stream.ToArray()), resized.Width, resized.Height);
    }

    private static string? ReadText(IntPtr hwnd, int maximumLength = ComputerUseContract.MaximumTextLength)
    {
        if (SendMessageTimeout(hwnd, 0x000E, UIntPtr.Zero, IntPtr.Zero, 0x0002, 200, out var characters) == IntPtr.Zero ||
            characters.ToUInt64() > (ulong)maximumLength) return null;
        var text = new StringBuilder(maximumLength + 1);
        return SendReadTimeout(hwnd, 0x000D, new UIntPtr((uint)text.Capacity), text, 0x0002, 200, out _) == IntPtr.Zero ? null : text.ToString();
    }
    private static bool SameBounds(WindowsComputerUseDesktop.WindowRect a, WindowsComputerUseDesktop.WindowRect b) => a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;
    private static ComputerUseResult Result(ComputerUseCommand c, string status, string? code = null, bool mayHaveApplied = false) =>
        new(ComputerUseContract.Version, c.OperationId, c.SessionId, status, code, Provider: c.Provider, MayHaveApplied: mayHaveApplied);
    public ValueTask DisposeAsync() { _elements.Clear(); return ValueTask.CompletedTask; }
    private sealed record NativeElement(IntPtr Handle, string ClassName, bool Password);

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint message, UIntPtr wParam, IntPtr lParam, uint flags, uint timeout, out UIntPtr result);
    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr SendTextTimeout(IntPtr hwnd, uint message, UIntPtr wParam, string text, uint flags, uint timeout, out UIntPtr result);
    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr SendReadTimeout(IntPtr hwnd, uint message, UIntPtr wParam, StringBuilder text, uint flags, uint timeout, out UIntPtr result);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr hwnd, int index);
}
