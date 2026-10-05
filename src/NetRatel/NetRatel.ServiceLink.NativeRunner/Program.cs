using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using NetRatel.Client.Service.Gateway;
using NetRatel.Client.Service.Logging;
using NetRatel.Infrastructure.Auth;

namespace NetRatel.ServiceLink.NativeRunner;

/// <summary>
/// Isolated real production agent clients, with process-local CA trust and private fixture credentials.
/// This runner never registers a gateway session or manufactures a job lifecycle/result.
/// </summary>
internal static class Program
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    public static async Task<int> Main(string[] args)
    {
        if (args.Length is not (1 or 2) || args.Length == 2 && args[0] != "--trust-probe") return 64;
        var configPath = Path.GetFullPath(args[^1]);
        if (!File.Exists(configPath) || new FileInfo(configPath).Length > 8192) return 64;
        NativeInput? input;
        try { input = JsonSerializer.Deserialize<NativeInput>(await File.ReadAllTextAsync(configPath), Json); }
        catch (JsonException) { return 64; }
        if (input is null || !Validate(input, Path.GetDirectoryName(configPath)!)) return 64;
        // Production executor diagnostics stay inside the parent-owned private fixture directory.
        LogManager.Initialize(Path.GetDirectoryName(configPath)!);

        // Both production clients use ordinary TLS validation. No custom validation callback is installed.
        if (args.Length == 2) return await ProbeTrustAsync(input);
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(input.MaximumLifetimeSeconds));
        var stopReader = StopOnInputAsync(budget);
        try
        {
            using var auth = new HttpClient(new HttpClientHandler
            { AllowAutoRedirect = false, UseProxy = false })
            { BaseAddress = new Uri(input.ApiBaseUrl), Timeout = TimeSpan.FromSeconds(30) };
            var credentials = new AgentCredentialStore(pathOverride: input.CredentialFile);
            var enrollment = new AgentEnrollmentService(auth, credentials);
            var enrolled = await enrollment.EnrollAsync(input.EnrollmentCode, budget.Token);
            await credentials.SaveAsync(enrolled.AgentId, enrolled.RefreshToken);
            var tokens = new ClientAgentTokenService(auth, credentials, credentials);
            _ = await tokens.GetAccessTokenAsync(budget.Token); // Actual HTTP issuer with device proof/refresh handling.
            var agentId = Guid.Parse(enrolled.AgentId);
            var sourceSha = typeof(Program).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .Single(attribute => attribute.Key == "ServiceLinkProofSourceSha").Value
                ?? throw new InvalidOperationException("The runner source stamp is unavailable.");
            var clientBytes = await File.ReadAllBytesAsync(typeof(AgentJobGatewayClient).Assembly.Location, budget.Token);
            var clientVersion = typeof(AgentJobGatewayClient).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
                ?? throw new InvalidOperationException("The actual production client version is unavailable.");
            var ready = new NativeReady(agentId, input.TenantId, true, false, false, sourceSha,
                Convert.ToHexStringLower(SHA256.HashData(clientBytes)), clientVersion);
            await WriteReadyAsync(input.ReadyFile, ready, budget.Token);
            var readyLock = new object();
            void RecordEvent(string message)
            {
                // Keep dynamic product diagnostics, URLs and any credential-bearing messages out of output.
                lock (readyLock)
                {
                    if (message.StartsWith("Presence admitted.", StringComparison.Ordinal))
                        ready = ready with { PresenceAdmitted = true };
                    else if (message == "Job gateway admitted. authority=akka.")
                        ready = ready with { JobAdmitted = true };
                    else return;
                    WriteReadyAsync(input.ReadyFile, ready, CancellationToken.None).GetAwaiter().GetResult();
                    Console.WriteLine(ready.JobAdmitted ? "native-job-admitted" : "native-presence-admitted");
                }
            }
            // Preserve all production #160 admission/heartbeat/renewal/teardown defaults.
            var options = new GatewayClientOptions { Endpoint = input.GatewayEndpoint };
            var jobs = new AgentJobGatewayClient(options, useInProcPowerShell: false, RecordEvent);
            var presence = new AgentGatewayPresenceClient(options, tokens, input.TenantId, agentId,
                clientVersion, [], RecordEvent,
                runForPresenceSession: jobs.RunForPresenceSessionAsync);
            await presence.RunAsync(budget.Token);
            return 0;
        }
        catch (OperationCanceledException) when (budget.IsCancellationRequested) { return 0; }
        catch (Exception error)
        {
            Console.Error.WriteLine("native-runner-failed:" + error.GetType().Name);
            return 1;
        }
        finally
        {
            budget.Cancel();
            try { await stopReader.WaitAsync(TimeSpan.FromSeconds(1)); }
            catch (Exception error) when (error is OperationCanceledException or TimeoutException) { }
        }
    }

    private static bool Validate(NativeInput input, string root)
    {
        bool WithinRoot(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            try { return Path.GetFullPath(path).StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal); }
            catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
        }
        return Uri.TryCreate(input.ApiBaseUrl, UriKind.Absolute, out var api) && api.Scheme is "http" or "https" &&
            api.UserInfo.Length == 0 && api.Query.Length == 0 && api.Fragment.Length == 0 &&
            Uri.TryCreate(input.GatewayEndpoint, UriKind.Absolute, out var gateway) && gateway.Scheme == "https" &&
            gateway.Host == "127.0.0.1" && gateway.UserInfo.Length == 0 && gateway.Query.Length == 0 && gateway.Fragment.Length == 0 &&
            input.TenantId > 0 && input.EnrollmentCode is { Length: > 0 and <= 4096 } &&
            input.MaximumLifetimeSeconds is >= 60 and <= 300 && WithinRoot(input.CredentialFile) && WithinRoot(input.ReadyFile);
    }

    private static async Task<int> ProbeTrustAsync(NativeInput input)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false });
        using var request = new HttpRequestMessage(HttpMethod.Get,
            input.GatewayEndpoint.TrimEnd('/') + "/api/integrations/service-link/metadata")
        { Version = HttpVersion.Version20, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
        try
        {
            using var response = await client.SendAsync(request, deadline.Token);
            if (!response.IsSuccessStatusCode || response.Version.Major != 2) return 43;
            var metadata = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: deadline.Token);
            if (metadata.GetProperty("product").GetString() != "netratel") return 43;
            Console.WriteLine("native-tls-http2-verified");
            return 0;
        }
        catch (HttpRequestException error) when (error.HttpRequestError == HttpRequestError.SecureConnectionError &&
            HasAuthenticationFailure(error))
        { Console.WriteLine("native-tls-certificate-rejected"); return 42; }
        catch (Exception error)
        { Console.Error.WriteLine("native-tls-probe-failed:" + error.GetType().Name); return 43; }
    }

    private static bool HasAuthenticationFailure(Exception error) =>
        error is AuthenticationException || error.InnerException is not null && HasAuthenticationFailure(error.InnerException);

    private static async Task StopOnInputAsync(CancellationTokenSource stopping)
    {
        try
        {
            var line = await Console.In.ReadLineAsync(stopping.Token);
            if (line is null or "stop") stopping.Cancel();
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
    }

    private static async Task WriteReadyAsync(string path, NativeReady ready, CancellationToken ct)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(ready, Json), ct);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private sealed record NativeInput(
        [property: JsonRequired] string ApiBaseUrl,
        [property: JsonRequired] string GatewayEndpoint,
        [property: JsonRequired] int TenantId,
        [property: JsonRequired] string EnrollmentCode,
        [property: JsonRequired] string CredentialFile,
        [property: JsonRequired] string ReadyFile,
        [property: JsonRequired] int MaximumLifetimeSeconds);
    private sealed record NativeReady(Guid AgentId, int TenantId, bool Enrolled, bool PresenceAdmitted, bool JobAdmitted,
        string SourceSha, string ClientAssemblySha256, string ClientProductVersion);
}
