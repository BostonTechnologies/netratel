using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace NetRatel.API.IntegrationTests.ServiceLinks;

// Owns only the reviewed helper process and its private stdin/stdout. The root
// helper's scope is its new UUID directory, loop device and exact Client IDs.
internal sealed class PhysicalDiskFixtureProcess : IAsyncDisposable
{
    private readonly Process process;
    private readonly Task<string> diagnostics;
    private readonly SemaphoreSlim exchange = new(1, 1);
    private bool closed;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public JsonElement? Cleanup { get; private set; }
    public bool OwnedMetadataRemoved { get; private set; }

    public PhysicalDiskFixtureProcess(string reviewedScript, string fixtureWorkRoot)
    {
        if (!OperatingSystem.IsLinux() || !Path.IsPathFullyQualified(reviewedScript) ||
            !File.Exists(reviewedScript) || !Path.IsPathFullyQualified(fixtureWorkRoot) ||
            !Directory.Exists(fixtureWorkRoot))
            throw new InvalidOperationException("The real Linux physical fixture requires its reviewed helper and owned disk-backed work root.");
        var start = new ProcessStartInfo("sudo")
        {
            UseShellExecute = false, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var argument in new[] { "-n", "python3", reviewedScript, "--work-root", fixtureWorkRoot })
            start.ArgumentList.Add(argument);
        process = Process.Start(start) ?? throw new InvalidOperationException("The owned physical fixture process could not start.");
        diagnostics = process.StandardError.ReadToEndAsync();
    }

    public Task<JsonElement> ProvisionAsync(CancellationToken ct) => SendAsync("provision", new { }, ct);

    public Task<JsonElement> StartClientAsync(string imageId, string sourceSha, string version,
        string executableSha256, string trustedCaPem, string apiBaseUrl, string gatewayEndpoint,
        string enrollmentCode, CancellationToken ct) => SendAsync("start", new
        {
            imageId, sourceSha, version, executableSha256, trustedCaPem,
            apiBaseUrl, gatewayEndpoint, enrollmentCode
        }, ct);

    public Task<JsonElement> ReleaseCommandAsync(CancellationToken ct) => SendAsync("release-command", new { }, ct);

    public Task<JsonElement> AllocateAsync(CancellationToken ct) => SendAsync("allocate", new { }, ct);
    public Task<JsonElement> RecoverAsync(CancellationToken ct) => SendAsync("recover", new { }, ct);
    public Task<JsonElement> AssertAliveAsync(CancellationToken ct) => SendAsync("alive", new { }, ct);

    private async Task<JsonElement> SendAsync(string operation, object input, CancellationToken ct)
    {
        await exchange.WaitAsync(ct);
        try
        {
            if (closed || process.HasExited)
                throw new InvalidOperationException("The owned physical fixture process is unavailable.");
            var id = Guid.NewGuid().ToString("N");
            var request = JsonSerializer.Serialize(new { id, operation, input }, Json);
            if (Encoding.UTF8.GetByteCount(request) > 65_535)
                throw new InvalidOperationException("The private physical fixture input exceeded its bound.");
            // The enrollment code travels only in this private redirected pipe.
            // Do not retain request bodies or include them in exceptions/logs.
            await process.StandardInput.WriteLineAsync(request.AsMemory(), ct);
            await process.StandardInput.FlushAsync(ct);
            var line = await process.StandardOutput.ReadLineAsync(ct);
            if (line is null || Encoding.UTF8.GetByteCount(line) > 65_536)
                throw new InvalidOperationException("The typed physical fixture response was absent or oversized.");
            try
            {
                using var document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 32 });
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 2 ||
                    root.GetProperty("id").GetString() != id ||
                    root.GetProperty("facts").ValueKind != JsonValueKind.Object)
                    throw new InvalidOperationException("The typed physical fixture response did not match its request.");
                return root.GetProperty("facts").Clone();
            }
            catch (JsonException)
            {
                throw new InvalidOperationException("The typed physical fixture response was malformed.");
            }
        }
        finally { exchange.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (closed) return;
        // Cleanup owns a separate bounded cancellation token so an expired
        // scenario still stops its Client before normal unmount/detach. No
        // producer lifetime or business protocol deadline is extended.
        using var teardown = new CancellationTokenSource(TimeSpan.FromSeconds(55));
        Exception? failure = null;
        try
        {
            if (!process.HasExited)
            {
                Cleanup = await SendAsync("close", new { }, teardown.Token);
                foreach (var name in new[] { "ownedClientStoppedAndRemoved", "ownedMountRemoved",
                    "ownedLoopDetached", "ownedBackingRemoved", "privateCredentialsRemoved" })
                    if (!Cleanup.Value.GetProperty(name).GetBoolean())
                        throw new InvalidOperationException("Owned physical cleanup did not complete.");
                if (Cleanup.Value.GetProperty("imagesRemoved").GetInt32() != 0 ||
                    Cleanup.Value.GetProperty("fleetChanges").GetInt32() != 0)
                    throw new InvalidOperationException("Owned physical cleanup exceeded its scope.");
                var ownership = Cleanup.Value.GetProperty("ownershipId").GetString();
                if (ownership is null || ownership.Length != 32 || !ownership.All(Uri.IsHexDigit))
                    throw new InvalidOperationException("Owned cleanup receipt had no exact helper ownership identity.");
                var acknowledged = await SendAsync("cleanup-ack", new { ownershipId = ownership }, teardown.Token);
                OwnedMetadataRemoved = acknowledged.GetProperty("ownedMetadataRemoved").GetBoolean();
                if (!OwnedMetadataRemoved)
                    throw new InvalidOperationException("Consumed owned cleanup metadata was not removed.");
            }
            else throw new InvalidOperationException("The owned physical fixture exited without a positive cleanup receipt.");
            process.StandardInput.Close();
            await process.WaitForExitAsync(teardown.Token);
            if (process.ExitCode != 0)
                throw new InvalidOperationException("The owned physical fixture cleanup process failed.");
        }
        catch (Exception error)
        {
            failure = error;
            // EOF requests normal owned cleanup even when the response pipe or
            // cancellation failed. Avoid SIGKILL before the helper can unmount.
            process.StandardInput.Close();
            try { await process.WaitForExitAsync(teardown.Token); }
            catch (OperationCanceledException) when (teardown.IsCancellationRequested) { }
        }
        finally
        {
            if (process.HasExited)
            {
                _ = await diagnostics; // Arbitrary sudo/OS diagnostics never enter public proof.
                process.Dispose();
                closed = true;
            }
        }
        if (failure is not null)
            throw new InvalidOperationException("The physical fixture failed bounded owned cleanup; acceptance is incomplete.", failure);
    }
}
