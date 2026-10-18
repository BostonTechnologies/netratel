using System.IO.Pipelines;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using NetRatel.AgentClient;
using NetRatel.Mcp;
using Xunit;

namespace NetRatel.Tests.Mcp;

public sealed class HttpMcpBridgeTests
{
    [Theory]
    [InlineData("2025-03-26")]
    [InlineData("2025-06-18")]
    [InlineData("2025-11-25")]
    public async Task Initialize_negotiates_with_the_real_http_transport_and_subsequent_reads_work(string protocolVersion)
    {
        await using var fixture = await Fixture.StartAsync(null);
        await fixture.SendAsync(JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0", id = "initialize", method = "initialize",
            @params = new { protocolVersion, capabilities = new { }, clientInfo = new { name = "bridge-regression", version = "1" } }
        }));
        var initialized = await fixture.ReadAsync();
        Assert.Equal("initialize", initialized.GetProperty("id").GetString());
        Assert.Equal(protocolVersion, initialized.GetProperty("result").GetProperty("protocolVersion").GetString());
        await fixture.SendAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
        await fixture.SendAsync("""{"jsonrpc":"2.0","id":"catalog","method":"tools/list","params":{}}""");
        var catalog = await fixture.ReadAsync();
        Assert.Equal("catalog", catalog.GetProperty("id").GetString());
        Assert.Equal("read_status", Assert.Single(catalog.GetProperty("result").GetProperty("tools").EnumerateArray()).GetProperty("name").GetString());
        await fixture.SendAsync("""{"jsonrpc":"2.0","id":"read","method":"tools/call","params":{"name":"read_status","arguments":{}}}""");
        var read = await fixture.ReadAsync();
        Assert.Equal("read", read.GetProperty("id").GetString());
        Assert.Equal("ready", read.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());
        await fixture.CompleteAsync();
    }

    [Fact]
    public async Task Split_utf8_sse_progress_is_observable_while_final_reply_is_gated()
    {
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finalStreamClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = await Fixture.StartAsync(async context =>
        {
            context.Response.ContentType = "text/event-stream";
            var bytes = Encoding.UTF8.GetBytes(": keepalive\r\revent: ping\r\ndata: keepalive\r\n\r\nevent: message\r\ndata: {\"jsonrpc\":\"2.0\",\"method\":\"notifications/progress\",\"params\":{\"message\":\"café 🦦\"}}\n\n");
            // Split inside a multi-byte UTF-8 scalar, as well as across SSE field boundaries.
            var split = Array.IndexOf(bytes, (byte)0xc3) + 1;
            await context.Response.Body.WriteAsync(bytes.AsMemory(0, split));
            await context.Response.Body.FlushAsync();
            await context.Response.Body.WriteAsync(bytes.AsMemory(split));
            await context.Response.Body.FlushAsync();
            await finish.Task.WaitAsync(context.RequestAborted);
            await context.Response.WriteAsync("data: {\"jsonrpc\":\"2.0\",\n");
            await context.Response.WriteAsync("data: \"id\":\"stream\",\"result\":{\"text\":\"café 🦦\"}}\n\n");
            await context.Response.Body.FlushAsync();
            try { await Task.Delay(Timeout.Infinite, context.RequestAborted); }
            catch (OperationCanceledException) { finalStreamClosed.SetResult(); }
        });
        await fixture.SendAsync("""{"jsonrpc":"2.0","id":"stream","method":"tools/call"}""");
        var progress = await fixture.ReadAsync();
        Assert.Equal("notifications/progress", progress.GetProperty("method").GetString());
        Assert.Equal("café 🦦", progress.GetProperty("params").GetProperty("message").GetString());
        Assert.False(finish.Task.IsCompleted); // final cannot have completed before the observed progress
        finish.SetResult();
        var reply = await fixture.ReadAsync();
        Assert.Equal("stream", reply.GetProperty("id").GetString());
        Assert.Equal("café 🦦", reply.GetProperty("result").GetProperty("text").GetString());
        await fixture.CompleteAsync();
        await finalStreamClosed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task String_cancellation_reaches_endpoint_while_pending_and_out_of_order_ids_stay_exact()
    {
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = await Fixture.StartAsync(async context =>
        {
            using var document = await JsonDocument.ParseAsync(context.Request.Body);
            var message = document.RootElement;
            if (message.GetProperty("method").GetString() == "notifications/cancelled")
            {
                Assert.False(disconnected.Task.IsCompleted);
                Assert.Equal("1", message.GetProperty("params").GetProperty("requestId").GetString());
                cancelled.SetResult();
                context.Response.StatusCode = 202;
                return;
            }
            var id = message.GetProperty("id");
            if (id.ValueKind == JsonValueKind.String)
            {
                pending.SetResult();
                try { await Task.Delay(Timeout.Infinite, context.RequestAborted); }
                catch (OperationCanceledException) { disconnected.SetResult(); }
                return;
            }
            await context.Response.WriteAsJsonAsync(new { jsonrpc = "2.0", id = 1, result = new { completed = true } });
        });
        await fixture.SendAsync("""{"jsonrpc":"2.0","id":"1","method":"tools/call"}""");
        await pending.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.SendAsync("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""");
        Assert.Equal(1, (await fixture.ReadAsync()).GetProperty("id").GetInt32());
        await fixture.SendAsync("""{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":"1"}}""");
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var cancellation = await fixture.ReadAsync();
        Assert.Equal("1", cancellation.GetProperty("id").GetString());
        Assert.Equal(-32800, cancellation.GetProperty("error").GetProperty("code").GetInt32());
        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.CompleteAsync();
        Assert.False(fixture.Output.TryRead(out _));
    }

    [Fact]
    public async Task Malformed_and_oversized_stdin_recover_without_replying_to_ordinary_notifications()
    {
        var seen = new List<string>();
        await using var fixture = await Fixture.StartAsync(async context =>
        {
            using var document = await JsonDocument.ParseAsync(context.Request.Body);
            var message = document.RootElement;
            lock (seen) seen.Add(message.GetProperty("method").GetString()!);
            if (!message.TryGetProperty("id", out var id)) { context.Response.StatusCode = 202; return; }
            await context.Response.WriteAsJsonAsync(new { jsonrpc = "2.0", id = id.Clone(), result = new { accepted = true } });
        });
        await fixture.SendAsync("{");
        var malformed = await fixture.ReadAsync();
        Assert.Equal(JsonValueKind.Null, malformed.GetProperty("id").ValueKind);
        Assert.Equal(-32700, malformed.GetProperty("error").GetProperty("code").GetInt32());
        await fixture.SendAsync(new string('x', HttpMcpBridge.MaximumFrameCharacters + 1));
        Assert.Equal(-32700, (await fixture.ReadAsync()).GetProperty("error").GetProperty("code").GetInt32());
        await fixture.SendAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
        await fixture.SendAsync("""{"jsonrpc":"2.0","id":"after","method":"tools/list"}""");
        Assert.Equal("after", (await fixture.ReadAsync()).GetProperty("id").GetString());
        await fixture.CompleteAsync();
        Assert.False(fixture.Output.TryRead(out _));
        Assert.Equal(2, seen.Count);
    }

    [Theory]
    [InlineData("12", 12)]
    [InlineData("invalid-secret-like-value", null)]
    [InlineData("999999999", null)]
    [InlineData("-1", null)]
    public async Task Rate_limit_preserves_only_safe_retry_detail_and_never_replays(string retry, int? seconds)
    {
        var calls = 0;
        await using var fixture = await Fixture.StartAsync(context =>
        {
            Interlocked.Increment(ref calls);
            context.Response.StatusCode = 429;
            context.Response.Headers.RetryAfter = retry;
            return context.Response.WriteAsync("private remote body must not escape");
        });
        await fixture.SendAsync("""{"jsonrpc":"2.0","id":44,"method":"tools/call"}""");
        var reply = await fixture.ReadAsync();
        var data = reply.GetProperty("error").GetProperty("data");
        Assert.Equal("rate_limited", data.GetProperty("code").GetString());
        Assert.Equal(429, data.GetProperty("httpStatus").GetInt32());
        if (seconds is null) Assert.Equal(JsonValueKind.Null, data.GetProperty("retryAfterSeconds").ValueKind);
        else Assert.Equal(seconds.Value, data.GetProperty("retryAfterSeconds").GetDouble());
        Assert.False(data.GetProperty("automaticReplay").GetBoolean());
        Assert.DoesNotContain("private remote", reply.GetRawText());
        Assert.DoesNotContain("invalid-secret", reply.GetRawText());
        await fixture.CompleteAsync();
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Timeout_distinguishes_uncertain_outcome_and_requires_reconciliation()
    {
        var calls = 0;
        await using var fixture = await Fixture.StartAsync(async context =>
        {
            Interlocked.Increment(ref calls);
            try { await Task.Delay(Timeout.Infinite, context.RequestAborted); }
            catch (OperationCanceledException) { }
        }, TimeSpan.FromMilliseconds(200));
        await fixture.SendAsync("""{"jsonrpc":"2.0","id":"mutate","method":"tools/call"}""");
        var error = (await fixture.ReadAsync()).GetProperty("error");
        Assert.Equal(-32003, error.GetProperty("code").GetInt32());
        var data = error.GetProperty("data");
        Assert.Equal("transport_timeout", data.GetProperty("code").GetString());
        Assert.Equal("uncertain", data.GetProperty("outcome").GetString());
        Assert.Equal("reconcile_before_retry", data.GetProperty("nextAction").GetString());
        await fixture.CompleteAsync();
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Outstanding_limit_keeps_stdin_available_and_rejects_excess_before_dispatch()
    {
        var entered = new SemaphoreSlim(0);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        await using var fixture = await Fixture.StartAsync(async context =>
        {
            Interlocked.Increment(ref calls);
            using var document = await JsonDocument.ParseAsync(context.Request.Body);
            var id = document.RootElement.GetProperty("id").Clone();
            entered.Release();
            await release.Task.WaitAsync(context.RequestAborted);
            await context.Response.WriteAsJsonAsync(new { jsonrpc = "2.0", id, result = new { } });
        });
        for (var index = 0; index < HttpMcpBridge.MaximumOutstandingRequests; index++)
            await fixture.SendAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id = index, method = "tools/call" }));
        for (var index = 0; index < HttpMcpBridge.MaximumOutstandingRequests; index++)
            Assert.True(await entered.WaitAsync(TimeSpan.FromSeconds(5)));
        await fixture.SendAsync("""{"jsonrpc":"2.0","id":"excess","method":"tools/call"}""");
        var rejected = await fixture.ReadAsync();
        Assert.Equal("excess", rejected.GetProperty("id").GetString());
        Assert.Equal("client_busy", rejected.GetProperty("error").GetProperty("data").GetProperty("code").GetString());
        Assert.Equal(HttpMcpBridge.MaximumOutstandingRequests, calls);
        release.SetResult();
        for (var index = 0; index < HttpMcpBridge.MaximumOutstandingRequests; index++)
            Assert.True((await fixture.ReadAsync()).TryGetProperty("result", out _));
        await fixture.CompleteAsync();
    }

    [Fact]
    public async Task Oversized_sse_event_is_rejected_without_accepting_a_later_final_response()
    {
        await using var fixture = await Fixture.StartAsync(async context =>
        {
            context.Response.ContentType = "text/event-stream";
            await context.Response.WriteAsync("data: " + new string('x', HttpMcpBridge.MaximumFrameCharacters) + "\n\n");
        });
        await fixture.SendAsync("""{"jsonrpc":"2.0","id":"bounded","method":"tools/call"}""");
        var reply = await fixture.ReadAsync();
        Assert.Equal("bounded", reply.GetProperty("id").GetString());
        Assert.Equal("transport_response_invalid", reply.GetProperty("error").GetProperty("data").GetProperty("code").GetString());
        await fixture.CompleteAsync();
    }

    [Fact]
    public async Task Session_get_starts_after_initialized_notification_is_accepted()
    {
        var initialized = false;
        var notificationEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var acceptNotification = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var getEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = await Fixture.StartAsync(async context =>
        {
            if (context.Request.Method == "GET")
            {
                Assert.True(initialized);
                Assert.Equal("synthetic-session", context.Request.Headers["Mcp-Session-Id"]);
                Assert.Equal("2025-11-25", context.Request.Headers["MCP-Protocol-Version"]);
                getEntered.SetResult();
                context.Response.ContentType = "text/event-stream";
                await context.Response.WriteAsync("data: {\"jsonrpc\":\"2.0\",\"method\":\"notifications/tools/list_changed\"}\n\n");
                await context.Response.Body.FlushAsync();
                try { await Task.Delay(Timeout.Infinite, context.RequestAborted); }
                catch (OperationCanceledException) { }
                return;
            }
            using var document = await JsonDocument.ParseAsync(context.Request.Body);
            var message = document.RootElement;
            if (message.GetProperty("method").GetString() == "initialize")
            {
                Assert.False(context.Request.Headers.ContainsKey("MCP-Protocol-Version"));
                context.Response.Headers["Mcp-Session-Id"] = "synthetic-session";
                await context.Response.WriteAsJsonAsync(new { jsonrpc = "2.0", id = 1, result = new { protocolVersion = "2025-11-25" } });
                return;
            }
            notificationEntered.SetResult();
            await acceptNotification.Task.WaitAsync(context.RequestAborted);
            initialized = true;
            context.Response.StatusCode = 202;
        });
        await fixture.SendAsync("""{"jsonrpc":"2.0","id":1,"method":"initialize"}""");
        Assert.Equal(1, (await fixture.ReadAsync()).GetProperty("id").GetInt32());
        await fixture.SendAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
        await notificationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(getEntered.Task.IsCompleted);
        acceptNotification.SetResult();
        await getEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("notifications/tools/list_changed", (await fixture.ReadAsync()).GetProperty("method").GetString());
        await fixture.CompleteAsync();
    }

    [Fact]
    public async Task Caller_cancellation_drains_pending_http_sends_before_shutdown_returns()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = await Fixture.StartAsync(async context =>
        {
            entered.SetResult();
            try { await Task.Delay(Timeout.Infinite, context.RequestAborted); }
            catch (OperationCanceledException) { disconnected.SetResult(); }
        });
        await fixture.SendAsync("""{"jsonrpc":"2.0","id":"shutdown","method":"tools/call"}""");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(fixture.CancelAsync);
        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(fixture.Output.TryRead(out _));
    }

    [Theory]
    [InlineData("local_credential_expired")]
    [InlineData("local_credential_revoked")]
    [InlineData("local_credential_invalid")]
    [InlineData("unknown-private-credential-text")]
    public async Task Auth_failure_retains_only_allowlisted_code_local_remediation_and_safe_correlation(string code)
    {
        var calls = 0;
        await using var fixture = await Fixture.StartAsync(context =>
        {
            Interlocked.Increment(ref calls);
            context.Response.StatusCode = 401;
            return context.Response.WriteAsJsonAsync(new
            {
                code, correlationId = "synthetic-correlation:1",
                failure = new { remediation = "remote secret text must not escape", retryable = true },
                credential = "synthetic-credential-body-must-not-escape"
            });
        });
        await fixture.SendAsync("""{"jsonrpc":"2.0","id":"auth","method":"tools/call"}""");
        var reply = await fixture.ReadAsync();
        var data = reply.GetProperty("error").GetProperty("data");
        if (code.StartsWith("local_credential_", StringComparison.Ordinal))
        {
            Assert.Equal(code, data.GetProperty("code").GetString());
            Assert.Equal("synthetic-correlation:1", data.GetProperty("correlationId").GetString());
            Assert.False(data.GetProperty("failure").GetProperty("retryable").GetBoolean());
            Assert.Equal("not_admitted", data.GetProperty("outcome").GetString());
            Assert.Equal(NetRatel.Shared.Operations.McpDelegationFailure.ForCode(code)!.Remediation,
                data.GetProperty("failure").GetProperty("remediation").GetString());
        }
        else
        {
            Assert.Equal("http_error", data.GetProperty("code").GetString());
            Assert.Equal(JsonValueKind.Null, data.GetProperty("correlationId").ValueKind);
            Assert.Equal(JsonValueKind.Null, data.GetProperty("failure").ValueKind);
        }
        Assert.DoesNotContain("remote secret", reply.GetRawText());
        Assert.DoesNotContain("must-not-escape", reply.GetRawText());
        Assert.DoesNotContain("unknown-private", reply.GetRawText());
        await fixture.CompleteAsync();
        Assert.Equal(1, calls);
    }

    private sealed class Fixture(WebApplication app, HttpClient http, Pipe input, StreamWriter writer,
        OutputWriter output, Task run, string directory, CancellationTokenSource lifetime) : IAsyncDisposable
    {
        public ChannelReader<string> Output => output.Frames.Reader;
        public async Task CancelAsync() { lifetime.Cancel(); await run.WaitAsync(TimeSpan.FromSeconds(5)); }
        public async Task SendAsync(string frame) { await writer.WriteLineAsync(frame); await writer.FlushAsync(); }
        public async Task<JsonElement> ReadAsync()
        {
            var frame = await Output.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.DoesNotContain('\n', frame);
            using var document = JsonDocument.Parse(frame);
            return document.RootElement.Clone();
        }
        public async Task CompleteAsync() { await input.Writer.CompleteAsync(); await run.WaitAsync(TimeSpan.FromSeconds(10)); }
        public static async Task<Fixture> StartAsync(RequestDelegate? handle, TimeSpan? timeout = null)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            if (handle is null) builder.Services.AddMcpServer()
                .WithHttpTransport(options => options.Stateless = true)
                .WithTools([McpServerTool.Create((Func<string>)(() => "ready"), new() { Name = "read_status" })]);
            var app = builder.Build();
            if (handle is null) app.MapMcp("/");
            else app.Run(handle);
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            var directory = Path.Combine(Path.GetTempPath(), "netratel-bridge-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var secretFile = Path.Combine(directory, "credential.secret");
            ConfigurationFileStorage.WriteAtomic(secretFile, "synthetic-dummy-credential");
            var config = NetRatelConfigurationService.ResolveSecretReferences(AgentClientConfiguration.Empty with { IntegrationCredentialFile = secretFile });
            var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
            var pipe = new Pipe();
            var reader = new StreamReader(pipe.Reader.AsStream(), new UTF8Encoding(false, true));
            var writer = new StreamWriter(pipe.Writer.AsStream(), new UTF8Encoding(false)) { NewLine = "\n" };
            var output = new OutputWriter();
            var bridge = new HttpMcpBridge(http, new Uri(address), _ => Task.FromResult(config.IntegrationCredential!), timeout);
            var lifetime = new CancellationTokenSource();
            return new(app, http, pipe, writer, output, bridge.RunAsync(reader, output, TextWriter.Null, lifetime.Token), directory, lifetime);
        }
        public async ValueTask DisposeAsync()
        {
            lifetime.Cancel();
            http.Dispose();
            await app.DisposeAsync();
            writer.Dispose();
            lifetime.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }
    private sealed class OutputWriter : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
        public Channel<string> Frames { get; } = Channel.CreateUnbounded<string>();
        public override Task WriteLineAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
            => Frames.Writer.WriteAsync(buffer.ToString(), cancellationToken).AsTask();
    }
}
