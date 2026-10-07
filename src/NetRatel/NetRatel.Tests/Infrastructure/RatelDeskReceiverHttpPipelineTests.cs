using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Options;
using NetRatel.Application.RatelDesk;
using NetRatel.Infrastructure.RatelDesk;
using NetRatel.Infrastructure.ServiceIdentity;
using NetRatel.Infrastructure.ServiceLinks;
using NetRatel.Shared.Contracts.RatelDesk;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

public sealed class RatelDeskReceiverHttpPipelineTests
{
    private const string Bearer = "rdk_synthetic_receiver_test_credential";

    [Theory]
    [InlineData(RatelDeskAuthenticationMode.ManualApiBearer, RatelDeskReceiverHttpPipeline.ManualClient)]
    [InlineData(RatelDeskAuthenticationMode.ManagedServiceLink, RatelDeskReceiverHttpPipeline.ManagedClient)]
    public async Task Create_sends_exact_durable_body_and_identity_headers_on_the_selected_client(RatelDeskAuthenticationMode mode, string client)
    {
        using var fixture = new HttpFixture();
        var prepared = RatelDeskReceiverFixture.Prepared(mode);
        fixture.Handler.Respond = (_, _) => Task.FromResult(ReceiptReply(prepared, 201));
        var result = await fixture.Transport.CreateAsync(prepared, Bearer, CancellationToken.None);
        Assert.Equal(RatelDeskReceiverObservationKind.Committed, result.Kind);
        var request = Assert.Single(fixture.Handler.Requests);
        Assert.Equal("POST", request.Method);
        Assert.Equal(prepared.Capability.Endpoints.Create, request.Uri);
        Assert.Equal("Bearer", request.AuthenticationScheme);
        Assert.Equal(Bearer, request.AuthenticationParameter);
        Assert.Equal(prepared.Peer.SourceInstanceId.ToString("D"), request.Source);
        Assert.Equal(prepared.ReceiverIdempotencyKey, request.Key);
        Assert.Equal("application/json", request.Accept);
        Assert.Equal("application/json", request.ContentType);
        Assert.Equal(prepared.Peer.ApiBaseUrl, request.ApprovedApiBase);
        Assert.Equal(Encoding.UTF8.GetBytes(prepared.ExactCreateBodyJson), request.Body);
        Assert.Equal(client, Assert.Single(fixture.Factory.Names));
    }

    [Theory]
    [InlineData(".", false)]
    [InlineData(".", true)]
    [InlineData("..", false)]
    [InlineData("..", true)]
    public async Task Receipt_lookup_preserves_dot_segments_at_the_HTTP_boundary_without_a_caller_opt_in(string key, bool optIn)
    {
        using var fixture = new HttpFixture();
        var peer = RatelDeskReceiverFixture.Peer();
        var endpoint = RatelDeskReceiverFixture.Api + "/api/v1/integrations/netratel/incident-receipts/" + key;
        _ = await fixture.Pipeline.ReadAsync(peer.Mode, peer.LocalTenantId, peer.ConnectorId, peer.ApiBaseUrl,
            peer.SourceInstanceId, Bearer, HttpMethod.Get, endpoint, null, CancellationToken.None, preserveDotKey: optIn);
        var request = Assert.Single(fixture.Handler.Requests);
        Assert.Equal(endpoint, request.Uri);
        Assert.Equal("/help/api/v1/integrations/netratel/incident-receipts/" + key, request.PathAndQuery);
        Assert.Null(request.Key);
        Assert.Empty(request.Body);
        Assert.Equal("GET", request.Method);
    }

    [Theory]
    [InlineData("/api/v1/incidents/../customers")]
    [InlineData("/api/v1/integrations/netratel/incident-receipts/a/b")]
    [InlineData("/api/v1/integrations/netratel/incident-receipts/%2e")]
    [InlineData("/api/v1/integrations/netratel/capabilities?token=unexpected")]
    [InlineData("/api/v1/integrations/netratel/capabilities#fragment")]
    [InlineData("/api/v1/integrations/netratel/unknown")]
    public async Task Endpoints_outside_the_fixed_protocol_are_rejected_before_HTTP(string suffix)
    {
        using var fixture = new HttpFixture();
        var peer = RatelDeskReceiverFixture.Peer();
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Pipeline.ReadAsync(peer.Mode, peer.LocalTenantId,
            peer.ConnectorId, peer.ApiBaseUrl, peer.SourceInstanceId, Bearer, HttpMethod.Get,
            peer.ApiBaseUrl + suffix, null, CancellationToken.None));
        Assert.Empty(fixture.Handler.Requests);
        Assert.Empty(fixture.Factory.Names);
    }

    [Fact]
    public async Task Receipt_raw_path_mode_cannot_be_applied_to_create_or_another_method()
    {
        using var fixture = new HttpFixture();
        var peer = RatelDeskReceiverFixture.Peer();
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Pipeline.ReadAsync(peer.Mode, peer.LocalTenantId,
            peer.ConnectorId, peer.ApiBaseUrl, peer.SourceInstanceId, Bearer, HttpMethod.Post,
            peer.ApiBaseUrl + ReceiverWireValidation.CreatePath, "{}"u8.ToArray(), CancellationToken.None, "valid-key", preserveDotKey: true));
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Pipeline.ReadAsync(peer.Mode, peer.LocalTenantId,
            peer.ConnectorId, peer.ApiBaseUrl, peer.SourceInstanceId, Bearer, HttpMethod.Post,
            peer.ApiBaseUrl + ReceiverWireValidation.CapabilitiesPath, null, CancellationToken.None));
        Assert.Empty(fixture.Handler.Requests);
    }

    [Theory]
    [InlineData(200)]
    [InlineData(201)]
    public async Task Create_success_requires_a_complete_matching_receipt(int status)
    {
        using var fixture = new HttpFixture();
        var prepared = RatelDeskReceiverFixture.Prepared();
        fixture.Handler.Respond = (_, _) => Task.FromResult(ReceiptReply(prepared, status));
        var result = await fixture.Transport.CreateAsync(prepared, Bearer, CancellationToken.None);
        Assert.Equal(RatelDeskReceiverObservationKind.Committed, result.Kind);
        Assert.Equal(Encoding.UTF8.GetString(RatelDeskReceiverFixture.Receipt(prepared)), result.Receipt!.ExactAcceptedBodyJson);
        Assert.Single(fixture.Handler.Requests);
    }

    [Theory]
    [InlineData("http-error")]
    [InlineData("io-error")]
    [InlineData("invalid-json")]
    [InlineData("missing-no-store")]
    [InlineData("wrong-media-type")]
    [InlineData("wrong-location")]
    [InlineData("oversize-response")]
    public async Task Create_uncertainty_is_preserved_while_equivalent_lookup_failures_remain_read_only(string fault)
    {
        using var fixture = new HttpFixture();
        var prepared = RatelDeskReceiverFixture.Prepared();
        fixture.Handler.Respond = (_, _) => FaultReply(prepared, fault);
        var create = await fixture.Transport.CreateAsync(prepared, Bearer, CancellationToken.None);
        var lookup = await fixture.Transport.LookupAsync(prepared, Bearer, CancellationToken.None);
        Assert.Equal(RatelDeskReceiverObservationKind.PossibleCommit, create.Kind);
        Assert.Equal(RatelDeskReceiverObservationKind.TransientReadFailure, lookup.Kind);
        Assert.Null(create.Receipt);
        Assert.Null(lookup.Receipt);
        Assert.Equal(2, fixture.Handler.Requests.Count);
    }

    [Theory]
    [InlineData(503, null, RatelDeskReceiverObservationKind.PossibleCommit)]
    [InlineData(302, null, RatelDeskReceiverObservationKind.PossibleCommit)]
    [InlineData(409, "unrelated-conflict", RatelDeskReceiverObservationKind.PossibleCommit)]
    [InlineData(400, "unrelated-validation", RatelDeskReceiverObservationKind.PossibleCommit)]
    [InlineData(409, "idempotency-payload-conflict", RatelDeskReceiverObservationKind.FingerprintConflict)]
    [InlineData(422, "invalid-incident-target", RatelDeskReceiverObservationKind.PayloadRejected)]
    [InlineData(401, null, RatelDeskReceiverObservationKind.AuthenticationRejected)]
    [InlineData(403, null, RatelDeskReceiverObservationKind.AuthenticationRejected)]
    [InlineData(429, null, RatelDeskReceiverObservationKind.RateLimited)]
    public async Task Only_explicit_receiver_rejections_erase_a_new_create_attempts_uncertainty(int status, string? code, RatelDeskReceiverObservationKind expected)
    {
        using var fixture = new HttpFixture();
        fixture.Handler.Respond = (_, _) => Task.FromResult(JsonReply(code is null ? "{}" : "{\"code\":\"" + code + "\"}", status));
        var result = await fixture.Transport.CreateAsync(RatelDeskReceiverFixture.Prepared(), Bearer, CancellationToken.None);
        Assert.Equal(expected, result.Kind);
        Assert.Single(fixture.Handler.Requests);
    }

    [Theory]
    [InlineData(404, RatelDeskReceiverObservationKind.Missing)]
    [InlineData(410, RatelDeskReceiverObservationKind.Gone)]
    [InlineData(503, RatelDeskReceiverObservationKind.TransientReadFailure)]
    public async Task Lookup_status_never_creates_or_retries_an_incident(int status, RatelDeskReceiverObservationKind expected)
    {
        using var fixture = new HttpFixture();
        fixture.Handler.Respond = (_, _) => Task.FromResult(JsonReply("{}", status));
        var result = await fixture.Transport.LookupAsync(RatelDeskReceiverFixture.Prepared(), Bearer, CancellationToken.None);
        Assert.Equal(expected, result.Kind);
        Assert.Equal("GET", Assert.Single(fixture.Handler.Requests).Method);
    }

    [Theory]
    [InlineData(404)]
    [InlineData(410)]
    public async Task Cached_missing_or_gone_lookup_is_not_accepted_as_current_receiver_evidence(int status)
    {
        using var fixture = new HttpFixture();
        fixture.Handler.Respond = (_, _) =>
        {
            var response = JsonReply("{}", status);
            response.Headers.CacheControl = null;
            return Task.FromResult(response);
        };
        var result = await fixture.Transport.LookupAsync(RatelDeskReceiverFixture.Prepared(), Bearer, CancellationToken.None);
        Assert.Equal(RatelDeskReceiverObservationKind.TransientReadFailure, result.Kind);
        Assert.Equal("GET", Assert.Single(fixture.Handler.Requests).Method);
    }

    [Fact]
    public async Task Lookup_success_keeps_the_original_receipt_and_sends_no_create_key_header()
    {
        using var fixture = new HttpFixture();
        var prepared = RatelDeskReceiverFixture.Prepared();
        fixture.Handler.Respond = (_, _) => Task.FromResult(ReceiptReply(prepared, 200));
        var result = await fixture.Transport.LookupAsync(prepared, Bearer, CancellationToken.None);
        Assert.Equal(RatelDeskReceiverObservationKind.Committed, result.Kind);
        Assert.Equal(Encoding.UTF8.GetString(RatelDeskReceiverFixture.Receipt(prepared)), result.Receipt!.ExactAcceptedBodyJson);
        var request = Assert.Single(fixture.Handler.Requests);
        Assert.Equal("GET", request.Method);
        Assert.Null(request.Key);
        Assert.Empty(request.Body);
    }

    [Theory]
    [InlineData(RatelDeskAuthenticationMode.ManualApiBearer)]
    [InlineData(RatelDeskAuthenticationMode.ManagedServiceLink)]
    public void Safe_handler_disables_redirects_proxies_cookies_and_connection_reuse(RatelDeskAuthenticationMode mode)
    {
        using var fixture = new HttpFixture();
        using var handler = RatelDeskReceiverSafeHttpMessageHandler.Create(mode, fixture.Network);
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseProxy);
        Assert.False(handler.UseCookies);
        Assert.Equal(TimeSpan.Zero, handler.PooledConnectionLifetime);
        Assert.Equal(TimeSpan.Zero, handler.PooledConnectionIdleTimeout);
        Assert.Equal(TimeSpan.FromSeconds(5), handler.ConnectTimeout);
        Assert.NotNull(handler.ConnectCallback);
    }

    [Fact]
    public async Task Cancellation_before_create_entry_sends_no_request_and_does_not_invent_a_possible_commit()
    {
        using var fixture = new HttpFixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var result = await fixture.Transport.CreateAsync(RatelDeskReceiverFixture.Prepared(), Bearer, cancellation.Token);
        Assert.Equal(RatelDeskReceiverObservationKind.Unavailable, result.Kind);
        Assert.Empty(fixture.Handler.Requests);
    }

    [Fact]
    public async Task Busy_admission_preserves_prior_post_uncertainty_without_entering_HTTP()
    {
        using var fixture = new HttpFixture();
        var prepared = RatelDeskReceiverFixture.Prepared();
        using var held = await fixture.Limiter.TryAcquireAsync(prepared.Peer.LocalTenantId, prepared.ConnectorId, CancellationToken.None);
        Assert.NotNull(held);
        Assert.Equal(RatelDeskReceiverObservationKind.PossibleCommit,
            (await fixture.Transport.CreateAsync(prepared, Bearer, CancellationToken.None)).Kind);
        Assert.Equal(RatelDeskReceiverObservationKind.TransientReadFailure,
            (await fixture.Transport.LookupAsync(prepared, Bearer, CancellationToken.None)).Kind);
        Assert.Empty(fixture.Handler.Requests);
    }

    [Fact]
    public async Task Operation_deadline_is_ten_seconds_and_covers_response_body_reads()
    {
        using var fixture = new HttpFixture();
        var stream = new WaitingStream();
        fixture.Handler.Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamReplyContent(stream)
        });
        var task = fixture.ReadCapabilityAsync();
        await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(TimeSpan.FromSeconds(10), RatelDeskReceiverHttpPipeline.OperationTimeout);
        Assert.Equal(TimeSpan.FromSeconds(10), Assert.Single(fixture.Clock.Timers).DueTime);
        fixture.Clock.Advance(TimeSpan.FromSeconds(9));
        Assert.False(task.IsCompleted);
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.True(stream.WasDisposed);
        fixture.Handler.Respond = (_, _) => Task.FromResult(JsonReply("{}"));
        _ = await fixture.ReadCapabilityAsync();
        Assert.Equal(2, fixture.Handler.Requests.Count);
    }

    [Fact]
    public async Task Earlier_caller_cancellation_remains_effective_and_releases_admission()
    {
        using var fixture = new HttpFixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Handler.Respond = async (_, token) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("The cancellation token must stop the request.");
        };
        using var caller = new CancellationTokenSource();
        var task = fixture.ReadCapabilityAsync(caller.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(RatelDeskReceiverFixture.Now, fixture.Clock.GetUtcNow());
        fixture.Handler.Respond = (_, _) => Task.FromResult(JsonReply("{}"));
        _ = await fixture.ReadCapabilityAsync();
        Assert.Equal(2, fixture.Handler.Requests.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Response_byte_limit_covers_declared_and_chunked_content(bool declaredLength)
    {
        using var fixture = new HttpFixture();
        HttpResponseMessage Response(int length) => new(HttpStatusCode.OK)
        {
            Content = declaredLength ? new ByteArrayContent(new byte[length]) : new StreamReplyContent(new MemoryStream(new byte[length]))
        };
        fixture.Handler.Respond = (_, _) => Task.FromResult(Response(RatelDeskConnectorLimits.MaximumResponseBytes));
        Assert.Equal(RatelDeskConnectorLimits.MaximumResponseBytes, (await fixture.ReadCapabilityAsync()).Body.Length);
        fixture.Handler.Respond = (_, _) => Task.FromResult(Response(RatelDeskConnectorLimits.MaximumResponseBytes + 1));
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.ReadCapabilityAsync());
        Assert.Equal(2, fixture.Handler.Requests.Count);
    }

    [Fact]
    public async Task Oversize_request_is_rejected_before_creating_a_client_or_sending()
    {
        using var fixture = new HttpFixture();
        var peer = RatelDeskReceiverFixture.Peer();
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Pipeline.ReadAsync(peer.Mode, peer.LocalTenantId,
            peer.ConnectorId, peer.ApiBaseUrl, peer.SourceInstanceId, Bearer, HttpMethod.Post,
            peer.ApiBaseUrl + ReceiverWireValidation.CreatePath,
            new byte[RatelDeskConnectorLimits.MaximumRequestBytes + 1], CancellationToken.None, "same-key"));
        Assert.Empty(fixture.Handler.Requests);
        Assert.Empty(fixture.Factory.Names);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(0.2, 1)]
    [InlineData(1.2, 2)]
    [InlineData(999, 300)]
    public async Task Retry_after_is_bounded_and_rounded_up_without_an_automatic_retry(double delaySeconds, int expectedSeconds)
    {
        using var fixture = new HttpFixture();
        fixture.Handler.Respond = (_, _) =>
        {
            var reply = JsonReply("{}", 429);
            reply.Headers.RetryAfter = new RetryConditionHeaderValue(fixture.Clock.GetUtcNow().AddSeconds(delaySeconds));
            return Task.FromResult(reply);
        };
        var reply = await fixture.ReadCapabilityAsync();
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), reply.RetryAfter);
        Assert.Single(fixture.Handler.Requests);
    }

    [Fact]
    public async Task Limiter_bounds_connector_tenant_and_global_operations_and_disposes_leases_once()
    {
        var limiter = new RatelDeskTransportLimiter();
        var held = new List<IDisposable>();
        try
        {
            var first = await AcquireDistinctAsync(limiter, 1);
            held.Add(first.Lease);
            Assert.Null(await limiter.TryAcquireAsync(1, first.Connector, CancellationToken.None));
            held.Add((await AcquireDistinctAsync(limiter, 1)).Lease);
            Assert.Null(await limiter.TryAcquireAsync(1, RatelDeskReceiverFixture.Id(999), CancellationToken.None));
            for (var tenant = 2; tenant <= 7; tenant++) held.Add((await AcquireDistinctAsync(limiter, tenant)).Lease);
            Assert.Equal(8, held.Count);
            Assert.Null(await limiter.TryAcquireAsync(8, RatelDeskReceiverFixture.Id(998), CancellationToken.None));
            first.Lease.Dispose();
            first.Lease.Dispose();
            using var replacement = await limiter.TryAcquireAsync(1, first.Connector, CancellationToken.None);
            Assert.NotNull(replacement);
            Assert.Null(await limiter.TryAcquireAsync(8, RatelDeskReceiverFixture.Id(997), CancellationToken.None));
        }
        finally { foreach (var lease in held) lease.Dispose(); }
        using var afterCleanup = await limiter.TryAcquireAsync(1, RatelDeskReceiverFixture.Id(999), CancellationToken.None);
        Assert.NotNull(afterCleanup);
    }

    private static async Task<(Guid Connector, IDisposable Lease)> AcquireDistinctAsync(RatelDeskTransportLimiter limiter, int tenant)
    {
        // Stripes may conservatively share slots. Find a free connector without depending on the hash implementation.
        for (var value = 100; value < 612; value++)
        {
            var id = RatelDeskReceiverFixture.Id(value);
            var lease = await limiter.TryAcquireAsync(tenant, id, CancellationToken.None);
            if (lease is not null) return (id, lease);
        }
        throw new InvalidOperationException("No connector slot was available below the documented capacity.");
    }

    private static Task<HttpResponseMessage> FaultReply(RatelDeskReceiverPreparationV2 prepared, string fault)
    {
        if (fault == "http-error") return Task.FromException<HttpResponseMessage>(new HttpRequestException("synthetic-network-failure"));
        if (fault == "io-error") return Task.FromException<HttpResponseMessage>(new IOException("synthetic-read-failure"));
        var reply = ReceiptReply(prepared, 200);
        if (fault == "invalid-json") reply.Content = new StringContent("{", Encoding.UTF8, "application/json");
        if (fault == "missing-no-store") reply.Headers.CacheControl = null;
        if (fault == "wrong-media-type") reply.Content.Headers.ContentType = new("text/html");
        if (fault == "wrong-location") reply.Headers.Location = new Uri("/help/api/v1/incidents/different", UriKind.Relative);
        if (fault == "oversize-response") reply.Content = new ByteArrayContent(new byte[RatelDeskConnectorLimits.MaximumResponseBytes + 1]);
        return Task.FromResult(reply);
    }

    private static HttpResponseMessage ReceiptReply(RatelDeskReceiverPreparationV2 prepared, int status)
    {
        var reply = JsonReply(Encoding.UTF8.GetString(RatelDeskReceiverFixture.Receipt(prepared)), status);
        reply.Headers.Location = new Uri(RatelDeskReceiverFixture.Location, UriKind.Relative);
        return reply;
    }

    private static HttpResponseMessage JsonReply(string json, int status = 200)
    {
        var reply = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        reply.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
        return reply;
    }

    private sealed class HttpFixture : IDisposable
    {
        internal RecordingHandler Handler { get; } = new();
        internal RecordingFactory Factory { get; }
        internal ManualClock Clock { get; } = new();
        internal RatelDeskTransportLimiter Limiter { get; } = new();
        internal RatelDeskReceiverNetworkPolicy Network { get; }
        internal RatelDeskReceiverHttpPipeline Pipeline { get; }
        internal RatelDeskReceiverTransport Transport { get; }
        internal HttpFixture()
        {
            Factory = new(Handler);
            Network = new RatelDeskReceiverNetworkPolicy(new Monitor<RatelDeskReceiverOptions>(new()),
                new Monitor<ServiceLinkOptions>(new()), new Monitor<ServiceIdentityOptions>(new()));
            Pipeline = new(Factory, Network, Clock, Limiter);
            Transport = new(Pipeline, Clock);
        }
        internal Task<RatelDeskReceiverReply> ReadCapabilityAsync(CancellationToken cancellationToken = default)
        {
            var peer = RatelDeskReceiverFixture.Peer();
            return Pipeline.ReadAsync(peer.Mode, peer.LocalTenantId, peer.ConnectorId, peer.ApiBaseUrl,
                peer.SourceInstanceId, Bearer, HttpMethod.Get, peer.ApiBaseUrl + ReceiverWireValidation.CapabilitiesPath, null, cancellationToken);
        }
        public void Dispose() => Handler.Dispose();
    }

    private sealed record RequestSnapshot(string Method, string Uri, string PathAndQuery, string? AuthenticationScheme,
        string? AuthenticationParameter, string Source, string? Key, string Accept, string? ContentType, string? ApprovedApiBase, byte[] Body);

    private sealed class RecordingHandler : HttpMessageHandler
    {
        internal List<RequestSnapshot> Requests { get; } = [];
        internal Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Respond { get; set; } = (_, _) => Task.FromResult(JsonReply("{}"));
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new(request.Method.Method, request.RequestUri!.AbsoluteUri, request.RequestUri.PathAndQuery,
                request.Headers.Authorization?.Scheme, request.Headers.Authorization?.Parameter,
                request.Headers.GetValues("X-NetRatel-Source-Instance").Single(),
                request.Headers.TryGetValues("Idempotency-Key", out var values) ? values.Single() : null,
                string.Join(",", request.Headers.Accept.Select(x => x.MediaType)), request.Content?.Headers.ContentType?.MediaType,
                request.Options.TryGetValue(RatelDeskReceiverSafeHttpMessageHandler.ApprovedApiBaseOption, out var approvedApiBase) ? approvedApiBase : null,
                request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken)));
            return await Respond(request, cancellationToken);
        }
    }

    private sealed class RecordingFactory(RecordingHandler handler) : IHttpClientFactory
    {
        internal List<string> Names { get; } = [];
        public HttpClient CreateClient(string name)
        {
            Names.Add(name);
            return new(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        }
    }

    private sealed class StreamReplyContent(Stream stream) : HttpContent
    {
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult(stream);
        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) => Task.FromResult(stream);
        protected override Task SerializeToStreamAsync(Stream target, TransportContext? context) => stream.CopyToAsync(target);
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override void Dispose(bool disposing) { if (disposing) stream.Dispose(); base.Dispose(disposing); }
    }

    private sealed class WaitingStream : Stream
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool WasDisposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { WasDisposed = true; base.Dispose(disposing); }
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = RatelDeskReceiverFixture.Now;
        internal List<ManualTimer> Timers { get; } = [];
        public override DateTimeOffset GetUtcNow() => _now;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state, dueTime);
            Timers.Add(timer);
            return timer;
        }
        internal void Advance(TimeSpan elapsed)
        {
            _now += elapsed;
            foreach (var timer in Timers.ToArray()) timer.FireIfDue(_now);
        }
        internal sealed class ManualTimer(ManualClock owner, TimerCallback callback, object? state, TimeSpan dueTime) : ITimer
        {
            private DateTimeOffset _due = owner.GetUtcNow() + dueTime;
            private bool _disposed;
            internal TimeSpan DueTime { get; } = dueTime;
            public bool Change(TimeSpan nextDue, TimeSpan period)
            {
                if (_disposed) return false;
                _due = nextDue == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : owner.GetUtcNow() + nextDue;
                return true;
            }
            internal void FireIfDue(DateTimeOffset now)
            {
                if (_disposed || now < _due) return;
                _disposed = true;
                callback(state);
            }
            public void Dispose() => _disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    private sealed class Monitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;
        public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
