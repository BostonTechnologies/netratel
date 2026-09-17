using NetRatel.Client.Service.Logging;
using NetRatel.Client.Service.RemoteDesktop;
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Security.Principal;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NetRatel.Client.Service.RemoteSupport;

internal static class RemoteSupportConsoleHelper
{
    private static readonly JsonSerializerOptions ConsoleJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private static readonly JsonSerializerOptions PipeJson = new(JsonSerializerDefaults.Web);
    private const int StreamBufferBytes = 256 * 1024;

    public static Task RunAsync(bool selfTest, bool captureSelfTest, bool captureBackendMatrix, bool sessionLaunchSelfTest, bool inputSelfTest, bool moveMouseProbe, bool inputVisualFeedbackSelfTest, string? typeProbe, bool sasSelfTest, bool sendSas, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Task.CompletedTask;
        }

#pragma warning disable CA1416
        if (sessionLaunchSelfTest)
        {
            return RunSessionLaunchSelfTestWindowsAsync(ct);
        }

        if (inputSelfTest)
        {
            return RunInputSelfTestWindowsAsync(moveMouseProbe, ct);
        }

        if (inputVisualFeedbackSelfTest)
        {
            return RunInputVisualFeedbackSelfTestWindowsAsync(moveMouseProbe, typeProbe, ct);
        }

        if (sasSelfTest)
        {
            return RunSasSelfTestWindowsAsync(sendSas, ct);
        }

        if (captureSelfTest || captureBackendMatrix)
        {
            return RunCaptureSelfTestWindowsAsync(captureBackendMatrix, ct);
        }

        return selfTest ? RunSelfTestWindowsAsync(ct) : RunWindowsWithExceptionLoggingAsync(ct);
#pragma warning restore CA1416
    }

    [SupportedOSPlatform("windows")]
    private static async Task RunWindowsWithExceptionLoggingAsync(CancellationToken ct)
    {
        try
        {
            await RunWindowsAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            WriteExceptionLog(ex);
            LogManager.WriteLog($"[RemoteSupportConsoleHelper] Fatal startup/runtime exception: {ex}");
            throw;
        }
    }

    [SupportedOSPlatform("windows")]
    private static async Task RunWindowsAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var decision = RemoteSupportProviderDiagnostics.EvaluateCurrentProcess();
        decision = decision with
        {
            Provider = RemoteSupportProviderKinds.ConsoleSecureDesktopHelper,
            SupportLevel = RemoteSupportSupportLevels.MediaPreview,
            StatusCode = RemoteSupportStatusCodes.ConsoleSecureDesktopMediaPreviewAvailable,
            Message = "Console secure-desktop helper is connected for media preview.",
            MediaSupported = true
        };

        LogManager.WriteLog($"[RemoteSupportConsoleHelper] Starting provider pipe client provider={decision.Provider} desktopState={decision.DesktopState} supportLevel={decision.SupportLevel} status={decision.StatusCode}");

        await using var pipe = new NamedPipeClientStream(
            ".",
            RemoteSupportConsoleProviderConstants.PipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await pipe.ConnectAsync(15000, ct).ConfigureAwait(false);

        using var reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: StreamBufferBytes, leaveOpen: true);
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), bufferSize: StreamBufferBytes, leaveOpen: true) { AutoFlush = true };
        var writerSync = new object();
        LogManager.WriteLog("[RemoteSupportConsoleHelper] console_provider_hello_build_started");
        var hello = new RemoteSupportConsoleProviderHello(
            Process.GetCurrentProcess().SessionId,
            Environment.ProcessId,
            GetCurrentVersion(),
            RemoteSupportProviderKinds.ConsoleSecureDesktopHelper,
            decision.DesktopState,
            decision.InputDesktopName,
            decision.ActiveConsoleSessionId,
            Process.GetCurrentProcess().SessionId == unchecked((int)(decision.ActiveConsoleSessionId ?? uint.MaxValue)),
            Native.GetProcessWindowStationName(),
            Native.GetCurrentThreadDesktopName());
        var helloJson = JsonSerializer.Serialize(hello, PipeJson);
        LogManager.WriteLog($"[RemoteSupportConsoleHelper] console_provider_hello_json={helloJson}");
        await WriteMessageAsync(
            writer,
            writerSync,
            new RemoteDesktopPipeMessage(
                "hello",
                helloJson),
            ct).ConfigureAwait(false);
        LogManager.WriteLog("[RemoteSupportConsoleHelper] console_provider_hello_written");
        await writer.FlushAsync().ConfigureAwait(false);
        LogManager.WriteLog("[RemoteSupportConsoleHelper] console_provider_hello_flush_completed");

        using var remoteSupport = new RemoteSupportInteractiveWebRtcManager(new RemoteSupportProviderMetadata(
            RemoteSupportProviderKinds.ConsoleSecureDesktopHelper,
            decision.DesktopState,
            RemoteSupportSupportLevels.MediaPreview,
            decision.InputDesktopName,
            decision.ReconnectRequired),
            request => WriteSasRequest(writer, writerSync, request));
        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var heartbeatTask = Task.Run(() => HeartbeatLoopAsync(writer, writerSync, heartbeatCts.Token), heartbeatCts.Token);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null)
                {
                    return;
                }

                var message = DeserializeMessage(line);
                if (message is null || string.IsNullOrWhiteSpace(message.PayloadJson))
                {
                    continue;
                }

                if (string.Equals(message.Kind, RemoteSupportPipeKinds.Offer, StringComparison.OrdinalIgnoreCase))
                {
                    var signal = JsonSerializer.Deserialize<RemoteSupportPipeSignal>(message.PayloadJson, PipeJson);
                    if (signal is not null)
                    {
                        await remoteSupport.HandleOfferAsync(signal, writer, writerSync, ct).ConfigureAwait(false);
                    }

                    continue;
                }

                if (string.Equals(message.Kind, RemoteSupportPipeKinds.Ice, StringComparison.OrdinalIgnoreCase))
                {
                    var signal = JsonSerializer.Deserialize<RemoteSupportPipeSignal>(message.PayloadJson, PipeJson);
                    if (signal is not null)
                    {
                        remoteSupport.HandleIce(signal);
                    }

                    continue;
                }

                if (string.Equals(message.Kind, RemoteSupportPipeKinds.Close, StringComparison.OrdinalIgnoreCase))
                {
                    var signal = JsonSerializer.Deserialize<RemoteSupportPipeSignal>(message.PayloadJson, PipeJson);
                    if (signal is not null)
                    {
                        await remoteSupport.CloseSessionAsync(signal.SessionId, "Remote support session closed.").ConfigureAwait(false);
                    }

                    continue;
                }

                if (string.Equals(message.Kind, RemoteSupportPipeKinds.SasResponse, StringComparison.OrdinalIgnoreCase))
                {
                    var response = JsonSerializer.Deserialize<RemoteSupportSasPipeResponse>(message.PayloadJson, PipeJson);
                    if (response is not null)
                    {
                        LogManager.WriteLog($"[RemoteSupportConsoleHelper] SAS response session={response.SessionId} request={response.RequestId} status={response.StatusCode} message={response.Message}");
                    }
                }
            }
        }
        finally
        {
            heartbeatCts.Cancel();
            try
            {
                await heartbeatTask.ConfigureAwait(false);
            }
            catch
            {
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static Task RunSasSelfTestWindowsAsync(bool send, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var result = new RemoteSupportSasController().RunSelfTest(send);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            result.StatusCode,
            result.Message,
            result.Invoked,
            result.EffectObserved,
            backend = result.Probe.Backend,
            apiAvailable = result.Probe.ApiAvailable,
            targetSessionId = result.Probe.TargetSessionId,
            policyRaw = result.Probe.Policy.RawValue,
            policy = result.Probe.Policy.InterpretedValue,
            allowsServices = result.Probe.Policy.AllowsServices,
            allowsEaseOfAccess = result.Probe.Policy.AllowsEaseOfAccess,
            policySource = result.Probe.Policy.Source,
            osCaption = result.Probe.OsCaption,
            osVersion = result.Probe.OsVersion,
            osBuild = result.Probe.OsBuild,
            osIsServer = result.Probe.OsIsServer,
            win32Error = result.Win32Error,
            hresult = result.HResult,
            error = result.Error
        }, ConsoleJson));
        return Task.CompletedTask;
    }

    private static void WriteSasRequest(StreamWriter writer, object writerSync, RemoteSupportSasPipeRequest request)
    {
        lock (writerSync)
        {
            writer.WriteLine(JsonSerializer.Serialize(
                new RemoteDesktopPipeMessage(
                    RemoteSupportPipeKinds.SasRequest,
                    JsonSerializer.Serialize(request, PipeJson)),
                PipeJson));
            writer.Flush();
        }
    }

    private static RemoteDesktopPipeMessage? DeserializeMessage(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<RemoteDesktopPipeMessage>(line, PipeJson);
        }
        catch (Exception ex)
        {
            LogManager.WriteLog($"[RemoteSupportConsoleHelper] Ignoring malformed pipe message: {ex.Message}");
            return null;
        }
    }

    private static async Task HeartbeatLoopAsync(StreamWriter writer, object writerSync, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false);
            await WriteMessageAsync(writer, writerSync, new RemoteDesktopPipeMessage("heartbeat", null), ct).ConfigureAwait(false);
        }
    }

    private static Task WriteMessageAsync(StreamWriter writer, object writerSync, RemoteDesktopPipeMessage message, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        lock (writerSync)
        {
            writer.WriteLine(JsonSerializer.Serialize(message, PipeJson));
            writer.Flush();
        }

        return Task.CompletedTask;
    }

    [SupportedOSPlatform("windows")]
    private static async Task RunSelfTestWindowsAsync(CancellationToken ct)
    {
        static void Write(string key, object? value) => Console.WriteLine($"{key}: {value}");
        Write("mode", "remote-support-console-helper-self-test");
        Write("logPath", GetLogDirectory());
        Write("pipePath", RemoteSupportConsoleProviderConstants.FullPipePath);
        var decision = RemoteSupportProviderDiagnostics.EvaluateCurrentProcess();
        Write("desktopProbeResult", $"{decision.DesktopState}; capture={decision.CaptureAvailable}; input={decision.InputAvailable}; error={decision.DiagnosticError ?? "<none>"}");
        var hello = new RemoteSupportConsoleProviderHello(
            Process.GetCurrentProcess().SessionId,
            Environment.ProcessId,
            GetCurrentVersion(),
            RemoteSupportProviderKinds.ConsoleSecureDesktopHelper,
            decision.DesktopState,
            decision.InputDesktopName,
            decision.ActiveConsoleSessionId,
            Process.GetCurrentProcess().SessionId == unchecked((int)(decision.ActiveConsoleSessionId ?? uint.MaxValue)),
            Native.GetProcessWindowStationName(),
            Native.GetCurrentThreadDesktopName());
        var helloJson = JsonSerializer.Serialize(hello, PipeJson);
        Write("helloJsonValid", CanDeserializeHello(helloJson) ? "yes" : "no");
        var connected = false;
        var helloWriteAttempted = false;
        try
        {
            await using var pipe = new NamedPipeClientStream(
                ".",
                RemoteSupportConsoleProviderConstants.PipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            await pipe.ConnectAsync(2000, ct).ConfigureAwait(false);
            connected = true;
            await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), bufferSize: StreamBufferBytes, leaveOpen: true) { AutoFlush = true };
            helloWriteAttempted = true;
            await writer.WriteLineAsync(JsonSerializer.Serialize(new RemoteDesktopPipeMessage("hello", helloJson), PipeJson)).ConfigureAwait(false);
            await writer.FlushAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Write("selfTestError", ex.Message);
        }

        Write("canConnectToPipe", connected ? "yes" : "no");
        Write("helloWriteAttempted", helloWriteAttempted ? "yes" : "no");
        Write("processWillStayResident", "no");
    }

    private static bool CanDeserializeHello(string helloJson)
    {
        try
        {
            return JsonSerializer.Deserialize<RemoteSupportConsoleProviderHello>(helloJson, PipeJson) is not null;
        }
        catch
        {
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private static Task RunSessionLaunchSelfTestWindowsAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        static void Write(string key, object? value) => Console.WriteLine($"{key}: {value}");
        var decision = RemoteSupportProviderDiagnostics.EvaluateCurrentProcess();
        var processSessionId = Process.GetCurrentProcess().SessionId;
        var activeConsoleSessionId = decision.ActiveConsoleSessionId;
        Write("mode", "remote-support-console-helper-session-launch-self-test");
        Write("processSessionId", processSessionId);
        Write("activeConsoleSessionId", activeConsoleSessionId?.ToString() ?? "<unknown>");
        Write("tokenSessionId", processSessionId);
        Write("processUser", WindowsIdentity.GetCurrent().Name);
        Write("windowStation", Native.GetProcessWindowStationName() ?? "<unknown>");
        Write("threadDesktop", Native.GetCurrentThreadDesktopName() ?? "<unknown>");
        Write("inputDesktopName", decision.InputDesktopName ?? "<unknown>");
        Write("canOpenWinSta0", CanOpenWinSta0(out var winSta0, out var winStaError) ? "yes" : $"no; win32={winStaError}");
        Write("canSetProcessWindowStation", CanSetProcessWindowStation(winSta0, out var setWinStaError) ? "yes" : $"no; win32={setWinStaError}");
        Write("canOpenWinlogonDesktop", CanOpenWinlogonDesktop(out var desktop, out var desktopName, out var desktopError) ? $"yes; name={desktopName ?? "<unknown>"}" : $"no; win32={desktopError}");
        var setThreadDesktop = CanSetThreadDesktop(desktop, out var setDesktopError);
        Write("canSetThreadDesktop", setThreadDesktop ? "yes" : $"no; win32={setDesktopError}");
        Write("canCreateGdiDc", CanCreateGdiDc(out var gdiError) ? "yes" : $"no; win32={gdiError}");
        Write("canRunMessagePump", "yes");
        Write("exitCode", 0);
        if (desktop != IntPtr.Zero && !setThreadDesktop)
        {
            Native.CloseDesktop(desktop);
        }

        if (winSta0 != IntPtr.Zero)
        {
            Native.CloseWindowStation(winSta0);
        }

        return Task.CompletedTask;
    }

    [SupportedOSPlatform("windows")]
    private static bool CanOpenWinSta0(out IntPtr station, out int error)
    {
        station = Native.OpenWindowStation("WinSta0", false, Native.WinstaAllAccess);
        error = station == IntPtr.Zero ? System.Runtime.InteropServices.Marshal.GetLastWin32Error() : 0;
        return station != IntPtr.Zero;
    }

    [SupportedOSPlatform("windows")]
    private static bool CanSetProcessWindowStation(IntPtr station, out int error)
    {
        if (station == IntPtr.Zero)
        {
            error = 0;
            return false;
        }

        var current = Native.GetProcessWindowStation();
        var ok = Native.SetProcessWindowStation(station);
        error = ok ? 0 : System.Runtime.InteropServices.Marshal.GetLastWin32Error();
        if (current != IntPtr.Zero)
        {
            _ = Native.SetProcessWindowStation(current);
        }

        return ok;
    }

    [SupportedOSPlatform("windows")]
    private static bool CanOpenWinlogonDesktop(out IntPtr desktop, out string? desktopName, out int error)
    {
        desktop = Native.TryOpenDesktop("Winlogon", out desktopName, out error);
        return desktop != IntPtr.Zero;
    }

    [SupportedOSPlatform("windows")]
    private static bool CanSetThreadDesktop(IntPtr desktop, out int error)
    {
        if (desktop == IntPtr.Zero)
        {
            error = 0;
            return false;
        }

        var ok = Native.SetThreadDesktop(desktop);
        error = ok ? 0 : System.Runtime.InteropServices.Marshal.GetLastWin32Error();
        return ok;
    }

    [SupportedOSPlatform("windows")]
    private static bool CanCreateGdiDc(out int error)
    {
        var screen = Native.GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero)
        {
            error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            return false;
        }

        var dc = Native.CreateCompatibleDC(screen);
        error = dc == IntPtr.Zero ? System.Runtime.InteropServices.Marshal.GetLastWin32Error() : 0;
        if (dc != IntPtr.Zero)
        {
            Native.DeleteDC(dc);
        }

        Native.ReleaseDC(IntPtr.Zero, screen);
        return dc != IntPtr.Zero;
    }

    [SupportedOSPlatform("windows")]
    private static Task RunInputSelfTestWindowsAsync(bool moveMouseProbe, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        static void Write(string key, object? value) => Console.WriteLine($"{key}: {value}");
        var decision = RemoteSupportProviderDiagnostics.EvaluateCurrentProcess();
        var threadDesktopBefore = Native.GetCurrentThreadDesktopName();
        var inputDesktop = Native.TryOpenInputDesktop(out var inputDesktopName, out var inputDesktopError);
        var setThreadDesktopSucceeded = false;
        var setThreadDesktopError = 0;
        if (inputDesktop != IntPtr.Zero)
        {
            setThreadDesktopSucceeded = Native.SetThreadDesktop(inputDesktop);
            setThreadDesktopError = setThreadDesktopSucceeded ? 0 : Marshal.GetLastWin32Error();
        }

        var sendInputAvailable = ProbeSendInput(moveMouseProbe, out var sendInputError, out var sendInputHresult);
        Write("mode", "remote-support-console-helper-input-self-test");
        Write("processSessionId", Process.GetCurrentProcess().SessionId);
        Write("activeConsoleSessionId", decision.ActiveConsoleSessionId?.ToString() ?? "<unknown>");
        Write("provider", RemoteSupportProviderKinds.ConsoleSecureDesktopHelper);
        Write("desktopName", inputDesktopName ?? decision.InputDesktopName ?? "<unknown>");
        Write("threadDesktopBefore", threadDesktopBefore ?? "<unknown>");
        Write("threadDesktopAfter", Native.GetCurrentThreadDesktopName() ?? "<unknown>");
        Write("setThreadDesktopSucceeded", setThreadDesktopSucceeded ? "yes" : "no");
        Write("inputDesktopOpenSucceeded", inputDesktop != IntPtr.Zero ? "yes" : "no");
        Write("mouseMoveProbeAvailable", sendInputAvailable ? "yes" : "no");
        Write("keyboardProbeAvailable", sendInputAvailable ? "yes" : "no");
        Write("sendInputAvailable", sendInputAvailable ? "yes" : "no");
        Write("lastWin32Error", sendInputError != 0 ? sendInputError : inputDesktopError != 0 ? inputDesktopError : setThreadDesktopError);
        Write("hresult", sendInputHresult != 0 ? $"0x{sendInputHresult:X8}" : setThreadDesktopError != 0 ? $"0x{Marshal.GetHRForLastWin32Error():X8}" : "<none>");
        if (inputDesktop != IntPtr.Zero && !setThreadDesktopSucceeded)
        {
            Native.CloseDesktop(inputDesktop);
        }

        return Task.CompletedTask;
    }

    [SupportedOSPlatform("windows")]
    private static bool ProbeSendInput(bool moveMouseProbe, out int error, out int hresult)
    {
        error = 0;
        hresult = 0;
        var input = new INPUT
        {
            type = 0,
            U = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    dx = 0,
                    dy = 0,
                    dwFlags = moveMouseProbe ? 0x0001u : 0u
                }
            }
        };
        var sent = SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
        if (sent == 1)
        {
            return true;
        }

        error = Marshal.GetLastWin32Error();
        hresult = Marshal.GetHRForLastWin32Error();
        return false;
    }

    [SupportedOSPlatform("windows")]
    private static async Task RunInputVisualFeedbackSelfTestWindowsAsync(bool moveMouseProbe, string? typeProbe, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        static void Write(string key, object? value) => Console.WriteLine($"{key}: {value}");
        var decision = RemoteSupportProviderDiagnostics.EvaluateCurrentProcess();
        Write("mode", "remote-support-console-helper-input-visual-feedback-self-test");
        Write("processSessionId", Process.GetCurrentProcess().SessionId);
        Write("activeConsoleSessionId", decision.ActiveConsoleSessionId?.ToString() ?? "<unknown>");
        Write("provider", RemoteSupportProviderKinds.ConsoleSecureDesktopHelper);
        Write("desktopName", decision.InputDesktopName ?? Native.GetCurrentThreadDesktopName() ?? "<unknown>");
        Write("browserRenderAckAvailable", "no");
        using var provider = new ConsoleSecureDesktopCaptureProvider("input-visual-feedback-self-test");
        var before = provider.CaptureFrame(1024, 576);
        var beforeHash = ComputeFrameHash(before.Bgr);
        Write("captureBeforeHash", beforeHash);
        Write("captureBackend", before.BackendName);
        Write("frameEncoded", "not_applicable");
        Write("frameSent", "not_applicable");
        var inputAttempted = false;
        var inputSucceeded = false;
        var typeProbeLength = 0;
        var typeProbeCategories = "<none>";
        if (moveMouseProbe)
        {
            inputAttempted = true;
            inputSucceeded = ProbeSendInput(moveMouseProbe: true, out var moveError, out var moveHresult);
            Write("moveMouseProbeAttempted", "yes");
            Write("moveMouseProbeSucceeded", inputSucceeded ? "yes" : "no");
            Write("moveMouseProbeWin32Error", moveError);
            Write("moveMouseProbeHresult", moveHresult == 0 ? "<none>" : $"0x{moveHresult:X8}");
        }
        else
        {
            Write("moveMouseProbeAttempted", "no");
        }

        if (!string.IsNullOrEmpty(typeProbe))
        {
            inputAttempted = true;
            typeProbeLength = typeProbe.Length;
            typeProbeCategories = DescribeTypeProbe(typeProbe);
            inputSucceeded = TypeProbe(typeProbe, out var sent, out var typeError) || inputSucceeded;
            Write("typeProbeAttempted", "yes");
            Write("typeProbeLength", typeProbeLength);
            Write("typeProbeCategories", typeProbeCategories);
            Write("typeProbeSendInputCount", sent);
            Write("typeProbeWin32Error", typeError);
        }
        else
        {
            Write("typeProbeAttempted", "no");
        }

        if (inputAttempted)
        {
            await Task.Delay(250, ct).ConfigureAwait(false);
        }

        provider.Refresh();
        var after = provider.CaptureFrame(1024, 576);
        var afterHash = ComputeFrameHash(after.Bgr);
        Write("captureAfterHash", afterHash);
        Write("captureChanged", string.Equals(beforeHash, afterHash, StringComparison.Ordinal) ? "no" : "yes");
        Write("inputAttempted", inputAttempted ? "yes" : "no");
        Write("inputSucceeded", inputSucceeded ? "yes" : "no");
        Write("typedTextLogged", "no");
    }

    private static string ComputeFrameHash(byte[] frame)
    {
        var hash = SHA256.HashData(frame);
        return Convert.ToHexString(hash, 0, 6).ToLowerInvariant();
    }

    [SupportedOSPlatform("windows")]
    private static bool TypeProbe(string typeProbe, out int sent, out int error)
    {
        sent = 0;
        error = 0;
        foreach (var ch in typeProbe)
        {
            var vk = char.ToUpperInvariant(ch) switch
            {
                >= 'A' and <= 'Z' => (ushort)char.ToUpperInvariant(ch),
                >= '0' and <= '9' => (ushort)ch,
                ' ' => (ushort)0x20,
                _ => (ushort)0
            };
            if (vk == 0)
            {
                continue;
            }

            sent += SendVirtualKey(vk, down: true, out error);
            sent += SendVirtualKey(vk, down: false, out error);
        }

        return sent > 0 && error == 0;
    }

    [SupportedOSPlatform("windows")]
    private static int SendVirtualKey(ushort vk, bool down, out int error)
    {
        var input = new INPUT
        {
            type = 1,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = vk,
                    dwFlags = down ? 0u : 2u
                }
            }
        };
        var result = SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
        error = result == 0 ? Marshal.GetLastWin32Error() : 0;
        return unchecked((int)result);
    }

    private static string DescribeTypeProbe(string typeProbe)
    {
        var letters = typeProbe.Count(char.IsLetter);
        var digits = typeProbe.Count(char.IsDigit);
        var spaces = typeProbe.Count(char.IsWhiteSpace);
        var punctuation = typeProbe.Count(ch => char.IsPunctuation(ch) || char.IsSymbol(ch));
        return $"letters={letters},digits={digits},spaces={spaces},punctuation={punctuation}";
    }

    [SupportedOSPlatform("windows")]
    private static Task RunCaptureSelfTestWindowsAsync(bool backendMatrix, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        static void Write(string key, object? value) => Console.WriteLine($"{key}: {value}");
        var decision = RemoteSupportProviderDiagnostics.EvaluateCurrentProcess();
        Write("mode", backendMatrix ? "remote-support-console-helper-capture-backend-matrix" : "remote-support-console-helper-capture-self-test");
        Write("sessionId", Process.GetCurrentProcess().SessionId);
        Write("activeConsoleSessionId", decision.ActiveConsoleSessionId?.ToString() ?? "<unknown>");
        Write("desktopName", decision.InputDesktopName ?? "<unknown>");
        Write("logPath", GetLogDirectory());
        Write("diagnosticsPath", GetDiagnosticsDirectory());
        Write("captureProviderSelected", nameof(ConsoleSecureDesktopCaptureProvider));
        using var provider = new ConsoleSecureDesktopCaptureProvider("capture-self-test");
        var result = provider.RunOneShotSelfTest();
        Write("backend", result.BackendName);
        Write("threadDesktopBefore", result.ThreadDesktopBefore ?? "<unknown>");
        Write("threadDesktopAfter", result.ThreadDesktopAfter ?? "<unknown>");
        Write("oneShotCaptureAttempted", "yes");
        Write("oneShotCaptureSucceeded", result.Succeeded ? "yes" : "no");
        Write("frameSize", result.FrameWidth.HasValue && result.FrameHeight.HasValue ? $"{result.FrameWidth}x{result.FrameHeight}" : "<none>");
        Write("frameBytes", result.FrameBytes?.ToString() ?? "<none>");
        Write("lastWin32Error", result.Win32Error?.ToString() ?? "<none>");
        Write("outputJpegPath", result.OutputPath ?? "<none>");
        Write("error", result.Error ?? "<none>");
        if (backendMatrix && result.Matrix is not null)
        {
            foreach (var backend in result.Matrix.Results)
            {
                Write(
                    $"backend.{backend.BackendName}",
                    $"success={(backend.FirstFrameSucceeded ? "yes" : "no")} available={(backend.BackendAvailable ? "yes" : "no")} init={(backend.BackendInitSucceeded ? "yes" : "no")} desktop={backend.TargetDesktop ?? "<unknown>"} threadBefore={backend.ThreadDesktopBefore ?? "<unknown>"} threadAfter={backend.ThreadDesktopAfter ?? "<unknown>"} win32={backend.Win32Error?.ToString() ?? "<none>"} hresult={backend.HResult?.ToString() ?? "<none>"} error={backend.Exception ?? backend.BackendInitError ?? "<none>"}");
            }

            Write("bestBackend", result.Matrix.BestBackend?.BackendName ?? "none");
            Write("failedBackends", string.Join(",", result.Matrix.Results.Where(x => !x.FirstFrameSucceeded).Select(x => x.BackendName)));
        }
        return Task.CompletedTask;
    }

    public static string GetLogDirectory()
    {
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (string.IsNullOrWhiteSpace(programData))
        {
            programData = Path.Combine(Path.GetTempPath(), "NetRatel");
        }

        return Path.Combine(programData, "NetRatel", "Client", "logs", "remote-support-console-provider");
    }

    public static string GetDiagnosticsDirectory()
    {
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (string.IsNullOrWhiteSpace(programData))
        {
            programData = Path.Combine(Path.GetTempPath(), "NetRatel");
        }

        return Path.Combine(programData, "NetRatel", "Client", "diagnostics");
    }

    public static string GetCaptureSelfTestPath() =>
        Path.Combine(GetDiagnosticsDirectory(), "console-capture-test.jpg");

    private static void WriteExceptionLog(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(GetLogDirectory());
            var path = Path.Combine(GetLogDirectory(), $"console-provider-exception-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Environment.ProcessId}.log");
            File.WriteAllText(
                path,
                $"utc: {DateTimeOffset.UtcNow:O}{Environment.NewLine}sessionId: {Process.GetCurrentProcess().SessionId}{Environment.NewLine}processId: {Environment.ProcessId}{Environment.NewLine}{ex}");
        }
        catch
        {
        }
    }

    private static string GetCurrentVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(RemoteSupportConsoleHelper).Assembly;
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "Unknown";
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public MOUSEINPUT mi;
        [FieldOffset(0)]
        public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public int mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }
}
