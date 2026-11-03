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

    private sealed record SessionUpdate(JsonElement? Output = null, SessionEnsureResult? Attachment = null);

    private sealed class SessionOwner(IAiAssistantChatClient client, NetclawRuntimeProfile profile, Guid? messageId, CancellationToken stoppingToken)
    {
        public IAiAssistantChatClient Client { get; } = client;
        public NetclawRuntimeProfile Profile { get; } = profile;
        public Guid? MessageId { get; } = messageId;
        public string SessionId { get; set; } = string.Empty;
        public bool Created { get; set; }
        public DateTimeOffset AuthenticatedAtUtc { get; set; }
        public Channel<SessionUpdate> Outputs { get; } = Channel.CreateBounded<SessionUpdate>(256);
        public CancellationTokenSource Lifetime { get; } = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        private readonly object lifetimeLock = new();
        private bool lifetimeDisposed;
        public void Cancel()
        {
            lock (lifetimeLock) if (!lifetimeDisposed) Lifetime.Cancel();
        }
        public void DisposeLifetime()
        {
            lock (lifetimeLock) { Lifetime.Dispose(); lifetimeDisposed = true; }
        }
        public Task Consumer { get; set; } = Task.CompletedTask;
        public TaskCompletionSource Joined { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public volatile bool Retired;
        public volatile bool Overflowed;
        public volatile bool AdmissionProven;
        public int TextAdmissionVersion;
        public int? TurnCountBeforeSend;
        public bool SendStarted;
        public JsonElement? PendingCompletion;
        public string? LastAssistantText;
        public int DiagnosticCount;
        public int DeltaDiagnosticCount;
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
            // Model calls and pending approvals can be silent for many minutes. Application
            // event silence is not a transport failure; the client owns transport liveness.
            if (profile?.Revision == owner.Profile.Revision && owner.Client.IsConnected && !owner.Consumer.IsCompleted && !owner.Overflowed) continue;
            await UnknownAsync(id, owner.MessageId, "The assistant connection changed or its output receiver stopped. Recover this session to check its outcome.", ct, true);
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
                await provider.GetRequiredService<InvestigationStore>().ValidateContextAsync(row, ct);
                row.State = InvestigationState.Processing; row.LastTransportActivityAtUtc = clock.GetUtcNow();
                await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var owner = await GetAsync(tenantId, id, timeout.Token);
            await owner.Joined.Task.WaitAsync(timeout.Token);
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
            await store.ValidateContextAsync(row, ct);
            await store.RecordContextAsync(id, context with { SendAttemptedAtUtc = clock.GetUtcNow() }, ct);
            sendAttempted = true; owner.SendStarted = true;
            await owner.Client.SendAsync(owner.SessionId, request, timeout.Token);
            // A successful RPC with negotiated admission v1 proves this exact explicit send.
            // It does not supply a missing pre-send turn baseline or prove terminal completion.
            if (owner.Created || owner.TextAdmissionVersion == 1 && owner.TurnCountBeforeSend is not null)
                owner.AdmissionProven = true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            logger.LogWarning("Netclaw message delivery unresolved for {InvestigationId}: {ExceptionType}", id, exception.GetType().Name);
            if (sendAttempted) await UnknownAsync(id, messageId, "Delivery could not be confirmed. Recover the existing session before taking another action.", ct);
            else await NotSentAsync(id, messageId, exception is NetclawAttachmentException ? exception.Message : "The message was saved but could not be sent. Check the Netclaw connection and send a new turn when ready.", ct);
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
            var owner = new SessionOwner(clients.Create(profile), profile, row.ActiveMessageId, stopping);
            try
            {
                var joined = await owner.Client.ConnectAsync(row.RemoteSessionId, output =>
                {
                    if (owner.Retired) return Task.CompletedTask;
                    Interlocked.Exchange(ref owner.LastObservedTicks, clock.GetUtcNow().UtcTicks);
                    if (!owner.Outputs.Writer.TryWrite(new(Output: output.Clone()))) owner.Overflowed = true;
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
                owner.Created = joined.Created;
                owner.TextAdmissionVersion = joined.TextAdmissionVersion;
                owner.TurnCountBeforeSend = binding.RemoteTurnCountBeforeSend;
                owner.AuthenticatedAtUtc = clock.GetUtcNow();
                Interlocked.Exchange(ref owner.LastObservedTicks, owner.AuthenticatedAtUtc.UtcTicks);
                await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
                // Serialize the RPC result after any initial snapshot. This is local
                // attachment evidence, never a fabricated daemon output or turn end.
                if (!owner.Outputs.Writer.TryWrite(new(Attachment: joined)))
                    throw new InvestigationConflictException("The session output exceeded its bounded buffer.");
                owners[id] = owner; owner.Consumer = ConsumeAsync(id, owner); return owner;
            }
            catch { owner.Retired = true; owner.DisposeLifetime(); await owner.Client.DisposeAsync(); throw; }
        }
        finally { acquisition.Release(); }
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
        var ct = owner.Lifetime.Token;
        try
        {
            await foreach (var update in owner.Outputs.Reader.ReadAllAsync(ct))
            {
                if (owner.Retired) return;
                if (owner.Overflowed) throw new IOException("Netclaw output buffer overflowed.");
                if (update.Attachment is { } attachment)
                {
                    await ConfirmAttachmentAsync(id, owner, attachment, ct);
                    continue;
                }
                var output = update.Output!.Value;
                if (Value(output, "type") == "transport_closed") throw new IOException("Netclaw transport interrupted.");
                var type = Value(output, "type");
                if (Value(output, "sessionId") != owner.SessionId)
                { TraceOutput(id, owner, output, "rejected", "foreign_session"); continue; }
                if (type == "text_delta")
                {
                    if ((owner.SendStarted || owner.AdmissionProven) &&
                        MatchesOptionalTurn(output, owner.TurnCountBeforeSend))
                    {
                        owner.AddDraft(Value(output, "text"));
                        TraceOutput(id, owner, output, "accepted", "draft");
                    }
                    else TraceOutput(id, owner, output, "rejected", "admission_or_turn_mismatch");
                    continue;
                }
                if (type is "thinking" or "thinking_delta") continue;
                await using var scope = scopes.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
                await using var tx = await db.Database.BeginTransactionAsync(ct);
                var row = await db.Set<AiInvestigationRecord>().FromSqlInterpolated($"SELECT * FROM \"AiInvestigations\" WHERE \"Id\" = {id} FOR UPDATE").SingleAsync(ct);
                if (row.ActiveMessageId != owner.MessageId || row.RemoteSessionId != owner.SessionId || row.ProfileFingerprint != owner.Profile.ProfileFingerprint)
                { TraceOutput(id, owner, output, "rejected", "binding_changed"); continue; }
                if (row.State == InvestigationState.Archived) { owner.Joined.TrySetResult(); continue; }
                if (type == "session_joined")
                {
                    if (row.State == InvestigationState.DeliveryUnknown)
                    {
                        owner.AdmissionProven = ProvesAdmission(output, id, row.ActiveMessageId);
                        await ReconcileHistoryAsync(db, row, output, owner.AdmissionProven, ct);
                    }
                    else if (row.State == InvestigationState.Processing && !owner.SendStarted)
                        owner.TurnCountBeforeSend = row.RemoteTurnCountBeforeSend = TurnCount(output);
                    row.LastTransportActivityAtUtc = clock.GetUtcNow();
                    await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); owner.Joined.TrySetResult(); continue;
                }
                if (row.State == InvestigationState.DeliveryUnknown && !owner.AdmissionProven && !(owner.SendStarted && owner.Created) ||
                    row.State is InvestigationState.Idle or InvestigationState.Queued)
                { TraceOutput(id, owner, output, "rejected", "inactive_or_unproven_admission"); continue; }
                if (!MatchesOptionalTurn(output, row.RemoteTurnCountBeforeSend))
                { TraceOutput(id, owner, output, "rejected", "turn_mismatch"); continue; }
                if (row.LastSequence >= 10000 && type != "turn_completed") throw new InvalidDataException("Investigation transcript capacity reached.");
                row.LastTransportActivityAtUtc = clock.GetUtcNow();
                switch (type)
                {
                    case "text":
                    case "buffer_flush":
                        PersistAssistant(db, row, owner, Value(output, "text"));
                        if (owner.PendingCompletion is { } pending)
                            await CompleteTurnAsync(db, row, owner, pending, ct, reconciled: true);
                        break;
                    case "turn_completed":
                        if (!(owner.SendStarted && owner.Created ||
                            (owner.SendStarted || owner.AdmissionProven) && ProvesLiveRecoveryEnd(output, row.RemoteTurnCountBeforeSend)))
                        {
                            PersistAssistant(db, row, owner, null);
                            if (owner.SendStarted)
                            {
                                row.State = InvestigationState.DeliveryUnknown;
                                SetRecoverySummary(db, row, "delivery_unknown",
                                    "The attached session reported a terminal event without a matching pre-send baseline and native turn number to associate it with this exact message. Its outcome remains unconfirmed. Check the daemon history and possible side effects before recovering or archiving. Nothing was resent.");
                                await InvestigationStore.StageTurnResultAsync(db, row, FlowAiResultStatus.DeliveryUnknown, clock.GetUtcNow(), ct);
                            }
                            break;
                        }
                        await CompleteTurnAsync(db, row, owner, output, ct); break;
                    case "tool_interaction":
                        var callId = Value(output, "callId");
                        if (string.IsNullOrWhiteSpace(callId) || callId.Length > 256) throw new InvalidDataException("Invalid approval identity.");
                        var previous = await db.Set<AiInvestigationApprovalRecord>().SingleOrDefaultAsync(x => x.InvestigationId == id && x.CallId == callId, ct);
                        if (previous is not null)
                        {
                            if (row.State == InvestigationState.DeliveryUnknown && owner.AdmissionProven && previous.MessageId == row.ActiveMessageId && previous.SelectedKey is null)
                            {
                                row.State = InvestigationState.AwaitingApproval;
                                row.RecoverySummary = "The exact resumed message is awaiting your decision on the saved approval. Review the request before responding; nothing was automatically approved.";
                                InvestigationStore.Append(db, row, "approval_resumed", "The resumed session confirmed this pending approval.", callId: callId);
                            }
                            break;
                        }
                        var options = OfferedOptions(output); if (options.Length == 0) throw new InvalidDataException("No supported approval options.");
                        var json = JsonSerializer.Serialize(options);
                        if (row.ActiveMessageId is not { } approvalMessage) throw new InvalidDataException("Approval has no active turn.");
                        if (await db.Set<AiInvestigationApprovalRecord>().CountAsync(x => x.InvestigationId == id && x.MessageId == approvalMessage && x.SelectedKey == null, ct) >= 32)
                            throw new InvalidDataException("Too many pending approvals.");
                        db.Add(new AiInvestigationApprovalRecord { InvestigationId = id, MessageId = approvalMessage, CallId = callId, OptionsJson = json });
                        row.State = InvestigationState.AwaitingApproval;
                        if (row.LastRecoveryAttemptAtUtc is not null)
                            row.RecoverySummary = "The resumed session is awaiting your decision. Review the current request before responding; nothing was automatically approved.";
                        InvestigationStore.Append(db, row, "approval_request", $"Approval requested for {SafeTool(Value(output, "toolName"))}.", callId: callId, options: json,
                            trace: NetclawTraceMapper.Map(output, owner.SessionId, row.ActiveMessageId, owner.Profile.DeviceToken)); break;
                    case "tool_call": case "tool_result":
                        var trace = NetclawTraceMapper.Map(output, owner.SessionId, row.ActiveMessageId, owner.Profile.DeviceToken);
                        InvestigationStore.Append(db, row, type, $"{SafeTool(trace.ToolName)}: {trace.Status}", callId: Bounded(Value(output, "callId"), 256), trace: trace); break;
                    case "error":
                        row.State = InvestigationState.DeliveryUnknown;
                        InvestigationStore.Append(db, row, "error", "Netclaw reported an error. Recover the session or review the daemon logs with this investigation reference.",
                            trace: NetclawTraceMapper.Map(output, owner.SessionId, row.ActiveMessageId, owner.Profile.DeviceToken));
                        await InvestigationStore.StageTurnResultAsync(db, row, FlowAiResultStatus.DeliveryUnknown, clock.GetUtcNow(), ct); break;
                    case "file":
                        InvestigationStore.Append(db, row, "artifact", "Netclaw produced an artifact. Review it through the configured tool permissions.",
                            trace: NetclawTraceMapper.Map(output, owner.SessionId, row.ActiveMessageId, owner.Profile.DeviceToken)); break;
                    case "usage": case "compaction": case "subagent": case "processing_state":
                        if (type == "processing_state" && owner.AdmissionProven && output.TryGetProperty("isProcessing", out var processing) && processing.ValueKind == JsonValueKind.True)
                        {
                            row.State = InvestigationState.Processing;
                            row.RecoverySummary = "The exact message was found and the resumed session reports it is running. Completion remains unconfirmed. Do not resend the message.";
                        }
                        InvestigationStore.Append(db, row, type, $"Netclaw {type.Replace('_', ' ')}.",
                            trace: NetclawTraceMapper.Map(output, owner.SessionId, row.ActiveMessageId, owner.Profile.DeviceToken)); break;
                }
                await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
                TraceOutput(id, owner, output, "persisted", row.State.ToString());
                if (row.State == InvestigationState.Idle || type == "turn_completed" && owner.PendingCompletion is null &&
                    row.State == InvestigationState.DeliveryUnknown && owner.SendStarted && row.RemoteTurnCountBeforeSend is null) return;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { logger.LogDebug("Investigation output stopped."); owner.Retired = true; }
        catch (Exception exception)
        {
            owner.Retired = true; owner.Joined.TrySetException(exception is NetclawAttachmentException ? exception : new InvestigationConflictException("Remote session history could not be confirmed."));
            logger.LogWarning("Netclaw output interrupted for {InvestigationId}: {ExceptionType}", id, exception.GetType().Name);
            try
            {
                if (owner.SendStarted || owner.AdmissionProven)
                    await UnknownAsync(id, owner.MessageId, "The assistant output was interrupted. Recover the session to check its outcome.", stopping, true);
            }
            catch (Exception failure) { logger.LogWarning("Investigation recovery deferred to restart: {ExceptionType}", failure.GetType().Name); owner.Outputs.Writer.TryComplete(failure); }
        }
        finally
        {
            owner.Retired = true;
            owner.Outputs.Writer.TryComplete(); owner.Joined.TrySetCanceled();
            try { await owner.Client.DisposeAsync(); }
            catch (Exception exception) { logger.LogWarning("Netclaw session disposal failed: {ExceptionType}", exception.GetType().Name); }
            owner.ClearDraft(); owner.DisposeLifetime();
            owners.TryRemove(new KeyValuePair<Guid, SessionOwner>(id, owner));
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

    public Task RetireAsync(Guid id) => owners.TryGetValue(id, out var owner) ? RetireAsync(id, owner) : Task.CompletedTask;

    private async Task RetireAsync(Guid id, SessionOwner owner)
    {
        if (!owners.TryRemove(new KeyValuePair<Guid, SessionOwner>(id, owner))) return;
        owner.Retired = true; owner.Outputs.Writer.TryComplete(); owner.Joined.TrySetCanceled();
        owner.Cancel();
        await owner.Consumer;
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
        history.EnumerateArray().Count(x => Value(x, "role") == "user" && Value(x, "content")?.StartsWith(MessageMarker(id, message.Value), StringComparison.Ordinal) == true) == 1 &&
        Value(history.EnumerateArray().LastOrDefault(x => Value(x, "role") == "user"), "content")?.StartsWith(MessageMarker(id, message.Value), StringComparison.Ordinal) == true;
    public static InvestigationOptionDto[] OfferedOptions(JsonElement output) => output.TryGetProperty("interactionOptions", out var options) && options.ValueKind == JsonValueKind.Array
        ? options.EnumerateArray().Select(x => Value(x, "key")).Where(x => x is "approve_once" or "approve_session" or "deny").Distinct()
            .Select(x => new InvestigationOptionDto(x!, x switch { "approve_once" => "Allow once", "approve_session" => "Allow for this investigation", _ => "Deny" })).ToArray() : [];
    private static string? Value(JsonElement output, string name) => output.ValueKind == JsonValueKind.Object && output.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static string? Bounded(string? text, int maximum) => text is null ? null : text.Length <= maximum ? text : text[..maximum];
    private static string SafeTool(string? value) => value is { Length: > 0 and <= 128 } && value.All(x => char.IsAsciiLetterOrDigit(x) || x is '.' or '_' or '-' or '/') ? value : "tool";
}
