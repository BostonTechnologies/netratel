using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using NetRatel.Shared.Contracts.ComputerUse;

namespace NetRatel.Client.Service.ComputerUse;

/// <summary>Private stdio MCP client with a pinned executable and a per-lease, exact-window capability manifest.</summary>
internal sealed class CuaDriverComputerUseProvider(ComputerUseOptions options) : IComputerUseProvider
{
    internal const string DriverVersion = "0.38.0";
    internal const string DriverExecutableSha256 = "ffa58d88779ee219304b0c307f4cfa970fe822f830c6ecf9254eda5c29e22bf2";
    internal const string ProtocolVersion = "2025-06-18";
    private static readonly string[] AllowedTools = ["list_windows", "start_session", "end_session", "get_window_state", "click", "type_text", "press_key", "scroll"];
    private static readonly string[] Actions = [ComputerUseActionKinds.Invoke, ComputerUseActionKinds.Click, ComputerUseActionKinds.Type, ComputerUseActionKinds.Key, ComputerUseActionKinds.Scroll];
    private readonly HashSet<string> _elementTokens = new(StringComparer.Ordinal);
    private McpClient? _client;
    private StdioClientTransport? _transport;
    private FileStream? _verifiedExecutable;
    private string? _manifestDirectory;
    private string? _captureId;
    private ComputerUseScreenshot? _screenshot;
    private Guid _sessionId;
    private bool _sessionStarted;

    public async Task<ComputerUseResult> ExecuteAsync(ComputerUseCommand command, CancellationToken cancellationToken = default)
    {
        await EnsureConnectedAsync(command, cancellationToken).ConfigureAwait(false);
        if (command.Operation is ComputerUseOperations.Open or ComputerUseOperations.Observe or ComputerUseOperations.Act && !_sessionStarted)
        {
            await CallAsync("start_session", new Dictionary<string, object?> { ["session"] = SessionLabel, ["capture_scope"] = "window" }, cancellationToken).ConfigureAwait(false);
            _sessionStarted = true;
        }
        switch (command.Operation)
        {
            case ComputerUseOperations.Capabilities:
                return Result(command, ComputerUseStatuses.Completed) with
                {
                    Capabilities = new(command.Provider, true,
                        [ComputerUseOperations.ListWindows, ComputerUseOperations.Open, ComputerUseOperations.Observe, ComputerUseOperations.Act],
                        Actions, DriverVersion, _client!.NegotiatedProtocolVersion)
                };
            case ComputerUseOperations.ListWindows:
            {
                var args = new Dictionary<string, object?> { ["on_screen_only"] = true };
                if (command.Target.ProcessId is { } pid) args["pid"] = pid;
                var response = await CallAsync("list_windows", args, cancellationToken).ConfigureAwait(false);
                var data = RequireStructured(response);
                if (!data.TryGetProperty("windows", out var windows) || windows.ValueKind != JsonValueKind.Array)
                    throw new ComputerUseRefusalException("provider_invalid_result", "Driver did not return a window inventory.");
                List<ComputerUseWindow> result = [];
                foreach (var window in windows.EnumerateArray().Take(128))
                {
                    if (!TryInt(window, "pid", out var processId) || processId <= 0 || !TryLong(window, "window_id", out var windowId) || windowId <= 0) continue;
                    if (command.Target.ProcessId is { } selected && selected != processId) continue;
                    result.Add(new(processId, windowId.ToString(CultureInfo.InvariantCulture), Text(window, "title", 256) ?? "", Text(window, "app_name", 128)));
                }
                return Result(command, ComputerUseStatuses.Observed) with { Windows = result };
            }
            case ComputerUseOperations.Open:
                return Result(command, ComputerUseStatuses.Observed) with { Observation = await ObserveAsync(command, cancellationToken).ConfigureAwait(false) };
            case ComputerUseOperations.Observe:
                return Result(command, ComputerUseStatuses.Observed) with { Observation = await ObserveAsync(command, cancellationToken).ConfigureAwait(false) };
            case ComputerUseOperations.Act:
                return await ActAsync(command, cancellationToken).ConfigureAwait(false);
            default:
                return ComputerUseResult.Refused(command, "unsupported_operation");
        }
    }

    private async Task EnsureConnectedAsync(ComputerUseCommand command, CancellationToken cancellationToken)
    {
        if (_client is not null) return;
        if (string.IsNullOrWhiteSpace(options.CuaDriverPath) || !Path.IsPathFullyQualified(options.CuaDriverPath) || !File.Exists(options.CuaDriverPath))
            throw new ComputerUseRefusalException("driver_not_installed", "Install the pinned Cua Driver sidecar before selecting this provider.");
        // Keep the verified file open without write/delete sharing for the child's entire lifetime.
        _verifiedExecutable = new FileStream(options.CuaDriverPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(_verifiedExecutable, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
        if (!string.Equals(hash, DriverExecutableSha256, StringComparison.Ordinal))
            throw new ComputerUseRefusalException("driver_integrity_mismatch", "The Cua Driver executable does not match the pinned build.");
        _sessionId = command.SessionId;
        _manifestDirectory = Directory.CreateTempSubdirectory("netratel-cua-").FullName;
        var manifestPath = Path.Combine(_manifestDirectory, "capabilities.json");
        var windows = command.Target.IsWindowTarget
            ? new[] { new { pid = command.Target.ProcessId!.Value, window_id = ParseWindowId(command.Target) } }
            : [];
        var listing = command.Operation == ComputerUseOperations.ListWindows;
        var permittedTools = listing ? ["list_windows", "end_session"] : command.ControlAllowed ? AllowedTools :
            AllowedTools.Where(tool => tool is "list_windows" or "get_window_state" or "start_session" or "end_session").ToArray();
        var manifest = JsonSerializer.Serialize(new
        {
            version = 3,
            expires_after = "15m",
            idle_timeout = "60s",
            resources = new { desktop = new { windows, applications = listing && command.Target.ProcessId.HasValue ? new[] { command.Target.ProcessId.Value } : Array.Empty<int>(), display = listing && !command.Target.ProcessId.HasValue } },
            allow = new { tools = permittedTools }
        });
        await File.WriteAllTextAsync(manifestPath, manifest, cancellationToken).ConfigureAwait(false);
        var environment = StdioClientTransportOptions.GetDefaultEnvironmentVariables();
        environment["CUA_DRIVER_PERMISSION_MODE"] = "bounded";
        environment["CUA_DRIVER_CAPABILITY_MANIFEST_FILE"] = manifestPath;
        environment["CUA_DRIVER_CAPABILITY_MANIFEST_APPROVED"] = "1";
        // --direct never attaches to an existing user's daemon; no HTTP port or inherited credentials.
        _transport = new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "NetRatel Cua Driver",
            Command = options.CuaDriverPath,
            Arguments = ["mcp", "--direct"],
            WorkingDirectory = _manifestDirectory,
            InheritEnvironmentVariables = false,
            EnvironmentVariables = environment,
            ShutdownTimeout = TimeSpan.FromSeconds(2)
        });
        _client = await McpClient.CreateAsync(_transport, new McpClientOptions
        {
            ProtocolVersion = ProtocolVersion,
            InitializationTimeout = TimeSpan.FromSeconds(10)
        }, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!string.Equals(_client.NegotiatedProtocolVersion, ProtocolVersion, StringComparison.Ordinal))
            throw new ComputerUseRefusalException("driver_protocol_mismatch", "Driver did not negotiate the supported MCP protocol revision.");
        var discovered = await _client.ListToolsAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var required = permittedTools;
        if (required.Any(name => discovered.All(tool => !string.Equals(tool.Name, name, StringComparison.Ordinal))))
            throw new ComputerUseRefusalException("driver_capability_missing", "The pinned Driver did not advertise the required bounded toolset.");
    }

    private async Task<ComputerUseObservation> ObserveAsync(ComputerUseCommand command, CancellationToken cancellationToken)
    {
        var args = WindowArguments(command.Target);
        args["include_screenshot"] = command.IncludeScreenshot;
        args["include_accessibility_tree"] = true;
        args["tree_format"] = "elements";
        args["max_elements"] = 128;
        args["max_depth"] = 12;
        args["max_image_dimension"] = 1280;
        args["timeout_ms"] = 1500;
        var response = await CallAsync("get_window_state", args, cancellationToken).ConfigureAwait(false);
        var observation = ParseObservation(command.Target, response);
        if (command.IncludeScreenshot && observation.Screenshot is null)
            throw new ComputerUseRefusalException("capture_unavailable", "Driver did not return a valid window screenshot.");
        return observation;
    }

    internal ComputerUseObservation ParseObservation(ComputerUseTarget target, CallToolResult response)
    {
        var data = RequireStructured(response);
        if (!TryInt(data, "pid", out var pid) || pid != target.ProcessId ||
            !TryLong(data, "window_id", out var windowId) || windowId != ParseWindowId(target))
            throw new ComputerUseRefusalException("provider_wrong_target", "Driver returned state for another process or window.");
        var state = data.GetRawText();
        if (state.Length > ComputerUseContract.MaximumStateLength)
            throw new ComputerUseRefusalException("provider_result_too_large", "Driver's observation exceeds the state budget.");
        List<ComputerUseElement> elements = [];
        _elementTokens.Clear();
        if (data.TryGetProperty("elements", out var raw) && raw.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in raw.EnumerateArray().Take(128))
            {
                var token = Text(item, "element_token", 256);
                if (string.IsNullOrWhiteSpace(token)) continue;
                _elementTokens.Add(token);
                elements.Add(new(token, Text(item, "role", 128) ?? "unknown", Text(item, "label", 512), Text(item, "value", 4096)));
            }
        }
        _captureId = Text(data, "capture_id", 256);
        _screenshot = null;
        var explicitlyInvalid = data.TryGetProperty("screenshot_frame_valid", out var valid) && valid.ValueKind == JsonValueKind.False;
        if (!explicitlyInvalid && response.Content.OfType<ImageContentBlock>().FirstOrDefault() is { } image &&
            TryInt(data, "screenshot_width", out var width) && width is > 0 and <= 1280 &&
            TryInt(data, "screenshot_height", out var height) && height is > 0 and <= 1280 &&
            image.MimeType == "image/png" && image.Data.Length <= 2 * 1024 * 1024)
            _screenshot = new(image.MimeType, Encoding.UTF8.GetString(image.Data.Span), width, height);
        return new(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, target, state, elements, _screenshot);
    }

    internal (string Tool, Dictionary<string, object?> Arguments) BuildActionRequest(ComputerUseCommand command)
    {
        var action = command.Action!;
        var args = WindowArguments(command.Target);
        args["delivery_mode"] = "background";
        string tool;
        if (action.ElementId is { } element)
        {
            if (!_elementTokens.Contains(element)) throw new ComputerUseRefusalException("stale_element", "Select an element from the current observation.");
            args["element_token"] = element;
        }
        switch (action.Kind)
        {
            case ComputerUseActionKinds.Invoke:
                if (action.ElementId is null) throw new ComputerUseRefusalException("element_required", "Select a semantic element from the current observation.");
                tool = "click";
                break;
            case ComputerUseActionKinds.Type:
                if (action.ElementId is null) throw new ComputerUseRefusalException("element_required", "Type into a semantic element from the current observation.");
                tool = "type_text";
                args["text"] = action.Text;
                break;
            case ComputerUseActionKinds.Key:
                tool = "press_key";
                args["key"] = DriverKey(action.Key);
                break;
            case ComputerUseActionKinds.Click:
                tool = "click";
                if (!TryAddPixelArguments(action, args)) throw new ComputerUseRefusalException("fresh_screenshot_required", "Pixel actions require a fresh screenshot and coordinates within it.");
                args["capture_id"] = _captureId;
                args["button"] = action.Button ?? "left";
                break;
            case ComputerUseActionKinds.Scroll:
                tool = "scroll";
                if (!TryAddPixelArguments(action, args)) throw new ComputerUseRefusalException("fresh_screenshot_required", "Pixel actions require a fresh screenshot and coordinates within it.");
                var horizontal = action.ScrollX.GetValueOrDefault() != 0;
                var delta = horizontal ? action.ScrollX!.Value : action.ScrollY.GetValueOrDefault();
                if (delta == 0 || Math.Abs((long)delta) > 50 || horizontal && action.ScrollY.GetValueOrDefault() != 0)
                    throw new ComputerUseRefusalException("invalid_scroll", "Select one scroll axis and at most fifty lines.");
                args["direction"] = horizontal ? delta > 0 ? "right" : "left" : delta > 0 ? "down" : "up";
                args["by"] = "line";
                args["amount"] = Math.Abs(delta);
                break;
            default: throw new ComputerUseRefusalException("unsupported_action", "Unsupported action.");
        }

        return (tool, args);
    }

    private async Task<ComputerUseResult> ActAsync(ComputerUseCommand command, CancellationToken cancellationToken)
    {
        var (tool, args) = BuildActionRequest(command);
        // Invalidate locally before dispatch. A lost/cancelled response must never be retried with old handles.
        _elementTokens.Clear();
        _captureId = null;
        _screenshot = null;
        CallToolResult actionResult;
        try
        {
            actionResult = await _client!.CallToolAsync(tool, args, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Result(command, ComputerUseStatuses.Cancelled, "action_cancelled_outcome_unknown", true);
        }
        var result = MapActionResult(command, actionResult);
        // Refusal/partial/unknown effects require re-observation, with no automatic mutation replay.
        try
        {
            return result with { Observation = await ObserveAsync(command, cancellationToken).ConfigureAwait(false) };
        }
        catch (Exception exception) when (exception is ComputerUseRefusalException or OperationCanceledException or IOException or ModelContextProtocol.McpException)
        {
            return result with { Code = result.Code ?? "readback_unavailable" };
        }
    }

    internal static ComputerUseResult MapActionResult(ComputerUseCommand command, CallToolResult response)
    {
        var data = response.StructuredContent;
        var effect = data is { } json ? Text(json, "effect", 64) : null;
        var code = data is { } structured ? Text(structured, "code", 128) : null;
        if (code is null && data is { } errorEnvelope && errorEnvelope.TryGetProperty("error", out var driverError))
            code = Text(driverError, "code", 128);
        var failed = response.IsError == true || effect == "refused";
        // A tool's claimed success alone is insufficient; only explicit value/window evidence confirms an effect.
        var verified = !failed && effect == "confirmed" && data is { } payload &&
            payload.TryGetProperty("evidence", out var evidence) && evidence.ValueKind == JsonValueKind.Array &&
            evidence.EnumerateArray().Any(item => Text(item, "kind", 64) is "value_readback" or "window_change");
        if (failed)
            return Result(command, effect == "refused" ? ComputerUseStatuses.Refused : ComputerUseStatuses.Unknown,
                code ?? "provider_refused", effect != "refused");
        return Result(command, verified ? ComputerUseStatuses.Verified : ComputerUseStatuses.AppliedUnverified,
            verified ? "provider_effect_verified" : effect ?? "effect_unverified", true);
    }

    private bool TryAddPixelArguments(ComputerUseAction action, Dictionary<string, object?> args)
    {
        if (string.IsNullOrEmpty(_captureId) || _screenshot is null || action.X is not { } x || action.Y is not { } y ||
            x < 0 || y < 0 || x >= _screenshot.Width || y >= _screenshot.Height) return false;
        args["x"] = x;
        args["y"] = y;
        return true;
    }
    private Dictionary<string, object?> WindowArguments(ComputerUseTarget target) => new()
    {
        ["pid"] = target.ProcessId!.Value,
        ["window_id"] = ParseWindowId(target),
        ["session"] = SessionLabel
    };
    private string SessionLabel => "netratel-" + _sessionId.ToString("N");
    private static long ParseWindowId(ComputerUseTarget target) =>
        long.TryParse(target.WindowId, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 ? id :
            throw new ComputerUseRefusalException("invalid_window", "Select a window from this provider's inventory.");
    private async Task<CallToolResult> CallAsync(string name, IReadOnlyDictionary<string, object?> args, CancellationToken cancellationToken)
    {
        if (!AllowedTools.Contains(name, StringComparer.Ordinal)) throw new InvalidOperationException("Tool is outside the NetRatel allowlist.");
        var response = await _client!.CallToolAsync(name, args, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (response.IsError == true)
            throw new ComputerUseRefusalException(response.StructuredContent is { } json ? Text(json, "code", 128) ?? "provider_refused" : "provider_refused", "Cua Driver refused the bounded operation.");
        return response;
    }
    private static JsonElement RequireStructured(CallToolResult response) => response.StructuredContent is { ValueKind: JsonValueKind.Object } value ? value :
        throw new ComputerUseRefusalException("provider_invalid_result", "Driver did not return structured state.");
    private static string? Text(JsonElement json, string name, int maxLength)
    {
        if (json.ValueKind != JsonValueKind.Object || !json.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String) return null;
        var result = value.GetString();
        return result?.Length > maxLength ? result[..maxLength] : result;
    }
    private static bool TryInt(JsonElement json, string name, out int value) { value = 0; return json.TryGetProperty(name, out var item) && item.TryGetInt32(out value); }
    private static bool TryLong(JsonElement json, string name, out long value) { value = 0; return json.TryGetProperty(name, out var item) && item.TryGetInt64(out value); }
    private static string DriverKey(string? key) => key switch
    {
        "Enter" => "return", "Tab" => "tab", "Escape" => "escape", "Backspace" => "backspace", "Delete" => "delete",
        "Space" => "space", "ArrowUp" => "up", "ArrowDown" => "down", "ArrowLeft" => "left", "ArrowRight" => "right",
        "Home" => "home", "End" => "end", "PageUp" => "pageup", "PageDown" => "pagedown",
        _ => throw new ComputerUseRefusalException("unsupported_key", "The requested key is outside the bounded key allowlist.")
    };
    private static ComputerUseResult Result(ComputerUseCommand command, string status, string? code = null, bool mayHaveApplied = false) =>
        new(ComputerUseContract.Version, command.OperationId, command.SessionId, status, code, Provider: command.Provider, MayHaveApplied: mayHaveApplied);

    public async ValueTask DisposeAsync()
    {
        _elementTokens.Clear();
        _captureId = null;
        _screenshot = null;
        try
        {
            if (_client is not null)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                try { await _client.CallToolAsync("end_session", new Dictionary<string, object?> { ["session"] = SessionLabel }, cancellationToken: timeout.Token).ConfigureAwait(false); }
                catch (Exception exception) when (exception is OperationCanceledException or IOException or ModelContextProtocol.McpException)
                {
                    ComputerUseDiagnostics.Record("driver_session_end_failed", exception);
                }
                await _client.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _client = null;
            _transport = null;
            if (_verifiedExecutable is not null) await _verifiedExecutable.DisposeAsync().ConfigureAwait(false);
            if (_manifestDirectory is not null)
            {
                try { Directory.Delete(_manifestDirectory, recursive: true); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    ComputerUseDiagnostics.Record("driver_manifest_cleanup_failed", exception);
                }
            }
        }
    }
}
