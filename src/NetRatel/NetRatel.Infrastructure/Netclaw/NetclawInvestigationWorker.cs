using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NetRatel.Application.Netclaw;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.Netclaw;
using NetRatel.Shared.Contracts.Flows;
using Npgsql;

namespace NetRatel.Infrastructure.Netclaw;

/// <summary>One fenced PostgreSQL transport owner; committed queued messages survive restart without duplicate sends.</summary>
public sealed partial class NetclawInvestigationWorker(IServiceScopeFactory scopes, IAiAssistantChatClientFactory clients,
    TimeProvider clock, ILogger<NetclawInvestigationWorker> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<Guid, SessionOwner> owners = new();
    private readonly SemaphoreSlim acquisition = new(1);
    private NpgsqlConnection? ownership;
    private CancellationToken stopping;
    private volatile bool ready;

    private sealed class SessionOwner(IAiAssistantChatClient client, NetclawRuntimeProfile profile, Guid? messageId)
    {
        public IAiAssistantChatClient Client { get; } = client;
        public NetclawRuntimeProfile Profile { get; } = profile;
        public Guid? MessageId { get; } = messageId;
        public string SessionId { get; set; } = string.Empty;
        public DateTimeOffset AuthenticatedAtUtc { get; set; }
        public Channel<JsonElement> Outputs { get; } = Channel.CreateBounded<JsonElement>(256);
        public Task Consumer { get; set; } = Task.CompletedTask;
        public TaskCompletionSource Joined { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public volatile bool Retired;
        public volatile bool Overflowed;
        public bool AdmissionProven;
        public bool SendStarted;
        public long LastObservedTicks = DateTimeOffset.UtcNow.UtcTicks;
        private readonly StringBuilder draft = new();
        private readonly object draftLock = new();
        public string Draft { get { lock (draftLock) return draft.ToString(); } }
        public void AddDraft(string? text) { lock (draftLock) if (text is not null && draft.Length < 128000) draft.Append(text.AsSpan(0, Math.Min(text.Length, 128000 - draft.Length))); }
        public void ClearDraft() { lock (draftLock) draft.Clear(); }
    }

    public string? Draft(Guid id) => owners.TryGetValue(id, out var owner) && !owner.Retired ? owner.Draft : null;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        stopping = stoppingToken;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2), clock);
        try
        {
            do
            {
                try { await TickAsync(stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception exception)
                {
                    logger.LogWarning("Netclaw transport worker is recovering: {ExceptionType}", exception.GetType().Name);
                    await ReleaseAsync();
                }
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        { logger.LogDebug("Netclaw transport worker stopped."); return; }
        finally { await ReleaseAsync(); }
    }

    private async Task<bool> OwnAsync(CancellationToken ct)
    {
        if (ready) return true;
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        // Native SignalR chat uses PostgreSQL row locks and a dedicated, non-pooled advisory owner socket.
        if (db.Database.GetDbConnection() is not NpgsqlConnection connection) return false;
        var candidate = connection.CloneWith(new NpgsqlConnectionStringBuilder(connection.ConnectionString) { Pooling = false }.ConnectionString);
        try
        {
            await candidate.OpenAsync(ct);
            await using var command = new NpgsqlCommand("SELECT pg_try_advisory_lock(823, 1)", candidate);
            if (await command.ExecuteScalarAsync(ct) is not true) { await candidate.DisposeAsync(); return false; }
            ownership = candidate;
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var interrupted = await db.Set<AiInvestigationRecord>().FromSqlInterpolated(
                $"SELECT * FROM \"AiInvestigations\" WHERE \"State\" = {(int)InvestigationState.Processing} OR \"State\" = {(int)InvestigationState.AwaitingApproval} FOR UPDATE").ToListAsync(ct);
            foreach (var row in interrupted)
            {
                row.State = InvestigationState.DeliveryUnknown;
                InvestigationStore.Append(db, row, "transport_restarted", "The API restarted during this turn. Recover the existing session to check its outcome.");
                await InvestigationStore.StageTurnResultAsync(db, row, FlowAiResultStatus.DeliveryUnknown, clock.GetUtcNow(), ct);
            }
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); ready = true;
            logger.LogInformation(new EventId(1, "TransportOwnershipAcquired"),
                "Netclaw transport owner is ready; {InterruptedCount} interrupted investigations require recovery.", interrupted.Count);
            return true;
        }
        catch { await candidate.DisposeAsync(); ownership = null; throw; }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        if (!await OwnAsync(ct)) return;
        if (!await StillOwnAsync(ct)) throw new InvalidOperationException("Netclaw transport ownership was lost.");
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var queued = await db.Set<AiInvestigationRecord>().AsNoTracking().Where(x => x.State == InvestigationState.Queued)
            .OrderBy(x => x.LastActivityUtc).Take(4).Select(x => new { x.Id, x.TenantId }).ToListAsync(ct);
        // Bound both concurrency and each pass: one unreachable daemon cannot block 25 sequential deadlines.
        await Task.WhenAll(queued.Select(row => DispatchQueuedAsync(row.TenantId, row.Id, ct)));
        var profiles = scope.ServiceProvider.GetRequiredService<NetclawProfileService>();
        foreach (var (id, owner) in owners.ToArray())
        {
            var profile = await profiles.ResolveAsync(owner.Profile.TenantId, ct);
            var inactive = clock.GetUtcNow().UtcTicks - Interlocked.Read(ref owner.LastObservedTicks) > TimeSpan.FromMinutes(5).Ticks;
            if (profile?.Revision == owner.Profile.Revision && owner.Client.IsConnected && !owner.Consumer.IsCompleted && !owner.Overflowed && !inactive) continue;
            await UnknownAsync(id, owner.MessageId, "The assistant connection changed or stopped reporting activity. Recover this session to check its outcome.", ct, true);
            await RetireAsync(id);
        }
    }

    private async Task DispatchQueuedAsync(int tenantId, Guid id, CancellationToken ct)
    {
        Guid? messageId = null;
        var sendAttempted = false;
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var provider = scope.ServiceProvider; var db = provider.GetRequiredService<OrchestratorDbContext>();
            AiInvestigationRecord row; AiInvestigationEventRecord message;
            await using (var tx = await db.Database.BeginTransactionAsync(ct))
            {
                row = await db.Set<AiInvestigationRecord>().FromSqlInterpolated($"SELECT * FROM \"AiInvestigations\" WHERE \"Id\" = {id} FOR UPDATE").SingleAsync(ct);
                if (row.State != InvestigationState.Queued || row.ActiveMessageId is null) return;
                messageId = row.ActiveMessageId;
                message = await db.Set<AiInvestigationEventRecord>().SingleAsync(x => x.InvestigationId == id && x.MessageId == messageId && x.Type == "operator", ct);
                if (!await AuthorizedAsync(provider, row, message.Actor!, ct))
                {
                    row.State = InvestigationState.Idle;
                    await InvestigationStore.StageTurnResultAsync(db, row, FlowAiResultStatus.Failed, clock.GetUtcNow(), ct);
                    InvestigationStore.Append(db, row, "dispatch_denied", "Current tenant permissions no longer allow this request.");
                    await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return;
                }
                await provider.GetRequiredService<InvestigationStore>().ValidateClientsAsync(tenantId, row.AgentIds, ct);
                row.State = InvestigationState.Processing; row.LastTransportActivityAtUtc = clock.GetUtcNow();
                await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var owner = await GetAsync(tenantId, id, timeout.Token);
            var store = provider.GetRequiredService<InvestigationStore>();
            InvestigationSentContextDto context;
            try { context = await store.ComposeAsync(row, messageId.Value, message.Text, owner.SessionId, ct); }
            catch (ArgumentException exception)
            {
                await NotSentAsync(id, messageId, exception.Message, ct);
                await RetireAsync(id); return;
            }
            var request = context.Request;
            if (!await db.Set<AiInvestigationRecord>().AsNoTracking().AnyAsync(x => x.Id == id && x.State == InvestigationState.Processing && x.ActiveMessageId == messageId, ct)) return;
            // Authorization and the pinned connection can change while session negotiation is awaited.
            if (!await AuthorizedAsync(provider, row, message.Actor!, ct) ||
                !await db.Set<NetclawConnectionRecord>().AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.Enabled && x.Revision == owner.Profile.Revision, ct) ||
                !await AdmitAutomaticMessageAsync(provider, row, owner.Profile, ct) || !await StillOwnAsync(ct))
            {
                await NotSentAsync(id, messageId, "The connection, permissions or monitoring state changed before the message was sent.", ct);
                await RetireAsync(id); return;
            }
            await store.ValidateClientsAsync(tenantId, row.AgentIds, ct);
            await store.RecordContextAsync(id, context with { SendAttemptedAtUtc = clock.GetUtcNow() }, ct);
            sendAttempted = true; owner.SendStarted = true;
            await owner.Client.SendAsync(owner.SessionId, request, timeout.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            logger.LogWarning("Netclaw message delivery unresolved for {InvestigationId}: {ExceptionType}", id, exception.GetType().Name);
            if (sendAttempted) await UnknownAsync(id, messageId, "Delivery could not be confirmed. Recover the existing session before taking another action.", ct);
            else await NotSentAsync(id, messageId, "The message was saved but could not be sent. Check the Netclaw connection and send a new turn when ready.", ct);
            await RetireAsync(id);
        }
    }

    private async Task<SessionOwner> GetAsync(int tenantId, Guid id, CancellationToken ct)
    {
        if (!ready) throw new InvestigationConflictException("The chat transport owner is temporarily unavailable.");
        await acquisition.WaitAsync(ct);
        try
        {
            await using var scope = scopes.CreateAsyncScope(); var provider = scope.ServiceProvider;
            var db = provider.GetRequiredService<OrchestratorDbContext>();
            var row = await db.Set<AiInvestigationRecord>().AsNoTracking().SingleAsync(x => x.TenantId == tenantId && x.Id == id, ct);
            var profile = await provider.GetRequiredService<NetclawProfileService>().ResolveAsync(tenantId, ct)
                ?? throw new InvestigationConflictException("The tenant Netclaw connection is unavailable.");
            if (row.State == InvestigationState.Archived) throw new InvestigationConflictException("This investigation is archived.");
            if (row.ProfileFingerprint is not null && row.ProfileFingerprint != profile.ProfileFingerprint)
                throw new InvestigationConflictException("This investigation belongs to another Netclaw daemon. Open a new investigation for this connection.");
            if (owners.TryGetValue(id, out var current))
            {
                if (!current.Retired && !current.Overflowed && current.MessageId == row.ActiveMessageId && current.Client.IsConnected && !current.Consumer.IsCompleted && current.Profile.Revision == profile.Revision) return current;
                await RetireAsync(id);
            }
            if (owners.Count >= 25) throw new InvestigationConflictException("The assistant is at connection capacity. Try again after another turn completes.");
            var owner = new SessionOwner(clients.Create(profile), profile, row.ActiveMessageId);
            try
            {
                var joined = await owner.Client.ConnectAsync(row.RemoteSessionId, output =>
                {
                    if (owner.Retired) return Task.CompletedTask;
                    Interlocked.Exchange(ref owner.LastObservedTicks, clock.GetUtcNow().UtcTicks);
                    if (!owner.Outputs.Writer.TryWrite(output.Clone())) owner.Overflowed = true;
                    return Task.CompletedTask;
                }, ct);
                if (string.IsNullOrWhiteSpace(joined.SessionId) || joined.SessionId.Length > 256 || owner.Overflowed ||
                    row.Automated && row.RemoteSessionId is null && !joined.Created ||
                    row.RemoteSessionId is not null && (joined.Created || row.RemoteSessionId != joined.SessionId))
                    throw new InvestigationConflictException("The original Netclaw session could not be resumed.");
                await using var tx = await db.Database.BeginTransactionAsync(ct);
                var binding = await db.Set<AiInvestigationRecord>().FromSqlInterpolated($"SELECT * FROM \"AiInvestigations\" WHERE \"Id\" = {id} FOR UPDATE").SingleAsync(ct);
                var revision = await db.Set<NetclawConnectionRecord>().AsNoTracking().Where(x => x.TenantId == tenantId && x.Enabled).Select(x => (long?)x.Revision).SingleOrDefaultAsync(ct);
                if (binding.State == InvestigationState.Archived || binding.ActiveMessageId != owner.MessageId || revision != profile.Revision ||
                    binding.ProfileFingerprint is not null && binding.ProfileFingerprint != profile.ProfileFingerprint ||
                    binding.RemoteSessionId is not null && binding.RemoteSessionId != joined.SessionId)
                    throw new InvestigationConflictException("The investigation or connection changed while the session connected.");
                binding.RemoteSessionId = joined.SessionId; binding.ProfileFingerprint = profile.ProfileFingerprint;
                owner.SessionId = joined.SessionId;
                owner.AuthenticatedAtUtc = clock.GetUtcNow();
                Interlocked.Exchange(ref owner.LastObservedTicks, owner.AuthenticatedAtUtc.UtcTicks);
                await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
                owners[id] = owner; owner.Consumer = ConsumeAsync(id, owner); return owner;
            }
            catch { owner.Retired = true; await owner.Client.DisposeAsync(); throw; }
        }
        finally { acquisition.Release(); }
    }

    public async Task ReconcileAsync(int tenantId, Guid id, string actor, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            var row = await db.Set<AiInvestigationRecord>().FromSqlInterpolated($"SELECT * FROM \"AiInvestigations\" WHERE \"TenantId\" = {tenantId} AND \"Id\" = {id} FOR UPDATE").SingleOrDefaultAsync(ct)
                ?? throw new KeyNotFoundException("Investigation not found.");
            if (row.State != InvestigationState.DeliveryUnknown || row.RemoteSessionId is null)
                throw new InvestigationConflictException("Only an unresolved turn with a saved remote session can be recovered.");
            InvestigationStore.Append(db, row, "recovery_requested", "Operator requested session history without resending the message.", actor);
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        }
        await RetireAsync(id);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var owner = await GetAsync(tenantId, id, timeout.Token);
        await owner.Joined.Task.WaitAsync(timeout.Token);
    }

    public async Task RespondAsync(int tenantId, Guid id, Guid messageId, string callId, string key, string actor, string? credential, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var owner = await GetAsync(tenantId, id, timeout.Token);
            await using var scope = scopes.CreateAsyncScope(); var provider = scope.ServiceProvider;
            var db = provider.GetRequiredService<OrchestratorDbContext>();
            if (owner.MessageId != messageId || !await provider.GetRequiredService<NetclawAuthority>().CanInvokeAsync(tenantId, actor, credential, false, ct) ||
                !await db.Set<AiInvestigationRecord>().AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.Id == id && x.ActiveMessageId == messageId &&
                    (x.State == InvestigationState.Processing || x.State == InvestigationState.AwaitingApproval), ct) ||
                !await db.Set<AiInvestigationApprovalRecord>().AsNoTracking().AnyAsync(x => x.InvestigationId == id && x.MessageId == messageId && x.CallId == callId && x.SelectedKey == key && x.Actor == actor, ct) ||
                !await db.Set<NetclawConnectionRecord>().AsNoTracking().AnyAsync(x => x.TenantId == tenantId && x.Enabled && x.Revision == owner.Profile.Revision, ct) || !await StillOwnAsync(ct))
                throw new InvestigationConflictException("The approval or current authority changed before delivery.");
            await owner.Client.RespondAsync(owner.SessionId, callId, key, timeout.Token);
        }
        catch (Exception exception)
        {
            logger.LogWarning("Netclaw approval delivery unresolved for {InvestigationId}: {ExceptionType}", id, exception.GetType().Name);
            using var completion = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await UnknownAsync(id, messageId, "The approval response could not be confirmed. Recover the session to check its outcome.", completion.Token, true);
            await RetireAsync(id);
        }
    }

    private async Task ConsumeAsync(Guid id, SessionOwner owner)
    {
        try
        {
            await foreach (var output in owner.Outputs.Reader.ReadAllAsync(stopping))
            {
                if (owner.Retired) return;
                if (owner.Overflowed || Value(output, "type") == "transport_closed") throw new IOException("Netclaw transport interrupted.");
                if (Value(output, "sessionId") != owner.SessionId) continue;
                var type = Value(output, "type");
                if (type == "text_delta") { owner.AddDraft(Value(output, "text")); continue; }
                if (type is "thinking" or "thinking_delta") continue;
                await using var scope = scopes.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
                await using var tx = await db.Database.BeginTransactionAsync(stopping);
                var row = await db.Set<AiInvestigationRecord>().FromSqlInterpolated($"SELECT * FROM \"AiInvestigations\" WHERE \"Id\" = {id} FOR UPDATE").SingleAsync(stopping);
                if (row.ActiveMessageId != owner.MessageId || row.RemoteSessionId != owner.SessionId || row.ProfileFingerprint != owner.Profile.ProfileFingerprint) continue;
                if (row.State == InvestigationState.Archived) { owner.Joined.TrySetResult(); continue; }
                if (type == "session_joined")
                {
                    if (row.State == InvestigationState.DeliveryUnknown)
                    {
                        owner.AdmissionProven = ProvesAdmission(output, id, row.ActiveMessageId);
                        InvestigationStore.Append(db, row, owner.AdmissionProven ? "recovery_admission_confirmed" : "recovery_inconclusive",
                            owner.AdmissionProven ? "Remote history confirms admission of this message. Waiting for completion evidence."
                                : "The session resumed but its history did not confirm admission of this message.");
                    }
                    await db.SaveChangesAsync(stopping); await tx.CommitAsync(stopping); owner.Joined.TrySetResult(); continue;
                }
                if (row.State == InvestigationState.DeliveryUnknown && !owner.AdmissionProven ||
                    row.State is InvestigationState.Idle or InvestigationState.Queued) continue;
                if (row.LastSequence >= 10000 && type != "turn_completed") throw new InvalidDataException("Investigation transcript capacity reached.");
                row.LastTransportActivityAtUtc = clock.GetUtcNow();
                switch (type)
                {
                    case "text":
                        InvestigationStore.Append(db, row, "assistant", Value(output, "text") ?? owner.Draft); owner.ClearDraft(); break;
                    case "turn_completed":
                        if (!(owner.SendStarted || owner.AdmissionProven)) break;
                        // The native output contract has no local message ID. Correlation comes from this isolated first
                        // automated session and its exact active owner/marker; unsupported remote fields add no authority.
                        if (owner.Draft.Length > 0) InvestigationStore.Append(db, row, "assistant", owner.Draft);
                        owner.ClearDraft(); row.State = InvestigationState.Idle;
                        InvestigationStore.Append(db, row, "turn_completed", "Investigation turn completed.");
                        await InvestigationStore.StageTurnResultAsync(db, row, FlowAiResultStatus.Completed, clock.GetUtcNow(), stopping,
                            Value(output, "summary"), Value(output, "evidence"), Value(output, "recommendations")); break;
                    case "tool_interaction":
                        var callId = Value(output, "callId");
                        if (string.IsNullOrWhiteSpace(callId) || callId.Length > 256) throw new InvalidDataException("Invalid approval identity.");
                        var previous = await db.Set<AiInvestigationApprovalRecord>().SingleOrDefaultAsync(x => x.InvestigationId == id && x.CallId == callId, stopping);
                        if (previous is not null)
                        {
                            if (row.State == InvestigationState.DeliveryUnknown && owner.AdmissionProven && previous.MessageId == row.ActiveMessageId && previous.SelectedKey is null)
                            {
                                row.State = InvestigationState.AwaitingApproval;
                                InvestigationStore.Append(db, row, "approval_resumed", "The resumed session confirmed this pending approval.", callId: callId);
                            }
                            break;
                        }
                        var options = OfferedOptions(output); if (options.Length == 0) throw new InvalidDataException("No supported approval options.");
                        var json = JsonSerializer.Serialize(options);
                        if (row.ActiveMessageId is not { } approvalMessage) throw new InvalidDataException("Approval has no active turn.");
                        if (await db.Set<AiInvestigationApprovalRecord>().CountAsync(x => x.InvestigationId == id && x.MessageId == approvalMessage && x.SelectedKey == null, stopping) >= 32)
                            throw new InvalidDataException("Too many pending approvals.");
                        db.Add(new AiInvestigationApprovalRecord { InvestigationId = id, MessageId = approvalMessage, CallId = callId, OptionsJson = json });
                        row.State = InvestigationState.AwaitingApproval;
                        InvestigationStore.Append(db, row, "approval_request", $"Approval requested for {SafeTool(Value(output, "toolName"))}.", callId: callId, options: json); break;
                    case "tool_call": case "tool_result":
                        InvestigationStore.Append(db, row, type, $"{SafeTool(Value(output, "toolName"))}: {(type == "tool_call" ? "started" : Value(output, "toolFailureCode") is null ? "completed" : "failed")}", callId: Bounded(Value(output, "callId"), 256)); break;
                    case "error":
                        row.State = InvestigationState.DeliveryUnknown;
                        InvestigationStore.Append(db, row, "error", "Netclaw reported an error. Recover the session or review the daemon logs with this investigation reference.");
                        await InvestigationStore.StageTurnResultAsync(db, row, FlowAiResultStatus.DeliveryUnknown, clock.GetUtcNow(), stopping); break;
                    case "file":
                        InvestigationStore.Append(db, row, "artifact", "Netclaw produced an artifact. Review it through the configured tool permissions."); break;
                }
                await db.SaveChangesAsync(stopping); await tx.CommitAsync(stopping);
                if (type == "turn_completed" && row.State == InvestigationState.Idle) { owners.TryRemove(new KeyValuePair<Guid, SessionOwner>(id, owner)); owner.Retired = true; owner.Outputs.Writer.TryComplete(); await owner.Client.DisposeAsync(); return; }
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { logger.LogDebug("Investigation output stopped."); owner.Retired = true; }
        catch (Exception exception)
        {
            owner.Retired = true; owner.Joined.TrySetException(new InvestigationConflictException("Remote session history could not be confirmed."));
            logger.LogWarning("Netclaw output interrupted for {InvestigationId}: {ExceptionType}", id, exception.GetType().Name);
            try { await UnknownAsync(id, owner.MessageId, "The assistant output was interrupted. Recover the session to check its outcome.", stopping, true); }
            catch (Exception failure) { logger.LogWarning("Investigation recovery deferred to restart: {ExceptionType}", failure.GetType().Name); owner.Outputs.Writer.TryComplete(failure); }
        }
    }

    private async Task UnknownAsync(Guid id, Guid? messageId, string reason, CancellationToken ct, bool includeApproval = false)
    {
        await using var scope = scopes.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var row = await db.Set<AiInvestigationRecord>().FromSqlInterpolated($"SELECT * FROM \"AiInvestigations\" WHERE \"Id\" = {id} FOR UPDATE").SingleAsync(ct);
        if ((row.State is InvestigationState.Processing or InvestigationState.Queued || includeApproval && row.State == InvestigationState.AwaitingApproval) &&
            (messageId is null || row.ActiveMessageId == messageId))
        {
            row.State = InvestigationState.DeliveryUnknown; InvestigationStore.Append(db, row, "delivery_unknown", reason);
            await InvestigationStore.StageTurnResultAsync(db, row, FlowAiResultStatus.DeliveryUnknown, clock.GetUtcNow(), ct);
            await db.SaveChangesAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    public async Task RetireAsync(Guid id)
    {
        if (!owners.TryRemove(id, out var owner)) return;
        owner.Retired = true; owner.Outputs.Writer.TryComplete(); owner.Joined.TrySetCanceled();
        try { await owner.Client.DisposeAsync(); }
        catch (Exception exception) { logger.LogWarning("Netclaw session disposal failed: {ExceptionType}", exception.GetType().Name); owner.Outputs.Writer.TryComplete(exception); }
        await owner.Consumer; owner.ClearDraft();
    }

    private async Task ReleaseAsync()
    {
        ready = false;
        foreach (var owner in owners.Values) owner.Retired = true;
        foreach (var id in owners.Keys) await RetireAsync(id);
        if (ownership is not null) { await ownership.DisposeAsync(); ownership = null; }
    }

    public static string MessageMarker(Guid id, Guid message) => $"[netratel-investigation:{id:D}:{message:D}]\n";
    public static bool ProvesAdmission(JsonElement output, Guid id, Guid? message) => message.HasValue &&
        output.TryGetProperty("recentMessages", out var history) && history.ValueKind == JsonValueKind.Array &&
        history.EnumerateArray().Count(x => Value(x, "role") == "user" && Value(x, "content")?.StartsWith(MessageMarker(id, message.Value), StringComparison.Ordinal) == true) == 1;
    public static InvestigationOptionDto[] OfferedOptions(JsonElement output) => output.TryGetProperty("interactionOptions", out var options) && options.ValueKind == JsonValueKind.Array
        ? options.EnumerateArray().Select(x => Value(x, "key")).Where(x => x is "approve_once" or "approve_session" or "deny").Distinct()
            .Select(x => new InvestigationOptionDto(x!, x switch { "approve_once" => "Allow once", "approve_session" => "Allow for this investigation", _ => "Deny" })).ToArray() : [];
    private static string? Value(JsonElement output, string name) => output.ValueKind == JsonValueKind.Object && output.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static string? Bounded(string? text, int maximum) => text is null ? null : text.Length <= maximum ? text : text[..maximum];
    private static string SafeTool(string? value) => value is { Length: > 0 and <= 128 } && value.All(x => char.IsAsciiLetterOrDigit(x) || x is '.' or '_' or '-' or '/') ? value : "tool";
}
