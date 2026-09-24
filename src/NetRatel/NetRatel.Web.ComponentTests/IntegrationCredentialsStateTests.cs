using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using NetRatel.Web.Components.Pages;
using Xunit;

namespace NetRatel.Web.ComponentTests;

public sealed class IntegrationCredentialsStateTests : AsyncBunitContext
{
    [Fact]
    public async Task Pending_create_cannot_close_back_or_reopen_and_its_secret_stays_in_its_acknowledgment()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices(options => options.PopoverOptions.CheckForPopoverProvider = false);
        using var transport = new CredentialTransport();
        Services.AddSingleton<IHttpClientFactory>(transport);
        var page = Render<IntegrationCredentials>();
        page.WaitForAssertion(() => Field<object>(page.Instance, "_authority").Should().NotBeNull());

        Call(page.Instance, "OpenCreate");
        Set(page.Instance, "_name", "Draft A");
        Set(page.Instance, "_step", 2);
        var pending = CallAsync(page.Instance, "CreateAsync");
        transport.PostRequested.Should().BeTrue();

        Call(page.Instance, "CloseCreate");
        CallWithArg(page.Instance, "CreateVisibleChanged", false);
        Call(page.Instance, "Previous");
        Call(page.Instance, "OpenCreate");
        Field<bool>(page.Instance, "_createOpen").Should().BeTrue();
        Field<int>(page.Instance, "_step").Should().Be(2);
        Field<string>(page.Instance, "_name").Should().Be("Draft A");

        transport.CompleteCreate("synthetic-one-time-secret");
        await pending;
        Field<int>(page.Instance, "_step").Should().Be(3);
        Field<string>(page.Instance, "_revealedSecret").Should().Be("synthetic-one-time-secret");
        Call(page.Instance, "CloseCreate");
        Field<string?>(page.Instance, "_revealedSecret").Should().BeNull();
        Call(page.Instance, "OpenCreate");
        Field<int>(page.Instance, "_step").Should().Be(0);
        Field<string?>(page.Instance, "_revealedSecret").Should().BeNull();
    }

    [Fact]
    public async Task Disposed_create_does_not_reveal_a_late_secret()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices(options => options.PopoverOptions.CheckForPopoverProvider = false);
        using var transport = new CredentialTransport();
        Services.AddSingleton<IHttpClientFactory>(transport);
        var page = Render<IntegrationCredentials>();
        page.WaitForAssertion(() => Field<object>(page.Instance, "_authority").Should().NotBeNull());
        Call(page.Instance, "OpenCreate");
        Set(page.Instance, "_name", "Draft A");
        Set(page.Instance, "_step", 2);
        var pending = CallAsync(page.Instance, "CreateAsync");

        page.Instance.Dispose();
        transport.CompleteCreate("synthetic-one-time-secret");
        await pending;

        Field<string?>(page.Instance, "_revealedSecret").Should().BeNull();
    }

    [Fact]
    public async Task Rejected_create_keeps_the_draft_for_correction()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices(options => options.PopoverOptions.CheckForPopoverProvider = false);
        using var transport = new CredentialTransport();
        Services.AddSingleton<IHttpClientFactory>(transport);
        var page = Render<IntegrationCredentials>();
        page.WaitForAssertion(() => Field<object>(page.Instance, "_authority").Should().NotBeNull());
        Call(page.Instance, "OpenCreate");
        Set(page.Instance, "_name", "Draft A");
        Set(page.Instance, "_step", 2);
        var pending = CallAsync(page.Instance, "CreateAsync");

        transport.RejectCreate();
        await pending;

        Field<string>(page.Instance, "_name").Should().Be("Draft A");
        Field<int>(page.Instance, "_step").Should().Be(0);
        Field<string>(page.Instance, "_error").Should().Be("Review the name.");
    }

    private static T Field<T>(object instance, string name) =>
        (T)instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;

    private static void Set(object instance, string name, object value) =>
        instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(instance, value);

    private static void Call(object instance, string name) =>
        instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(instance, null);

    private static void CallWithArg(object instance, string name, object value) =>
        instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(instance, [value]);

    private static Task CallAsync(object instance, string name) =>
        (Task)instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(instance, null)!;

    private sealed class CredentialTransport : HttpMessageHandler, IHttpClientFactory
    {
        private readonly TaskCompletionSource<HttpResponseMessage> _create = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool PostRequested { get; private set; }
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false) { BaseAddress = new Uri("https://api.example.test") };
        public void CompleteCreate(string secret) => _create.SetResult(new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = JsonContent.Create(new { credentialId = "one", publicId = "one", tokenPrefix = "one", secret, expiresAtUtc = DateTimeOffset.UtcNow.AddDays(1) })
        });
        public void RejectCreate() => _create.SetResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = JsonContent.Create(new { errors = new { name = new[] { "Review the name." } } })
        });
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post)
            {
                PostRequested = true;
                return _create.Task;
            }
            if (request.RequestUri!.AbsolutePath.EndsWith("/authority", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new { tenants = Array.Empty<object>(), instancePermissions = Array.Empty<object>(), configuredHttpMcpServerUrl = "https://mcp.example.test/mcp" })
                });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Array.Empty<object>()) });
        }
    }
}
