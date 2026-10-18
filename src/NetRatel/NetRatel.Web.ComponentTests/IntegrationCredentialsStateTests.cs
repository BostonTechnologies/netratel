using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using Bunit;
using AwesomeAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using NetRatel.Web.Components.Pages;
using NetRatel.Web.Services.Pairing;
using NetRatel.Shared.SystemPairing;
using NetRatel.Shared.Connectivity;
using NetRatel.Infrastructure.Identity;
using NetRatel.Web.Components.Shared.Integrations;
using Xunit;

namespace NetRatel.Web.ComponentTests;

public sealed class IntegrationCredentialsStateTests : AsyncBunitContext
{
    public IntegrationCredentialsStateTests() => Services.AddSingleton<IPairingApiClient>(new NoPairingApi());

    [Fact]
    public async Task Pending_create_cannot_close_back_or_reopen_and_its_secret_stays_in_its_acknowledgment()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices(options => options.PopoverOptions.CheckForPopoverProvider = false);
        using var transport = new CredentialTransport();
        Services.AddSingleton<IHttpClientFactory>(transport);
        SetRendererInfo(new RendererInfo("Server", true));
        var page = Render<IntegrationCredentials>();
        page.WaitForAssertion(() => Field<object>(page.Instance, "_authority").Should().NotBeNull());

        Call(page.Instance, "OpenCreate");
        Set(page.Instance, "_name", "Draft A");
        Set(page.Instance, "_step", 2);
        Set(page.Instance, "_createConfirmed", true);
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
        SetRendererInfo(new RendererInfo("Server", true));
        var page = Render<IntegrationCredentials>();
        page.WaitForAssertion(() => Field<object>(page.Instance, "_authority").Should().NotBeNull());
        Call(page.Instance, "OpenCreate");
        Set(page.Instance, "_name", "Draft A");
        Set(page.Instance, "_step", 2);
        Set(page.Instance, "_createConfirmed", true);
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
        SetRendererInfo(new RendererInfo("Server", true));
        var page = Render<IntegrationCredentials>();
        page.WaitForAssertion(() => Field<object>(page.Instance, "_authority").Should().NotBeNull());
        Call(page.Instance, "OpenCreate");
        Set(page.Instance, "_name", "Draft A");
        Set(page.Instance, "_step", 2);
        Set(page.Instance, "_createConfirmed", true);
        var pending = CallAsync(page.Instance, "CreateAsync");

        transport.RejectCreate();
        await pending;

        Field<string>(page.Instance, "_name").Should().Be("Draft A");
        Field<int>(page.Instance, "_step").Should().Be(0);
        Field<string>(page.Instance, "_error").Should().Be("Review the name.");
    }

    [Theory]
    [InlineData(null, null, null, false)]
    [InlineData("7", null, 7, false)]
    [InlineData(null, "7", 7, false)]
    [InlineData("7", "7", 7, false)]
    [InlineData("7", "8", null, true)]
    [InlineData("invalid", null, null, true)]
    [InlineData(null, "-1", null, true)]
    [InlineData("", "7", null, true)]
    public void Tenant_aliases_resolve_consistently_and_conflicting_or_invalid_links_are_explicit(string? snake, string? camel, int? expected, bool invalid)
    {
        var result = IntegrationCredentials.ResolveTenantContext(snake, camel);
        result.TenantId.Should().Be(expected);
        (result.Error is not null).Should().Be(invalid);
    }

    [Fact]
    public async Task Inactive_cleanup_uses_only_explicit_visible_selection_and_retains_partial_failures()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices(options => options.PopoverOptions.CheckForPopoverProvider = false);
        using var transport = new CredentialTransport { IncludeCredentials = true, FailDeleteId = "revoked-fail" };
        Services.AddSingleton<IHttpClientFactory>(transport);
        SetRendererInfo(new RendererInfo("Server", true));
        var page = Render<IntegrationCredentials>();
        page.WaitForAssertion(() => Field<object>(page.Instance, "_authority").Should().NotBeNull());
        var selected = Field<HashSet<string>>(page.Instance, "_selectedInactive");
        selected.UnionWith(["active", "revoked-ok", "revoked-fail"]);
        Call(page.Instance, "ConfirmDeleteSelected");
        await CallAsync(page.Instance, "RevokeAsync");
        transport.DeletedIds.Should().BeEquivalentTo(["revoked-ok", "revoked-fail"]);
        Field<string>(page.Instance, "_managementError").Should().Contain("1 credential(s)");
        selected.Should().Contain("revoked-fail").And.NotContain("revoked-ok");
        var credentials = ((System.Collections.IEnumerable)Field<object>(page.Instance, "_credentials")).Cast<object>().ToArray();
        credentials.Select(credential => credential.GetType().GetProperty("Id")!.GetValue(credential)).Should().BeEquivalentTo(["active", "revoked-fail", "expired-unselected"]);

        transport.FailDeleteId = null;
        Call(page.Instance, "SetStatusFilter", "Expired");
        selected.Should().BeEmpty();
        selected.Add("revoked-fail"); // Even stale caller selection cannot include a hidden row in the batch.
        Call(page.Instance, "ConfirmDeleteSelected");
        Field<bool>(page.Instance, "_revokeOpen").Should().BeFalse();
        transport.DeletedIds.Should().HaveCount(2);
        Call(page.Instance, "SetStatusFilter", "All");
        selected.Add("revoked-fail");
        Call(page.Instance, "ConfirmDeleteSelected");
        await CallAsync(page.Instance, "RevokeAsync");
        selected.Should().BeEmpty();
        var remaining = ((System.Collections.IEnumerable)Field<object>(page.Instance, "_credentials")).Cast<object>();
        remaining.Select(credential => credential.GetType().GetProperty("Id")!.GetValue(credential)).Should().BeEquivalentTo(["active", "expired-unselected"]);
    }

    [Fact]
    public async Task Delayed_refresh_preserves_acknowledged_delete_revoke_and_failed_cleanup_rows()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices(options => options.PopoverOptions.CheckForPopoverProvider = false);
        using var transport = new CredentialTransport { IncludeCredentials = true, FailDeleteId = "revoked-fail" };
        Services.AddSingleton<IHttpClientFactory>(transport);
        SetRendererInfo(new RendererInfo("Server", true));
        var page = Render<IntegrationCredentials>();
        page.WaitForAssertion(() => Field<object>(page.Instance, "_authority").Should().NotBeNull());
        transport.PauseNextList();
        var refresh = CallAsync(page.Instance, "LoadAsync");
        transport.DelayedListRequested.Should().BeTrue();

        var selected = Field<HashSet<string>>(page.Instance, "_selectedInactive");
        selected.UnionWith(["revoked-ok", "revoked-fail"]);
        Call(page.Instance, "ConfirmDeleteSelected");
        await CallAsync(page.Instance, "RevokeAsync");
        Field<string>(page.Instance, "_managementError").Should().Contain("1 credential(s)");
        selected.Should().Equal("revoked-fail");
        var active = ((System.Collections.IEnumerable)Field<object>(page.Instance, "_credentials")).Cast<object>()
            .Single(credential => (string)credential.GetType().GetProperty("Id")!.GetValue(credential)! == "active");
        Call(page.Instance, "ConfirmRevoke", active);
        await CallAsync(page.Instance, "RevokeAsync");

        transport.CompleteDelayedList();
        await refresh;
        var credentials = ((System.Collections.IEnumerable)Field<object>(page.Instance, "_credentials")).Cast<object>().ToArray();
        credentials.Select(credential => credential.GetType().GetProperty("Id")!.GetValue(credential))
            .Should().BeEquivalentTo(["active", "revoked-fail", "expired-unselected"]);
        active = credentials.Single(credential => (string)credential.GetType().GetProperty("Id")!.GetValue(credential)! == "active");
        active.GetType().GetProperty("RevokedAtUtc")!.GetValue(active).Should().NotBeNull();
        selected.Should().Equal("revoked-fail");
        transport.DeletedIds.Should().BeEquivalentTo(["revoked-ok", "revoked-fail"]);
        transport.RevokedIds.Should().Equal("active");
    }

    [Fact]
    public async Task Selecting_and_removing_tenant_access_invalidates_create_confirmation()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices(options => options.PopoverOptions.CheckForPopoverProvider = false);
        using var transport = new CredentialTransport();
        Services.AddSingleton<IHttpClientFactory>(transport);
        SetRendererInfo(new RendererInfo("Server", true));
        var page = Render<IntegrationCredentials>();
        page.WaitForAssertion(() => Field<object>(page.Instance, "_authority").Should().NotBeNull());
        Call(page.Instance, "OpenCreate");
        Set(page.Instance, "_createConfirmed", true);
        Call(page.Instance, "SetTenantPermission", 7, "telemetry.read", true);
        Field<bool>(page.Instance, "_createConfirmed").Should().BeFalse();
        Set(page.Instance, "_createConfirmed", true);
        Call(page.Instance, "RemoveTenant", 7);
        Field<bool>(page.Instance, "_createConfirmed").Should().BeFalse();
        Field<HashSet<(int TenantId, string Permission)>>(page.Instance, "_selectedGrants").Should().BeEmpty();
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Http_credential_acknowledgment_and_details_refresh_current_admission_without_revealing_stored_secrets()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices(options => options.PopoverOptions.CheckForPopoverProvider = false);
        using var transport = new CredentialTransport { IncludeHttpMcpCredential = true, Admission = Admission(false) };
        Services.AddSingleton<IHttpClientFactory>(transport);
        SetRendererInfo(new RendererInfo("Server", true));
        var page = Render<IntegrationCredentials>();
        page.WaitForAssertion(() => Field<object>(page.Instance, "_authority").Should().NotBeNull());
        Call(page.Instance, "OpenCreate");
        Set(page.Instance, "_purpose", IntegrationCredentialPurpose.HttpMcp);
        Set(page.Instance, "_name", "Bounded HTTP reader");
        Set(page.Instance, "_step", 2); Set(page.Instance, "_createConfirmed", true);
        transport.CompleteCreate("synthetic-one-time-secret");
        await CallAsync(page.Instance, "CreateAsync");
        var configured = Field<CredentialMcpAdmission>(page.Instance, "_createdAdmission");
        configured.EffectiveAdmission.Should().OnlyContain(x => x.ApplicationGrantConfigured && !x.AuthorizationAdmitted);
        var view = Render<CredentialAdmissionView>(p => p.Add(x => x.Admission, configured));
        view.Markup.Should().Contain("Matching Production operator policies").And.Contain("current durable owner permission")
            .And.Contain("2030-04-01T10:11:12.0000000Z").And.NotContain("synthetic-one-time-secret");
        Call(page.Instance, "CloseCreate");
        var credential = ((System.Collections.IEnumerable)Field<object>(page.Instance, "_credentials")).Cast<object>().Single();
        transport.Admission = Admission(true);
        await (Task)page.Instance.GetType().GetMethod("OpenDetailAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page.Instance, [credential])!;
        var refreshed = Field<CredentialMcpAdmission>(page.Instance, "_detailAdmission");
        refreshed.EffectiveAdmission.Should().OnlyContain(x => x.AuthorizationAdmitted && x.PolicyAllowed == true);
        var refreshedView = Render<CredentialAdmissionView>(p => p.Add(x => x.Admission, refreshed));
        refreshedView.Markup.Should().Contain("dispatch readiness not checked").And.NotContain("synthetic-one-time-secret");
        transport.Admission = Admission(false) with { CredentialState = "revoked", AuthenticationStatus = "delegated_identity_revoked" };
        await (Task)page.Instance.GetType().GetMethod("RefreshAdmissionAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page.Instance, ["one", false])!;
        Field<CredentialMcpAdmission>(page.Instance, "_detailAdmission").CredentialState.Should().Be("revoked");
    }

    private static CredentialMcpAdmission Admission(bool admitted) => new("one", DateTimeOffset.Parse("2026-10-18T12:00:00Z"), "active",
        "current_credential_valid", "https://mcp.example.test/mcp", "prod", "Production", DateTimeOffset.Parse("2030-04-01T10:11:12Z"),
        [new(17, "flow.read")], [], [new(new(17, "netratel_flows", "list"), "netratel.mcp.observe", ["Operator"], true, admitted, admitted,
            admitted ? null : "tenant_not_authorized", []) { OwnerPermissionGranted = true, AuthorizationAdmitted = admitted }],
        admitted ? ["Dispatch rechecks runtime readiness."] : ["Matching Production operator policies are a separate prerequisite."],
        "Use the independently reviewed request, inspect and resume flow.");

    private static T Field<T>(object instance, string name) =>
        (T)instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;

    private static void Set(object instance, string name, object value) =>
        instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(instance, value);

    private static void Call(object instance, string name, params object[] arguments) =>
        instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(instance, arguments);

    private static void CallWithArg(object instance, string name, object value) =>
        instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(instance, [value]);

    private static Task CallAsync(object instance, string name) =>
        (Task)instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(instance, null)!;

    private sealed class NoPairingApi : IPairingApiClient
    {
        public Task<PairingConnectionDto[]> ListAsync(CancellationToken ct = default) => Task.FromResult(Array.Empty<PairingConnectionDto>());
        public Task<PairingCodeResponse> GenerateCodeAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<PairingConnectionDto> ConnectAsync(PairingConnectRequest request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<PairingDirectories> DirectoryAsync(string pairId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<PairingConnectionDto> SaveAsync(PairingMapping mapping, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<PairingTestResult> TestAsync(PairingConnectionDto connection, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteAsync(PairingConnectionDto connection, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class CredentialTransport : HttpMessageHandler, IHttpClientFactory
    {
        private readonly TaskCompletionSource<HttpResponseMessage> _create = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool PostRequested { get; private set; }
        public bool IncludeCredentials { get; set; }
        public bool IncludeHttpMcpCredential { get; set; }
        public CredentialMcpAdmission? Admission { get; set; }
        public string? FailDeleteId { get; set; }
        public List<string> DeletedIds { get; } = [];
        public List<string> RevokedIds { get; } = [];
        private bool _pauseNextList;
        private TaskCompletionSource<HttpResponseMessage>? _delayedList;
        private HttpResponseMessage? _delayedSnapshot;
        public bool DelayedListRequested => _delayedSnapshot is not null;
        public void PauseNextList()
        {
            _pauseNextList = true;
            _delayedList = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        public void CompleteDelayedList() => _delayedList!.SetResult(_delayedSnapshot!);
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false) { BaseAddress = new Uri("https://api.example.test") };
        public void CompleteCreate(string secret) => _create.SetResult(new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = JsonContent.Create(new { credentialId = "one", publicId = "one", tokenPrefix = "one", secret, expiresAtUtc = DateTimeOffset.UtcNow.AddDays(1) })
        });
        public void RejectCreate() => _create.SetResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = JsonContent.Create(new { errors = new { name = new[] { "Review the name." } } })
        });
        private static object Credential(string id, DateTimeOffset expiresAt, DateTimeOffset? revokedAt) => new {
            id, publicId = id, tokenPrefix = "synthetic", name = id, purpose = 0, resource = (string?)null,
            createdAtUtc = DateTimeOffset.UtcNow.AddDays(-2), expiresAtUtc = expiresAt, revokedAtUtc = revokedAt,
            lastUsedAtUtc = (DateTimeOffset?)null, grants = Array.Empty<object>(), instancePermissions = Array.Empty<string>() };
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/admission", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(Admission is null ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK) { Content = JsonContent.Create(Admission) });
            if (request.Method == HttpMethod.Delete)
            {
                var id = request.RequestUri!.AbsolutePath.Split('/')[^1];
                DeletedIds.Add(id);
                return Task.FromResult(new HttpResponseMessage(id == FailDeleteId ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.NoContent));
            }
            if (request.RequestUri!.AbsolutePath == "/api/v2/access/self")
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { isInstanceAdministrator = false }) });
            if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith("/revoke", StringComparison.Ordinal))
            {
                RevokedIds.Add(request.RequestUri.AbsolutePath.Split('/')[^2]);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }
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
            var now = DateTimeOffset.UtcNow;
            object[] credentials = IncludeHttpMcpCredential ? [new {
                id = "one", publicId = "one", tokenPrefix = "synthetic", name = "Bounded HTTP reader", purpose = 1,
                resource = "https://mcp.example.test/mcp", createdAtUtc = now, expiresAtUtc = now.AddDays(1),
                revokedAtUtc = (DateTimeOffset?)null, lastUsedAtUtc = (DateTimeOffset?)null,
                grants = new[] { new { tenantId = 17, permission = "flow.read" } }, instancePermissions = Array.Empty<string>()
            }] : IncludeCredentials ? new[] {
                Credential("active", now.AddDays(1), null), Credential("revoked-ok", now.AddDays(1), now),
                Credential("revoked-fail", now.AddDays(1), now), Credential("expired-unselected", now.AddDays(-1), null) } : [];
            var snapshot = new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(credentials) };
            if (_pauseNextList)
            {
                _pauseNextList = false; _delayedSnapshot = snapshot;
                return _delayedList!.Task;
            }
            return Task.FromResult(snapshot);
        }
    }
}
