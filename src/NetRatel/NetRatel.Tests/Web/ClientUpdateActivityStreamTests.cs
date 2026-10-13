using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using AwesomeAssertions;
using Microsoft.AspNetCore.Components.Authorization;
using NetRatel.Web.Services;
using Xunit;

namespace NetRatel.Tests.Web;

public sealed class ClientUpdateActivityStreamTests
{
    [Fact]
    public async Task Reconnect_delivers_a_new_ready_snapshot_and_deduplicates_bounded_invalidations()
    {
        var id = Guid.NewGuid();
        var handler = new SequenceHandler([
            $"data: {{\"kind\":\"ready\",\"id\":\"{Guid.NewGuid()}\",\"connected\":true}}\n\n" +
            $"data: {{\"kind\":\"invalidate\",\"id\":\"{id}\",\"connected\":true}}\n\n" +
            $"data: {{\"kind\":\"invalidate\",\"id\":\"{id}\",\"connected\":true}}\n\n",
            $"data: {{\"kind\":\"ready\",\"id\":\"{Guid.NewGuid()}\",\"connected\":true}}\n\n"]);
        using var http = new HttpClient(handler) { BaseAddress = new("https://api.invalid") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "fixture-credential");
        await using var transport = new SseClient(http);
        await using var stream = new ClientUpdateActivityStream(transport, new FixtureAuthentication());
        var events = new List<ClientUpdateActivityEvent>();
        var reconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await stream.StartAsync(7, notification =>
        {
            events.Add(notification);
            if (events.Count(x => x.Kind == "ready") == 2) reconnected.TrySetResult();
            return Task.CompletedTask;
        }, _ => { });
        await reconnected.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await stream.StopAsync();
        events.Count(x => x.Kind == "invalidate").Should().Be(1);
        handler.Requests.Should().Be(2);
        handler.LastUri!.Query.Should().Be("?tenantId=7");
        handler.Authorization!.Parameter.Should().Be("fixture-credential");
    }

    [Fact]
    public async Task Forbidden_stream_pauses_instead_of_retrying_with_rejected_credentials()
    {
        var handler = new SequenceHandler([]) { Status = HttpStatusCode.Forbidden };
        using var http = new HttpClient(handler) { BaseAddress = new("https://api.invalid") };
        await using var transport = new SseClient(http);
        await using var stream = new ClientUpdateActivityStream(transport, new FixtureAuthentication());
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await stream.StartAsync(null, _ => Task.CompletedTask, status => { if (status == "Paused") paused.TrySetResult(); });
        await paused.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await stream.StopAsync();
        handler.Requests.Should().Be(1);
    }

    [Fact]
    public async Task Rapid_scope_replacement_waits_for_old_callbacks_and_keeps_logout_cancellation()
    {
        var handler = new SequenceHandler([
            $"data: {{\"kind\":\"ready\",\"id\":\"{Guid.NewGuid()}\",\"connected\":true}}\n\n",
            $"data: {{\"kind\":\"ready\",\"id\":\"{Guid.NewGuid()}\",\"connected\":true}}\n\n"]);
        using var http = new HttpClient(handler) { BaseAddress = new("https://api.invalid") };
        var authentication = new FixtureAuthentication();
        await using var transport = new SseClient(http);
        await using var stream = new ClientUpdateActivityStream(transport, authentication);
        var oldCallback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOldCallback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await stream.StartAsync(7, async _ => { oldCallback.TrySetResult(); await releaseOldCallback.Task; }, _ => { });
        await oldCallback.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var stopping = stream.StopAsync();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var replacing = stream.StartAsync(8, _ => { ready.TrySetResult(); return Task.CompletedTask; },
            status => { if (status == "Paused") paused.TrySetResult(); });
        replacing.IsCompleted.Should().BeFalse("a replacement waits for the old stream and callback to drain");
        releaseOldCallback.SetResult();
        await Task.WhenAll(stopping, replacing).WaitAsync(TimeSpan.FromSeconds(2));
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(2));
        authentication.Logout();
        await paused.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await stream.StopAsync();
        handler.Requests.Should().Be(2);
    }

    private sealed class SequenceHandler(string[] responses) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;
        public Uri? LastUri { get; private set; }
        public AuthenticationHeaderValue? Authorization { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastUri = request.RequestUri;
            Authorization = request.Headers.Authorization;
            var index = Requests++;
            return Task.FromResult(new HttpResponseMessage(Status)
            { Content = new StringContent(index < responses.Length ? responses[index] : string.Empty, Encoding.UTF8, "text/event-stream") });
        }
    }

    private sealed class FixtureAuthentication : AuthenticationStateProvider
    {
        public void Logout() => NotifyAuthenticationStateChanged(Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity()))));
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "fixture")], "fixture"))));
    }
}
