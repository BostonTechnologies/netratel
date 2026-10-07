using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using NetRatel.Shared.Contracts.Services;

namespace NetRatel.Client.Service.Services;

/// <summary>Read-only SCM discovery and explicit service status lookups.</summary>
internal sealed class WindowsScmServiceInventoryCollector : IServiceInventoryCollector
{
    private readonly IWindowsScmServiceInventoryAdapter _adapter;
    private readonly TimeProvider _timeProvider;
    private readonly Func<bool> _isWindows;
    private readonly TimeSpan _timeout;
    private readonly SemaphoreSlim _nativeWorker = new(1, 1);

    public WindowsScmServiceInventoryCollector(
        IWindowsScmServiceInventoryAdapter? adapter = null,
        TimeProvider? timeProvider = null,
        Func<bool>? isWindows = null,
        TimeSpan? timeout = null)
    {
        _adapter = adapter ?? new WindowsScmServiceInventoryAdapter();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _isWindows = isWindows ?? OperatingSystem.IsWindows;
        _timeout = timeout ?? ClientServicesLimits.CollectionTimeout;
        if (_timeout <= TimeSpan.Zero || _timeout > TimeSpan.FromSeconds(30))
            throw new ArgumentOutOfRangeException(nameof(timeout));
    }

    public Task<ServiceCollectionResult> CollectInventoryAsync(CancellationToken cancellationToken) =>
        CollectAsync(ServiceSnapshotKind.Inventory, [], 0, cancellationToken);

    public Task<ServiceCollectionResult> CollectWatchAsync(
        IReadOnlyList<string> names, ulong policyRevision, CancellationToken cancellationToken) =>
        CollectAsync(ServiceSnapshotKind.Watch, names, policyRevision, cancellationToken);

    private async Task<ServiceCollectionResult> CollectAsync(
        ServiceSnapshotKind kind, IReadOnlyList<string> names, ulong policyRevision, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_isWindows()) return Result(ServiceCollectionStatus.Unsupported, "platform_unsupported");
        if (kind == ServiceSnapshotKind.Watch &&
            (names.Count > ClientServicesLimits.MaximumWatchServices || names.Any(name => !ValidName(name))))
            return Result(ServiceCollectionStatus.Error, "invalid_watch_selection");
        var selected = names.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var selectedNames = selected.ToDictionary(name => name, StringComparer.OrdinalIgnoreCase);
        if (kind == ServiceSnapshotKind.Watch && selected.Length == 0)
            return new(Guid.NewGuid(), kind, ServiceCollectionStatus.Complete, _timeProvider.GetUtcNow(), [], null, policyRevision);
        if (!await _nativeWorker.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return Result(ServiceCollectionStatus.Error, "collection_busy");

        using var deadline = new CancellationTokenSource(_timeout, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var collectionToken = linked.Token;
        // Local SCM calls have no cancellation API. Retain the gate until the native worker
        // actually exits, even after a timeout, so repeated requests cannot accumulate workers.
        var worker = Task.Run(() =>
        {
            try
            {
                return kind == ServiceSnapshotKind.Inventory
                    ? _adapter.ReadInventory(ClientServicesLimits.MaximumServices, collectionToken)
                    : _adapter.ReadSelected(selected, collectionToken);
            }
            finally { _nativeWorker.Release(); }
        }, CancellationToken.None);
        _ = worker.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

        try
        {
            var read = await worker.WaitAsync(linked.Token).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var observedAt = _timeProvider.GetUtcNow();
            var bounded = read.Services.Take(kind == ServiceSnapshotKind.Inventory
                ? ClientServicesLimits.MaximumServices : ClientServicesLimits.MaximumWatchServices).ToArray();
            var entries = bounded.Where(service => ValidName(service.Name))
                .Where(service => kind != ServiceSnapshotKind.Watch || selectedNames.ContainsKey(service.Name))
                .DistinctBy(service => service.Name, StringComparer.OrdinalIgnoreCase)
                .Select(service => kind == ServiceSnapshotKind.Watch
                    ? service with { Name = selectedNames[service.Name] } : service)
                .Select(service => Map(service, observedAt))
                .Where(observation => ClientServiceContractValidator.TryValidateObservation(observation, out _)).ToArray();
            var complete = read.Complete && bounded.Length == read.Services.Count && entries.Length == bounded.Length &&
                bounded.All(service => service.DisplayName.Length <= ClientServicesLimits.MaximumDisplayNameLength &&
                    !service.DisplayName.Any(char.IsControl));
            if (!complete)
            {
                entries = entries.Select(observation => observation.AuthoritativeMissing
                    ? observation with { State = ClientServiceState.Unknown, AuthoritativeMissing = false }
                    : observation).ToArray();
            }
            return new(Guid.NewGuid(), kind,
                complete ? ServiceCollectionStatus.Complete : entries.Length > 0 ? ServiceCollectionStatus.Partial : ServiceCollectionStatus.Error,
                observedAt, entries, read.ErrorCode ?? (complete ? null : "inventory_incomplete"), policyRevision);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            return Result(ServiceCollectionStatus.Error, "collection_timeout");
        }
        catch (Win32Exception exception)
        {
            return Result(ServiceCollectionStatus.Error, WindowsScmServiceInventoryAdapter.ErrorCode(exception.NativeErrorCode));
        }
        catch (UnauthorizedAccessException)
        {
            return Result(ServiceCollectionStatus.Error, "scm_access_denied");
        }

        ServiceCollectionResult Result(ServiceCollectionStatus status, string errorCode) =>
            new(Guid.NewGuid(), kind, status, _timeProvider.GetUtcNow(), [], errorCode, policyRevision);
    }

    private static bool ValidName(string? name) =>
        ClientServiceContractValidator.IsValidServiceName(name, ClientServicePlatform.Windows);

    internal static ClientServiceObservation Map(WindowsScmService service, DateTimeOffset observedAt)
    {
        var rawState = service.AuthoritativeMissing ? "ERROR_SERVICE_DOES_NOT_EXIST" : service.CurrentState switch
        {
            1 => "SERVICE_STOPPED",
            2 => "SERVICE_START_PENDING",
            3 => "SERVICE_STOP_PENDING",
            4 => "SERVICE_RUNNING",
            5 => "SERVICE_CONTINUE_PENDING",
            6 => "SERVICE_PAUSE_PENDING",
            7 => "SERVICE_PAUSED",
            _ => service.CurrentState.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
        var state = service.AuthoritativeMissing ? ClientServiceState.Missing : service.CurrentState switch
        {
            // ERROR_SERVICE_NEVER_STARTED describes a service that has not run
            // since boot. The SCM ignores the service-specific code unless the
            // Win32 code is ERROR_SERVICE_SPECIFIC_ERROR (1066).
            1 when service.Win32ExitCode is not (0 or 1077) => ClientServiceState.Failed,
            1 => ClientServiceState.Stopped,
            2 or 5 => ClientServiceState.Starting,
            3 => ClientServiceState.Stopping,
            4 => ClientServiceState.Running,
            7 => ClientServiceState.Paused,
            _ => ClientServiceState.Unknown
        };
        var startMode = service.StartType switch
        {
            0 => "Boot",
            1 => "System",
            2 => "Automatic",
            3 => "Manual",
            4 => "Disabled",
            _ => null
        };
        if (state == ClientServiceState.Failed)
            rawState += service.Win32ExitCode == 1066
                ? $" (Win32 exit 1066; service exit {service.ServiceSpecificExitCode})"
                : $" (Win32 exit {service.Win32ExitCode})";
        var displayName = new string(service.DisplayName.Take(ClientServicesLimits.MaximumDisplayNameLength)
            .Select(character => char.IsControl(character) ? ' ' : character).ToArray());
        if (displayName.Length > 0 && char.IsHighSurrogate(displayName[^1])) displayName = displayName[..^1];
        return new(service.Name, displayName, ClientServicePlatform.Windows, state, rawState, startMode,
            null, null, null, null, observedAt, service.AuthoritativeMissing);
    }
}

internal interface IWindowsScmServiceInventoryAdapter
{
    WindowsScmReadResult ReadInventory(int maximumServices, CancellationToken cancellationToken);
    WindowsScmReadResult ReadSelected(IReadOnlyList<string> names, CancellationToken cancellationToken);
}

internal sealed record WindowsScmReadResult(IReadOnlyList<WindowsScmService> Services, bool Complete, string? ErrorCode = null);
internal sealed record WindowsScmService(string Name, string DisplayName, uint CurrentState,
    uint Win32ExitCode = 0, uint ServiceSpecificExitCode = 0, uint? StartType = null, bool AuthoritativeMissing = false);

/// <summary>Uses only SCM enumeration, service configuration, and status query rights.</summary>
internal sealed class WindowsScmServiceInventoryAdapter : IWindowsScmServiceInventoryAdapter
{
    private const uint ScManagerConnect = 0x0001;
    private const uint ScManagerEnumerateService = 0x0004;
    private const uint ServiceQueryConfig = 0x0001;
    private const uint ServiceQueryStatus = 0x0004;
    private const uint ServiceWin32 = 0x0030;
    private const uint ServiceStateAll = 0x0003;
    private const int ErrorAccessDenied = 5;
    private const int ErrorInsufficientBuffer = 122;
    private const int ErrorMoreData = 234;
    private const int ErrorServiceDoesNotExist = 1060;
    private const int EnumerationBufferBytes = 256 * 1024;
    private const int ConfigurationBufferBytes = 8 * 1024;
    private const int MaximumEnumerationPages = 32;

    public WindowsScmReadResult ReadInventory(int maximumServices, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var manager = OpenSCManager(null, null, ScManagerConnect | ScManagerEnumerateService);
        if (manager.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError());
        var services = new List<WindowsScmService>();
        var complete = true;
        string? errorCode = null;
        var buffer = Marshal.AllocHGlobal(EnumerationBufferBytes);
        try
        {
            uint resume = 0;
            for (var page = 0; page < MaximumEnumerationPages; page++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var previousResume = resume;
                var success = EnumServicesStatusEx(manager, 0, ServiceWin32, ServiceStateAll,
                    buffer, EnumerationBufferBytes, out _, out var count, ref resume, null);
                var error = success ? 0 : Marshal.GetLastPInvokeError();
                if (!success && error != ErrorMoreData)
                    return new(services, false, ErrorCode(error));
                var structureSize = Marshal.SizeOf<NativeEnumerationRecord>();
                if (count > EnumerationBufferBytes / structureSize)
                    return new(services, false, "scm_invalid_response");
                for (var index = 0; index < count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (services.Count >= maximumServices)
                        return new(services, false, "inventory_limit");
                    var service = ParseEnumerationRecord(IntPtr.Add(buffer, index * structureSize));
                    var startType = ReadConfiguration(manager, service.Name, out _, out var configError);
                    if (configError is not null) { complete = false; errorCode ??= configError; }
                    services.Add(service with { StartType = startType });
                }
                if (success) return new(services, complete, errorCode);
                if (resume == previousResume || page == MaximumEnumerationPages - 1)
                    return new(services, false, "inventory_limit");
            }
            return new(services, false, "inventory_limit");
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    public WindowsScmReadResult ReadSelected(IReadOnlyList<string> names, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var manager = OpenSCManager(null, null, ScManagerConnect);
        if (manager.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError());
        var services = new List<WindowsScmService>(names.Count);
        string? errorCode = null;
        foreach (var name in names)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var service = OpenService(manager, name, ServiceQueryStatus);
            if (service.IsInvalid)
            {
                var error = Marshal.GetLastPInvokeError();
                services.Add(new(name, name, 0, AuthoritativeMissing: error == ErrorServiceDoesNotExist));
                if (error != ErrorServiceDoesNotExist) errorCode ??= ErrorCode(error);
                continue;
            }
            if (!QueryServiceStatusEx(service, 0, out var status, Marshal.SizeOf<NativeServiceStatus>(), out _))
            {
                errorCode ??= ErrorCode(Marshal.GetLastPInvokeError());
                services.Add(new(name, name, 0));
                continue;
            }
            var startType = ReadConfiguration(manager, name, out var displayName, out var configError);
            errorCode ??= configError;
            services.Add(new(name, displayName ?? name, status.CurrentState, status.Win32ExitCode, status.ServiceSpecificExitCode, startType));
        }
        return new(services, errorCode is null, errorCode);
    }

    internal static WindowsScmService ParseEnumerationRecord(IntPtr pointer)
    {
        var native = Marshal.PtrToStructure<NativeEnumerationRecord>(pointer);
        return new(ReadBoundedString(native.ServiceName, ClientServicesLimits.MaximumNameLength + 1),
            ReadBoundedString(native.DisplayName, ClientServicesLimits.MaximumDisplayNameLength),
            native.Status.CurrentState, native.Status.Win32ExitCode, native.Status.ServiceSpecificExitCode);
    }

    private static string ReadBoundedString(IntPtr pointer, int maximumLength)
    {
        if (pointer == IntPtr.Zero) return string.Empty;
        var length = 0;
        while (length < maximumLength && Marshal.ReadInt16(pointer, length * sizeof(char)) != 0) length++;
        return Marshal.PtrToStringUni(pointer, length) ?? string.Empty;
    }

    private static uint? ReadConfiguration(SafeScmHandle manager, string name, out string? displayName, out string? errorCode)
    {
        displayName = null;
        errorCode = null;
        using var service = OpenService(manager, name, ServiceQueryConfig);
        if (service.IsInvalid)
        {
            errorCode = ErrorCode(Marshal.GetLastPInvokeError());
            return null;
        }
        var buffer = Marshal.AllocHGlobal(ConfigurationBufferBytes);
        try
        {
            if (!QueryServiceConfig(service, buffer, ConfigurationBufferBytes, out _))
            {
                var error = Marshal.GetLastPInvokeError();
                errorCode = error == ErrorInsufficientBuffer ? "scm_configuration_limit" : ErrorCode(error);
                return null;
            }
            var configuration = Marshal.PtrToStructure<NativeServiceConfiguration>(buffer);
            // Decode only the public display name. Executable paths, accounts and
            // dependencies stay as opaque pointers and never enter the inventory.
            displayName = ReadBoundedString(configuration.DisplayName, ClientServicesLimits.MaximumDisplayNameLength);
            return configuration.StartType;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    internal static string ErrorCode(int error) => error == ErrorAccessDenied ? "scm_access_denied" : "scm_query_failed";

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeEnumerationRecord
    {
        public IntPtr ServiceName;
        public IntPtr DisplayName;
        public NativeServiceStatus Status;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeServiceStatus
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
        public uint ProcessId;
        public uint ServiceFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeServiceConfiguration
    {
        public uint ServiceType;
        public uint StartType;
        public uint ErrorControl;
        public IntPtr BinaryPathName;
        public IntPtr LoadOrderGroup;
        public uint TagId;
        public IntPtr Dependencies;
        public IntPtr ServiceStartName;
        public IntPtr DisplayName;
    }

    private sealed class SafeScmHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private SafeScmHandle() : base(true) { }
        protected override bool ReleaseHandle() => CloseServiceHandle(handle);
    }

    [DllImport("advapi32.dll", EntryPoint = "OpenSCManagerW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeScmHandle OpenSCManager(string? machineName, string? databaseName, uint desiredAccess);

    [DllImport("advapi32.dll", EntryPoint = "OpenServiceW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeScmHandle OpenService(SafeScmHandle manager, string serviceName, uint desiredAccess);

    [DllImport("advapi32.dll", EntryPoint = "EnumServicesStatusExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumServicesStatusEx(SafeScmHandle manager, int infoLevel, uint serviceType,
        uint serviceState, IntPtr services, int bufferBytes, out uint bytesNeeded, out uint servicesReturned,
        ref uint resumeHandle, string? groupName);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatusEx(SafeScmHandle service, int infoLevel,
        out NativeServiceStatus status, int bufferBytes, out uint bytesNeeded);

    [DllImport("advapi32.dll", EntryPoint = "QueryServiceConfigW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceConfig(SafeScmHandle service, IntPtr config, int bufferBytes, out uint bytesNeeded);

    [DllImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr service);
}
