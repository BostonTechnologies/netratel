using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NetRatel.Application.Notifications;
using NetRatel.Shared.Contracts.Notifications;

namespace NetRatel.Infrastructure.Notifications;

/// <summary>One bounded webhook attempt. Delivery uncertainty never triggers an automatic replay.</summary>
public sealed class NotificationWebhookTransport : INotificationWebhookTransport, IDisposable
{
    internal static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(5);
    internal const int MaximumResponseBytes = 16 * 1024;
    private static readonly TimeSpan MinimumSendInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MissingRetryAfter = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _client;
    private readonly TimeProvider _clock;
    // Fixed stripes retain no credentials and bound memory. Collisions conservatively share admission.
    private readonly long[] _nextAttemptUtcTicks = new long[256];
    private long _discordGlobalRetryUtcTicks;

    public NotificationWebhookTransport() : this(CreateHandler(), TimeProvider.System) { }

    internal NotificationWebhookTransport(HttpMessageHandler handler, TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
        _client = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    internal static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        UseCookies = false,
        ConnectTimeout = OperationTimeout,
        MaxResponseHeadersLength = 16,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        // A null propagator prevents SocketsHttpHandler from inserting DiagnosticsHandler, which
        // otherwise records url.full including the credential in a webhook's path. No client
        // factory is used, so factory HTTP logging cannot record the secret-bearing request either.
        ActivityHeadersPropagator = null,
        MeterFactory = null
    };

    public async Task<NotificationDeliveryResult> SendAsync(NotificationProvider provider, string webhookUrl,
        string message, string? discordUsername, string? discordThreadId, CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested)
            return new(NotificationDeliveryStatus.Unavailable, "notification-canceled-before-send");
        if (provider is not (NotificationProvider.Discord or NotificationProvider.Slack))
            return new(NotificationDeliveryStatus.Rejected, "notification-provider-invalid");

        Uri address;
        try { address = NotificationWebhookAddress.Validate(provider, webhookUrl); }
        catch (ArgumentException)
        {
            return new(NotificationDeliveryStatus.Rejected, NotificationWebhookAddress.InvalidAddressCode);
        }

        if (string.IsNullOrWhiteSpace(message) || message.Length > NotificationConnectorLimits.MaximumMessageLength)
            return new(NotificationDeliveryStatus.Rejected, "notification-message-invalid");
        discordUsername = string.IsNullOrWhiteSpace(discordUsername) ? null : discordUsername.Trim();
        discordThreadId = string.IsNullOrWhiteSpace(discordThreadId) ? null : discordThreadId.Trim();
        if (discordUsername is { } name && (name.Length > 80 || name.Any(char.IsControl)) ||
            discordThreadId is { } thread && !NotificationWebhookAddress.IsSnowflake(thread) ||
            provider == NotificationProvider.Slack && (discordUsername is not null || discordThreadId is not null))
            return new(NotificationDeliveryStatus.Rejected, "notification-options-invalid");

        var stripe = (int)((uint)StringComparer.Ordinal.GetHashCode(address.AbsoluteUri) % (uint)_nextAttemptUtcTicks.Length);
        var now = _clock.GetUtcNow().UtcTicks;
        var blockedUntil = provider == NotificationProvider.Discord ? Volatile.Read(ref _discordGlobalRetryUtcTicks) : 0;
        while (true)
        {
            var previous = Volatile.Read(ref _nextAttemptUtcTicks[stripe]);
            var notBefore = Math.Max(previous, blockedUntil);
            if (notBefore > now)
                return new(NotificationDeliveryStatus.RateLimited, "notification-local-rate-limited",
                    RetryAfter: TimeSpan.FromTicks(notBefore - now));
            if (Interlocked.CompareExchange(ref _nextAttemptUtcTicks[stripe], now + MinimumSendInterval.Ticks, previous) == previous)
                break;
        }

        using var timeout = new CancellationTokenSource(OperationTimeout, _clock);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        var uri = provider == NotificationProvider.Discord
            ? new UriBuilder(address) { Query = "wait=true" + (discordThreadId is null ? "" : "&thread_id=" + discordThreadId) }.Uri
            : address;
        var plainMessage = NormalizeText(message);
        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = provider == NotificationProvider.Discord
                ? JsonContent.Create(new { content = plainMessage, username = discordUsername, allowed_mentions = new { parse = Array.Empty<string>() } }, options: JsonOptions)
                : JsonContent.Create(new { text = EscapeSlackText(plainMessage), mrkdwn = false, parse = "none", unfurl_links = false, unfurl_media = false }, options: JsonOptions)
        };
        if (linked.IsCancellationRequested)
            return new(NotificationDeliveryStatus.Unavailable, "notification-canceled-before-send");

        var rateLimited = false;
        TimeSpan? retryAfter = null;
        try
        {
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                rateLimited = true;
                retryAfter = ReadRetryAfterHeader(response);
                if (provider == NotificationProvider.Discord)
                {
                    var body = await ReadBoundedBodyAsync(response.Content, linked.Token).ConfigureAwait(false);
                    if (body is { } bytes && TryReadDiscordRetry(bytes, out var providerDelay, out var global))
                    {
                        retryAfter = retryAfter is { } headerDelay && headerDelay > providerDelay ? headerDelay : providerDelay;
                        if (global) ExtendDeadline(ref _discordGlobalRetryUtcTicks, _clock.GetUtcNow().UtcTicks + retryAfter.Value.Ticks);
                    }
                }
                return RateLimitResult(stripe, retryAfter);
            }

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.Gone)
                return new(NotificationDeliveryStatus.Rejected, "notification-credentials-rejected");
            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.MethodNotAllowed or
                HttpStatusCode.RequestEntityTooLarge or HttpStatusCode.UnsupportedMediaType or HttpStatusCode.UnprocessableEntity)
                return new(NotificationDeliveryStatus.Rejected, "notification-payload-rejected");
            if (response.StatusCode != HttpStatusCode.OK)
                return new(NotificationDeliveryStatus.DeliveryUnknown, "notification-delivery-unconfirmed");

            var responseBody = await ReadBoundedBodyAsync(response.Content, linked.Token).ConfigureAwait(false);
            if (responseBody is not { } confirmedBody)
                return new(NotificationDeliveryStatus.DeliveryUnknown, "notification-response-too-large");
            if (provider == NotificationProvider.Slack)
                return Encoding.UTF8.GetString(confirmedBody.Span).Trim() == "ok"
                    ? new(NotificationDeliveryStatus.Succeeded, "notification-delivered")
                    : new(NotificationDeliveryStatus.DeliveryUnknown, "notification-delivery-unconfirmed");

            var messageId = ReadDiscordMessageId(confirmedBody);
            return messageId is not null
                ? new(NotificationDeliveryStatus.Succeeded, "notification-delivered", messageId)
                : new(NotificationDeliveryStatus.DeliveryUnknown, "notification-delivery-unconfirmed");
        }
        catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException)
        {
            // A received 429 proves rejection even if reading its optional body fails. Otherwise a
            // sent POST may have committed; neither its exception text nor an unsafe replay escapes.
            return rateLimited ? RateLimitResult(stripe, retryAfter)
                : new(NotificationDeliveryStatus.DeliveryUnknown, "notification-delivery-unknown");
        }
    }

    private NotificationDeliveryResult RateLimitResult(int stripe, TimeSpan? retryAfter)
    {
        var delay = retryAfter ?? MissingRetryAfter;
        ExtendDeadline(ref _nextAttemptUtcTicks[stripe], _clock.GetUtcNow().UtcTicks + delay.Ticks);
        return new(NotificationDeliveryStatus.RateLimited, "notification-provider-rate-limited", RetryAfter: delay);
    }

    private static void ExtendDeadline(ref long current, long deadline)
    {
        long previous;
        do
        {
            previous = Volatile.Read(ref current);
            if (previous >= deadline) return;
        } while (Interlocked.CompareExchange(ref current, deadline, previous) != previous);
    }

    private TimeSpan? ReadRetryAfterHeader(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Retry-After", out var values)) return null;
        foreach (var value in values)
        {
            if (double.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds) && TryDelay(seconds, out var delay))
                return delay;
            if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) &&
                TryDelay((date - _clock.GetUtcNow()).TotalSeconds, out delay))
                return delay;
        }
        return null;
    }

    private static bool TryReadDiscordRetry(ReadOnlyMemory<byte> body, out TimeSpan retryAfter, out bool global)
    {
        retryAfter = default;
        global = false;
        try
        {
            using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("retry_after", out var value) ||
                value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var seconds) || !TryDelay(seconds, out retryAfter))
                return false;
            global = root.TryGetProperty("global", out var globalValue) && globalValue.ValueKind == JsonValueKind.True;
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static bool TryDelay(double seconds, out TimeSpan delay)
    {
        delay = default;
        if (!double.IsFinite(seconds) || seconds < 0 || seconds > TimeSpan.FromDays(365).TotalSeconds) return false;
        delay = TimeSpan.FromSeconds(Math.Max(1, seconds));
        return true;
    }

    private static string? ReadDiscordMessageId(ReadOnlyMemory<byte> body)
    {
        try
        {
            using var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 16 });
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String)
                return null;
            return id.GetString() is { } value && NotificationWebhookAddress.IsSnowflake(value) ? value : null;
        }
        catch (JsonException) { return null; }
    }

    private static async Task<ReadOnlyMemory<byte>?> ReadBoundedBodyAsync(HttpContent content, CancellationToken ct)
    {
        if (content.Headers.ContentLength > MaximumResponseBytes) return null;
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var buffer = new byte[MaximumResponseBytes + 1];
        var count = 0;
        while (count < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(count), ct).ConfigureAwait(false);
            if (read == 0) return buffer.AsMemory(0, count);
            count += read;
        }
        return null;
    }

    private static string NormalizeText(string message)
    {
        var result = new StringBuilder(message.Length);
        foreach (var rune in message.EnumerateRunes())
            if (!Rune.IsControl(rune) || rune.Value is '\n' or '\t') result.Append(rune.ToString());
        return result.ToString();
    }

    private static string EscapeSlackText(string text) => text.Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);

    public void Dispose() => _client.Dispose();
}
