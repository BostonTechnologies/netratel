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
    private static readonly Lazy<SoftwareInventoryJsonContext> OutboxMetadata = new(
        () => new(new JsonSerializerOptions(SoftwareInventoryWire.JsonOptions)));

    public async Task RunAsync(GatewayPresenceSession session, string accessToken, CancellationToken ct)
    {
        var directory = Path.Combine(stateDirectory, "software-inventory", session.TenantId + "-" + session.AgentId.ToString("N"));
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var path = Path.Combine(directory, "pending.json");
        while (!ct.IsCancellationRequested)
        {
            var retry = false;
            try
            {
                if (File.Exists(path))
                {
                    var info = new FileInfo(path);
                    if (info.Length > 128 * 1024 * 1024) throw new InvalidDataException("Inventory outbox exceeds its limit.");
                    var queued = JsonSerializer.Deserialize(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false), OutboxMetadata.Value.PendingInventory)
                        ?? throw new InvalidDataException("Invalid inventory outbox.");
                    var acknowledged = true;
                    foreach (var chunk in queued.Chunks)
                    {
                        using var request = Request("/api/v2/agent/software-inventory/chunks", chunk);
                        using var response = await SendAsync(request).ConfigureAwait(false);
                        if (response.StatusCode == HttpStatusCode.Conflict)
                        {
                            var rejected = await response.Content.ReadFromJsonAsync(SoftwareInventoryJsonContext.Default.SoftwareChunkAcknowledgement, cancellationToken: ct).ConfigureAwait(false);
                            if (rejected is { Accepted: false, Status: "StaleCollection" or "UnknownCollection" } &&
                                rejected.CollectionId == chunk.CollectionId && rejected.Index == chunk.Index)
                            { log("A superseded or expired software scan was retired."); break; }
                        }
                        if (!response.IsSuccessStatusCode)
                            log($"Software inventory upload {chunk.Index + 1}/{chunk.Count} received HTTP {(int)response.StatusCode}; the durable outbox is retained.");
                        response.EnsureSuccessStatusCode();
                        var ack = await response.Content.ReadFromJsonAsync(SoftwareInventoryJsonContext.Default.SoftwareChunkAcknowledgement, cancellationToken: ct).ConfigureAwait(false);
                        if (ack is null || !ack.Accepted || ack.CollectionId != chunk.CollectionId || ack.Index != chunk.Index)
                        { acknowledged = false; retry = true; break; }
                    }
                    if (acknowledged) { File.Delete(path); continue; }
                }
                else
                {
                    using var request = Request("/api/v2/agent/software-inventory/work", null);
                    using var response = await SendAsync(request).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();
                    var work = await response.Content.ReadFromJsonAsync(SoftwareInventoryJsonContext.Default.SoftwareScanWork, cancellationToken: ct).ConfigureAwait(false);
                    if (work?.CollectionId is { } id)
                    {
                        SoftwareCollection collection;
                        using (var collectionTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
                        {
                            collectionTimeout.CancelAfter(TimeSpan.FromMinutes(2));
                            try { collection = await _collector.CollectAsync(collectionTimeout.Token).ConfigureAwait(false); }
                            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                            {
                                collection = new([], false, OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux" : "unsupported", ["collection_timeout"]);
                                log("Software inventory collection reached its deadline; reporting a failed attempt without replacing the last complete snapshot.");
                            }
                        }
                        var queued = new PendingInventory(SoftwareInventoryWire.Encode(id, collection, DateTimeOffset.UtcNow));
                        var temporary = path + ".tmp";
                        await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 8192,
                            FileOptions.Asynchronous | FileOptions.WriteThrough))
                        {
                            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                            await JsonSerializer.SerializeAsync(stream, queued, OutboxMetadata.Value.PendingInventory, ct).ConfigureAwait(false);
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
            {
                retry = true;
                log("Software inventory delivery is pending; it will retry after reconnect or backoff (" + error.GetType().Name + ").");
            }
            // Discovery never collects unless the server issues work. Keep healthy interactive discovery short,
            // but back off transport/validation failures rather than retrying uploads at the interactive cadence.
            await Task.Delay(pollingInterval ?? TimeSpan.FromSeconds(retry ? 20 + Random.Shared.Next(10) : 5 + Random.Shared.Next(3)), ct).ConfigureAwait(false);
        }

        async Task<HttpResponseMessage> SendAsync(HttpRequestMessage message)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            return await http.SendAsync(message, HttpCompletionOption.ResponseContentRead, timeout.Token).ConfigureAwait(false);
        }

        HttpRequestMessage Request(string route, SoftwareChunkUpload? upload)
        {
            var message = new HttpRequestMessage(HttpMethod.Post, route);
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.GetAccessToken(accessToken));
            if (upload is not null) message.Content = JsonContent.Create(upload, SoftwareInventoryJsonContext.Default.SoftwareChunkUpload);
            return message;
        }
    }

    public sealed record PendingInventory(IReadOnlyList<SoftwareChunkUpload> Chunks);
}
