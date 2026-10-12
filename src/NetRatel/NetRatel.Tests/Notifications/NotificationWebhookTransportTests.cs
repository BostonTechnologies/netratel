using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using NetRatel.Infrastructure.Notifications;
using NetRatel.Shared.Contracts.Notifications;
using Xunit;

namespace NetRatel.Tests.Notifications;

public sealed class NotificationWebhookTransportTests
{
    private const string DiscordUrl = "https://discord.com/api/webhooks/123456789/synthetic_discord_secret";
    private const string SlackUrl = "https://hooks.slack.com/services/T123/B456/synthetic_slack_secret";

    [Theory]
    [InlineData(NotificationProvider.Discord, DiscordUrl, DiscordUrl)]
    [InlineData(NotificationProvider.Discord, "https://DISCORD.com:443/api/v10/webhooks/123/token", "https://discord.com/api/v10/webhooks/123/token")]
    [InlineData(NotificationProvider.Slack, SlackUrl, SlackUrl)]
    [InlineData(NotificationProvider.Slack, "https://hooks.slack-gov.com/services/T123/B456/token", "https://hooks.slack-gov.com/services/T123/B456/token")]
    public void Approved_provider_execution_addresses_are_normalized(NotificationProvider provider, string input, string expected)
        => Assert.Equal(expected, NotificationWebhookAddress.Validate(provider, input).AbsoluteUri);

    [Theory]
    [InlineData(NotificationProvider.Discord, "https://discord.com.attacker.invalid/api/webhooks/123/secret")]
    [InlineData(NotificationProvider.Discord, "https://discord.com@attacker.invalid/api/webhooks/123/secret")]
    [InlineData(NotificationProvider.Discord, "https://user:secret@discord.com/api/webhooks/123/token")]
    [InlineData(NotificationProvider.Discord, "https://discord.com:8443/api/webhooks/123/secret")]
    [InlineData(NotificationProvider.Discord, "http://discord.com/api/webhooks/123/secret")]
    [InlineData(NotificationProvider.Discord, "https://discord.com/api/webhooks/123/secret?wait=false")]
    [InlineData(NotificationProvider.Discord, "https://discord.com/api/webhooks/123/secret#fragment")]
    [InlineData(NotificationProvider.Discord, "https://discord.com/api/webhooks/123/secret/")]
    [InlineData(NotificationProvider.Discord, "https://discord.com/api/webhooks/123/%2e%2e/secret")]
    [InlineData(NotificationProvider.Discord, "https://discord.com/api/webhooks/123/../123/secret")]
    [InlineData(NotificationProvider.Discord, "https://discord.com/api/webhooks/123/./secret")]
    [InlineData(NotificationProvider.Discord, "https://discord.com/api/webhooks/123\\secret")]
    [InlineData(NotificationProvider.Discord, "https://discord.com/api/webhooks/123/sec\nret")]
    [InlineData(NotificationProvider.Discord, "https://discord.com/api/v9/webhooks/123/secret")]
    [InlineData(NotificationProvider.Slack, "https://hooks.slack.com/services/T123/B456/secret/extra")]
    [InlineData(NotificationProvider.Slack, "https://hooks.slack.com/services/T123/B456/sec%72et")]
    [InlineData(NotificationProvider.Slack, DiscordUrl)]
    public async Task Invalid_addresses_cannot_reach_HTTP_or_escape_in_errors(NotificationProvider provider, string url)
    {
        var error = Assert.Throws<ArgumentException>(() => NotificationWebhookAddress.Validate(provider, url));
        Assert.DoesNotContain(url, error.Message);
        using var handler = new RecordingHandler();
        using var transport = new NotificationWebhookTransport(handler);
        var result = await transport.SendAsync(provider, url, "Client offline", null, null);
        Assert.Equal(NotificationDeliveryStatus.Rejected, result.Status);
        Assert.Equal(NotificationWebhookAddress.InvalidAddressCode, result.Code);
        Assert.DoesNotContain("secret", result.ToString());
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Discord_waits_for_a_message_receipt_and_disables_all_mentions()
    {
        using var handler = new RecordingHandler();
        using var transport = new NotificationWebhookTransport(handler);
        const string message = "Factory outage\n<@1234> @everyone\0 affected";
        var result = await transport.SendAsync(NotificationProvider.Discord, DiscordUrl, message, "NetRatel", "987654321");
        Assert.Equal(NotificationDeliveryStatus.Succeeded, result.Status);
        Assert.Equal("111222333", result.MessageId);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("POST", request.Method);
        Assert.Equal(DiscordUrl + "?wait=true&thread_id=987654321", request.Uri);
        Assert.Equal("application/json", request.ContentType);
        Assert.False(request.HasAuthorization);
        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal("Factory outage\n<@1234> @everyone affected", body.RootElement.GetProperty("content").GetString());
        Assert.Equal("NetRatel", body.RootElement.GetProperty("username").GetString());
        Assert.Empty(body.RootElement.GetProperty("allowed_mentions").GetProperty("parse").EnumerateArray());
    }

    [Fact]
    public async Task Slack_escapes_manual_mentions_and_uses_the_webhooks_fixed_identity()
    {
        using var handler = new RecordingHandler { Respond = (_, _) => Task.FromResult(Reply(200, "ok\n")) };
        using var transport = new NotificationWebhookTransport(handler);
        var result = await transport.SendAsync(NotificationProvider.Slack, SlackUrl, "Factory & HQ: <!everyone> <@U123> @here", null, null);
        Assert.Equal(NotificationDeliveryStatus.Succeeded, result.Status);
        Assert.Null(result.MessageId);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(SlackUrl, request.Uri);
        using var body = JsonDocument.Parse(request.Body);
        var payload = body.RootElement;
        Assert.Equal("Factory &amp; HQ: &lt;!everyone&gt; &lt;@U123&gt; @here", payload.GetProperty("text").GetString());
        Assert.False(payload.GetProperty("mrkdwn").GetBoolean());
        Assert.Equal("none", payload.GetProperty("parse").GetString());
        Assert.False(payload.GetProperty("unfurl_links").GetBoolean());
        Assert.False(payload.TryGetProperty("link_names", out _));
        Assert.False(payload.TryGetProperty("channel", out _));
        Assert.False(payload.TryGetProperty("username", out _));
    }

    [Theory]
    [InlineData(NotificationProvider.Discord, 204, "")]
    [InlineData(NotificationProvider.Discord, 200, "{}")]
    [InlineData(NotificationProvider.Discord, 200, "{\"id\":\"synthetic_secret\"}")]
    [InlineData(NotificationProvider.Discord, 200, "not-json synthetic_secret")]
    [InlineData(NotificationProvider.Slack, 200, "invalid_payload synthetic_secret")]
    [InlineData(NotificationProvider.Slack, 202, "ok")]
    [InlineData(NotificationProvider.Slack, 204, "")]
    [InlineData(NotificationProvider.Discord, 503, "synthetic_secret")]
    [InlineData(NotificationProvider.Discord, 302, "synthetic_secret")]
    public async Task An_unconfirmed_POST_is_never_reported_as_success_or_replayed(NotificationProvider provider, int status, string body)
    {
        using var handler = new RecordingHandler { Respond = (_, _) => Task.FromResult(Reply(status, body)) };
        using var transport = new NotificationWebhookTransport(handler);
        var result = await transport.SendAsync(provider, Url(provider), "Client offline", null, null);
        Assert.Equal(NotificationDeliveryStatus.DeliveryUnknown, result.Status);
        Assert.Null(result.MessageId);
        Assert.DoesNotContain("synthetic_secret", result.ToString());
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(404)]
    [InlineData(410)]
    public async Task Explicit_provider_rejections_preserve_a_safe_diagnostic(int status)
    {
        using var handler = new RecordingHandler { Respond = (_, _) => Task.FromResult(Reply(status, DiscordUrl)) };
        using var transport = new NotificationWebhookTransport(handler);
        var result = await transport.SendAsync(NotificationProvider.Discord, DiscordUrl, "Client offline", null, null);
        Assert.Equal(NotificationDeliveryStatus.Rejected, result.Status);
        Assert.DoesNotContain("synthetic_discord_secret", result.ToString());
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Discord_fractional_global_rate_limit_blocks_other_webhooks_without_retrying()
    {
        using var handler = new RecordingHandler
        {
            Respond = (_, _) => Task.FromResult(Reply(429, "{\"retry_after\":1.25,\"global\":true}"))
        };
        var clock = new ManualClock();
        using var transport = new NotificationWebhookTransport(handler, clock);
        var result = await transport.SendAsync(NotificationProvider.Discord, DiscordUrl, "Client offline", null, null);
        Assert.Equal(NotificationDeliveryStatus.RateLimited, result.Status);
        Assert.Equal(TimeSpan.FromSeconds(1.25), result.RetryAfter);
        var next = await transport.SendAsync(NotificationProvider.Discord, DiscordUrl.Replace("123456789", "987654321", StringComparison.Ordinal), "Another outage", null, null);
        Assert.Equal(NotificationDeliveryStatus.RateLimited, next.Status);
        Assert.Equal(TimeSpan.FromSeconds(1.25), next.RetryAfter);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(NotificationProvider.Discord)]
    [InlineData(NotificationProvider.Slack)]
    public async Task Retry_after_header_is_honored_even_when_the_optional_response_body_is_invalid(NotificationProvider provider)
    {
        using var handler = new RecordingHandler
        {
            Respond = (_, _) =>
            {
                var response = Reply(429, "not-json synthetic_secret");
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(45));
                return Task.FromResult(response);
            }
        };
        using var transport = new NotificationWebhookTransport(handler);
        var result = await transport.SendAsync(provider, Url(provider), "Client offline", null, null);
        Assert.Equal(NotificationDeliveryStatus.RateLimited, result.Status);
        Assert.Equal(TimeSpan.FromSeconds(45), result.RetryAfter);
        Assert.DoesNotContain("synthetic_secret", result.ToString());
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Target_pacing_does_not_wait_or_send_again_until_a_second_has_elapsed()
    {
        using var handler = new RecordingHandler();
        var clock = new ManualClock();
        using var transport = new NotificationWebhookTransport(handler, clock);
        Assert.Equal(NotificationDeliveryStatus.Succeeded,
            (await transport.SendAsync(NotificationProvider.Discord, DiscordUrl, "First outage", null, null)).Status);
        var blocked = await transport.SendAsync(NotificationProvider.Discord, DiscordUrl, "Second outage", null, null);
        Assert.Equal(NotificationDeliveryStatus.RateLimited, blocked.Status);
        Assert.Equal(TimeSpan.FromSeconds(1), blocked.RetryAfter);
        Assert.Single(handler.Requests);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(NotificationDeliveryStatus.Succeeded,
            (await transport.SendAsync(NotificationProvider.Discord, DiscordUrl, "Second outage", null, null)).Status);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Cancellation_before_entry_is_unavailable_and_sends_nothing()
    {
        using var handler = new RecordingHandler();
        using var transport = new NotificationWebhookTransport(handler);
        using var caller = new CancellationTokenSource();
        caller.Cancel();
        var result = await transport.SendAsync(NotificationProvider.Discord, DiscordUrl, "Client offline", null, null, caller.Token);
        Assert.Equal(NotificationDeliveryStatus.Unavailable, result.Status);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Deadline_and_caller_cancellation_cover_body_reads_and_preserve_POST_uncertainty(bool cancelCaller)
    {
        var stream = new PendingReadStream();
        using var handler = new RecordingHandler
        {
            Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) })
        };
        var clock = new ManualClock();
        using var transport = new NotificationWebhookTransport(handler, clock);
        using var caller = new CancellationTokenSource();
        var pending = transport.SendAsync(NotificationProvider.Discord, DiscordUrl, "Client offline", null, null, caller.Token);
        await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(TimeSpan.FromSeconds(5), Assert.Single(clock.Timers).DueTime);
        if (cancelCaller) caller.Cancel();
        else clock.Advance(TimeSpan.FromSeconds(5));
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(NotificationDeliveryStatus.DeliveryUnknown, result.Status);
        Assert.True(stream.WasDisposed);
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Response_limits_cover_declared_lengths_and_streamed_bodies(bool declaredLength)
    {
        var bytes = Encoding.UTF8.GetBytes(new string('x', NotificationWebhookTransport.MaximumResponseBytes + 100));
        using var handler = new RecordingHandler
        {
            Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = declaredLength ? new ByteArrayContent(bytes) : new StreamContent(new NonSeekableMemoryStream(bytes))
            })
        };
        using var transport = new NotificationWebhookTransport(handler);
        var result = await transport.SendAsync(NotificationProvider.Discord, DiscordUrl, "Client offline", null, null);
        Assert.Equal(NotificationDeliveryStatus.DeliveryUnknown, result.Status);
        Assert.Equal("notification-response-too-large", result.Code);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Exception_messages_never_escape_the_transport()
    {
        using var handler = new RecordingHandler
        {
            Respond = (_, _) => Task.FromException<HttpResponseMessage>(new HttpRequestException("Failed " + DiscordUrl))
        };
        using var transport = new NotificationWebhookTransport(handler);
        var result = await transport.SendAsync(NotificationProvider.Discord, DiscordUrl, "Client offline", null, null);
        Assert.Equal(NotificationDeliveryStatus.DeliveryUnknown, result.Status);
        Assert.DoesNotContain("synthetic_discord_secret", result.ToString());
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Oversized_messages_and_invalid_thread_options_are_rejected_before_HTTP()
    {
        using var handler = new RecordingHandler();
        using var transport = new NotificationWebhookTransport(handler);
        Assert.Equal(NotificationDeliveryStatus.Rejected,
            (await transport.SendAsync(NotificationProvider.Discord, DiscordUrl,
                new string('x', NotificationConnectorLimits.MaximumMessageLength + 1), null, null)).Status);
        Assert.Equal(NotificationDeliveryStatus.Rejected,
            (await transport.SendAsync(NotificationProvider.Discord, DiscordUrl, "Client offline", null, "123&wait=false")).Status);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Production_handler_prevents_secret_bearing_request_activities_without_touching_the_network()
    {
        var activities = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "System.Net.Http",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = activities.Enqueue
        };
        ActivitySource.AddActivityListener(listener);
        using var parent = new Activity("notification-transport-secret-boundary").Start();
        using var handler = NotificationWebhookTransport.CreateHandler();
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseProxy);
        Assert.False(handler.UseCookies);
        Assert.Null(handler.ActivityHeadersPropagator);
        Assert.Null(handler.MeterFactory);
        var interceptedConnections = 0;
        handler.ConnectCallback = (_, _) =>
        {
            Interlocked.Increment(ref interceptedConnections);
            // Stop before DNS or socket access. This exercises the production handler's diagnostics
            // composition while ensuring no notification can leave the test process.
            return ValueTask.FromException<Stream>(new IOException("synthetic-connect-blocked"));
        };
        using var transport = new NotificationWebhookTransport(handler);
        var result = await transport.SendAsync(NotificationProvider.Discord, DiscordUrl, "Client offline", null, null);
        Assert.True(interceptedConnections > 0);
        Assert.Equal(NotificationDeliveryStatus.DeliveryUnknown, result.Status);
        Assert.Empty(activities.Where(activity => activity.TraceId == parent.TraceId));
    }

    private static string Url(NotificationProvider provider) => provider == NotificationProvider.Discord ? DiscordUrl : SlackUrl;
    private static HttpResponseMessage Reply(int status, string body) => new((HttpStatusCode)status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private sealed record RecordedRequest(string Method, string Uri, string? ContentType, bool HasAuthorization, string Body);

    private sealed class RecordingHandler : HttpMessageHandler
    {
        internal List<RecordedRequest> Requests { get; } = [];
        internal Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Respond { get; init; } =
            (_, _) => Task.FromResult(Reply(200, "{\"id\":\"111222333\"}"));
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(new(request.Method.Method, request.RequestUri!.AbsoluteUri, request.Content?.Headers.ContentType?.MediaType,
                request.Headers.Authorization is not null, request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct)));
            return await Respond(request, ct);
        }
    }

    private sealed class NonSeekableMemoryStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }

    private sealed class PendingReadStream : Stream
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool WasDisposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            Entered.TrySetResult();
            return await new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously).Task.WaitAsync(ct);
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
        private DateTimeOffset _now = new(2026, 10, 12, 10, 0, 0, TimeSpan.Zero);
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
        internal sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state, TimeSpan dueTime) : ITimer
        {
            private DateTimeOffset _due = clock.GetUtcNow() + dueTime;
            private bool _disposed;
            internal TimeSpan DueTime { get; } = dueTime;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (_disposed) return false;
                _due = dueTime == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : clock.GetUtcNow() + dueTime;
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
}
