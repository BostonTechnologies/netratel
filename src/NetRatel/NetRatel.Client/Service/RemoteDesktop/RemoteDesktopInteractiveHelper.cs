using NetRatel.Client.Service.Logging;
using NetRatel.Client.Service.RemoteSupport;
using NetRatel.Shared.Contracts.RemoteDesktop;
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace NetRatel.Client.Service.RemoteDesktop;

internal static class RemoteDesktopInteractiveHelper
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const int StreamBufferBytes = 256 * 1024;

    public static async Task RunResidentAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await RunResidentWindowsAsync(ct).ConfigureAwait(false);
    }

    public static async Task RunAsync(string pipeName, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await RunWindowsAsync(pipeName, ct).ConfigureAwait(false);
    }

    [SupportedOSPlatform("windows")]
    private static async Task RunResidentWindowsAsync(CancellationToken ct)
    {
        LogManager.WriteLog($"[RemoteDesktopHelper] Resident helper starting sessionId={Process.GetCurrentProcess().SessionId} interactive={Environment.UserInteractive}");
        var lastWaitingLog = DateTimeOffset.MinValue;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (DateTimeOffset.UtcNow - lastWaitingLog > TimeSpan.FromSeconds(15))
                {
                    lastWaitingLog = DateTimeOffset.UtcNow;
                    LogManager.WriteLog($"[RemoteDesktopHelper] helper_pipe_waiting path={RemoteDesktopUserHelperConstants.FullPipePath}");
                }

                LogManager.WriteLog($"[RemoteDesktopHelper] helper_pipe_connecting path={RemoteDesktopUserHelperConstants.FullPipePath}");
                await using var pipe = new NamedPipeClientStream(
                    ".",
                    RemoteDesktopUserHelperConstants.PipeName,
                    PipeDirection.InOut,
                    PipeOptions.Asynchronous);
                await pipe.ConnectAsync(15000, ct).ConfigureAwait(false);
                LogManager.WriteLog($"[RemoteDesktopHelper] helper_pipe_connected path={RemoteDesktopUserHelperConstants.FullPipePath}");

                using var reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: StreamBufferBytes, leaveOpen: true);
                await using var writer = new StreamWriter(pipe, Encoding.UTF8, bufferSize: StreamBufferBytes, leaveOpen: true) { AutoFlush = true };
                var writerSync = new object();
                using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                await WriteMessageAsync(
                    writer,
                    writerSync,
                    new RemoteDesktopPipeMessage(
                        "hello",
                        JsonSerializer.Serialize(new RemoteDesktopHelperHello(
                            Process.GetCurrentProcess().SessionId,
                            Environment.ProcessId,
                            GetHelperVersion(),
                            Environment.UserInteractive,
                            GetUserName()), Json)),
                    ct).ConfigureAwait(false);
                LogManager.WriteLog("[RemoteDesktopHelper] helper_hello_sent");

                var heartbeatTask = Task.Run(() => HeartbeatLoopAsync(writer, writerSync, connectionCts.Token), connectionCts.Token);
                try
                {
                    await RunResidentControlLoopAsync(reader, writer, writerSync, connectionCts.Token).ConfigureAwait(false);
                }
                finally
                {
                    connectionCts.Cancel();
                    try
                    {
                        await heartbeatTask.ConfigureAwait(false);
                    }
                    catch
                    {
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                LogManager.WriteLog($"[RemoteDesktopHelper] Resident helper pipe loop failed: {ex.Message}");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
            }
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

    [SupportedOSPlatform("windows")]
    private static async Task RunResidentControlLoopAsync(
        StreamReader reader,
        StreamWriter writer,
        object writerSync,
        CancellationToken ct)
    {
        CancellationTokenSource? captureCts = null;
        Task? captureTask = null;
        using var remoteSupport = new RemoteSupportInteractiveWebRtcManager();

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
                if (message is null)
                {
                    continue;
                }

                if (string.Equals(message.Kind, "start", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(message.PayloadJson) &&
                    !string.IsNullOrWhiteSpace(message.StreamId))
                {
                    captureCts?.Cancel();
                    captureCts?.Dispose();
                    captureCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    var options = JsonSerializer.Deserialize<RemoteDesktopControlPayload>(message.PayloadJson, Json)
                        ?? new RemoteDesktopControlPayload(RemoteDesktopControlActions.Start, 1024, 576, 4, 40, 0);
                    var streamId = message.StreamId;
                    captureTask = string.Equals(options.Action, RemoteDesktopControlActions.Screenshot, StringComparison.OrdinalIgnoreCase)
                        ? Task.Run(() => CaptureScreenshotAsync(writer, writerSync, streamId, options, captureCts.Token), captureCts.Token)
                        : Task.Run(() => CaptureLoopAsync(writer, writerSync, streamId, options, captureCts.Token), captureCts.Token);
                    continue;
                }

                if (string.Equals(message.Kind, "stop", StringComparison.OrdinalIgnoreCase))
                {
                    captureCts?.Cancel();
                    continue;
                }

                if (string.Equals(message.Kind, RemoteSupportPipeKinds.Offer, StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(message.PayloadJson))
                {
                    var signal = JsonSerializer.Deserialize<RemoteSupportPipeSignal>(message.PayloadJson, Json);
                    if (signal is not null)
                    {
                        await remoteSupport.HandleOfferAsync(signal, writer, writerSync, ct).ConfigureAwait(false);
                    }

                    continue;
                }

                if (string.Equals(message.Kind, RemoteSupportPipeKinds.Ice, StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(message.PayloadJson))
                {
                    var signal = JsonSerializer.Deserialize<RemoteSupportPipeSignal>(message.PayloadJson, Json);
                    if (signal is not null)
                    {
                        remoteSupport.HandleIce(signal);
                    }

                    continue;
                }

                if (string.Equals(message.Kind, RemoteSupportPipeKinds.Close, StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(message.PayloadJson))
                {
                    var signal = JsonSerializer.Deserialize<RemoteSupportPipeSignal>(message.PayloadJson, Json);
                    if (signal is not null)
                    {
                        await remoteSupport.CloseSessionAsync(signal.SessionId, "Service closed remote support session.").ConfigureAwait(false);
                    }

                    continue;
                }

                if (string.Equals(message.Kind, RemoteDesktopStreamDirections.Input, StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(message.PayloadJson))
                {
                    try
                    {
                        var input = JsonSerializer.Deserialize<RemoteDesktopInputRequest>(message.PayloadJson, Json);
                        if (input is not null)
                        {
                            ApplyWindowsInput(input);
                        }
                    }
                    catch (Exception ex)
                    {
                        LogManager.WriteLog($"[RemoteDesktopHelper] Input failed: {ex}");
                        await WriteStatusAsync(writer, writerSync, message.StreamId, "input_failed", ex.Message, null, null, ct).ConfigureAwait(false);
                    }
                }
            }
        }
        finally
        {
            captureCts?.Cancel();
            if (captureTask is not null)
            {
                try
                {
                    await captureTask.ConfigureAwait(false);
                }
                catch
                {
                }
            }

            captureCts?.Dispose();
        }
    }

    [SupportedOSPlatform("windows")]
    private static async Task RunWindowsAsync(string pipeName, CancellationToken ct)
    {
        LogManager.WriteLog($"[RemoteDesktopHelper] Starting pipe={pipeName} session={Environment.UserInteractive}");
        await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(15000, ct).ConfigureAwait(false);

        using var reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: StreamBufferBytes, leaveOpen: true);
        await using var writer = new StreamWriter(pipe, Encoding.UTF8, bufferSize: StreamBufferBytes, leaveOpen: true) { AutoFlush = true };
        var firstLine = await reader.ReadLineAsync(ct).ConfigureAwait(false);
        var start = DeserializeMessage(firstLine);
        if (start is null ||
            !string.Equals(start.Kind, "start", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(start.PayloadJson))
        {
            await WriteStatusAsync(writer, "capture_failed", "Remote desktop helper did not receive a start payload.", null, null, ct).ConfigureAwait(false);
            return;
        }

        var options = JsonSerializer.Deserialize<RemoteDesktopControlPayload>(start.PayloadJson, Json)
            ?? new RemoteDesktopControlPayload(RemoteDesktopControlActions.Start, 1024, 576, 4, 40, 0);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var inputTask = Task.Run(() => ReadInputsAsync(reader, cts.Token), cts.Token);

        try
        {
            await CaptureLoopAsync(writer, options, cts.Token).ConfigureAwait(false);
        }
        finally
        {
            cts.Cancel();
            try
            {
                await inputTask.ConfigureAwait(false);
            }
            catch
            {
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static async Task CaptureLoopAsync(StreamWriter writer, RemoteDesktopControlPayload options, CancellationToken ct)
    {
        var screenWidth = GetSystemMetrics(0);
        var screenHeight = GetSystemMetrics(1);
        if (screenWidth <= 0 || screenHeight <= 0)
        {
            await WriteStatusAsync(writer, "capture_failed", "No interactive desktop was detected in the helper session.", null, null, ct).ConfigureAwait(false);
            return;
        }

        var maxWidth = Math.Clamp(options.MaxWidth ?? 1024, 320, 1280);
        var maxHeight = Math.Clamp(options.MaxHeight ?? 576, 180, 720);
        var scale = Math.Min((double)maxWidth / screenWidth, (double)maxHeight / screenHeight);
        scale = Math.Min(1.0, Math.Max(0.1, scale));
        var frameWidth = Math.Max(1, (int)Math.Round(screenWidth * scale));
        var frameHeight = Math.Max(1, (int)Math.Round(screenHeight * scale));
        var delay = TimeSpan.FromMilliseconds(1000.0 / Math.Clamp(options.TargetFps ?? 4, 1, 10));

        await WriteStatusAsync(writer, "started", "Remote desktop helper capture started.", frameWidth, frameHeight, ct).ConfigureAwait(false);
        LogManager.WriteLog($"[RemoteDesktopHelper] Capture started source={screenWidth}x{screenHeight} frame={frameWidth}x{frameHeight}");

        while (!ct.IsCancellationRequested)
        {
            var jpeg = await CaptureJpegWithRetryAsync(
                null,
                screenWidth,
                screenHeight,
                frameWidth,
                frameHeight,
                Math.Clamp(options.JpegQuality ?? 40, 25, 80),
                ct).ConfigureAwait(false);
            var payload = new RemoteDesktopFramePayload(
                frameWidth,
                frameHeight,
                options.Monitor ?? 0,
                "jpeg",
                Convert.ToBase64String(jpeg),
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            await writer.WriteLineAsync(JsonSerializer.Serialize(new RemoteDesktopPipeMessage(
                RemoteDesktopStreamDirections.Frame,
                JsonSerializer.Serialize(payload, Json)), Json)).ConfigureAwait(false);
            await Task.Delay(delay, ct).ConfigureAwait(false);
        }
    }

    [SupportedOSPlatform("windows")]
    private static async Task CaptureScreenshotAsync(
        StreamWriter writer,
        object writerSync,
        string streamId,
        RemoteDesktopControlPayload options,
        CancellationToken ct)
    {
        try
        {
            var screenWidth = GetSystemMetrics(0);
            var screenHeight = GetSystemMetrics(1);
            if (screenWidth <= 0 || screenHeight <= 0)
            {
                await WriteStatusAsync(writer, writerSync, streamId, "capture_failed", "No interactive desktop was detected in the helper session.", null, null, ct).ConfigureAwait(false);
                return;
            }

            var (frameWidth, frameHeight) = CalculateFrameSize(
                screenWidth,
                screenHeight,
                Math.Clamp(options.MaxWidth ?? 1024, 320, 1280),
                Math.Clamp(options.MaxHeight ?? 576, 180, 720));

            await WriteStatusAsync(writer, writerSync, streamId, "started", "Remote desktop screenshot capture started.", frameWidth, frameHeight, ct).ConfigureAwait(false);
            LogManager.WriteLog($"[RemoteDesktopHelper] Screenshot capture started streamId={streamId} source={screenWidth}x{screenHeight} frame={frameWidth}x{frameHeight}");

            var jpeg = await CaptureJpegWithRetryAsync(
                streamId,
                screenWidth,
                screenHeight,
                frameWidth,
                frameHeight,
                Math.Clamp(options.JpegQuality ?? 50, 25, 80),
                ct).ConfigureAwait(false);
            var payload = new RemoteDesktopFramePayload(
                frameWidth,
                frameHeight,
                options.Monitor ?? 0,
                "jpeg",
                Convert.ToBase64String(jpeg),
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Sequence: 1);
            var payloadJson = JsonSerializer.Serialize(payload, Json);
            await WriteMessageAsync(
                    writer,
                    writerSync,
                    new RemoteDesktopPipeMessage(RemoteDesktopStreamDirections.Frame, payloadJson, streamId),
                    ct)
                .ConfigureAwait(false);
            LogManager.WriteLog($"[RemoteDesktopTrace] stage=helper_screenshot_pipe_send_success streamId={streamId} bytes={payloadJson.Length} jpegBytes={jpeg.Length} width={frameWidth} height={frameHeight}");
            await WriteStatusAsync(writer, writerSync, streamId, "stopped", "Remote desktop screenshot captured.", frameWidth, frameHeight, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await WriteStatusAsync(writer, writerSync, streamId, "stopped", "Remote desktop screenshot capture stopped.", null, null, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogManager.WriteLog($"[RemoteDesktopHelper] Screenshot capture failed streamId={streamId}: {ex}");
            await WriteStatusAsync(writer, writerSync, streamId, "capture_failed", ex.Message, null, null, CancellationToken.None).ConfigureAwait(false);
        }
    }

    [SupportedOSPlatform("windows")]
    private static async Task CaptureLoopAsync(
        StreamWriter writer,
        object writerSync,
        string streamId,
        RemoteDesktopControlPayload options,
        CancellationToken ct)
    {
        try
        {
            var screenWidth = GetSystemMetrics(0);
            var screenHeight = GetSystemMetrics(1);
            if (screenWidth <= 0 || screenHeight <= 0)
            {
                await WriteStatusAsync(writer, writerSync, streamId, "capture_failed", "No interactive desktop was detected in the helper session.", null, null, ct).ConfigureAwait(false);
                return;
            }

            var (frameWidth, frameHeight) = CalculateFrameSize(
                screenWidth,
                screenHeight,
                Math.Clamp(options.MaxWidth ?? 1024, 320, 1280),
                Math.Clamp(options.MaxHeight ?? 576, 180, 720));
            var delay = TimeSpan.FromMilliseconds(1000.0 / Math.Clamp(options.TargetFps ?? 4, 1, 10));
            var frames = 0L;
            var bytes = 0L;
            var dropped = 0L;
            var lastStatsAt = DateTimeOffset.UtcNow;
            var frameQueue = Channel.CreateBounded<HelperQueuedFrame>(new BoundedChannelOptions(1)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = true
            });

            await WriteStatusAsync(writer, writerSync, streamId, "started", "Remote desktop helper capture started.", frameWidth, frameHeight, ct).ConfigureAwait(false);
            LogManager.WriteLog($"[RemoteDesktopHelper] Capture started streamId={streamId} source={screenWidth}x{screenHeight} frame={frameWidth}x{frameHeight}");
            var writerTask = Task.Run(
                () => WriteQueuedFramesAsync(writer, writerSync, streamId, frameQueue.Reader, ct),
                ct);

            while (!ct.IsCancellationRequested)
            {
                var jpeg = await CaptureJpegWithRetryAsync(
                    streamId,
                    screenWidth,
                    screenHeight,
                    frameWidth,
                    frameHeight,
                    Math.Clamp(options.JpegQuality ?? 40, 25, 80),
                    ct).ConfigureAwait(false);
                bytes += jpeg.Length;
                frames++;
                var payload = new RemoteDesktopFramePayload(
                    frameWidth,
                    frameHeight,
                    options.Monitor ?? 0,
                    "jpeg",
                    Convert.ToBase64String(jpeg),
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    Sequence: (ulong)frames);
                var payloadJson = JsonSerializer.Serialize(payload, Json);
                if (ShouldTraceFrame(frames))
                {
                    LogManager.WriteLog($"[RemoteDesktopTrace] stage=helper_frame_encoded streamId={streamId} seq={frames} bytes={payloadJson.Length} jpegBytes={jpeg.Length} width={frameWidth} height={frameHeight}");
                }

                if (frameQueue.Reader.Count > 0)
                {
                    dropped++;
                }

                frameQueue.Writer.TryWrite(new HelperQueuedFrame((ulong)frames, payloadJson, payloadJson.Length, frameWidth, frameHeight));

                var now = DateTimeOffset.UtcNow;
                if (now - lastStatsAt >= TimeSpan.FromSeconds(5))
                {
                    lastStatsAt = now;
                    LogManager.WriteLog($"[RemoteDesktopPerf] streamId={streamId} helperCapturedSeq={frames} bytes={bytes} queued={frameQueue.Reader.Count} dropped={dropped}");
                }

                await Task.Delay(delay, ct).ConfigureAwait(false);
            }

            frameQueue.Writer.TryComplete();
            await writerTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await WriteStatusAsync(writer, writerSync, streamId, "stopped", "Remote desktop helper capture stopped.", null, null, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogManager.WriteLog($"[RemoteDesktopHelper] Capture failed streamId={streamId}: {ex}");
            await WriteStatusAsync(writer, writerSync, streamId, "capture_failed", ex.Message, null, null, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static async Task WriteQueuedFramesAsync(
        StreamWriter writer,
        object writerSync,
        string streamId,
        ChannelReader<HelperQueuedFrame> reader,
        CancellationToken ct)
    {
        while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
        {
            HelperQueuedFrame? latest = null;
            while (reader.TryRead(out var queued))
            {
                latest = queued;
            }

            if (latest is null)
            {
                continue;
            }

            try
            {
                await WriteMessageAsync(
                    writer,
                    writerSync,
                    new RemoteDesktopPipeMessage(
                        RemoteDesktopStreamDirections.Frame,
                        latest.PayloadJson,
                        streamId),
                    ct).ConfigureAwait(false);
                if (ShouldTraceFrame((long)latest.Sequence))
                {
                    LogManager.WriteLog($"[RemoteDesktopTrace] stage=helper_pipe_send_success streamId={streamId} seq={latest.Sequence} bytes={latest.Bytes} width={latest.Width} height={latest.Height}");
                }
            }
            catch (Exception ex)
            {
                LogManager.WriteLog($"[RemoteDesktopTrace] stage=helper_pipe_send_failed streamId={streamId} seq={latest.Sequence} bytes={latest.Bytes} error={ex.Message}");
                throw;
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static async Task ReadInputsAsync(StreamReader reader, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
            if (line is null)
            {
                return;
            }

            var message = DeserializeMessage(line);
            if (message is null ||
                !string.Equals(message.Kind, RemoteDesktopStreamDirections.Input, StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(message.PayloadJson))
            {
                continue;
            }

            try
            {
                var input = JsonSerializer.Deserialize<RemoteDesktopInputRequest>(message.PayloadJson, Json);
                if (input is not null)
                {
                    ApplyWindowsInput(input);
                }
            }
            catch (Exception ex)
            {
                LogManager.WriteLog($"[RemoteDesktopHelper] Input failed: {ex}");
            }
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
            return JsonSerializer.Deserialize<RemoteDesktopPipeMessage>(line, Json);
        }
        catch
        {
            return null;
        }
    }

    private static Task WriteStatusAsync(StreamWriter writer, string status, string? message, int? width, int? height, CancellationToken ct)
    {
        var payload = new RemoteDesktopStatusPayload(status, message, width, height, 0);
        return writer.WriteLineAsync(JsonSerializer.Serialize(new RemoteDesktopPipeMessage(
            RemoteDesktopStreamDirections.Status,
            JsonSerializer.Serialize(payload, Json)), Json).AsMemory(), ct);
    }

    private static Task WriteStatusAsync(
        StreamWriter writer,
        object writerSync,
        string? streamId,
        string status,
        string? message,
        int? width,
        int? height,
        CancellationToken ct)
    {
        var payload = new RemoteDesktopStatusPayload(status, message, width, height, 0);
        return WriteMessageAsync(
            writer,
            writerSync,
            new RemoteDesktopPipeMessage(
                RemoteDesktopStreamDirections.Status,
                JsonSerializer.Serialize(payload, Json),
                streamId),
            ct);
    }

    private static Task WriteMessageAsync(StreamWriter writer, object writerSync, RemoteDesktopPipeMessage message, CancellationToken ct)
    {
        var line = JsonSerializer.Serialize(message, Json);
        lock (writerSync)
        {
            writer.WriteLine(line);
            writer.Flush();
        }

        return Task.CompletedTask;
    }

    private static bool ShouldTraceFrame(long sequence) => sequence <= 3 || sequence % 200 == 0;

    private static (int Width, int Height) CalculateFrameSize(int screenWidth, int screenHeight, int maxWidth, int maxHeight)
    {
        var scale = Math.Min((double)maxWidth / screenWidth, (double)maxHeight / screenHeight);
        scale = Math.Min(1.0, Math.Max(0.1, scale));
        return (
            Math.Max(1, (int)Math.Round(screenWidth * scale)),
            Math.Max(1, (int)Math.Round(screenHeight * scale)));
    }

    private sealed record HelperQueuedFrame(
        ulong Sequence,
        string PayloadJson,
        int Bytes,
        int Width,
        int Height);

    private static string GetHelperVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(RemoteDesktopInteractiveHelper).Assembly;
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "Unknown";
    }

    private static string GetUserName()
    {
        var domain = Environment.UserDomainName;
        var user = Environment.UserName;
        if (string.IsNullOrWhiteSpace(domain))
        {
            return string.IsNullOrWhiteSpace(user) ? "unknown" : user;
        }

        return $"{domain}\\{user}";
    }

    [SupportedOSPlatform("windows")]
    private static byte[] CaptureJpeg(int sourceWidth, int sourceHeight, int frameWidth, int frameHeight, int quality)
    {
        using var source = new Bitmap(sourceWidth, sourceHeight, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(source))
        {
            graphics.CopyFromScreen(0, 0, 0, 0, new Size(sourceWidth, sourceHeight), CopyPixelOperation.SourceCopy);
        }

        using var scaled = new Bitmap(frameWidth, frameHeight, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(scaled))
        {
            graphics.DrawImage(source, 0, 0, frameWidth, frameHeight);
        }

        using var ms = new MemoryStream();
        var codec = ImageCodecInfo.GetImageEncoders().FirstOrDefault(x => string.Equals(x.MimeType, "image/jpeg", StringComparison.OrdinalIgnoreCase));
        if (codec is null)
        {
            scaled.Save(ms, ImageFormat.Jpeg);
        }
        else
        {
            using var parameters = new EncoderParameters(1);
            parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);
            scaled.Save(ms, codec, parameters);
        }

        return ms.ToArray();
    }

    [SupportedOSPlatform("windows")]
    private static async Task<byte[]> CaptureJpegWithRetryAsync(
        string? streamId,
        int sourceWidth,
        int sourceHeight,
        int frameWidth,
        int frameHeight,
        int quality,
        CancellationToken ct)
    {
        const int attempts = 3;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                return CaptureJpeg(sourceWidth, sourceHeight, frameWidth, frameHeight, quality);
            }
            catch (Exception ex) when (attempt < attempts && !ct.IsCancellationRequested)
            {
                LogManager.WriteLog($"[RemoteDesktopTrace] stage=helper_capture_retry streamId={streamId ?? string.Empty} attempt={attempt} error={ex.Message}");
                await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), ct).ConfigureAwait(false);
            }
        }

        return CaptureJpeg(sourceWidth, sourceHeight, frameWidth, frameHeight, quality);
    }

    [SupportedOSPlatform("windows")]
    private static void ApplyWindowsInput(RemoteDesktopInputRequest input)
    {
        if (input.Type is RemoteDesktopInputTypes.MouseMove or RemoteDesktopInputTypes.MouseDown or RemoteDesktopInputTypes.MouseUp)
        {
            var x = (int)Math.Round(Math.Clamp(input.X ?? 0, 0, 1) * Math.Max(1, GetSystemMetrics(0) - 1));
            var y = (int)Math.Round(Math.Clamp(input.Y ?? 0, 0, 1) * Math.Max(1, GetSystemMetrics(1) - 1));
            SetCursorPos(x, y);
        }

        switch (input.Type)
        {
            case RemoteDesktopInputTypes.MouseDown:
                SendMouseButton(input.Button ?? 0, true);
                break;
            case RemoteDesktopInputTypes.MouseUp:
                SendMouseButton(input.Button ?? 0, false);
                break;
            case RemoteDesktopInputTypes.MouseWheel:
                SendMouse(MouseEventFlags.Wheel, -(input.DeltaY ?? 0));
                break;
            case RemoteDesktopInputTypes.KeyDown:
                SendKey(input.Key, true);
                break;
            case RemoteDesktopInputTypes.KeyUp:
                SendKey(input.Key, false);
                break;
        }
    }

    [SupportedOSPlatform("windows")]
    private static void SendMouseButton(int button, bool down)
    {
        var flags = button switch
        {
            1 => down ? MouseEventFlags.RightDown : MouseEventFlags.RightUp,
            2 => down ? MouseEventFlags.MiddleDown : MouseEventFlags.MiddleUp,
            _ => down ? MouseEventFlags.LeftDown : MouseEventFlags.LeftUp
        };
        SendMouse(flags, 0);
    }

    [SupportedOSPlatform("windows")]
    private static void SendMouse(MouseEventFlags flags, int data)
    {
        var input = new INPUT
        {
            type = 0,
            U = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    dwFlags = flags,
                    mouseData = data
                }
            }
        };
        _ = SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    [SupportedOSPlatform("windows")]
    private static void SendKey(string? key, bool down)
    {
        var vk = MapVirtualKey(key);
        if (vk == 0)
        {
            return;
        }

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
        _ = SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    private static ushort MapVirtualKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return 0;
        }

        if (key.Length == 1)
        {
            var ch = char.ToUpperInvariant(key[0]);
            if (ch is >= 'A' and <= 'Z' or >= '0' and <= '9')
            {
                return ch;
            }
        }

        return key switch
        {
            "Enter" => 0x0D,
            "Escape" => 0x1B,
            "Backspace" => 0x08,
            "Tab" => 0x09,
            " " or "Space" => 0x20,
            "ArrowLeft" => 0x25,
            "ArrowUp" => 0x26,
            "ArrowRight" => 0x27,
            "ArrowDown" => 0x28,
            "Delete" => 0x2E,
            "Home" => 0x24,
            "End" => 0x23,
            "PageUp" => 0x21,
            "PageDown" => 0x22,
            _ => 0
        };
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

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
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public int mouseData;
        public MouseEventFlags dwFlags;
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

    [Flags]
    private enum MouseEventFlags : uint
    {
        LeftDown = 0x0002,
        LeftUp = 0x0004,
        RightDown = 0x0008,
        RightUp = 0x0010,
        MiddleDown = 0x0020,
        MiddleUp = 0x0040,
        Wheel = 0x0800
    }
}

internal sealed record RemoteDesktopPipeMessage(string Kind, string? PayloadJson, string? StreamId = null);

internal sealed record RemoteDesktopHelperHello(
    int SessionId,
    int ProcessId,
    string Version,
    bool UserInteractive,
    string User);
