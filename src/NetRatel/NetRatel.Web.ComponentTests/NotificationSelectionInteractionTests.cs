using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using NetRatel.Application.Notifications;
using NetRatel.Shared.Contracts;
using NetRatel.Web.Components.Pages;
using NetRatel.Web.Services.Notifications;
using Xunit;

namespace NetRatel.Web.ComponentTests;

public sealed class NotificationSelectionInteractionTests : AsyncBunitContext
{
    [Fact]
    public void Rendered_selection_excludes_read_rows_and_posts_only_selected_unread_ids()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices(options => options.PopoverOptions.CheckForPopoverProvider = false);
        using var transport = new NotificationTransport();
        Services.AddSingleton<IHttpClientFactory>(transport);
        Services.AddHttpContextAccessor();
        Services.AddScoped<INetRatelNotificationApiClient, NetRatelNotificationApiClient>();
        Services.AddSingleton<NetRatelNotificationEventBus>();
        Services.AddScoped<NetRatelNotificationStreamService>();
        var cut = Render<Notifications>();
        cut.WaitForAssertion(() => cut.FindAll("input[type=checkbox]").Count.Should().Be(4));
        cut.FindAll("input[type=checkbox]")[3].HasAttribute("disabled").Should().BeTrue();
        cut.FindAll("input[type=checkbox]")[1].Change(true);
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Mark Selected Read (1)"));
        cut.FindAll("input[type=checkbox]")[0].Change(true);
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Mark Selected Read (2)"));
        cut.FindAll("input[type=checkbox]")[0].Change(false);
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Mark Selected Read (0)"));
        cut.FindAll("input[type=checkbox]")[0].Change(true);
        cut.FindAll("input[type=checkbox]")[1].Change(false);
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Mark Selected Read (1)"));
        cut.FindAll("button").Single(button => button.TextContent.Contains("Mark Selected Read")).Click();
        cut.WaitForAssertion(() => transport.Marked.Should().Equal(transport.Items[1].Id));
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Mark Selected Read (0)"));
        transport.DetailRequests.Should().Be(0);
    }

    private sealed class NotificationTransport : HttpMessageHandler, IHttpClientFactory
    {
        public NetRatelNotificationDto[] Items { get; } = Enumerable.Range(0, 3).Select(index => new NetRatelNotificationDto
        {
            Id = Guid.NewGuid(), EventType = "Test.Event", Message = $"Notification {index}",
            Source = "Test", Status = "Published", CorrelationId = $"correlation-{index}",
            OccurredUtc = DateTimeOffset.UtcNow, IsRead = index == 2
        }).ToArray();
        public Guid[] Marked { get; private set; } = [];
        public int DetailRequests { get; private set; }
        public HttpClient CreateClient(string name) => new(this, false) { BaseAddress = new Uri("https://api.example.test") };
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/stream", StringComparison.Ordinal))
            {
                // The stream stays pending until the component is disposed.
                var completion = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
                using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
                return await completion.Task;
            }
            if (path == "/api/v1/notifications/mark-read")
            {
                request.Method.Should().Be(HttpMethod.Post);
                using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                Marked = json.RootElement.GetProperty("ids").EnumerateArray().Select(item => item.GetGuid()).ToArray();
                return Ok(new { updated = Marked.Length });
            }
            if (path == "/api/v1/notifications/summary")
                return Ok(new NetRatelNotificationSummaryDto { TotalCount = 3, UnreadCount = 2 });
            if (path == "/api/v1/notifications")
                return Ok(new PagedResult<NetRatelNotificationDto>(Items, 1, 20, 3));
            DetailRequests++;
            throw new InvalidOperationException($"Unexpected API request: {request.Method} {path}");
        }
        private static HttpResponseMessage Ok<T>(T body) => new(HttpStatusCode.OK) { Content = JsonContent.Create(body) };
    }
}
