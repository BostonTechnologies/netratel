using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.Application.Jobs;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.RatelDesk;
using NetRatel.Infrastructure.Services;

namespace NetRatel.API.IntegrationTests.ServiceLinks;

internal sealed record PhysicalApiProcess(int ProcessId, DateTimeOffset StartedAtUtc,
    DateTimeOffset? ExitedAtUtc, int? ExitCode, string Role, int Generation);

internal sealed partial class ServiceLinkNetRatelPeer
{
    private readonly string physicalRoot = Path.Combine(Path.GetTempPath(), "netratel-physical-api-" + Guid.NewGuid().ToString("N"));
    private ServiceProvider? physicalReadServices;
    private OwnedPhysicalApiProcess? physicalPrimary;
    private OwnedPhysicalApiProcess? physicalReplica;
    private IReadOnlyDictionary<string, string?>? physicalSettings;
    private readonly List<PhysicalApiProcess> processHistory = [];
    private int generation;
    public IReadOnlyList<PhysicalApiProcess> PhysicalProcesses => processHistory.Concat(
        new[] { physicalPrimary, physicalReplica }.Where(x => x is not null).Select(x => x!.Proof)).ToArray();
    public string PhysicalReplicaBaseUrl => physicalReplica?.BaseUrl ?? throw new InvalidOperationException("The actual API replica is unavailable.");
    public bool PhysicalOwnedCleanupComplete => !physicalIncidentMode || physicalPrimary is null && physicalReplica is null &&
        physicalReadServices is null && physicalSettings is null && !Directory.Exists(physicalRoot);

    private async Task StartPhysicalProcessAsync(CancellationToken ct = default)
    {
        if (nativeListener is null) throw new InvalidOperationException("Physical API requires the owned real TLS listener identity.");
        if (physicalSettings is null)
        {
            Directory.CreateDirectory(physicalRoot);
            File.SetUnixFileMode(physicalRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var apiDll = PhysicalApiAssembly();
            var baseline = new ConfigurationBuilder().SetBasePath(Path.GetDirectoryName(apiDll)!)
                .AddJsonFile("appsettings.json", optional: true).Build();
            var settings = baseline.AsEnumerable().Where(x => x.Value is not null)
                .ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
            foreach (var item in databaseOwner.PhysicalRuntimeSettings()) settings[item.Key] = item.Value;
            foreach (var item in configuration) settings[item.Key] = item.Value;
            var certificate = Path.Combine(physicalRoot, "listener.pfx");
            var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            await File.WriteAllBytesAsync(certificate, nativeListener.ServerCertificate.Export(X509ContentType.Pfx, password));
            File.SetUnixFileMode(certificate, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            // Kestrel's supported endpoint configuration augments Program's unchanged
            // separate REST/h2c listeners. No certificate validation is overridden.
            settings["Kestrel:Endpoints:Physical:Url"] = nativeListener.Endpoint;
            settings["Kestrel:Endpoints:Physical:Protocols"] = "Http2";
            settings["Kestrel:Endpoints:Physical:Certificate:Path"] = certificate;
            settings["Kestrel:Endpoints:Physical:Certificate:Password"] = password;
            // ASP.NET's documented HTTPS_PORT input keeps the independent fixture
            // REST listener from redirecting into the native HTTP/2-only listener.
            settings["HTTPS_PORT"] = "-1";
            settings["TelemetryInteractive:BaselineSlowIntervalSeconds"] = "5";
            physicalSettings = settings;
            var services = new ServiceCollection();
            // This provider can only read durable state. It contains no gateway,
            // actor, hosted worker, bootstrap operation, dispatcher or fake clock.
            services.AddDbContext<OrchestratorDbContext>(o => o.UseNpgsql(settings["ConnectionStrings:NetRatelDb"]));
            services.AddSingleton<IRatelDeskReceiverFingerprint, RatelDeskReceiverFingerprint>();
            services.AddScoped<IJobRunService, JobRunService>();
            physicalReadServices = services.BuildServiceProvider();
        }
        physicalPrimary = await OwnedPhysicalApiProcess.StartAsync(physicalRoot, "primary", ++generation,
            physicalSettings, backendPort, ct);
        Anonymous = NewClient(); Administrator = NewClient(cookies: true);
        using var login = await Administrator.PostAsJsonAsync("/api/v2/local-auth/login", new
        { Email = global::ApiFactory.LocalAdministratorEmail, Password = global::ApiFactory.LocalAdministratorPassword, RememberMe = false }, ct);
        if (login.StatusCode != HttpStatusCode.NoContent)
            throw new InvalidOperationException("The actual physical API process did not authenticate the disposable administrator.");
    }

    public async Task StartPhysicalWorkerReplicaAsync(CancellationToken ct = default)
    {
        if (!physicalIncidentMode || physicalSettings is null || physicalReplica is not null)
            throw new InvalidOperationException("The actual physical API replica cannot start in this phase.");
        var port = ServiceLinkHttpProxy.AllocatePort();
        var settings = physicalSettings.Where(x => !x.Key.StartsWith("Kestrel:Endpoints:Physical:", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
        settings["NetRatel_HTTP_PORT"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        settings["NetRatelAkka:GatewayGrpcPort"] = ServiceLinkHttpProxy.AllocatePort().ToString(System.Globalization.CultureInfo.InvariantCulture);
        // The same database, Data Protection ring, AgentAuth signing key and issuer
        // inputs are retained. This starts Program and all production workers.
        physicalReplica = await OwnedPhysicalApiProcess.StartAsync(physicalRoot, "replica", ++generation, settings, port, ct);
        AssertPhysicalWorkerProcesses();
    }

    public void AssertPhysicalWorkerProcesses()
    {
        if (physicalPrimary is null || physicalReplica is null || physicalPrimary.ProcessId == physicalReplica.ProcessId ||
            !physicalPrimary.Alive || !physicalReplica.Alive)
            throw new InvalidOperationException("Two independent actual production API/worker OS processes are required.");
    }

    private async Task RestartPhysicalProcessesAsync(CancellationToken ct)
    {
        AssertPhysicalWorkerProcesses();
        Administrator.Dispose(); Anonymous.Dispose();
        foreach (var owned in new[] { physicalPrimary!, physicalReplica! })
        {
            await owned.CrashOwnedAsync();
            processHistory.Add(owned.Proof);
        }
        physicalPrimary = null; physicalReplica = null;
        await StartPhysicalProcessAsync(ct);
        await StartPhysicalWorkerReplicaAsync(ct);
    }

    private async Task StopPhysicalProcessesAsync()
    {
        if (!physicalIncidentMode) return;
        var failures = new List<Exception>();
        // A failed owner remains reachable for a subsequent bounded teardown;
        // its sibling is always attempted during this teardown too.
        if (physicalReplica is not null)
        {
            try { await physicalReplica.DisposeAsync(); processHistory.Add(physicalReplica.Proof); physicalReplica = null; }
            catch (Exception e) { failures.Add(e); }
        }
        if (physicalPrimary is not null)
        {
            try { await physicalPrimary.DisposeAsync(); processHistory.Add(physicalPrimary.Proof); physicalPrimary = null; }
            catch (Exception e) { failures.Add(e); }
        }
        if (physicalReadServices is not null)
        {
            try { await physicalReadServices.DisposeAsync(); physicalReadServices = null; }
            catch (Exception e) { failures.Add(e); }
        }
        // Keep exact private inputs if a process is not yet positively closed.
        // Removing them could discard the remaining owner's cleanup identity.
        if (physicalPrimary is null && physicalReplica is null && physicalReadServices is null)
        {
            try { if (Directory.Exists(physicalRoot)) Directory.Delete(physicalRoot, recursive: true); physicalSettings = null; }
            catch (Exception e) { failures.Add(e); }
        }
        if (failures.Count > 0) throw new AggregateException("Owned physical API cleanup was incomplete.", failures);
    }

    private sealed class OwnedPhysicalApiProcess : IAsyncDisposable
    {
        private readonly Process process;
        private readonly Task stdout;
        private readonly Task stderr;
        private readonly StartupOutputMarkers stdoutMarkers;
        private readonly StartupOutputMarkers stderrMarkers;
        private readonly string directory;
        public PhysicalApiProcess Proof { get; private set; }
        public int ProcessId => process.Id;
        public string BaseUrl { get; }
        public bool Alive => !process.HasExited;
        private bool closed;
        private bool processDisposed;
        private bool outputDrained;

        private OwnedPhysicalApiProcess(Process process, string directory, string role, int generation, int restPort)
        {
            this.process = process; this.directory = directory;
            BaseUrl = $"http://127.0.0.1:{restPort}";
            Proof = new(process.Id, DateTimeOffset.UtcNow, null, null, role, generation);
            stdoutMarkers = new(restPort);
            stderrMarkers = new(restPort);
            stdout = DrainAsync(process.StandardOutput, stdoutMarkers);
            stderr = DrainAsync(process.StandardError, stderrMarkers);
        }

        public static async Task<OwnedPhysicalApiProcess> StartAsync(string root, string role, int generation,
            IReadOnlyDictionary<string, string?> settings, int restPort, CancellationToken outer)
        {
            if (!OperatingSystem.IsLinux()) throw new InvalidOperationException("The owned physical API process requires Linux private-state permissions.");
            var directory = Path.Combine(root, role + "-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var file = Path.Combine(directory, "appsettings.json");
            await File.WriteAllTextAsync(file, JsonSerializer.Serialize(settings));
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
            { UseShellExecute = false, WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(PhysicalApiAssembly());
            // Deployment inputs inherited from a different host may not shadow this
            // fixture's private exact configuration. No settings value enters argv.
            foreach (var key in start.Environment.Keys.Where(key => settings.ContainsKey(key.Replace("__", ":", StringComparison.Ordinal)) ||
                key.StartsWith("NETRATEL_SERVICE_LINK_PRIVATE_", StringComparison.Ordinal) ||
                key.StartsWith("ASPNETCORE_", StringComparison.Ordinal) || key is "DOTNET_ENVIRONMENT").ToArray())
                start.Environment.Remove(key);
            start.Environment["DOTNET_ENVIRONMENT"] = "Production";
            start.Environment["ASPNETCORE_CONTENTROOT"] = directory;
            // Both supported host prefixes identify this owned private configuration.
            // The REST port is a nonsensitive fixture binding, identical to its probe.
            start.Environment["DOTNET_CONTENTROOT"] = directory;
            start.Environment["NetRatel_HTTP_PORT"] = restPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var owned = new OwnedPhysicalApiProcess(Process.Start(start) ?? throw new InvalidOperationException("The owned production API process could not start."), directory, role, generation, restPort);
            var readinessStarted = Stopwatch.GetTimestamp();
            var probeAttempts = 0;
            int? lastObservedHttpStatus = null;
            var lastProbeException = "none";
            var lastHttpRequestError = "none";
            var lastSocketError = "none";
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(outer);
                deadline.CancelAfter(TimeSpan.FromSeconds(30));
                using var client = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false })
                { BaseAddress = new Uri($"http://127.0.0.1:{restPort}"), Timeout = TimeSpan.FromSeconds(2) };
                while (true)
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    if (!owned.Alive) throw new InvalidOperationException("The actual production API process exited during startup.");
                    try
                    {
                        probeAttempts++;
                        using var response = await client.GetAsync("/api/integrations/service-link/metadata", deadline.Token);
                        lastObservedHttpStatus = (int)response.StatusCode;
                        lastProbeException = "none";
                        lastHttpRequestError = "none";
                        lastSocketError = "none";
                        if (response.StatusCode == HttpStatusCode.OK) break;
                        if ((int)response.StatusCode is >= 300 and < 400)
                            throw new InvalidOperationException("The physical REST endpoint redirected into a different protocol endpoint.");
                    }
                    catch (HttpRequestException error)
                    {
                        lastProbeException = nameof(HttpRequestException);
                        lastHttpRequestError = error.HttpRequestError.ToString();
                        lastSocketError = error.InnerException is System.Net.Sockets.SocketException socket
                            ? socket.SocketErrorCode.ToString() : "none";
                    }
                    catch (TaskCanceledException) when (!deadline.IsCancellationRequested)
                    {
                        lastProbeException = nameof(TaskCanceledException);
                        lastHttpRequestError = "none";
                        lastSocketError = "none";
                    }
                    await Task.Delay(200, deadline.Token);
                }
                return owned;
            }
            catch (Exception error)
            {
                // Project only fixed codes and numeric readiness facts before owned
                // cleanup. Never retain or emit raw process output, response bodies,
                // headers, paths, settings, exception messages or credentials.
                try
                {
                    var ownerRole = role is "primary" or "replica" ? role : "unknown";
                    var alive = owned.Alive;
                    var failureType = error switch
                    {
                        TaskCanceledException => nameof(TaskCanceledException),
                        OperationCanceledException => nameof(OperationCanceledException),
                        HttpRequestException => nameof(HttpRequestException),
                        InvalidOperationException => nameof(InvalidOperationException),
                        _ => "other-startup-exception"
                    };
                    var observedStatus = lastObservedHttpStatus?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none";
                    var exitCode = alive ? "none" : owned.process.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    Console.Error.WriteLine($"Owned physical API readiness failed: role={ownerRole}; failureType={failureType}; " +
                        $"elapsedMs={(long)Stopwatch.GetElapsedTime(readinessStarted).TotalMilliseconds}; probes={probeAttempts}; " +
                        $"lastObservedHttpStatus={observedStatus}; lastProbeException={lastProbeException}; " +
                        $"lastHttpRequestError={lastHttpRequestError}; lastSocketError={lastSocketError}; " +
                        $"alive={alive}; exitCode={exitCode}; outerCancelled={outer.IsCancellationRequested}; " +
                        $"stdoutMarkers={owned.stdoutMarkers.Snapshot()}; stderrMarkers={owned.stderrMarkers.Snapshot()}");
                }
                catch
                {
                    // Diagnostics cannot replace the original startup failure or
                    // prevent the unchanged exact owned-process cleanup below.
                }
                await owned.DisposeAsync();
                throw;
            }
        }

        public async Task CrashOwnedAsync()
        {
            // Actual termination of only this newly owned process exercises persisted
            // leases and MayHaveCommitted recovery. No application completion is seeded.
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Proof = Proof with { ExitedAtUtc = DateTimeOffset.UtcNow, ExitCode = process.ExitCode };
            await DisposeAsync();
        }

        public async ValueTask DisposeAsync()
        {
            if (closed) return;
            var failures = new List<Exception>();
            if (!processDisposed)
            {
                try
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                    Proof = Proof with { ExitedAtUtc = Proof.ExitedAtUtc ?? DateTimeOffset.UtcNow, ExitCode = process.ExitCode };
                }
                catch (Exception e) { failures.Add(e); }
                // Draining/private cleanup is still attempted after termination
                // failure whenever this exact process is observed to have exited.
                if (Proof.ExitedAtUtc is not null)
                {
                    try { await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(5)); outputDrained = true; }
                    catch (Exception e) { failures.Add(e); }
                    if (outputDrained)
                        try { process.Dispose(); processDisposed = true; } catch (Exception e) { failures.Add(e); }
                }
            }
            if (Proof.ExitedAtUtc is not null)
                try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
                catch (Exception e) { failures.Add(e); }
            closed = processDisposed && outputDrained && !Directory.Exists(directory);
            if (failures.Count > 0 || !closed)
                throw new AggregateException("Owned API process cleanup was incomplete.", failures);
        }
        private static async Task DrainAsync(StreamReader stream, StartupOutputMarkers markers)
        {
            var buffer = new char[2048];
            var matches = new int[markers.PatternCount];
            try
            {
                int read;
                while ((read = await stream.ReadAsync(buffer)) != 0)
                    markers.Observe(buffer.AsSpan(0, read), matches);
            }
            finally { Array.Clear(buffer); }
        }

        // Streaming fixed-marker projection keeps the original drain bounded.
        // Each channel retains only match positions and a fixed-size boolean set;
        // no raw line, arbitrary exception text or private value survives a read.
        private sealed class StartupOutputMarkers
        {
            private static readonly (string Text, string Code)[] CommonPatterns =
            [
                ("Now listening on:", "listener-reported"),
                ("Application started.", "host-started"),
                ("Application is shutting down", "host-stopping"),
                ("Agent-auth signing key loaded.", "agent-signing-key-loaded"),
                ("Agent-auth signing key probe failed;", "agent-signing-key-probe-failed"),
                ("NetRatel infrastructure migration failed during startup.", "startup-migration-failed"),
                ("Unhandled exception.", "unhandled-exception-marker"),
                ("System.InvalidOperationException", "invalid-operation-exception-marker"),
                ("System.IO.IOException", "io-exception-marker"),
                ("System.Net.Sockets.SocketException", "socket-exception-marker"),
                ("Microsoft.AspNetCore.Connections.AddressInUseException", "address-in-use-exception-marker"),
                ("Npgsql.NpgsqlException", "npgsql-exception-marker"),
                ("System.TimeoutException", "timeout-exception-marker")
            ];
            private readonly (string Text, string Code)[] patterns;
            public int PatternCount => patterns.Length;
            private readonly object sync = new();
            private readonly bool[] observed;

            public StartupOutputMarkers(int expectedRestPort)
            {
                var port = expectedRestPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
                patterns =
                [
                    .. CommonPatterns,
                    ($"Now listening on: http://[::]:{port}\n", "expected-rest-listener-reported"),
                    ($"Now listening on: http://0.0.0.0:{port}\n", "expected-rest-listener-reported"),
                    ($"Now listening on: http://127.0.0.1:{port}\n", "expected-rest-listener-reported")
                ];
                observed = new bool[patterns.Length];
            }

            public void Observe(ReadOnlySpan<char> chunk, int[] matches)
            {
                lock (sync)
                {
                    foreach (var character in chunk)
                        for (var index = 0; index < patterns.Length; index++)
                        {
                            var text = patterns[index].Text;
                            var matched = matches[index];
                            matched = character == text[matched] ? matched + 1 : character == text[0] ? 1 : 0;
                            if (matched == text.Length) { observed[index] = true; matched = 0; }
                            matches[index] = matched;
                        }
                }
            }

            public string Snapshot()
            {
                lock (sync)
                {
                    var codes = patterns.Where((_, index) => observed[index]).Select(x => x.Code).Distinct(StringComparer.Ordinal).ToArray();
                    return codes.Length == 0 ? "none" : string.Join(',', codes);
                }
            }
        }
    }

    private static string PhysicalApiAssembly()
    {
        var configuration = typeof(ServiceLinkNativeArtifacts).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration;
        for (var root = new DirectoryInfo(AppContext.BaseDirectory); root is not null; root = root.Parent)
        {
            if (!File.Exists(Path.Combine(root.FullName, "NetRatel.sln"))) continue;
            var assembly = Path.Combine(root.FullName, "src", "NetRatel", "NetRatel.API", "bin", configuration!, "net10.0", "NetRatel.API.dll");
            if (configuration is not ("Release" or "Debug") || !File.Exists(assembly) ||
                !File.Exists(Path.ChangeExtension(assembly, ".runtimeconfig.json")) ||
                !SHA256.HashData(File.ReadAllBytes(assembly)).SequenceEqual(SHA256.HashData(File.ReadAllBytes(typeof(global::Program).Assembly.Location))))
                throw new InvalidOperationException("The standalone production API artifact differs from the compiled candidate referenced by the test.");
            return assembly;
        }
        throw new InvalidOperationException("The standalone physical API requires the actual clean source checkout.");
    }
}
