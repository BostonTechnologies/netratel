using System.Net;
using System.Security.Claims;
using System.Text;
using AwesomeAssertions;
using Microsoft.AspNetCore.Components.Authorization;
using NetRatel.Web.Services;
using NetRatel.Web.Services.ClientTasks;
using Xunit;

namespace NetRatel.Tests.Web;

public sealed class TaskExecutionStreamTests
{
    [Fact]
    public async Task ReconnectAndFailedSnapshot_KeepRecoveringPersistedOutput_AndNeverClaimDisconnectedLive()
    {
        var handler = new SequenceHandler([
            "data: {\"requestId\":\"task-a\",\"connected\":true}\n\n" +
            "data: {\"requestId\":\"task-a\",\"connected\":false}\n\n",
            "data: {\"requestId\":\"task-a\",\"connected\":true}\n\n"]);
        using var http = new HttpClient(handler) { BaseAddress = new("https://api.invalid") };
        await using var transport = new SseClient(http);
        await using var stream = new TaskExecutionStream(transport, new FixtureAuthentication());
        var attempts = 0;
        var statuses = new List<string>();
        var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await stream.StartAsync("task-a", _ =>
        {
            if (++attempts == 1) throw new IOException("Temporary output fetch failure");
            return Task.FromResult(true);
        }, status =>
        {
            statuses.Add(status);
            if (status == "Live") recovered.TrySetResult();
        });
        await recovered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await stream.StopAsync();
        attempts.Should().Be(3);
        statuses.Should().ContainInOrder("Reconnecting", "Recovering", "Reconnecting", "Live");
        handler.Requests.Should().Be(2);
        handler.LastUri!.Query.Should().Be("?requestId=task-a");
    }

    [Fact]
    public async Task ForbiddenStream_PausesInsteadOfRetryingRejectedCredentials()
    {
        var handler = new SequenceHandler([]) { Status = HttpStatusCode.Forbidden };
        using var http = new HttpClient(handler) { BaseAddress = new("https://api.invalid") };
        await using var transport = new SseClient(http);
        await using var stream = new TaskExecutionStream(transport, new FixtureAuthentication());
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await stream.StartAsync("task-a", _ => Task.FromResult(true), status => { if (status == "Paused") paused.TrySetResult(); });
        await paused.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await stream.StopAsync();
        handler.Requests.Should().Be(1);
    }

    private sealed class SequenceHandler(string[] responses) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;
        public Uri? LastUri { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastUri = request.RequestUri;
            var index = Requests++;
            return Task.FromResult(new HttpResponseMessage(Status)
            { Content = new StringContent(index < responses.Length ? responses[index] : string.Empty, Encoding.UTF8, "text/event-stream") });
        }
    }

    private sealed class FixtureAuthentication : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "fixture")], "fixture"))));
    }
}
