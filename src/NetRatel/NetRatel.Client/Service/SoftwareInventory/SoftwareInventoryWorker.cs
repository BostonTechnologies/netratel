using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NetRatel.Client.Service.Gateway;
using NetRatel.Shared.Contracts.SoftwareInventory;

namespace NetRatel.Client.Service.SoftwareInventory;

/// <summary>Presence-scoped poller with an installation-local atomic outbox. Delete only after matching acknowledgement.</summary>
public sealed class SoftwareInventoryWorker(HttpClient http, string stateDirectory, Action<string> log,
    ISoftwareInventoryCollector? collector = null, TimeSpan? pollingInterval = null)
{
    private readonly ISoftwareInventoryCollector _collector = collector ?? (OperatingSystem.IsWindows()
        ? new WindowsSoftwareInventoryCollector() : OperatingSystem.IsLinux() ? new LinuxSoftwareInventoryCollector() : new UnsupportedSoftwareInventoryCollector());

    public async Task RunAsync(GatewayPresenceSession session, string accessToken, CancellationToken ct)
    {
        var directory = Path.Combine(stateDirectory, "software-inventory", session.TenantId + "-" + session.AgentId.ToString("N"));
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var path = Path.Combine(directory, "pending.json");
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (File.Exists(path))
                {
                    var info = new FileInfo(path);
                    if (info.Length > 128 * 1024 * 1024) throw new InvalidDataException("Inventory outbox exceeds its limit.");
                    var queued = JsonSerializer.Deserialize<PendingInventory>(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false), SoftwareInventoryWire.JsonOptions)
                        ?? throw new InvalidDataException("Invalid inventory outbox.");
                    var acknowledged = true;
                    foreach (var chunk in queued.Chunks)
                    {
                        using var request = Request("/api/v2/agent/software-inventory/chunks", chunk);
                        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
                        if (response.StatusCode == HttpStatusCode.Conflict)
                        {
                            var rejected = await response.Content.ReadFromJsonAsync<SoftwareChunkAcknowledgement>(cancellationToken: ct).ConfigureAwait(false);
                            if (rejected is { Accepted: false, Status: "StaleCollection" or "UnknownCollection" } && rejected.CollectionId == chunk.CollectionId)
                            { log("A superseded software scan was retired."); break; }
                        }
                        response.EnsureSuccessStatusCode();
                        var ack = await response.Content.ReadFromJsonAsync<SoftwareChunkAcknowledgement>(cancellationToken: ct).ConfigureAwait(false);
                        if (ack is null || !ack.Accepted || ack.CollectionId != chunk.CollectionId || ack.Index != chunk.Index)
                        { acknowledged = false; break; }
                    }
                    if (acknowledged) File.Delete(path);
                }
                else
                {
                    using var request = Request("/api/v2/agent/software-inventory/work", null);
                    using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();
                    var work = await response.Content.ReadFromJsonAsync<SoftwareScanWork>(cancellationToken: ct).ConfigureAwait(false);
                    if (work?.CollectionId is { } id)
                    {
                        var collection = await _collector.CollectAsync(ct).ConfigureAwait(false);
                        var queued = new PendingInventory(SoftwareInventoryWire.Encode(id, collection, DateTimeOffset.UtcNow));
                        var temporary = path + ".tmp";
                        await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 8192,
                            FileOptions.Asynchronous | FileOptions.WriteThrough))
                        {
                            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                            await JsonSerializer.SerializeAsync(stream, queued, SoftwareInventoryWire.JsonOptions, ct).ConfigureAwait(false);
                            await stream.FlushAsync(ct).ConfigureAwait(false);
                            stream.Flush(true);
                        }
                        File.Move(temporary, path, true);
                        continue;
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception error) when (error is HttpRequestException or IOException or JsonException or ArgumentException or OperationCanceledException)
            { log("Software inventory delivery is pending; it will retry after reconnect or backoff (" + error.GetType().Name + ")."); }
            await Task.Delay(pollingInterval ?? TimeSpan.FromSeconds(20 + Random.Shared.Next(10)), ct).ConfigureAwait(false);
        }

        HttpRequestMessage Request(string route, SoftwareChunkUpload? upload)
        {
            var message = new HttpRequestMessage(HttpMethod.Post, route);
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.GetAccessToken(accessToken));
            if (upload is not null) message.Content = JsonContent.Create(upload);
            return message;
        }
    }

    public sealed record PendingInventory(IReadOnlyList<SoftwareChunkUpload> Chunks);
}
