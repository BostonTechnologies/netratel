using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.ServiceProcess;
using System.Threading;
using System.Threading.Tasks;
using NetRatel.Shared.Contracts.Services;

namespace NetRatel.Client.Service.Services;

internal interface IClientServiceControlAdapter
{
    Task<string?> ExecuteAsync(ClientServiceControlPayload operation, CancellationToken cancellationToken);
}

/// <summary>Validates an exact native identity before mutation and reports a fresh observation afterwards.</summary>
internal sealed class ClientServiceControlExecutor(
    IServiceInventoryCollector? inventory = null,
    IClientServiceControlAdapter? adapter = null,
    TimeProvider? timeProvider = null,
    ClientServicePlatform? platform = null)
{
    private readonly IServiceInventoryCollector _inventory = inventory ?? ServiceInventoryCollectorFactory.Create(timeProvider ?? TimeProvider.System);
    private readonly IClientServiceControlAdapter _adapter = adapter ?? new NativeClientServiceControlAdapter();
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly ClientServicePlatform _platform = platform ?? CurrentPlatform;

    public static ClientServicePlatform CurrentPlatform => OperatingSystem.IsWindows() ? ClientServicePlatform.Windows :
        OperatingSystem.IsLinux() && Directory.Exists("/run/systemd/system") &&
            (File.Exists("/usr/bin/systemctl") || File.Exists("/bin/systemctl"))
            ? ClientServicePlatform.LinuxSystemd : ClientServicePlatform.Unknown;

    public async Task<ClientServiceControlResult> ExecuteAsync(ClientServiceControlPayload operation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (operation.OperationId == Guid.Empty || !ClientServiceControlRules.IsValidSelection(operation.ServiceName, operation.Platform, operation.Action))
            return Result("invalid_service_selection");
        if (_platform == ClientServicePlatform.Unknown || operation.Platform != _platform) return Result("platform_unsupported");
        if (ClientServiceControlRules.IsProtectedService(operation.ServiceName)) return Result("agent_service_protected");
        var now = _clock.GetUtcNow();
        if (operation.ExpiresAtUtc <= now) return Result("service_operation_expired");
        if (operation.IssuedAtUtc is { } issuedAt)
        {
            var lifetime = operation.ExpiresAtUtc - issuedAt;
            if (lifetime <= TimeSpan.Zero || lifetime > ClientServiceControlRules.DispatchLifetime)
                return Result("invalid_service_operation_window");
            if (issuedAt - now > ClientServiceControlRules.IssuanceClockSkew)
                return Result("service_operation_clock_skew");
        }
        else if (operation.ExpiresAtUtc - now > ClientServiceControlRules.DispatchLifetime + ClientServiceControlRules.IssuanceClockSkew)
        {
            // Legacy issuers used expiry = issuance + DispatchLifetime without sending issuance.
            return Result("service_operation_clock_skew");
        }

        var before = await ObserveAsync(operation, cancellationToken).ConfigureAwait(false);
        if (before is null) return Result("service_identity_unavailable");
        if (before.AuthoritativeMissing) return Result("service_not_found", before.State);
        // Reading an exact identity may have consumed the queue deadline.
        if (operation.ExpiresAtUtc <= _clock.GetUtcNow()) return Result("service_operation_expired", before.State);

        string? error;
        try { error = await _adapter.ExecuteAsync(operation, cancellationToken).ConfigureAwait(false); }
        catch (System.TimeoutException) { return Result("service_operation_timeout"); }
        catch (UnauthorizedAccessException) { return Result("service_access_denied"); }
        catch (Win32Exception exception) { return Result(NativeClientServiceControlAdapter.ErrorCode(exception.NativeErrorCode)); }
        catch (IOException) { return Result("service_controller_unavailable"); }
        if (error is not null) return Result(error);

        var after = await ObserveAsync(operation, cancellationToken).ConfigureAwait(false);
        if (after is null) return Result("service_result_unconfirmed");
        var expected = operation.Action == ClientServiceControlAction.Stop ? ClientServiceState.Stopped : ClientServiceState.Running;
        return Result(after.State == expected ? null : "service_state_not_reached", after.State);

        ClientServiceControlResult Result(string? code, ClientServiceState state = ClientServiceState.Unknown) =>
            new(operation.OperationId, operation.ServiceName, operation.Platform, operation.Action, code is null, state, _clock.GetUtcNow(), code);
    }

    private async Task<ClientServiceObservation?> ObserveAsync(ClientServiceControlPayload operation, CancellationToken cancellationToken)
    {
        var result = await _inventory.CollectWatchAsync([operation.ServiceName], 1, cancellationToken).ConfigureAwait(false);
        return result.Status == ServiceCollectionStatus.Complete
            ? result.Services.SingleOrDefault(service => service.Platform == operation.Platform &&
                ClientServiceContractValidator.GetServiceNameComparer(operation.Platform).Equals(service.Name, operation.ServiceName))
            : null;
    }
}

internal sealed class NativeClientServiceControlAdapter : IClientServiceControlAdapter
{
    // Keep a hung native SCM call from accumulating workers across reconnects.
    private static readonly SemaphoreSlim WindowsWorker = new(1, 1);

    public async Task<string?> ExecuteAsync(ClientServiceControlPayload operation, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows() && operation.Platform == ClientServicePlatform.Windows)
            return await ExecuteWindowsAsync(operation, cancellationToken).ConfigureAwait(false);
        if (!OperatingSystem.IsLinux() || operation.Platform != ClientServicePlatform.LinuxSystemd)
            return "platform_unsupported";
        var executable = File.Exists("/usr/bin/systemctl") ? "/usr/bin/systemctl" : "/bin/systemctl";
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
        };
        start.Environment["LC_ALL"] = "C";
        start.Environment["SYSTEMD_COLORS"] = "0";
        start.ArgumentList.Add("--no-ask-password");
        start.ArgumentList.Add("--no-pager");
        start.ArgumentList.Add(operation.Action.ToString().ToLowerInvariant());
        start.ArgumentList.Add("--");
        start.ArgumentList.Add(operation.ServiceName);
        var result = await BoundedProcessRunner.RunAsync(start, ClientServiceControlRules.ExecutionTimeout,
            "control the selected systemd service", cancellationToken).ConfigureAwait(false);
        // Native diagnostics can contain paths or configuration; transmit only a fixed failure category.
        return result.ExitCode == 0 ? null : "systemd_control_failed";
    }

    [SupportedOSPlatform("windows")]
    private static async Task<string?> ExecuteWindowsAsync(ClientServiceControlPayload operation, CancellationToken cancellationToken)
    {
        if (!await WindowsWorker.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return "service_controller_busy";
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(ClientServiceControlRules.ExecutionTimeout);
        var token = deadline.Token;
        var worker = Task.Run(async () =>
        {
            try
            {
                using var controller = new ServiceController(operation.ServiceName);
                if (operation.Action is ClientServiceControlAction.Stop or ClientServiceControlAction.Restart && controller.Status != ServiceControllerStatus.Stopped)
                {
                    if (!controller.CanStop) return "service_cannot_stop";
                    // Never implicitly stop dependent services.
                    controller.Stop(stopDependentServices: false);
                    await WaitForStatusAsync(controller, ServiceControllerStatus.Stopped, token).ConfigureAwait(false);
                }
                if (operation.Action is ClientServiceControlAction.Start or ClientServiceControlAction.Restart && controller.Status != ServiceControllerStatus.Running)
                {
                    if (controller.Status != ServiceControllerStatus.StartPending) controller.Start();
                    await WaitForStatusAsync(controller, ServiceControllerStatus.Running, token).ConfigureAwait(false);
                }
                return null;
            }
            catch (InvalidOperationException exception) when (exception.InnerException is Win32Exception native)
            { return ErrorCode(native.NativeErrorCode); }
            finally { WindowsWorker.Release(); }
        }, CancellationToken.None);
        _ = worker.ContinueWith(completed => { _ = completed.Exception; }, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        try { return await worker.WaitAsync(token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new System.TimeoutException("The service control deadline expired."); }
    }

    [SupportedOSPlatform("windows")]
    private static async Task WaitForStatusAsync(ServiceController controller, ServiceControllerStatus expected, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            controller.Refresh();
            if (controller.Status == expected) return;
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
        }
    }

    internal static string ErrorCode(int code) => code switch
    {
        5 => "service_access_denied",
        1058 => "service_disabled",
        1060 => "service_not_found",
        1051 => "service_has_running_dependents",
        1061 => "service_busy",
        _ => "service_control_failed"
    };
}
