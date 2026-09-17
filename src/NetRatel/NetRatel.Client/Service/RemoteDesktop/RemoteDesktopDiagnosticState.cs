using NetRatel.Client.Service.Logging;
using System;
using System.IO;
using System.Text.Json;

namespace NetRatel.Client.Service.RemoteDesktop;

internal static class RemoteDesktopDiagnosticState
{
    private static readonly object Sync = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static string StatePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "NetRatel",
            "Client",
            "remote-desktop-state.json");

    public static void Update(Action<State> update)
    {
        try
        {
            lock (Sync)
            {
                var state = ReadCurrent();
                state.UpdatedUtc = DateTimeOffset.UtcNow;
                update(state);
                Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
                File.WriteAllText(StatePath, JsonSerializer.Serialize(state, Json));
            }
        }
        catch (Exception ex)
        {
            LogManager.WriteLog($"[RemoteDesktop] Diagnostic state update failed: {ex.Message}");
        }
    }

    public static State Read() => ReadCurrent();

    private static State ReadCurrent()
    {
        try
        {
            if (!File.Exists(StatePath))
            {
                return new State();
            }

            return JsonSerializer.Deserialize<State>(File.ReadAllText(StatePath), Json) ?? new State();
        }
        catch
        {
            return new State();
        }
    }

    public sealed class State
    {
        public DateTimeOffset UpdatedUtc { get; set; }
        public bool PipeHostStarted { get; set; }
        public bool HelperConnected { get; set; }
        public int? HelperPid { get; set; }
        public int? HelperSessionId { get; set; }
        public string? HelperUser { get; set; }
        public string? HelperVersion { get; set; }
        public DateTimeOffset? HelperConnectedUtc { get; set; }
        public string? LatestStreamId { get; set; }
        public string? LatestStage { get; set; }
        public ulong? LatestFrameSequence { get; set; }
        public int? LatestFrameBytes { get; set; }
        public int? LatestFrameWidth { get; set; }
        public int? LatestFrameHeight { get; set; }
        public string? LatestError { get; set; }
    }
}
