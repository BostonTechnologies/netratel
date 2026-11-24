using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using NetRatel.Shared.Contracts.Secrets;

namespace NetRatel.Client.Service.Shells;

[SupportedOSPlatform("windows")]
internal sealed class WindowsAccountJobProcess(Process process, AnonymousPipeServerStream input,
    AnonymousPipeServerStream stdout, AnonymousPipeServerStream stderr, Encoding encoding,
    ObservedExecutionIdentity identity) : IDisposable
{
    public Process Process { get; } = process;
    public Stream Input => input;
    public StreamReader Output { get; } = new(stdout, encoding);
    public StreamReader Error { get; } = new(stderr, encoding);
    public ObservedExecutionIdentity Identity { get; } = identity;

    public static WindowsAccountJobProcess Start(WindowsExecutionAccount account, ProcessStartInfo start,
        string? directory, IReadOnlyDictionary<string, string>? environment, Encoding encoding)
    {
        var stdin = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable);
        var stdout = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        var stderr = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        Process? process = null;
        var native = default(WindowsExecutionAccount.ProcessInformation);
        var attributes = IntPtr.Zero;
        var handles = IntPtr.Zero;
        var initialized = false;
        try
        {
            InitializeAttributeList(IntPtr.Zero, 1, 0, out var size);
            attributes = Marshal.AllocHGlobal(size);
            if (!InitializeAttributeList(attributes, 1, 0, out _)) throw LaunchFailed();
            initialized = true;
            handles = Marshal.AllocHGlobal(3 * IntPtr.Size);
            var childInput = stdin.ClientSafePipeHandle.DangerousGetHandle();
            var childOutput = stdout.ClientSafePipeHandle.DangerousGetHandle();
            var childError = stderr.ClientSafePipeHandle.DangerousGetHandle();
            Marshal.WriteIntPtr(handles, 0, childInput);
            Marshal.WriteIntPtr(handles, IntPtr.Size, childOutput);
            Marshal.WriteIntPtr(handles, 2 * IntPtr.Size, childError);
            if (!UpdateAttribute(attributes, 0, (IntPtr)0x00020002, handles, (IntPtr)(3 * IntPtr.Size), IntPtr.Zero, IntPtr.Zero))
                throw LaunchFailed();
            var startup = new WindowsExecutionAccount.StartupInfoEx
            {
                Attributes = attributes,
                Info = new()
                {
                    Size = Marshal.SizeOf<WindowsExecutionAccount.StartupInfoEx>(), Flags = 0x00000100,
                    Input = childInput, Output = childOutput, Error = childError
                }
            };
            (native, var identity) = account.StartSuspended(start.FileName, start.Arguments, directory,
                WindowsExecutionAccount.ExtendedStartupInfo | WindowsExecutionAccount.CreateNoWindow,
                true, ref startup, environment);
            stdin.DisposeLocalCopyOfClientHandle();
            stdout.DisposeLocalCopyOfClientHandle();
            stderr.DisposeLocalCopyOfClientHandle();
            process = Process.GetProcessById(native.ProcessId);
            // Retain the process even if it exits as soon as the suspended thread is resumed.
            _ = process.SafeHandle;
            WindowsExecutionAccount.Resume(native);
            return new(process, stdin, stdout, stderr, encoding, identity);
        }
        catch
        {
            if (native.ProcessHandle != IntPtr.Zero) WindowsExecutionAccount.TerminateProcess(native.ProcessHandle, 126);
            process?.Dispose();
            stdin.Dispose();
            stdout.Dispose();
            stderr.Dispose();
            throw;
        }
        finally
        {
            if (native.ThreadHandle != IntPtr.Zero) WindowsExecutionAccount.CloseHandle(native.ThreadHandle);
            if (native.ProcessHandle != IntPtr.Zero) WindowsExecutionAccount.CloseHandle(native.ProcessHandle);
            if (initialized) DeleteAttributeList(attributes);
            Marshal.FreeHGlobal(attributes);
            Marshal.FreeHGlobal(handles);
        }
    }

    public void Dispose()
    {
        Input.Dispose();
        Output.Dispose();
        Error.Dispose();
        Process.Dispose();
    }

    private static InvalidOperationException LaunchFailed() => new("The private Windows execution streams could not be created.");

    [DllImport("kernel32.dll", EntryPoint = "InitializeProcThreadAttributeList", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeAttributeList(IntPtr attributes, int count, int flags, out IntPtr size);

    [DllImport("kernel32.dll", EntryPoint = "UpdateProcThreadAttribute", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateAttribute(IntPtr attributes, uint flags, IntPtr attribute, IntPtr value, IntPtr size,
        IntPtr previous, IntPtr returnSize);

    [DllImport("kernel32.dll", EntryPoint = "DeleteProcThreadAttributeList")]
    private static extern void DeleteAttributeList(IntPtr attributes);
}
