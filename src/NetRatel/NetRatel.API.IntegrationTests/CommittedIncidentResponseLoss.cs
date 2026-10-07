// Fault injection in the actual transparent peer fixture. A loss is successful
// only after an independent committed-row read of the original production action.
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace NetRatel.API.IntegrationTests.ServiceLinks;

internal sealed record PhysicalReceiptIdentity(Guid SourceInstanceId, Guid NamespaceId, string Key,
    string Fingerprint, string IncidentId, Guid ReceiptId, Guid ConfirmationEffectId);
internal sealed record PhysicalIncidentForwarding(Guid SourceInstanceId, string Key, string RequestBytesSha256);

internal sealed class CommittedIncidentResponseLoss : IAsyncDisposable
{
    private readonly object sync = new();
    private readonly Action? onDispose;
    private readonly string createPath;
    private readonly string receiptPrefix;
    private readonly Guid approvedSource;
    private readonly Guid approvedNamespace;
    private readonly Func<PhysicalIncidentForwarding, JsonElement, CancellationToken, Task<PhysicalReceiptIdentity>> independentCommittedRead;
    private readonly TaskCompletionSource<PhysicalReceiptIdentity> committed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<PhysicalIncidentForwarding> restartedRecovery = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private PhysicalIncidentForwarding? claimed;
    private bool firstClaimed;
    private bool processesRestarted;
    private bool disposed;

    public CommittedIncidentResponseLoss(Uri actualApprovedPeerApiBase, Guid source, Guid receiverNamespace,
        Func<PhysicalIncidentForwarding, JsonElement, CancellationToken, Task<PhysicalReceiptIdentity>> independentCommittedRead,
        Action? onDispose = null)
    {
        if (!actualApprovedPeerApiBase.IsAbsoluteUri || actualApprovedPeerApiBase.UserInfo.Length != 0 ||
            actualApprovedPeerApiBase.Query.Length != 0 || actualApprovedPeerApiBase.Fragment.Length != 0 ||
            source == Guid.Empty || receiverNamespace == Guid.Empty)
            throw new ArgumentException("An actual current approved peer and source/receiver namespace are required.");
        // Match the exact receiver contract paths with the approved path base.
        var pathBase = actualApprovedPeerApiBase.AbsolutePath.TrimEnd('/');
        createPath = pathBase + "/api/v1/incidents/";
        receiptPrefix = pathBase + "/api/v1/integrations/netratel/incident-receipts/";
        approvedSource = source;
        approvedNamespace = receiverNamespace;
        this.independentCommittedRead = independentCommittedRead;
        this.onDispose = onDispose;
    }

    public bool Matches(HttpContext context) => !context.Request.QueryString.HasValue &&
        (HttpMethods.IsPost(context.Request.Method) && context.Request.Path == createPath ||
         HttpMethods.IsGet(context.Request.Method) && context.Request.Path.Value?.StartsWith(receiptPrefix, StringComparison.Ordinal) == true);

    public Task<PhysicalReceiptIdentity> Committed => committed.Task;
    public Task<PhysicalIncidentForwarding> PostRestartRecovery => restartedRecovery.Task;

    // The transparent proxy has already bounded a candidate request to131072
    // bytes and forwards those exact bytes. No bearer or arbitrary JSON survives.
    public async Task<bool> BeforeForwardAsync(HttpContext context, ReadOnlyMemory<byte> exactRequestBytes)
    {
        if (context.Request.QueryString.HasValue) return false;
        var isCreate = HttpMethods.IsPost(context.Request.Method) && context.Request.Path == createPath;
        var isLookup = HttpMethods.IsGet(context.Request.Method) && context.Request.Path.Value?.StartsWith(receiptPrefix, StringComparison.Ordinal) == true;
        if (!isCreate && !isLookup) return false;
        var sourceValues = context.Request.Headers["X-NetRatel-Source-Instance"];
        if (sourceValues.Count != 1 || !Guid.TryParseExact(sourceValues[0], "D", out var source) || source != approvedSource)
            return false;
        var keyValues = context.Request.Headers["Idempotency-Key"];
        var key = isCreate ? keyValues.Count == 1 ? keyValues[0] : null :
            context.Request.Path.Value![receiptPrefix.Length..];
        if (!ValidKey(key)) return false;
        var observed = new PhysicalIncidentForwarding(source, key!,
            Convert.ToHexStringLower(SHA256.HashData(exactRequestBytes.Span)));
        bool first;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!firstClaimed)
            {
                if (!isCreate) return false;
                firstClaimed = true;
                claimed = observed;
                first = true;
            }
            else
            {
                if (claimed!.SourceInstanceId != source || claimed.Key != key) return false;
                if (isCreate && observed.RequestBytesSha256 != claimed.RequestBytesSha256)
                    throw new InvalidOperationException("The same actual receiver key was retried with changed body bytes.");
                if (processesRestarted) restartedRecovery.TrySetResult(observed);
                first = false;
            }
        }
        if (first) return true; // HookAfterActualResponse applies only to this claim.
        // Keep the original production transport deadline and cancellation; do
        // not manufacture502/404, change retries, or bypass an HTTP limiter.
        await released.Task.WaitAsync(context.RequestAborted);
        return false;
    }

    public async Task AfterActualResponseAsync(HttpContext downstream, HttpResponseMessage actualUpstream,
        ReadOnlyMemory<byte> exactResponseBytes, bool isClaimedFirstCreate)
    {
        if (!isClaimedFirstCreate) return;
        try
        {
            if (actualUpstream.StatusCode != HttpStatusCode.Created || exactResponseBytes.Length is <= 0 or > 131072)
                throw new InvalidOperationException("The actual first receiver create did not return bounded HTTP201.");
            using var response = JsonDocument.Parse(exactResponseBytes, new JsonDocumentOptions { MaxDepth = 32 });
            var receipt = response.RootElement.GetProperty("integrationReceipt");
            PhysicalIncidentForwarding request;
            lock (sync) request = claimed ?? throw new InvalidOperationException("The receiver claim disappeared.");
            if (receipt.GetProperty("contractVersion").GetString() != "rateldesk.incident-create.v1" ||
                receipt.GetProperty("sourceInstanceId").GetString() != approvedSource.ToString("D") ||
                receipt.GetProperty("sourceNamespaceId").GetString() != approvedNamespace.ToString("D") ||
                receipt.GetProperty("key").GetString() != request.Key || receipt.GetProperty("outcome").GetString() != "committed")
                throw new InvalidOperationException("The actual receiver result differs from the approved action identity.");
            // This delegate must use a new read-only PostgreSQL scope and the
            // actual durable V2 action/receipt/incident/effects rows. No transaction
            // or DB lock spans HTTP; receipt fake/seed/response-only proof is forbidden.
            var proof = await independentCommittedRead(request, response.RootElement, downstream.RequestAborted);
            if (proof.SourceInstanceId != approvedSource || proof.NamespaceId != approvedNamespace ||
                proof.Key != request.Key || proof.Fingerprint != receipt.GetProperty("fingerprint").GetString() ||
                proof.IncidentId != receipt.GetProperty("incidentId").GetString() || proof.ReceiptId == Guid.Empty ||
                proof.ConfirmationEffectId == Guid.Empty)
                throw new InvalidOperationException("The independent durable receipt/incident/confirmation read did not match.");
            // Complete only after the real receiver201 and independent committed
            // row read. The original response has not been copied downstream.
            downstream.Abort();
            committed.TrySetResult(proof);
        }
        catch (Exception error)
        {
            committed.TrySetException(new InvalidOperationException("Actual after-commit loss proof failed.", error));
            throw; // This is a failed acceptance attempt, never a successful loss.
        }
    }

    public void MarkActualProcessesRestarted()
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!committed.Task.IsCompletedSuccessfully)
                throw new InvalidOperationException("Actual receiver commit/loss must precede the verified process restart fence.");
            processesRestarted = true;
        }
    }

    public void Release() => released.TrySetResult();
    private static bool ValidKey(string? value) => value is { Length: > 0 and <= 256 } &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '~' or '-');

    public ValueTask DisposeAsync()
    {
        lock (sync)
        {
            if (disposed) return ValueTask.CompletedTask;
            disposed = true;
            claimed = null;
            released.TrySetResult();
            committed.TrySetCanceled();
            restartedRecovery.TrySetCanceled();
        }
        onDispose?.Invoke();
        return ValueTask.CompletedTask;
    }
}
