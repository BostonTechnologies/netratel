using Microsoft.Win32;
using NetRatel.Client.Service.Logging;
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;

namespace NetRatel.Client.Service.RemoteSupport;

internal sealed record RemoteSupportSasPolicyInfo(
    int? RawValue,
    string InterpretedValue,
    bool AllowsServices,
    bool AllowsEaseOfAccess,
    string Source);

internal sealed record RemoteSupportSasProbe(
    string Backend,
    bool ApiAvailable,
    RemoteSupportSasPolicyInfo Policy,
    uint? TargetSessionId,
    int? ProviderProcessSessionId,
    bool ProviderMatchesActiveConsole,
    string? OsCaption,
    string? OsVersion,
    string? OsBuild,
    bool OsIsServer,
    int? Win32Error = null,
    int? HResult = null,
    string? Error = null);

internal sealed record RemoteSupportSasResult(
    string StatusCode,
    string Message,
    bool Invoked,
    bool EffectObserved,
    RemoteSupportSasProbe Probe,
    DateTimeOffset AttemptedAt,
    int? Win32Error = null,
    int? HResult = null,
    string? Error = null);

[SupportedOSPlatform("windows")]
internal sealed class RemoteSupportSasController
{
    private const string ServiceBackend = "service_owned_send_sas";
    private const string ProviderProbeBackend = "provider_local_send_sas_probe";
    private static readonly Guid SendSasFalseArgument = Guid.Empty;

    private delegate void SendSasDelegate(bool asUser);

    public RemoteSupportSasProbe ProbeServiceOwned(uint? targetSessionId, int? providerProcessSessionId = null, bool providerMatchesActiveConsole = false)
    {
        var policy = ReadSoftwareSasPolicy();
        var apiAvailable = TryLoadSendSas(out _, out var apiError, out var hresult);
        var os = ReadOsInfo();
        var probe = new RemoteSupportSasProbe(
            ServiceBackend,
            apiAvailable,
            policy,
            targetSessionId ?? GetActiveConsoleSessionIdOrNull(),
            providerProcessSessionId,
            providerMatchesActiveConsole,
            os.Caption,
            os.Version,
            os.Build,
            os.IsServer,
            HResult: hresult,
            Error: apiError);
        UpdateDiagnostics(probe, null);
        return probe;
    }

    public RemoteSupportSasProbe ProbeProviderLocal(uint? targetSessionId, int? providerProcessSessionId = null, bool providerMatchesActiveConsole = false)
    {
        var policy = ReadSoftwareSasPolicy();
        var apiAvailable = TryLoadSendSas(out _, out var apiError, out var hresult);
        var os = ReadOsInfo();
        var probe = new RemoteSupportSasProbe(
            ProviderProbeBackend,
            apiAvailable,
            policy,
            targetSessionId ?? GetActiveConsoleSessionIdOrNull(),
            providerProcessSessionId,
            providerMatchesActiveConsole,
            os.Caption,
            os.Version,
            os.Build,
            os.IsServer,
            HResult: hresult,
            Error: apiError);
        UpdateDiagnostics(probe, null);
        return probe;
    }

    public async Task<RemoteSupportSasResult> InvokeServiceOwnedAsync(
        string sessionId,
        uint? targetSessionId,
        int? providerProcessSessionId,
        bool providerMatchesActiveConsole,
        string? providerDesktopState,
        string? beforeCaptureHash,
        CancellationToken ct)
    {
        var attemptedAt = DateTimeOffset.UtcNow;
        var probe = ProbeServiceOwned(targetSessionId, providerProcessSessionId, providerMatchesActiveConsole);

        if (probe.TargetSessionId is null || probe.TargetSessionId == uint.MaxValue)
        {
            return Complete(probe, RemoteSupportStatusCodes.SasFailedBeforeInvoke, "No active console session is available for Ctrl+Alt+Del.", false, false, attemptedAt);
        }

        string? apiError = null;
        int? apiHresult = null;
        if (!probe.ApiAvailable || !TryLoadSendSas(out var sendSas, out apiError, out apiHresult))
        {
            return Complete(probe with { Error = apiError, HResult = apiHresult }, RemoteSupportStatusCodes.SasApiNotAvailable, "Windows SendSAS API is not available on this host.", false, false, attemptedAt, hresult: apiHresult, error: apiError);
        }

        if (!probe.Policy.AllowsServices)
        {
            return Complete(probe, RemoteSupportStatusCodes.SasPolicyNotEnabled, "Windows software SAS policy does not allow services to simulate Ctrl+Alt+Del.", false, false, attemptedAt);
        }

        if (providerProcessSessionId.HasValue && !providerMatchesActiveConsole)
        {
            return Complete(probe, RemoteSupportStatusCodes.SasFailedBeforeInvoke, "Console helper session no longer matches the active console session.", false, false, attemptedAt);
        }

        var desktopBefore = SafeEvaluateCurrentProcessDesktopState();
        int? win32Error = null;
        int? hresult = null;
        string? error = null;
        try
        {
            sendSas(false);
            win32Error = Marshal.GetLastWin32Error();
            LogManager.WriteLog($"[RemoteSupportSAS] SendSAS invoked session={sessionId} targetSession={probe.TargetSessionId} backend={probe.Backend} win32={win32Error}");
        }
        catch (Exception ex)
        {
            hresult = ex.HResult;
            error = ex.Message;
            return Complete(probe, RemoteSupportStatusCodes.SasFailedBeforeInvoke, "Ctrl+Alt+Del could not be invoked before calling SendSAS.", false, false, attemptedAt, win32Error, hresult, error);
        }

        var effectObserved = await WaitForVisibleEffectAsync(desktopBefore, providerDesktopState, beforeCaptureHash, ct).ConfigureAwait(false);
        var status = effectObserved
            ? RemoteSupportStatusCodes.SasEffectObserved
            : RemoteSupportStatusCodes.SasNoVisibleEffect;
        var message = effectObserved
            ? "Ctrl+Alt+Del effect observed on the console desktop."
            : "Ctrl+Alt+Del was invoked, but no visible lock/logon screen effect was observed yet.";
        return Complete(probe, status, message, true, effectObserved, attemptedAt, win32Error, hresult, error);
    }

    public RemoteSupportSasResult RunSelfTest(bool send)
    {
        var attemptedAt = DateTimeOffset.UtcNow;
        var probe = ProbeProviderLocal(GetActiveConsoleSessionIdOrNull(), Environment.ProcessId, true);
        if (!send)
        {
            return Complete(probe, probe.ApiAvailable ? "sas_dry_run_available" : RemoteSupportStatusCodes.SasApiNotAvailable, "SAS self-test dry-run completed; pass --send to invoke SendSAS.", false, false, attemptedAt);
        }

        string? apiError = null;
        int? apiHresult = null;
        if (!probe.ApiAvailable || !TryLoadSendSas(out var sendSas, out apiError, out apiHresult))
        {
            return Complete(probe with { Error = apiError, HResult = apiHresult }, RemoteSupportStatusCodes.SasApiNotAvailable, "Windows SendSAS API is not available on this host.", false, false, attemptedAt, hresult: apiHresult, error: apiError);
        }

        try
        {
            sendSas(false);
            var win32 = Marshal.GetLastWin32Error();
            return Complete(probe, RemoteSupportStatusCodes.SasInvoked, "Provider-local diagnostic SendSAS probe invoked. Visible effect must be verified separately.", true, false, attemptedAt, win32);
        }
        catch (Exception ex)
        {
            return Complete(probe, RemoteSupportStatusCodes.SasFailedBeforeInvoke, ex.Message, false, false, attemptedAt, hresult: ex.HResult, error: ex.Message);
        }
    }

    private static RemoteSupportSasResult Complete(
        RemoteSupportSasProbe probe,
        string statusCode,
        string message,
        bool invoked,
        bool effectObserved,
        DateTimeOffset attemptedAt,
        int? win32Error = null,
        int? hresult = null,
        string? error = null)
    {
        var result = new RemoteSupportSasResult(statusCode, message, invoked, effectObserved, probe, attemptedAt, win32Error, hresult, error);
        UpdateDiagnostics(probe, result);
        return result;
    }

    private static async Task<bool> WaitForVisibleEffectAsync(string? desktopBefore, string? providerDesktopState, string? beforeCaptureHash, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(3);
        while (DateTimeOffset.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            await Task.Delay(250, ct).ConfigureAwait(false);
            var after = SafeEvaluateCurrentProcessDesktopState();
            if (!string.IsNullOrWhiteSpace(after) &&
                !string.Equals(after, desktopBefore, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        var state = RemoteSupportDiagnosticState.Read();
        if (!string.IsNullOrWhiteSpace(beforeCaptureHash) &&
            !string.IsNullOrWhiteSpace(state.CaptureFrameHash) &&
            !string.Equals(beforeCaptureHash, state.CaptureFrameHash, StringComparison.Ordinal))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(providerDesktopState) &&
            !string.IsNullOrWhiteSpace(state.DesktopState) &&
            !string.Equals(providerDesktopState, state.DesktopState, StringComparison.OrdinalIgnoreCase);
    }

    private static string? SafeEvaluateCurrentProcessDesktopState()
    {
        try
        {
            return RemoteSupportProviderDiagnostics.EvaluateCurrentProcess().DesktopState;
        }
        catch
        {
            return null;
        }
    }

    private static bool TryLoadSendSas(out SendSasDelegate sendSas, out string? error, out int? hresult)
    {
        sendSas = null!;
        error = null;
        hresult = null;
        try
        {
            if (!NativeLibrary.TryLoad("sas.dll", out var library))
            {
                error = "sas.dll could not be loaded.";
                return false;
            }

            if (!NativeLibrary.TryGetExport(library, "SendSAS", out var address))
            {
                error = "SendSAS export was not found in sas.dll.";
                return false;
            }

            sendSas = Marshal.GetDelegateForFunctionPointer<SendSasDelegate>(address);
            _ = SendSasFalseArgument;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            hresult = ex.HResult;
            return false;
        }
    }

    private static RemoteSupportSasPolicyInfo ReadSoftwareSasPolicy()
    {
        const string path = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";
        const string valueName = "SoftwareSASGeneration";
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(path, writable: false);
            if (key?.GetValue(valueName) is not int value)
            {
                return new RemoteSupportSasPolicyInfo(null, "not_configured", false, false, $@"HKLM\{path}\{valueName}");
            }

            return new RemoteSupportSasPolicyInfo(
                value,
                value switch
                {
                    0 => "none",
                    1 => "services",
                    2 => "ease_of_access",
                    3 => "services_and_ease_of_access",
                    _ => $"unknown_{value}"
                },
                value is 1 or 3,
                value is 2 or 3,
                $@"HKLM\{path}\{valueName}");
        }
        catch (Exception ex)
        {
            return new RemoteSupportSasPolicyInfo(null, $"read_failed:{ex.GetType().Name}", false, false, $@"HKLM\{path}\{valueName}");
        }
    }

    private static (string? Caption, string? Version, string? Build, bool IsServer) ReadOsInfo()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion", writable: false);
            var caption = key?.GetValue("ProductName")?.ToString();
            var version = key?.GetValue("DisplayVersion")?.ToString()
                ?? key?.GetValue("ReleaseId")?.ToString()
                ?? key?.GetValue("CurrentVersion")?.ToString();
            var build = key?.GetValue("CurrentBuildNumber")?.ToString();
            var installationType = key?.GetValue("InstallationType")?.ToString();
            return (caption, version, build, installationType?.Contains("Server", StringComparison.OrdinalIgnoreCase) == true || caption?.Contains("Server", StringComparison.OrdinalIgnoreCase) == true);
        }
        catch
        {
            return (RuntimeInformation.OSDescription, Environment.OSVersion.VersionString, Environment.OSVersion.Version.Build.ToString(), false);
        }
    }

    private static uint? GetActiveConsoleSessionIdOrNull()
    {
        var sessionId = WTSGetActiveConsoleSessionId();
        return sessionId == uint.MaxValue ? null : sessionId;
    }

    private static void UpdateDiagnostics(RemoteSupportSasProbe probe, RemoteSupportSasResult? result)
    {
        RemoteSupportDiagnosticState.UpdateConsoleProvider(state =>
        {
            state.RemoteSupportSasSupported = probe.ApiAvailable && probe.Policy.AllowsServices;
            state.RemoteSupportSasApiAvailable = probe.ApiAvailable;
            state.RemoteSupportSasPolicyAllowsServices = probe.Policy.AllowsServices;
            state.RemoteSupportSasProvider = probe.Backend;
            state.RemoteSupportSasTargetSessionId = probe.TargetSessionId;
            state.SoftwareSasGenerationRawValue = probe.Policy.RawValue;
            state.SoftwareSasGenerationInterpretedValue = probe.Policy.InterpretedValue;
            state.SoftwareSasAllowsServices = probe.Policy.AllowsServices;
            state.SoftwareSasAllowsEaseOfAccess = probe.Policy.AllowsEaseOfAccess;
            state.SoftwareSasPolicySource = probe.Policy.Source;
            state.RemoteSupportOsCaption = probe.OsCaption;
            state.RemoteSupportOsVersion = probe.OsVersion;
            state.RemoteSupportOsBuild = probe.OsBuild;
            state.RemoteSupportOsIsServer = probe.OsIsServer;
            if (result is not null)
            {
                state.RemoteSupportSasLastAttemptAt = result.AttemptedAt;
                state.RemoteSupportSasLastResult = result.StatusCode;
                state.RemoteSupportSasLastError = result.Error ?? result.Message;
                state.RemoteSupportSasLastWin32Error = result.Win32Error ?? probe.Win32Error;
                state.RemoteSupportSasLastHresult = result.HResult ?? probe.HResult;
            }
        });
    }

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();
}
