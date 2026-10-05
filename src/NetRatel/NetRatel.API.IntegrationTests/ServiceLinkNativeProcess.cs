using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace NetRatel.API.IntegrationTests.ServiceLinks;

/// <summary>Owns one real client process; no enrollment/refresh/access credentials escape its private directory.</summary>
internal sealed class ServiceLinkNativeProcess : IAsyncDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string root = Path.Combine(Path.GetTempPath(), "netratel-physical-agent", Guid.NewGuid().ToString("N"));
    private Process? process;
    private Task? stdoutDrain;
    private Task? stderrDrain;
    private string inputFile => Path.Combine(root, "input.json");
    private string readyFile => Path.Combine(root, "ready.json");
    public string ReleaseFile => Path.Combine(root, "release-command");
    public Guid AgentId { get; private set; }
    public bool TrustVerified { get; private set; }
    public bool UntrustedCaRejected { get; private set; }
    public string SourceSha { get; private set; } = "";
    public string ClientAssemblySha256 { get; private set; } = "";
    public string ClientProductVersion { get; private set; } = "";
    public string ProofMarker { get; } = "native-proof-" + Guid.NewGuid().ToString("N");
    public string Command =>
        "i=0; while [ \"$i\" -lt 200 ]; do " +
        "if [ -f " + ShellQuote(ReleaseFile) + " ]; then printf '%s\\n' " + ShellQuote(ProofMarker) + "; exit 0; fi; " +
        "sleep 0.1; i=$((i+1)); done; exit 124";

    public static async Task<ServiceLinkNativeProcess> StartAsync(ServiceLinkNetRatelPeer peer,
        ServiceLinkNativeListener listener, CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("The required physical pair lane uses Linux native command execution and PostgreSQL/Docker.");
        var artifacts = await ServiceLinkNativeArtifacts.ResolveAsync(ct);
        var runner = artifacts.Runner;
        var owned = new ServiceLinkNativeProcess();
        try
        {
            Directory.CreateDirectory(owned.root);
            File.SetUnixFileMode(owned.root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            using var response = await peer.Administrator.PostAsJsonAsync($"/api/v1/tenants/{peer.TenantId}/enrollment-codes",
                new { ValidForMinutes = 5, MaxUses = 1, Note = "isolated actual service-link command proof" }, ct);
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Actual enrollment-code issue returned HTTP {(int)response.StatusCode}.");
            var issued = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            var code = issued.GetProperty("code").GetString();
            if (string.IsNullOrEmpty(code)) throw new InvalidOperationException("The actual enrollment issuer returned no code.");
            await File.WriteAllTextAsync(owned.inputFile, JsonSerializer.Serialize(new
            {
                ApiBaseUrl = peer.BaseUrl, GatewayEndpoint = listener.Endpoint,
                TenantId = int.Parse(peer.TenantId, System.Globalization.CultureInfo.InvariantCulture),
                EnrollmentCode = code, CredentialFile = Path.Combine(owned.root, "agent.dat"),
                ReadyFile = owned.readyFile, MaximumLifetimeSeconds = 300
            }, Json), ct);
            File.SetUnixFileMode(owned.inputFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);

            var caBundle = Path.Combine(owned.root, "trusted-ca-bundle.pem");
            var existingBundle = Environment.GetEnvironmentVariable("SSL_CERT_FILE");
            if (string.IsNullOrEmpty(existingBundle) || !File.Exists(existingBundle)) existingBundle = "/etc/ssl/certs/ca-certificates.crt";
            var systemRoots = File.Exists(existingBundle) ? await File.ReadAllTextAsync(existingBundle, ct) : "";
            await File.WriteAllTextAsync(caBundle, systemRoots + "\n" + listener.CaPem + "\n", ct);
            File.SetUnixFileMode(caBundle, UnixFileMode.UserRead | UnixFileMode.UserWrite);

            // Positive first proves the exact same endpoint really serves this product over TLS + HTTP/2.
            var trusted = await owned.ProbeAsync(runner, caBundle, ct);
            if (trusted.ExitCode != 0 || trusted.Output != "native-tls-http2-verified")
                throw new InvalidOperationException("Default TLS validation did not trust the actual product HTTP/2 listener.");
            owned.TrustVerified = true;
            var untrusted = await owned.ProbeAsync(runner, null, ct);
            if (untrusted.ExitCode != 42 || untrusted.Output != "native-tls-certificate-rejected")
                throw new InvalidOperationException("An unrelated process trust store did not reject the disposable CA; the trust negative is not proven.");
            owned.UntrustedCaRejected = true;

            owned.process = owned.Start(runner, caBundle, probe: false);
            owned.stdoutDrain = DrainAsync(owned.process.StandardOutput);
            owned.stderrDrain = DrainAsync(owned.process.StandardError);
            var ready = await owned.WaitReadyAsync(requireAdmission: false, TimeSpan.FromSeconds(30), ct);
            owned.AgentId = ready.AgentId;
            if (ready.SourceSha != artifacts.CandidateSourceSha || ready.ClientAssemblySha256 != artifacts.ClientAssemblySha256 ||
                ready.TenantId.ToString(System.Globalization.CultureInfo.InvariantCulture) != peer.TenantId || string.IsNullOrWhiteSpace(ready.ClientProductVersion))
                throw new InvalidOperationException("The actual native runner, production client assembly or enrolled tenant differs from the same candidate checkout.");
            owned.SourceSha = ready.SourceSha; owned.ClientAssemblySha256 = ready.ClientAssemblySha256;
            owned.ClientProductVersion = ready.ClientProductVersion;
            return owned;
        }
        catch { await owned.DisposeAsync(); throw; }
    }

    public async Task WaitAdmittedAsync(CancellationToken ct) =>
        _ = await WaitReadyAsync(requireAdmission: true, TimeSpan.FromSeconds(30), ct);

    public Task ReleaseCommandAsync(CancellationToken ct) => File.WriteAllTextAsync(ReleaseFile, "actual-ack-recorded", ct);

    private async Task<NativeReady> WaitReadyAsync(bool requireAdmission, TimeSpan timeout, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        while (true)
        {
            deadline.Token.ThrowIfCancellationRequested();
            if (process?.HasExited == true) throw new InvalidOperationException($"The actual native runner exited before required admission (code {process.ExitCode}).");
            if (File.Exists(readyFile))
            {
                var ready = JsonSerializer.Deserialize<NativeReady>(await File.ReadAllTextAsync(readyFile, deadline.Token), Json);
                if (ready is { AgentId: var id, Enrolled: true } && id != Guid.Empty &&
                    (!requireAdmission || ready.PresenceAdmitted && ready.JobAdmitted)) return ready;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(100), deadline.Token);
        }
    }

    private async Task<(int ExitCode, string Output)> ProbeAsync(string runner, string? caBundle, CancellationToken ct)
    {
        using var probe = Start(runner, caBundle, probe: true);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        var output = probe.StandardOutput.ReadToEndAsync(deadline.Token);
        var errors = DrainAsync(probe.StandardError);
        try
        {
            await probe.WaitForExitAsync(deadline.Token);
            await errors;
            return (probe.ExitCode, (await output).Trim());
        }
        finally
        {
            if (!probe.HasExited)
            {
                probe.Kill(entireProcessTree: true);
                await probe.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            // Observe both private stream tasks on failure/cancellation as well as success.
            try { await Task.WhenAll(output, errors).WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
        }
    }

    private Process Start(string runner, string? caBundle, bool probe)
    {
        var info = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            UseShellExecute = false, WorkingDirectory = root,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        info.ArgumentList.Add(runner);
        if (probe) info.ArgumentList.Add("--trust-probe");
        info.ArgumentList.Add(inputFile); // Private path only; the one-time code is not a command-line argument.
        info.Environment.Remove("SSL_CERT_DIR");
        if (caBundle is null) info.Environment.Remove("SSL_CERT_FILE");
        else info.Environment["SSL_CERT_FILE"] = caBundle;
        info.Environment["NO_PROXY"] = "127.0.0.1,localhost," + new Uri(JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(inputFile)).GetProperty("apiBaseUrl").GetString()!).Host;
        return Process.Start(info) ?? throw new InvalidOperationException("The native client process could not start.");
    }

    private static async Task DrainAsync(StreamReader reader)
    {
        // Drain to avoid blocking a physical stream; do not artifact arbitrary product diagnostics or secrets.
        var buffer = new char[2048];
        while (await reader.ReadAsync(buffer) != 0) { }
    }

    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (process is not null)
            {
                if (!process.HasExited)
                {
                    try
                    {
                        await process.StandardInput.WriteLineAsync("stop").WaitAsync(TimeSpan.FromSeconds(1));
                        process.StandardInput.Close();
                        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
                    }
                    catch (Exception error) when (error is IOException or InvalidOperationException or TimeoutException)
                    {
                        if (!process.HasExited) process.Kill(entireProcessTree: true);
                    }
                }
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                if (stdoutDrain is not null && stderrDrain is not null)
                    await Task.WhenAll(stdoutDrain, stderrDrain).WaitAsync(TimeSpan.FromSeconds(2));
            }
        }
        finally
        {
            process?.Dispose(); process = null;
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed record NativeReady(Guid AgentId, int TenantId, bool Enrolled, bool PresenceAdmitted, bool JobAdmitted,
        string SourceSha, string ClientAssemblySha256, string ClientProductVersion);
}
