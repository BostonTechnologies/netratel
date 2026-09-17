using System.Net;
using System.Reflection;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using NetRatel.Application.Notifications;
using NetRatel.Shared.Contracts;
using NetRatel.Web.Services.Notifications;
using NotificationsPage = NetRatel.Web.Components.Pages.Notifications;
using Xunit;

namespace NetRatel.Web.ComponentTests;

public sealed class NotificationsPageTests : AsyncBunitContext
{
    private readonly StubNotificationApiClient _notifications = new();

    public NotificationsPageTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices(options => options.PopoverOptions.CheckForPopoverProvider = false);
        Services.AddSingleton<INetRatelNotificationApiClient>(_notifications);
        Services.AddSingleton<NetRatelNotificationEventBus>();
        Services.AddSingleton<IHttpClientFactory>(new StubHttpClientFactory());
        Services.AddScoped<NetRatelNotificationStreamService>();
    }

    [Fact]
    public async Task NotificationsPage_AppliesOccurredRangeToPageQuery()
    {
        var cut = RenderNotificationsPage();
        await WaitForInitialLoadAsync(cut);

        SetPrivate(cut.Instance, "pendingFromDate", new DateTime(2026, 5, 20));
        SetPrivate(cut.Instance, "pendingFromTime", new TimeSpan(5, 30, 0));
        SetPrivate(cut.Instance, "pendingToDate", new DateTime(2026, 5, 20));
        SetPrivate(cut.Instance, "pendingToTime", new TimeSpan(6, 45, 0));

        await cut.InvokeAsync(() => InvokePrivateTaskAsync(cut.Instance, "ApplyOccurredFilterAsync"));

        _notifications.LastFrom.Should().NotBeNull();
        _notifications.LastTo.Should().NotBeNull();
        _notifications.LastFrom!.Value.ToLocalTime().Date.Should().Be(new DateTime(2026, 5, 20));
        _notifications.LastFrom!.Value.ToLocalTime().TimeOfDay.Should().Be(new TimeSpan(5, 30, 0));
        _notifications.LastTo!.Value.ToLocalTime().Date.Should().Be(new DateTime(2026, 5, 20));
        _notifications.LastTo!.Value.ToLocalTime().TimeOfDay.Should().Be(new TimeSpan(6, 45, 0));
        cut.Render();
        cut.Markup.Should().Contain("Occurred *");
        cut.Markup.Should().Contain("05/20 05:30");
        cut.Markup.Should().Contain("05/20 06:45");
    }

    [Fact]
    public async Task NotificationsPage_ClearFiltersRemovesOccurredRange()
    {
        var cut = RenderNotificationsPage();
        await WaitForInitialLoadAsync(cut);

        SetPrivate(cut.Instance, "pendingFromDate", new DateTime(2026, 5, 20));
        SetPrivate(cut.Instance, "pendingToDate", new DateTime(2026, 5, 20));
        await cut.InvokeAsync(() => InvokePrivateTaskAsync(cut.Instance, "ApplyOccurredFilterAsync"));

        _notifications.LastFrom.Should().NotBeNull();
        _notifications.LastTo.Should().NotBeNull();

        await cut.InvokeAsync(() => InvokePrivateTaskAsync(cut.Instance, "ClearFiltersAsync"));

        _notifications.LastFrom.Should().BeNull();
        _notifications.LastTo.Should().BeNull();
        cut.Markup.Should().Contain(">Occurred<");
    }

    [Fact]
    public async Task NotificationsPage_LiveNotificationsRespectOccurredRange()
    {
        _notifications.PageItems = [];
        var cut = RenderNotificationsPage();
        await WaitForInitialLoadAsync(cut);

        var outside = NewNotification("Outside event", new DateTimeOffset(2026, 5, 20, 2, 0, 0, TimeSpan.Zero));
        var inside = NewNotification("Inside event", new DateTimeOffset(2026, 5, 20, 5, 30, 0, TimeSpan.Zero));
        SetPrivate(cut.Instance, "occurredFrom", inside.OccurredUtc.AddMinutes(-10));
        SetPrivate(cut.Instance, "occurredTo", inside.OccurredUtc.AddMinutes(10));

        await cut.InvokeAsync(() =>
        {
            InvokePrivate(cut.Instance, "InsertLiveNotification", outside);
            InvokePrivate(cut.Instance, "InsertLiveNotification", inside);
        });
        cut.Render();

        cut.Markup.Should().Contain("Inside event");
        cut.Markup.Should().NotContain("Outside event");
    }

    private IRenderedComponent<NotificationsPage> RenderNotificationsPage()
        => Render<NotificationsPage>();

    private async Task WaitForInitialLoadAsync(IRenderedComponent<NotificationsPage> cut)
    {
        await cut.InvokeAsync(() => Task.CompletedTask);
        cut.WaitForAssertion(() => _notifications.PageCalls.Should().BeGreaterThan(0));
    }

    private static NetRatelNotificationDto NewNotification(string message, DateTimeOffset occurredUtc) => new()
    {
        Id = Guid.NewGuid(),
        EventType = "DomainEvent.Agent.TokenIssued",
        Source = "Agent",
        CorrelationId = Guid.NewGuid().ToString("N"),
        EntityId = Guid.NewGuid().ToString("N"),
        Message = message,
        OccurredUtc = occurredUtc,
        Severity = NetRatelNotificationSeverity.Info,
        Status = "Published"
    };

    private static void SetPrivate(object instance, string fieldName, object? value)
        => GetField(instance, fieldName).SetValue(instance, value);

    private static void InvokePrivate(object instance, string methodName, params object?[] args)
        => GetMethod(instance, methodName).Invoke(instance, args);

    private static async Task InvokePrivateTaskAsync(object instance, string methodName, params object?[] args)
    {
        var task = (Task)GetMethod(instance, methodName).Invoke(instance, args)!;
        await task;
    }

    private static FieldInfo GetField(object instance, string fieldName)
        => instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
           ?? throw new MissingFieldException(instance.GetType().Name, fieldName);

    private static MethodInfo GetMethod(object instance, string methodName)
        => instance.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)
           ?? throw new MissingMethodException(instance.GetType().Name, methodName);

    private sealed class StubNotificationApiClient : INetRatelNotificationApiClient
    {
        public int PageCalls { get; private set; }
        public DateTimeOffset? LastFrom { get; private set; }
        public DateTimeOffset? LastTo { get; private set; }
        public IReadOnlyList<NetRatelNotificationDto> PageItems { get; set; } =
        [
            NewNotification("Initial notification", DateTimeOffset.UtcNow)
        ];

        public Task<PagedResult<NetRatelNotificationDto>> GetPageAsync(
            int page = 1,
            int pageSize = 20,
            string? eventType = null,
            string? correlationId = null,
            string? entityId = null,
            string? status = null,
            DateTimeOffset? from = null,
            DateTimeOffset? to = null,
            string? searchTerm = null,
            string? source = null,
            NetRatelNotificationSeverity? severity = null,
            CancellationToken ct = default)
        {
            PageCalls++;
            LastFrom = from;
            LastTo = to;
            return Task.FromResult(new PagedResult<NetRatelNotificationDto>(PageItems, page, pageSize, PageItems.Count));
        }

        public Task<NetRatelNotificationDto?> GetByIdAsync(Guid id, CancellationToken ct = default)
            => Task.FromResult(PageItems.FirstOrDefault(x => x.Id == id));

        public Task<IReadOnlyList<NetRatelNotificationDto>> GetUnreadErrorsAsync(int take = 20, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<NetRatelNotificationDto>>(Array.Empty<NetRatelNotificationDto>());

        public Task<NetRatelNotificationSummaryDto> GetSummaryAsync(CancellationToken ct = default)
            => Task.FromResult(new NetRatelNotificationSummaryDto { TotalCount = PageItems.Count });

        public Task<int> MarkReadBulkAsync(IEnumerable<Guid> ids, CancellationToken ct = default)
            => Task.FromResult(0);

        public Task RetryAsync(Guid id, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => new(new StubHandler())
            {
                BaseAddress = new Uri("https://netratel.test")
            };
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new WaitingStream())
            });
    }

    private sealed class WaitingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => 0;
        public override long Position { get => 0; set { } }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => 0;
        public override long Seek(long offset, SeekOrigin origin) => 0;
        public override void SetLength(long value) { }
        public override void Write(byte[] buffer, int offset, int count) { }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }
}
