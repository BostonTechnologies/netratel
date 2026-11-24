using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.Shared.Contracts.Secrets;

namespace NetRatel.Client.Service.Shells;

[SupportedOSPlatform("windows")]
internal sealed class WindowsExecutionAccount(SafeAccessTokenHandle token, string sid) : IDisposable
{
    private IntPtr _profile;
    private IntPtr _environment;
    internal const int ExtendedStartupInfo = 0x00080000;
    private const int CreateSuspended = 0x00000004;
    private const int CreateUnicodeEnvironment = 0x00000400;
    internal const int CreateNoWindow = 0x08000000;

    public static bool IsAvailable
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity.IsSystem;
        }
    }

    internal static (string Name, string? Domain) Qualify(string name, string domain)
    {
        var slash = name.IndexOf('\\');
        if (slash >= 0)
        {
            if (domain.Length != 0 || slash == 0 || slash == name.Length - 1 || name.IndexOf('\\', slash + 1) >= 0)
                throw Unavailable();
            return (name[(slash + 1)..], name[..slash]);
        }
        if (name.Contains('@'))
        {
            if (domain.Length != 0) throw Unavailable();
            return (name, null);
        }
        return (name, domain.Length == 0 ? "." : domain);
    }

    public static WindowsExecutionAccount LogOn(RunAsCredential credential, bool terminal)
    {
        if (!IsAvailable || credential.Password.Length == 0 || credential.Password.Contains('\0'))
            throw Unavailable();
        var (name, domain) = Qualify(credential.Username, credential.Domain);
        var password = Marshal.StringToHGlobalUni(credential.Password);
        SafeAccessTokenHandle login;
        try
        {
            const int interactive = 2;
            const int batch = 4;
            if (!LogonUser(name, domain, password, terminal ? interactive : batch, 0, out login))
            {
                login?.Dispose();
                throw Unavailable();
            }
        }
        finally { Marshal.ZeroFreeGlobalAllocUnicode(password); }

        WindowsExecutionAccount? account = null;
        try
        {
            using var identity = new WindowsIdentity(login.DangerousGetHandle());
            if (identity.User?.Value is not { } actual || !ObservedExecutionIdentity.IsWindowsAccountId(actual) ||
                (credential.ExpectedAccountId.Length != 0 && credential.ExpectedAccountId != actual))
                throw Unavailable();
            account = new(login, actual);
            var profile = new ProfileInformation
            {
                Size = Marshal.SizeOf<ProfileInformation>(),
                Flags = 1, // PI_NOUI
                UserName = identity.Name.Split('\\')[^1]
            };
            if (!LoadUserProfile(login, ref profile)) throw Unavailable();
            account._profile = profile.Handle;
            if (!CreateEnvironmentBlock(out account._environment, login, false)) throw Unavailable();
            return account;
        }
        catch
        {
            if (account is null) login.Dispose();
            else account.Dispose();
            throw;
        }
    }

    public (ProcessInformation Process, ObservedExecutionIdentity Identity) StartSuspended(
        string executable, string arguments, string? directory, int flags, bool inheritHandles, ref StartupInfoEx startup,
        IReadOnlyDictionary<string, string>? parameters = null)
    {
        var command = new StringBuilder($"\"{executable}\" {arguments}");
        var overridden = IntPtr.Zero;
        try
        {
            if (parameters is { Count: > 0 })
            {
                var variables = ReadEnvironment();
                foreach (var (name, value) in parameters) variables[name] = value;
                var block = new StringBuilder();
                foreach (var (name, value) in variables) block.Append(name).Append('=').Append(value).Append('\0');
                overridden = Marshal.StringToHGlobalUni(block.Append('\0').ToString());
            }
            if (!CreateProcessAsUser(token, executable, command, IntPtr.Zero, IntPtr.Zero, inheritHandles,
                    flags | CreateSuspended | CreateUnicodeEnvironment, overridden == IntPtr.Zero ? _environment : overridden,
                    directory ?? ReadEnvironment().GetValueOrDefault("USERPROFILE"), ref startup, out var started))
                throw Unavailable();
            try
            {
                using var process = new SafeProcessHandle(started.ProcessHandle, ownsHandle: false);
                if (NativeProcessIdentity.ObserveWindowsProcess(process) is not { } identity || identity.AccountId != sid)
                    throw Unavailable();
                return (started, identity);
            }
            catch
            {
                TerminateProcess(started.ProcessHandle, 126);
                CloseHandle(started.ThreadHandle);
                CloseHandle(started.ProcessHandle);
                throw;
            }
        }
        finally
        {
            if (overridden != IntPtr.Zero) Marshal.ZeroFreeGlobalAllocUnicode(overridden);
        }
    }

    private SortedDictionary<string, string> ReadEnvironment()
    {
        var variables = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var current = _environment; Marshal.ReadInt16(current) != 0;)
        {
            var entry = Marshal.PtrToStringUni(current)!;
            var separator = entry.IndexOf('=', 1);
            if (separator > 0) variables[entry[..separator]] = entry[(separator + 1)..];
            current += (entry.Length + 1) * sizeof(char);
        }
        return variables;
    }

    public static void Resume(ProcessInformation started)
    {
        if (ResumeThread(started.ThreadHandle) != uint.MaxValue) return;
        TerminateProcess(started.ProcessHandle, 126);
        throw Unavailable();
    }

    public void Dispose()
    {
        if (_environment != IntPtr.Zero) DestroyEnvironmentBlock(_environment);
        if (_profile != IntPtr.Zero) UnloadUserProfile(token, _profile);
        _environment = IntPtr.Zero;
        _profile = IntPtr.Zero;
        token.Dispose();
    }

    private static InvalidOperationException Unavailable() => new("Windows could not authenticate or launch the selected account on this client.");

    [StructLayout(LayoutKind.Sequential)]
    internal struct StartupInfo
    {
        public int Size;
        public IntPtr Reserved, Desktop, Title;
        public int X, Y, Width, Height, XCountChars, YCountChars, FillAttribute, Flags;
        public ushort ShowWindow, ReservedLength;
        public IntPtr ReservedBytes, Input, Output, Error;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct StartupInfoEx
    {
        public StartupInfo Info;
        public IntPtr Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ProcessInformation
    {
        public IntPtr ProcessHandle, ThreadHandle;
        public int ProcessId, ThreadId;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProfileInformation
    {
        public int Size, Flags;
        public string UserName;
        public string? ProfilePath, DefaultPath, ServerName, PolicyPath;
        public IntPtr Handle;
    }

    [DllImport("advapi32.dll", EntryPoint = "LogonUserW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LogonUser(string name, string? domain, IntPtr password, int kind, int provider, out SafeAccessTokenHandle token);

    [DllImport("userenv.dll", EntryPoint = "LoadUserProfileW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LoadUserProfile(SafeAccessTokenHandle token, ref ProfileInformation profile);

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnloadUserProfile(SafeAccessTokenHandle token, IntPtr profile);

    [DllImport("userenv.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateEnvironmentBlock(out IntPtr environment, SafeAccessTokenHandle token,
        [MarshalAs(UnmanagedType.Bool)] bool inherit);

    [DllImport("userenv.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyEnvironmentBlock(IntPtr environment);

    [DllImport("advapi32.dll", EntryPoint = "CreateProcessAsUserW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessAsUser(SafeAccessTokenHandle token, string application, StringBuilder command,
        IntPtr processAttributes, IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inherit,
        int flags, IntPtr environment, string? directory, ref StartupInfoEx startup, out ProcessInformation process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(IntPtr thread);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(IntPtr handle);
}
