using System.Net;
using System.Net.Sockets;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;
using NetRatel.Shared.ServiceLinks;

namespace NetRatel.API.IntegrationTests.ServiceLinks;

/// <summary>Forwards actual peer HTTP; a fault drops only a completed backend response.</summary>
internal sealed class ServiceLinkHttpProxy : IAsyncDisposable
{
    private readonly HttpClient forward = new(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false, UseCookies = false });
    private readonly string backend;
    private WebApplication? app;
    private string? lostResponseSuffix;
    private int lossPending;
    private readonly object observationSync = new();
    private PendingObservation? pendingObservation;
    private readonly List<ServiceLinkObservedOperation> completedObservations = [];
    private readonly List<ServiceLinkRotationResponseFault> rotationResponseFaults = [];
    private readonly ConcurrentQueue<ServiceLinkBusinessObservation> businessObservations = new();
    private TerminalDeliveryGate? terminalDelivery;
    public IReadOnlyList<ServiceLinkBusinessObservation> BusinessObservations => businessObservations.ToArray();
    public string BaseUrl { get; }
    public int LostResponses { get; private set; }
    public ConcurrentDictionary<string, ServiceLinkObservedClient> ObservedClients { get; } = new(StringComparer.Ordinal);

    public ServiceLinkHttpProxy(string backend, string reachableHost)
    {
        this.backend = backend;
        BaseUrl = $"http://{reachableHost}:{AllocatePort()}";
    }

    public static int AllocatePort()
    {
        using var listener = new TcpListener(IPAddress.Any, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    public async Task StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://0.0.0.0:{new Uri(BaseUrl).Port}");
        app = builder.Build();
        app.Run(async context =>
        {
            try { await ForwardAsync(context); }
            catch (Exception error)
            {
                // Exception types and source stacks diagnose fixture transport without
                // printing URLs, request bodies, authorization or credential values.
                Console.Error.WriteLine($"Service-link fixture proxy failure: {error.GetType().FullName}\n{error.StackTrace}");
                throw;
            }
        });
        await app.StartAsync();
    }

    public void LoseNextCompletedResponse(string pathSuffix)
    {
        lostResponseSuffix = pathSuffix;
        Interlocked.Exchange(ref lossPending, 1);
    }

    public ServiceLinkRotationResponseFault LoseNextCommittedRotationResponse(string exactPath, string phase, string? rotationId = null,
        ServiceLinkRotationResponseFault? bindToOffer = null)
    {
        if (!exactPath.StartsWith(ServiceLinkContract.EndpointPath + "/links/", StringComparison.Ordinal) ||
            !exactPath.EndsWith(phase == "verify" ? "/verify" : "/rotate", StringComparison.Ordinal) ||
            phase is not ("offer" or "verify" or "verified" or "switched") ||
            rotationId is null && phase != "offer" && bindToOffer is null ||
            bindToOffer is not null && bindToOffer.Phase != "offer")
            throw new ArgumentException("A rotation fault requires an exact phase route and its durable rotation identity.");
        var fault = new ServiceLinkRotationResponseFault(this, exactPath, phase, rotationId, bindToOffer);
        lock (observationSync)
        {
            if (rotationResponseFaults.Any(x => x.Path == exactPath && x.Phase == phase))
                throw new InvalidOperationException("That exact rotation phase is already armed.");
            rotationResponseFaults.Add(fault);
        }
        return fault;
    }

    internal void Release(ServiceLinkRotationResponseFault fault)
    {
        lock (observationSync) rotationResponseFaults.Remove(fault);
    }

    public IDisposable PauseTerminalDelivery()
    {
        var pause = new TerminalDeliveryGate();
        if (Interlocked.CompareExchange(ref terminalDelivery, pause, null) is not null)
            throw new InvalidOperationException("Terminal delivery is already paused.");
        return new TerminalDeliveryPause(this, pause);
    }

    public Task WaitForPausedTerminalDeliveryAsync() =>
        (Volatile.Read(ref terminalDelivery)?.Arrived.Task ?? throw new InvalidOperationException("Terminal delivery is not paused."))
        .WaitAsync(TimeSpan.FromSeconds(10));

    public Task<ServiceLinkObservedOperation> ObserveNextSuccessfulOperation(string exactPath, int maximumPayloadBytes)
    {
        if (!exactPath.StartsWith(ServiceLinkContract.EndpointPath + "/", StringComparison.Ordinal) ||
            !(exactPath.EndsWith("/exchange", StringComparison.Ordinal) || exactPath.EndsWith("/rotate", StringComparison.Ordinal)) ||
            maximumPayloadBytes is < 4096 or > 1048576)
            throw new ArgumentException("Observation requires an exact bounded exchange or rotation route.");
        lock (observationSync)
        {
            if (pendingObservation is not null) throw new InvalidOperationException("A peer observation is already armed.");
            pendingObservation = new(exactPath, maximumPayloadBytes);
            return pendingObservation.Completion.Task;
        }
    }

    private static string? ReadSafeProblemCode(byte[] payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("code", out var field) || field.ValueKind != JsonValueKind.String)
                return null;
            var code = field.GetString();
            // Emit only fixed public protocol constants. A shape filter alone
            // could still mistake an arbitrary secret for a diagnostic code.
            return code switch
            {
                "invalid-request" or "invalid-operation-fields" or "invalid-rotation-offer" or
                "credential-binding-mismatch" or "invalid-set" or "rotation-binding-conflict" or
                "operation-payload-conflict" or "rotation-payload-conflict" or "credential-revision-conflict" or
                "rotation-conflict" or "service-link-conflict" or "successor-not-verified" or
                "rotation-offer-expired" or "rotation-not-authorized" or "candidate-control-restricted" or
                "rotation-switch-conflict" or "successor-not-active" => code,
                _ => null
            };
        }
        catch (JsonException) { return null; }
    }

    private async Task ForwardAsync(HttpContext context)
    {
        var pause = Volatile.Read(ref terminalDelivery);
        if (pause is not null && HttpMethods.IsPost(context.Request.Method) &&
            context.Request.Path.Value is { } path && path.StartsWith(ServiceLinkContract.EndpointPath + "/links/", StringComparison.Ordinal) &&
            (path.EndsWith("/abort", StringComparison.Ordinal) || path.EndsWith("/revoke", StringComparison.Ordinal)))
        {
            pause.Arrived.TrySetResult(true);
            await pause.Released.Task.WaitAsync(TimeSpan.FromSeconds(30), context.RequestAborted);
        }
        using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method),
            backend.TrimEnd('/') + context.Request.Path + context.Request.QueryString);
        ServiceLinkObservedClient? oauthClient = null;
        PendingObservation? observation;
        ServiceLinkRotationResponseFault[] possibleRotationFaults;
        lock (observationSync)
        {
            possibleRotationFaults = HttpMethods.IsPost(context.Request.Method) && !context.Request.QueryString.HasValue
                ? rotationResponseFaults.Where(x => x.Path == context.Request.Path.Value).ToArray() : [];
            observation = pendingObservation is { Claimed: false } pending && HttpMethods.IsPost(context.Request.Method) &&
                context.Request.Path.Value == pending.Path && !context.Request.QueryString.HasValue ? pending : null;
            if (observation is not null) observation.Claimed = true;
        }
        byte[]? observedRequest = null;
        byte[]? observedResponse = null;
        byte[]? forwardedRequest = null;
        byte[]? forwardedResponse = null;
        ServiceLinkRotationResponseFault? rotationFault = null;
        byte[]? businessRequest = null;
        try
        {
            var observeBusiness = HttpMethods.IsPost(context.Request.Method) && !context.Request.QueryString.HasValue &&
                context.Request.Path.Value is "/internal/ingest" or "/api/v1/orchestration/provider/callback";
            if (possibleRotationFaults.Length != 0)
            {
                observedRequest = await ReadBoundedAsync(context.Request.Body, 131_072, context.RequestAborted);
                var lifecycle = ServiceLinkCanonicalJson.Deserialize<ServiceLinkLifecycleRequest>(Encoding.UTF8.GetString(observedRequest));
                lock (observationSync)
                    rotationFault = possibleRotationFaults.SingleOrDefault(x => x.Matches(lifecycle));
                if (rotationFault is not null && !rotationFault.TryClaim(lifecycle))
                {
                    // After losing the committed response, hold identical recovery delivery
                    // unavailable until the test has restarted the real products.
                    // RequestAborted retains each product's existing transport deadline
                    // (20 seconds for NetRatel). Avoid immediate 502 token-rate loops.
                    await rotationFault.WaitForReleaseAsync(context.RequestAborted);
                    rotationFault = null;
                }
                forwardedRequest = observedRequest.ToArray();
                request.Content = new ByteArrayContent(forwardedRequest);
            }
            else if (observation is not null)
            {
                observedRequest = await ReadBoundedAsync(context.Request.Body, observation.MaximumPayloadBytes, context.RequestAborted);
                observation.RequestBytes = observedRequest;
                // Replay/observation forwards the original bytes, never a reserialized DTO.
                forwardedRequest = observedRequest.ToArray();
                request.Content = new ByteArrayContent(forwardedRequest);
            }
            else if (context.Request.Path == "/connect/token")
            {
                using var body = new StreamReader(context.Request.Body, Encoding.UTF8, leaveOpen: true);
                var payload = await body.ReadToEndAsync(context.RequestAborted);
                var form = QueryHelpers.ParseQuery(payload);
                if (form.TryGetValue("client_id", out var clientId) && form.TryGetValue("client_secret", out var secret))
                    oauthClient = new() { ClientId = clientId.ToString(), Secret = secret.ToString() };
                forwardedRequest = Encoding.UTF8.GetBytes(payload);
                request.Content = new ByteArrayContent(forwardedRequest);
            }
            else if (observeBusiness)
            {
                businessRequest = await ReadBoundedAsync(context.Request.Body, 131_072, context.RequestAborted);
                using var parsed = JsonDocument.Parse(businessRequest);
                if (parsed.RootElement.ValueKind != JsonValueKind.Object)
                    throw new InvalidOperationException("The actual synthetic business request was not a JSON object.");
                forwardedRequest = businessRequest.ToArray();
                request.Content = new ByteArrayContent(forwardedRequest);
            }
            else if (context.Request.ContentLength is > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding"))
                request.Content = new StreamContent(context.Request.Body);
            foreach (var header in context.Request.Headers)
            {
                if (header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase)) continue;
                if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()))
                    request.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
            }
            using var response = await forward.SendAsync(request, HttpCompletionOption.ResponseContentRead, context.RequestAborted);
            if (rotationFault is not null)
            {
                if (!response.IsSuccessStatusCode)
                {
                    await using var responseBody = await response.Content.ReadAsStreamAsync(context.RequestAborted);
                    observedResponse = await ReadBoundedAsync(responseBody, 131_072, context.RequestAborted);
                    var problemCode = ReadSafeProblemCode(observedResponse);
                    // Retain only a fixed public protocol code in the failure. Forward
                    // the original error bytes and erase both private buffers below.
                    forwardedResponse = observedResponse.ToArray();
                    var originalContent = response.Content;
                    var headers = originalContent.Headers.ToArray();
                    response.Content = new ByteArrayContent(forwardedResponse);
                    foreach (var header in headers) response.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                    originalContent.Dispose();
                    rotationFault.Fail((int)response.StatusCode, problemCode);
                }
                else
                {
                    await using var responseBody = await response.Content.ReadAsStreamAsync(context.RequestAborted);
                    observedResponse = await ReadBoundedAsync(responseBody, 131_072, context.RequestAborted);
                    var accepted = new ServiceLinkObservedOperation(observedRequest!, observedResponse);
                    rotationFault.Complete(accepted);
                    // Ownership now belongs to the disposable private accepted operation.
                    observedRequest = null;
                    observedResponse = null;
                    LostResponses++;
                    // Backend success is observed only after the actual handler's transaction
                    // has committed. Never discard the request or synthesize a protocol result.
                    context.Abort();
                    return;
                }
            }
            if (observation is not null)
            {
                if (response.IsSuccessStatusCode)
                {
                    await using var responseBody = await response.Content.ReadAsStreamAsync(context.RequestAborted);
                    observedResponse = await ReadBoundedAsync(responseBody, observation.MaximumPayloadBytes, context.RequestAborted);
                    var accepted = new ServiceLinkObservedOperation(observedRequest!, observedResponse);
                    forwardedResponse = observedResponse.ToArray();
                    lock (observationSync)
                    {
                        completedObservations.Add(accepted);
                        pendingObservation = null;
                    }
                    observedRequest = null;
                    observedResponse = null;
                    observation.Completion.TrySetResult(accepted);
                    // Keep the original response available to the caller after the observation read.
                    var originalContent = response.Content;
                    var headers = originalContent.Headers.ToArray();
                    response.Content = new ByteArrayContent(forwardedResponse);
                    foreach (var header in headers) response.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                    originalContent.Dispose();
                }
                else
                {
                    // A co-armed rotation fault already owns the bounded capture.
                    // Reuse it without losing clearing ownership, and retain this
                    // observer's original independent payload bound.
                    if (observedResponse is null)
                    {
                        await using var responseBody = await response.Content.ReadAsStreamAsync(context.RequestAborted);
                        observedResponse = await ReadBoundedAsync(responseBody, observation.MaximumPayloadBytes, context.RequestAborted);
                    }
                    else if (observedResponse.Length > observation.MaximumPayloadBytes)
                        throw new InvalidOperationException("The private peer observation exceeded the configured payload bound.");
                    var problemCode = ReadSafeProblemCode(observedResponse);
                    // Forward the actual error bytes unchanged; diagnostics expose
                    // only the bounded protocol code, never a body or credential.
                    forwardedResponse ??= observedResponse.ToArray();
                    var originalContent = response.Content;
                    var headers = originalContent.Headers.ToArray();
                    response.Content = new ByteArrayContent(forwardedResponse);
                    foreach (var header in headers) response.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                    originalContent.Dispose();
                    lock (observationSync) pendingObservation = null;
                    observation.Completion.TrySetException(new InvalidOperationException($"The observed actual peer operation returned HTTP {(int)response.StatusCode} (code={problemCode ?? "unclassified"})."));
                }
            }
            if (observeBusiness)
            {
                await using var responseBody = await response.Content.ReadAsStreamAsync(context.RequestAborted);
                var responseBytes = await ReadBoundedAsync(responseBody, 131_072, context.RequestAborted);
                try
                {
                    var accepted = new ServiceLinkBusinessObservation(context.Request.Path.Value!, (int)response.StatusCode,
                        new UTF8Encoding(false, true).GetString(businessRequest!),
                        responseBytes.Length == 0 ? null : new UTF8Encoding(false, true).GetString(responseBytes));
                    lock (observationSync)
                    {
                        if (businessObservations.Count >= 128)
                            throw new InvalidOperationException("The bounded synthetic business observation count was exceeded.");
                        businessObservations.Enqueue(accepted);
                    }
                    // Preserve exact product UTF-8 response bytes and the existing 204/304/HEAD rule.
                    var originalContent = response.Content;
                    var headers = originalContent.Headers.ToArray();
                    forwardedResponse = responseBytes.ToArray();
                    response.Content = new ByteArrayContent(forwardedResponse);
                    foreach (var header in headers) response.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                    originalContent.Dispose();
                }
                finally { Array.Clear(businessRequest!); Array.Clear(responseBytes); }
                // Only synthetic business bodies/statuses are retained. No headers, cookies,
                // OAuth material, browser proofs or enrollment/device credentials enter this observer.
            }
            // Observe only an actually accepted OAuth request. This material stays in memory and is never logged/artifacted.
            if (response.IsSuccessStatusCode && oauthClient is not null)
                ObservedClients[oauthClient.ClientId] = oauthClient;
            if (response.IsSuccessStatusCode && context.Request.Path.Value?.EndsWith(lostResponseSuffix ?? "\0", StringComparison.Ordinal) == true &&
                Interlocked.CompareExchange(ref lossPending, 0, 1) == 1)
            {
                LostResponses++;
                // The real product has finished its handler and committed; discard its response, never its request.
                context.Abort();
                return;
            }
            context.Response.StatusCode = (int)response.StatusCode;
            foreach (var header in response.Headers.Concat(response.Content.Headers))
                if (!header.Key.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
                    context.Response.Headers[header.Key] = header.Value.ToArray();
            if (!HttpMethods.IsHead(context.Request.Method) && (int)response.StatusCode >= 200 &&
                response.StatusCode is not HttpStatusCode.NoContent and not HttpStatusCode.NotModified)
                await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
        }
        finally
        {
            // Erase every raw array still owned by this forwarding operation on
            // success, cancellation, release timeout, parse/send/read or copy failure.
            if (observedRequest is not null) Array.Clear(observedRequest);
            if (observedResponse is not null) Array.Clear(observedResponse);
            if (forwardedRequest is not null) Array.Clear(forwardedRequest);
            if (forwardedResponse is not null) Array.Clear(forwardedResponse);
            if (businessRequest is not null) Array.Clear(businessRequest);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref terminalDelivery, null)?.Released.TrySetResult(true);
        if (app is not null) await app.DisposeAsync();
        lock (observationSync)
        {
            pendingObservation?.Completion.TrySetCanceled();
            if (pendingObservation?.RequestBytes is { } pendingBytes) Array.Clear(pendingBytes);
            pendingObservation = null;
            foreach (var observation in completedObservations) observation.Dispose();
            completedObservations.Clear();
            foreach (var fault in rotationResponseFaults.ToArray()) fault.Dispose();
            rotationResponseFaults.Clear();
            ObservedClients.Clear();
            while (businessObservations.TryDequeue(out _)) { }
        }
        forward.Dispose();
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, int maximum, CancellationToken ct)
    {
        using var memory = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        try
        {
            while ((read = await stream.ReadAsync(buffer, ct)) != 0)
            {
                if (memory.Length + read > maximum) throw new InvalidOperationException("The private peer observation exceeded the configured payload bound.");
                memory.Write(buffer, 0, read);
            }
            return memory.ToArray();
        }
        finally
        {
            Array.Clear(buffer);
            if (memory.TryGetBuffer(out var retained)) Array.Clear(retained.Array!);
        }
    }

    private sealed class PendingObservation(string path, int maximumPayloadBytes)
    {
        public string Path { get; } = path;
        public int MaximumPayloadBytes { get; } = maximumPayloadBytes;
        public bool Claimed { get; set; }
        public byte[]? RequestBytes { get; set; }
        public TaskCompletionSource<ServiceLinkObservedOperation> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class TerminalDeliveryGate
    {
        public TaskCompletionSource<bool> Arrived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Released { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class TerminalDeliveryPause(ServiceLinkHttpProxy owner, TerminalDeliveryGate pause) : IDisposable
    {
        public void Dispose()
        {
            Interlocked.CompareExchange(ref owner.terminalDelivery, null, pause);
            pause.Released.TrySetResult(true);
        }
    }
}

/// <summary>Private memory only: never include this accepted payload in test output or proof artifacts.</summary>
internal sealed class ServiceLinkObservedOperation(byte[] request, byte[] response) : IDisposable
{
    private bool disposed;
    public T ReadRequest<T>()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return ServiceLinkCanonicalJson.Deserialize<T>(Encoding.UTF8.GetString(request));
    }
    public bool ResponseMatches(string actualJson)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return string.Equals(ServiceLinkCanonicalJson.Canonicalize(Encoding.UTF8.GetString(response)),
            ServiceLinkCanonicalJson.Canonicalize(actualJson), StringComparison.Ordinal);
    }
    public T ReadResponse<T>()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return ServiceLinkCanonicalJson.Deserialize<T>(Encoding.UTF8.GetString(response));
    }
    public void Dispose()
    {
        Array.Clear(request);
        Array.Clear(response);
        disposed = true;
    }
}

/// <summary>Private successful phase bytes are erased on disposal; no bearer or secret is exported.</summary>
internal sealed class ServiceLinkRotationResponseFault(ServiceLinkHttpProxy owner, string path, string phase, string? rotationId,
    ServiceLinkRotationResponseFault? bindToOffer) : IDisposable
{
    private int claimed;
    private string? requestFingerprint;
    private readonly object recoverySync = new();
    private long recoveryGeneration;
    private long awaitedRecoveryGeneration;
    private TaskCompletionSource<long>? postRestartRecovery;
    private int postRestartRecoveriesProven;
    private bool disposed;
    private ServiceLinkObservedOperation? accepted;
    public string Path { get; } = path;
    public string Phase { get; } = phase;
    private string? boundRotationId = rotationId;
    public string? RotationId => boundRotationId ?? bindToOffer?.RotationId;
    private readonly TaskCompletionSource<ServiceLinkObservedOperation> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<ServiceLinkObservedOperation> Completed => completion.Task;
    public int PostRestartRecoveriesProven { get { lock (recoverySync) return postRestartRecoveriesProven; } }
    public Task<long> ObserveRecoveryAfterRestart()
    {
        lock (recoverySync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (postRestartRecovery is not null)
                throw new InvalidOperationException("A post-restart recovery generation is already armed.");
            awaitedRecoveryGeneration = checked(recoveryGeneration + 1);
            postRestartRecovery = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return postRestartRecovery.Task;
        }
    }
    internal bool Matches(ServiceLinkLifecycleRequest request)
    {
        if ((Phase == "verify" ? request.RotationPhase is not null : request.RotationPhase != Phase) ||
            request.RotationId is null || Phase != "offer" && RotationId is null ||
            RotationId is not null && request.RotationId != RotationId)
            return false;
        if (Phase == "offer") boundRotationId ??= request.RotationId;
        return true;
    }
    internal bool TryClaim(ServiceLinkLifecycleRequest request)
    {
        var fingerprint = ServiceLinkLifecycleProjection.Hash(Phase == "verify" ? "verify" : "rotate", request);
        lock (recoverySync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (claimed == 0) { claimed = 1; requestFingerprint = fingerprint; return true; }
            if (requestFingerprint != fingerprint)
                throw new InvalidOperationException("The real background recovery changed a durable rotation operation's semantic payload.");
            recoveryGeneration = checked(recoveryGeneration + 1);
            if (postRestartRecovery is not null && recoveryGeneration >= awaitedRecoveryGeneration)
            {
                postRestartRecoveriesProven++;
                postRestartRecovery.TrySetResult(recoveryGeneration);
                postRestartRecovery = null;
            }
            return false;
        }
    }
    internal Task WaitForReleaseAsync(CancellationToken ct) => released.Task.WaitAsync(TimeSpan.FromSeconds(25), ct);
    internal void Complete(ServiceLinkObservedOperation value)
    {
        lock (recoverySync)
        {
            if (disposed || !completion.TrySetResult(value))
            {
                value.Dispose();
                return;
            }
            accepted = value;
        }
    }
    internal void Fail(int status, string? problemCode) => completion.TrySetException(new InvalidOperationException($"The actual rotation phase returned HTTP {status} (code={problemCode ?? "unclassified"}) before the response-loss point."));
    public void Release() { owner.Release(this); released.TrySetResult(true); }
    public void Dispose()
    {
        ServiceLinkObservedOperation? detached;
        lock (recoverySync)
        {
            if (disposed) return;
            disposed = true;
            detached = accepted;
            accepted = null;
            completion.TrySetCanceled();
            postRestartRecovery?.TrySetCanceled();
            postRestartRecovery = null;
        }
        // Owner removal takes observationSync; keep it outside recoverySync
        // because the proxy's own cleanup enters these locks in the other order.
        Release();
        detached?.Dispose();
    }
}

internal sealed class ServiceLinkObservedClient
{
    public required string ClientId { get; init; }
    public required string Secret { get; init; }
}

internal sealed record ServiceLinkBusinessObservation(string Path, int StatusCode, string RequestJson, string? ResponseJson);
