[CmdletBinding()]
param()

# Run in the independently enrolled Windows canary's ordinary interactive user
# session. This process creates only a disposable local GUI and stores no data.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'This owner-run GUI fixture requires Windows.'
}

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;

namespace NetRatel.CuaFixture
{
    public static class Window
    {
        private const uint Child = 0x40000000, Visible = 0x10000000, Border = 0x00800000;
        private static readonly WndProc Callback = HandleMessage;
        private static IntPtr Input, Output;

        public static void Run()
        {
            var instance = GetModuleHandle(null);
            var className = "NetRatelCuaCanary" + System.Diagnostics.Process.GetCurrentProcess().Id;
            var descriptor = new WindowClass
            {
                Procedure = Callback, Instance = instance, Cursor = LoadCursor(IntPtr.Zero, (IntPtr)32512),
                Background = (IntPtr)6, ClassName = className
            };
            if (RegisterClass(ref descriptor) == 0) throw new System.ComponentModel.Win32Exception();
            var title = "NetRatel CUA Canary - PID " + System.Diagnostics.Process.GetCurrentProcess().Id;
            var window = CreateWindowEx(0, className, title, 0x00CF0000 | Visible, 100, 100, 560, 260,
                IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
            if (window == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
            CreateWindowEx(0, "Static", "Enter a disposable value, then invoke Apply.", Child | Visible,
                20, 20, 500, 25, window, IntPtr.Zero, instance, IntPtr.Zero);
            Input = CreateWindowEx(0, "Edit", "", Child | Visible | Border | 0x80,
                20, 55, 490, 28, window, (IntPtr)101, instance, IntPtr.Zero);
            CreateWindowEx(0, "Button", "Apply", Child | Visible, 20, 100, 120, 32,
                window, (IntPtr)102, instance, IntPtr.Zero);
            CreateWindowEx(0, "Button", "Reset", Child | Visible, 160, 100, 120, 32,
                window, (IntPtr)103, instance, IntPtr.Zero);
            Output = CreateWindowEx(0, "Static", "Applied: (none)", Child | Visible | Border,
                20, 155, 490, 30, window, (IntPtr)104, instance, IntPtr.Zero);
            if (Input == IntPtr.Zero || Output == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
            Console.WriteLine(title);
            Console.WriteLine("Close this window to end the fixture. Nothing is saved.");
            Message message;
            int result;
            while ((result = GetMessage(out message, IntPtr.Zero, 0, 0)) > 0)
            {
                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }
            if (result < 0) throw new System.ComponentModel.Win32Exception();
        }

        private static IntPtr HandleMessage(IntPtr window, uint message, IntPtr word, IntPtr data)
        {
            if (message == 0x0111 && ((word.ToInt64() >> 16) & 0xffff) == 0)
            {
                var control = (int)(word.ToInt64() & 0xffff);
                if (control == 102)
                {
                    var value = new StringBuilder(4097);
                    GetWindowText(Input, value, value.Capacity);
                    SetWindowText(Output, "Applied: " + value);
                    return IntPtr.Zero;
                }
                if (control == 103)
                {
                    SetWindowText(Input, "");
                    SetWindowText(Output, "Applied: (none)");
                    return IntPtr.Zero;
                }
            }
            if (message == 0x0002) { PostQuitMessage(0); return IntPtr.Zero; }
            return DefWindowProc(window, message, word, data);
        }

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate IntPtr WndProc(IntPtr window, uint message, IntPtr word, IntPtr data);
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WindowClass
        {
            public uint Style;
            public WndProc Procedure;
            public int ClassExtra, WindowExtra;
            public IntPtr Instance, Icon, Cursor, Background;
            public string MenuName, ClassName;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct Message
        {
            public IntPtr Window;
            public uint Id;
            public UIntPtr Word;
            public IntPtr Data;
            public uint Time;
            public int X, Y;
            public uint Private;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string name);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClass(ref WindowClass value);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateWindowEx(uint exStyle, string className, string title, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DefWindowProc(IntPtr window, uint message, IntPtr word, IntPtr data);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadCursor(IntPtr instance, IntPtr name);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder text, int maximum);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool SetWindowText(IntPtr window, string text);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern int GetMessage(out Message message, IntPtr window, uint min, uint max);
        [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message message);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DispatchMessage(ref Message message);
        [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
    }
}
'@

[NetRatel.CuaFixture.Window]::Run()
