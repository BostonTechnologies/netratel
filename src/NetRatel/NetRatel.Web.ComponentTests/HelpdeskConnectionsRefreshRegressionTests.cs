using System.Net;
using System.Net.Http.Json;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using NetRatel.Shared.ServiceLinks;
using NetRatel.Web.Components.Shared.ServiceLinks;
using NetRatel.Web.Services.ServiceLinks;
using Xunit;

namespace NetRatel.Web.ComponentTests;

public sealed class HelpdeskConnectionsRefreshRegressionTests : AsyncBunitContext
{
    private static readonly TimeSpan CompletionBound = TimeSpan.FromSeconds(3);

    public HelpdeskConnectionsRefreshRegressionTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices(options => options.PopoverOptions.CheckForPopoverProvider = false);
        Services.AddScoped<HelpdeskM2MApiClient>();
        ComponentFactories.AddStub<ServiceClientRegistryPanel>();
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Refresh_removes_previous_rows_confirmation_and_probe_before_the_response_and_rejects_queued_actions(HttpStatusCode status)
    {
        using var transport = RegisterTransport();
        var panel = Render<HelpdeskConnectionsPanel>();
        await ShowSuccessfulProbeAsync(panel);
        var callbacks = await CaptureRowCallbacksAsync(panel);
        var response = transport.DelayNextGet();
        var refresh = InvokeAsync(panel, callbacks.Refresh);
        try
        {
            panel.WaitForAssertion(() =>
            {
                Assert.Equal(2, transport.GetRequests);
                AssertNoRetainedState(panel);
                Assert.True(Button(panel, "Refresh status").Instance.Disabled);
            });
            await InvokeAsync(panel, callbacks.Refresh);
            await InvokeAllRowCallbacksAsync(panel, callbacks);
            Assert.Equal(2, transport.GetRequests);
            Assert.Single(transport.PostPaths); // Only the successful probe before refresh.
            AssertNoRetainedState(panel);
        }
        finally
        {
            response.TrySetResult(status == HttpStatusCode.OK
                ? JsonResponse(Array.Empty<ServiceLinkAdminStatus>())
                : new HttpResponseMessage(status) { Content = JsonContent.Create(new { code = "not-authorized" }) });
            await refresh.WaitAsync(CompletionBound);
        }

        await InvokeAllRowCallbacksAsync(panel, callbacks);
        Assert.Single(transport.PostPaths);
        Assert.Equal(2, transport.GetRequests);
        panel.WaitForAssertion(() =>
        {
            AssertNoRetainedState(panel);
            Assert.False(Button(panel, "Refresh status").Instance.Disabled);
            if (status == HttpStatusCode.OK)
                Assert.Contains("No reciprocal connection is configured.", panel.Markup);
            else
            {
                Assert.Contains("Current integration management permission is required.", panel.Markup);
                Assert.DoesNotContain("No reciprocal connection is configured.", panel.Markup);
            }
        });
    }

    [Fact]
    public async Task Same_link_identifier_in_a_fresh_response_does_not_reauthorize_old_callbacks_but_fresh_confirmation_writes_once()
    {
        using var transport = RegisterTransport();
        var panel = Render<HelpdeskConnectionsPanel>();
        var previous = await CaptureRowCallbacksAsync(panel);
        transport.Current = ActiveStatus(2, "Fresh organization");

        await InvokeAsync(panel, previous.Refresh);

        panel.WaitForAssertion(() => Assert.Contains("Fresh organization", panel.Markup));
        await InvokeAllRowCallbacksAsync(panel, previous);
        Assert.Empty(transport.PostPaths);
        AssertNoConfirmation(panel);

        await InvokeAsync(panel, Button(panel, "Disconnect").Instance.OnClick);
        var freshConfirm = Button(panel, "Confirm disconnect").Instance.OnClick;
        // A queued confirmation from the previous object must also fail while a new
        // confirmation is visible for the same LinkId.
        await InvokeAllRowCallbacksAsync(panel, previous);
        Assert.Empty(transport.PostPaths);
        Assert.Single(panel.FindComponents<MudButton>(), button => HasLabel(button, "Confirm disconnect"));

        await InvokeAsync(panel, freshConfirm);

        Assert.Equal(new[] { ConnectionsTransport.LinkRoot + "/links/shared-link/revoke" }, transport.PostPaths);
        Assert.Equal(3, transport.GetRequests); // Initial load, explicit refresh, successful mutation refresh.
    }

    [Fact]
    public async Task Refresh_canceled_by_the_current_HTTP_client_budget_keeps_old_rows_actions_and_probe_cleared()
    {
        using var transport = RegisterTransport();
        var panel = Render<HelpdeskConnectionsPanel>();
        await ShowSuccessfulProbeAsync(panel);
        var previous = await CaptureRowCallbacksAsync(panel);
        transport.RequestTimeout = TimeSpan.FromMilliseconds(100);
        transport.DelayNextGet();

        await InvokeAsync(panel, previous.Refresh).WaitAsync(CompletionBound);

        Assert.Contains(ConnectionsTransport.LinkRoot, transport.CanceledPaths);
        panel.WaitForAssertion(() =>
        {
            AssertNoRetainedState(panel);
            Assert.Contains("Reciprocal connection status is unavailable. Refresh to retry.", panel.Markup);
            Assert.DoesNotContain("No reciprocal connection is configured.", panel.Markup);
            Assert.False(Button(panel, "Refresh status").Instance.Disabled);
        });
        await InvokeAllRowCallbacksAsync(panel, previous);
        Assert.Single(transport.PostPaths);
        Assert.Equal(2, transport.GetRequests);
    }

    [Fact]
    public async Task Probe_and_lifecycle_budget_cancellation_show_safe_errors_and_preserve_authoritative_stored_status()
    {
        using var transport = RegisterTransport();
        var panel = Render<HelpdeskConnectionsPanel>();
        await ShowSuccessfulProbeAsync(panel);
        transport.RequestTimeout = TimeSpan.FromMilliseconds(100);
        transport.DelayNextProbe();

        await InvokeAsync(panel, Button(panel, "Test connection (read-only)").Instance.OnClick).WaitAsync(CompletionBound);

        Assert.Contains(ConnectionsTransport.LinkRoot + "/links/shared-link/test", transport.CanceledPaths);
        panel.WaitForAssertion(() =>
        {
            Assert.Single(panel.FindAll("[data-testid='helpdesk-link-status']"));
            Assert.Empty(panel.FindAll("[data-testid='helpdesk-connection-test-result']"));
            Assert.Contains("The read-only authenticated probes could not be confirmed.", panel.Markup);
        });
        await InvokeAsync(panel, Button(panel, "Disconnect").Instance.OnClick);
        transport.DelayNextAction();

        await InvokeAsync(panel, Button(panel, "Confirm disconnect").Instance.OnClick).WaitAsync(CompletionBound);

        Assert.Contains(ConnectionsTransport.LinkRoot + "/links/shared-link/revoke", transport.CanceledPaths);
        panel.WaitForAssertion(() =>
        {
            Assert.Single(panel.FindAll("[data-testid='helpdesk-link-status']"));
            Assert.Empty(panel.FindAll("[data-testid='helpdesk-connection-test-result']"));
            Assert.Contains("The lifecycle operation could not be confirmed. The current durable connection status remains authoritative.", panel.Markup);
            Assert.False(Button(panel, "Confirm disconnect").Instance.Disabled);
        });
        Assert.Equal(1, transport.GetRequests); // An uncertain mutation must not be treated as a successful refresh.
        Assert.Equal(3, transport.PostPaths.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Disposal_during_a_pending_status_or_probe_request_rejects_late_results_and_queued_callbacks(bool probe)
    {
        using var transport = RegisterTransport();
        var panel = Render<HelpdeskConnectionsPanel>();
        await ShowSuccessfulProbeAsync(panel);
        var previous = await CaptureRowCallbacksAsync(panel);
        var response = probe ? transport.DelayNextProbe() : transport.DelayNextGet();
        var operation = InvokeAsync(panel, probe ? previous.Test : previous.Refresh);
        panel.WaitForAssertion(() => Assert.Equal(probe ? 2 : 1, transport.PostPaths.Count));
        if (!probe) panel.WaitForAssertion(() => Assert.Equal(2, transport.GetRequests));

        // Use the component's public IDisposable hook while retaining the renderer
        // so a late event continuation can be inspected without private reflection.
        await panel.InvokeAsync(() => ((IDisposable)panel.Instance).Dispose());
        try
        {
            await InvokeAllRowCallbacksAsync(panel, previous);
            await InvokeAsync(panel, previous.Refresh);
            Assert.Equal(probe ? 2 : 1, transport.PostPaths.Count);
            Assert.Equal(probe ? 1 : 2, transport.GetRequests);
        }
        finally
        {
            response.TrySetResult(probe
                ? JsonResponse(new ServiceLinkTestResult(true, true, false, null))
                : JsonResponse(new[] { ActiveStatus(2, "Late organization") }));
            await operation.WaitAsync(CompletionBound);
        }

        await InvokeAllRowCallbacksAsync(panel, previous);
        await InvokeAsync(panel, previous.Refresh);
        panel.WaitForAssertion(() =>
        {
            AssertNoRetainedState(panel);
            Assert.DoesNotContain("Late organization", panel.Markup);
        });
        Assert.Equal(probe ? 2 : 1, transport.PostPaths.Count);
        Assert.Equal(probe ? 1 : 2, transport.GetRequests);
    }

    private ConnectionsTransport RegisterTransport()
    {
        var transport = new ConnectionsTransport();
        Services.AddSingleton<IHttpClientFactory>(transport);
        return transport;
    }

    private static async Task ShowSuccessfulProbeAsync(IRenderedComponent<HelpdeskConnectionsPanel> panel)
    {
        panel.WaitForAssertion(() => Assert.Single(panel.FindAll("[data-testid='helpdesk-link-status']")));
        await InvokeAsync(panel, Button(panel, "Test connection (read-only)").Instance.OnClick);
        panel.WaitForAssertion(() =>
        {
            Assert.Single(panel.FindAll("[data-testid='helpdesk-connection-test-result']"));
            Assert.Contains("fresh authenticated probe: passed", panel.Markup);
        });
    }

    private static async Task<RowCallbacks> CaptureRowCallbacksAsync(IRenderedComponent<HelpdeskConnectionsPanel> panel)
    {
        panel.WaitForAssertion(() => Assert.Single(panel.FindAll("[data-testid='helpdesk-link-status']")));
        var refresh = Button(panel, "Refresh status").Instance.OnClick;
        var resume = Button(panel, "Resume").Instance.OnClick;
        var disconnect = Button(panel, "Disconnect").Instance.OnClick;
        var rotation = Button(panel, "Rotate NetRatel → RatelDesk").Instance.OnClick;
        var test = Button(panel, "Test connection (read-only)").Instance.OnClick;
        await InvokeAsync(panel, disconnect);
        var confirmDisconnect = Button(panel, "Confirm disconnect").Instance.OnClick;
        await InvokeAsync(panel, Button(panel, "Keep connection").Instance.OnClick);
        await InvokeAsync(panel, rotation);
        var confirmRotation = Button(panel, "Confirm rotation").Instance.OnClick;
        return new(refresh, resume, disconnect, confirmDisconnect, rotation, confirmRotation, test);
    }

    private static async Task InvokeAllRowCallbacksAsync(IRenderedComponent<HelpdeskConnectionsPanel> panel, RowCallbacks callbacks)
    {
        foreach (var callback in new[] { callbacks.Resume, callbacks.PrepareDisconnect, callbacks.ConfirmDisconnect,
                     callbacks.PrepareRotation, callbacks.ConfirmRotation, callbacks.Test })
            await InvokeAsync(panel, callback).WaitAsync(CompletionBound);
    }

    private static Task InvokeAsync(IRenderedComponent<HelpdeskConnectionsPanel> panel, EventCallback<MouseEventArgs> callback) =>
        panel.InvokeAsync(() => callback.InvokeAsync(new MouseEventArgs()));

    private static IRenderedComponent<MudButton> Button<T>(IRenderedComponent<T> panel, string label) where T : class, IComponent =>
        panel.FindComponents<MudButton>().Single(button => HasLabel(button, label));

    private static bool HasLabel(IRenderedComponent<MudButton> button, string label) =>
        button.FindAll("button").Any(element => string.Equals(element.TextContent.Trim(), label, StringComparison.Ordinal));

    private static void AssertNoRetainedState(IRenderedComponent<HelpdeskConnectionsPanel> panel)
    {
        Assert.Empty(panel.FindAll("[data-testid='helpdesk-link-status']"));
        Assert.Empty(panel.FindAll("[data-testid='helpdesk-connection-test-result']"));
        AssertNoConfirmation(panel);
    }

    private static void AssertNoConfirmation(IRenderedComponent<HelpdeskConnectionsPanel> panel)
    {
        Assert.DoesNotContain("Disable both directions and request peer revocation?", panel.Markup);
        Assert.DoesNotContain("under its existing approved grant?", panel.Markup);
    }

    private static HttpResponseMessage JsonResponse<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };

    private static ServiceLinkAdminStatus ActiveStatus(long revision, string peerTenant = "Existing organization") => new(
        "shared-attempt", "shared-link", revision, "active", "7", "peer-fixture", peerTenant, "commit", "commit-fixture", "grant-hash",
        new ServiceLinkRequestDescriptor
        {
            AttemptId = "shared-attempt", DescriptorHash = "descriptor-hash",
            InitiatorEndpointSnapshot = new ServiceLinkMetadata { Product = "netratel", InstanceId = "local", WebBaseUrl = "https://web.example.test" },
            ResponderEndpointSnapshot = new ServiceLinkMetadata { Product = "rateldesk", InstanceId = "peer-fixture", WebBaseUrl = "https://desk.example.test" }
        },
        new ServiceLinkGrantSummary
        {
            AttemptId = "shared-attempt", LinkId = "shared-link", ProposedLinkRevision = revision,
            Grants = [new ServiceLinkGrant { DirectionId = ServiceLinkContract.InitiatorToResponder,
                CallerProduct = "NetRatel", TargetProduct = "RatelDesk" }]
        }, true, true, true, true, true, null, false, []);

    private sealed record RowCallbacks(EventCallback<MouseEventArgs> Refresh, EventCallback<MouseEventArgs> Resume,
        EventCallback<MouseEventArgs> PrepareDisconnect, EventCallback<MouseEventArgs> ConfirmDisconnect,
        EventCallback<MouseEventArgs> PrepareRotation, EventCallback<MouseEventArgs> ConfirmRotation, EventCallback<MouseEventArgs> Test);

    private sealed class ConnectionsTransport : HttpMessageHandler, IHttpClientFactory
    {
        public const string LinkRoot = "/api/v1/admin/service-links";
        private TaskCompletionSource<HttpResponseMessage>? nextGet, nextProbe, nextAction;
        public ServiceLinkAdminStatus Current { get; set; } = ActiveStatus(1);
        public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(10);
        public int GetRequests { get; private set; }
        public List<string> PostPaths { get; } = [];
        public List<string> CanceledPaths { get; } = [];

        public HttpClient CreateClient(string name)
        {
            Assert.Equal("ServiceLinkApi", name);
            return new HttpClient(this, disposeHandler: false)
            { BaseAddress = new Uri("https://api.example.test"), Timeout = RequestTimeout };
        }

        public TaskCompletionSource<HttpResponseMessage> DelayNextGet() => nextGet = NewGate();
        public TaskCompletionSource<HttpResponseMessage> DelayNextProbe() => nextProbe = NewGate();
        public TaskCompletionSource<HttpResponseMessage> DelayNextAction() => nextAction = NewGate();
        private static TaskCompletionSource<HttpResponseMessage> NewGate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            TaskCompletionSource<HttpResponseMessage>? gate;
            if (request.Method == HttpMethod.Get && path == LinkRoot)
            {
                ++GetRequests;
                gate = nextGet;
                nextGet = null;
                if (gate is null) return JsonResponse(new[] { Current });
            }
            else if (request.Method == HttpMethod.Post && path.StartsWith(LinkRoot + "/links/", StringComparison.Ordinal))
            {
                PostPaths.Add(path);
                var probe = path.EndsWith("/test", StringComparison.Ordinal);
                gate = probe ? nextProbe : nextAction;
                if (probe) nextProbe = null; else nextAction = null;
                if (gate is null) return probe ? JsonResponse(new ServiceLinkTestResult(true, true, false, null)) : new HttpResponseMessage(HttpStatusCode.NoContent);
            }
            else throw new InvalidOperationException("Unexpected connection fixture request: " + request.Method + " " + path);

            try { return await gate.Task.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            { CanceledPaths.Add(path); throw; }
        }
    }
}
