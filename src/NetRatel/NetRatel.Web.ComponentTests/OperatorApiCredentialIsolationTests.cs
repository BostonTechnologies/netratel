using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NetRatel.Shared;
using NetRatel.Shared.Contracts;
using NetRatel.Web.Services.Authentication;
using NetRatel.Web.Services;
using NetRatel.Web.Services.Clients;
using NetRatel.Web.Services.Telemetry;
using Xunit;

namespace NetRatel.Web.ComponentTests;

public sealed class OperatorApiCredentialIsolationTests
{
    [Fact]
    public async Task Telemetry_reauthentication_after_directory_success_keeps_the_directory_with_real_operator_handlers()
    {
        var requests = new ConcurrentQueue<ObservedRequest>();
        var principal = CreateOidcPrincipal("alice-directory");
        OperatorApiCredentialState? state = null;
        var telemetryRequestCount = 0;
        using var services = CreateServices(
            () => new DirectoryThenClearCredentialHandler(
                () => state!.ClearCredential(principal),
                requests,
                () => Interlocked.Increment(ref telemetryRequestCount)),
            new Uri("https://netratel.test/"));
        using var scope = services.CreateScope();
        state = scope.ServiceProvider.GetRequiredService<OperatorApiCredentialState>();
        var accessor = services.GetRequiredService<IHttpContextAccessor>();
        accessor.HttpContext = CreateOidcContext(scope.ServiceProvider, principal, CreateAccessToken("alice-directory"));
        var authentication = scope.ServiceProvider.GetRequiredService<TestAuthenticationStateProvider>();
        var circuitHandler = scope.ServiceProvider.GetRequiredService<OperatorApiCredentialCircuitHandler>();
        await PublishAndAssertCredentialAsync(
            authentication,
            circuitHandler,
            state,
            principal);
        accessor.HttpContext = null;

        var factory = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>();
        var presentation = new ClientPresentationService(
            new ClientPresenceApiService(factory),
            new GatewayTelemetryApiService(factory),
            NullLogger<ClientPresentationService>.Instance);
        var result = await presentation.GetClientsAsync();

        Assert.True(result.IsAvailable);
        Assert.Null(result.Failure);
        Assert.Equal("directory-agent", Assert.Single(result.Clients).DisplayName);
        Assert.Null(result.Clients[0].Telemetry);
        Assert.Equal("alice-directory", Assert.Single(requests).BearerSubject);
        Assert.Equal(0, telemetryRequestCount);
        GC.KeepAlive(circuitHandler);
    }

    [Fact]
    public async Task Pooled_factory_keeps_two_circuit_tokens_isolated_when_http_context_is_absent_and_auth_updates_arrive_out_of_order()
    {
        var requests = new ConcurrentQueue<ObservedRequest>();
        using var services = CreateServices(
            () => new RecordingApiHandler(requests),
            new Uri("https://netratel.test/"));
        var accessor = services.GetRequiredService<IHttpContextAccessor>();
        var alice = CreateOidcPrincipal("alice");
        var staleAlice = CreateOidcPrincipal("alice-old");
        var bob = CreateOidcPrincipal("bob");

        using var aliceScope = services.CreateScope();
        accessor.HttpContext = CreateOidcContext(aliceScope.ServiceProvider, alice, CreateAccessToken("alice"));
        var aliceAuthentication = aliceScope.ServiceProvider.GetRequiredService<TestAuthenticationStateProvider>();
        var aliceHandler = aliceScope.ServiceProvider.GetRequiredService<OperatorApiCredentialCircuitHandler>();
        var aliceState = aliceScope.ServiceProvider.GetRequiredService<OperatorApiCredentialState>();
        var staleState = new TaskCompletionSource<AuthenticationState>();
        aliceAuthentication.PublishPending(staleState.Task);
        await aliceHandler.AuthenticationStateChangeAsync(aliceAuthentication.PublishAsync(alice));
        Assert.NotNull(aliceState.GetCredential(alice));

        using var bobScope = services.CreateScope();
        accessor.HttpContext = CreateOidcContext(bobScope.ServiceProvider, bob, CreateAccessToken("bob"));
        var bobAuthentication = bobScope.ServiceProvider.GetRequiredService<TestAuthenticationStateProvider>();
        var bobHandler = bobScope.ServiceProvider.GetRequiredService<OperatorApiCredentialCircuitHandler>();
        var bobState = bobScope.ServiceProvider.GetRequiredService<OperatorApiCredentialState>();
        await PublishAndAssertCredentialAsync(bobAuthentication, bobHandler, bobState, bob);

        // The older Alice auth-state task now finishes after the current Alice
        // state. The circuit version fence must ignore it without rebinding.
        staleState.SetResult(new AuthenticationState(staleAlice));
        await aliceHandler.PendingAuthenticationChangesAsync();
        Assert.NotNull(aliceState.GetCredential(alice));
        Assert.Null(aliceState.GetCredential(staleAlice));

        // Both scoped factories use the same IHttpClientFactory pooled pipeline.
        // Once the initial request contexts disappear, each circuit must still
        // forward its own protected server-side credential.
        accessor.HttpContext = null;
        using var aliceClient = aliceScope.ServiceProvider.GetRequiredService<IHttpClientFactory>().CreateClient("OrchestratorApi");
        using var bobClient = bobScope.ServiceProvider.GetRequiredService<IHttpClientFactory>().CreateClient("OrchestratorApi");
        using (var response = await aliceClient.GetAsync("api/v2/client-presence"))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        using (var response = await bobClient.GetAsync("api/v2/client-presence"))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        using (var response = await aliceClient.GetAsync("api/v2/client-presence"))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        Assert.Equal(
            new[] { "alice", "bob", "alice" },
            requests.Select(request => request.BearerSubject).ToArray());
        Assert.All(requests, request =>
        {
            Assert.Null(request.Cookie);
            Assert.Null(request.AccountRequest);
        });
        GC.KeepAlive(bobHandler);
    }

    [Fact]
    public async Task Late_token_capture_cannot_become_the_current_token_after_same_user_reauthentication()
    {
        var requests = new ConcurrentQueue<ObservedRequest>();
        var oldToken = CreateAccessToken("same-user-old-token");
        var authenticationService = new BlockingAuthenticationService(oldToken);
        using var services = CreateServices(
            () => new RecordingApiHandler(requests),
            new Uri("https://netratel.test/"),
            authenticationService);
        var accessor = services.GetRequiredService<IHttpContextAccessor>();
        var principal = CreateOidcPrincipal("same-user");
        using var scope = services.CreateScope();
        accessor.HttpContext = CreateOidcContext(scope.ServiceProvider, principal, oldToken);
        var handler = scope.ServiceProvider.GetRequiredService<OperatorApiCredentialCircuitHandler>();
        var state = scope.ServiceProvider.GetRequiredService<OperatorApiCredentialState>();
        var authState = scope.ServiceProvider.GetRequiredService<TestAuthenticationStateProvider>();
        using var client = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>().CreateClient("OrchestratorApi");

        var oldRequest = client.GetAsync("api/v2/client-presence");
        try
        {
            await authenticationService.OldAuthenticationStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

            var newToken = CreateAccessToken("same-user-new-token");
            accessor.HttpContext = CreateOidcContext(scope.ServiceProvider, principal, newToken);
            await PublishAndAssertCredentialAsync(authState, handler, state, principal);
            Assert.Equal(newToken, state.GetCredential(principal)?.Value);

            // The current credential is invalidated before the old request finishes.
            // A late cache write must retain its original generation and fail closed.
            state.ClearCredential(principal);
        }
        finally
        {
            authenticationService.ReleaseOldAuthentication();
        }

        using (var response = await oldRequest.WaitAsync(TimeSpan.FromSeconds(10)))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        accessor.HttpContext = null;
        using var nextResponse = await client.GetAsync("api/v2/client-presence").WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(HttpStatusCode.Unauthorized, nextResponse.StatusCode);
        Assert.Empty(requests);
        GC.KeepAlive(handler);
    }

    [Fact]
    public async Task Pooled_http_transport_does_not_merge_another_local_session_cookie_or_api_set_cookie()
    {
        await using var api = new LoopbackApiServer();
        using var services = CreateServices(
            () => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false },
            api.BaseAddress);
        var accessor = services.GetRequiredService<IHttpContextAccessor>();
        var alice = CreateLocalPrincipal("local-alice");
        var bob = CreateLocalPrincipal("local-bob");

        using var aliceScope = services.CreateScope();
        accessor.HttpContext = CreateLocalContext(aliceScope.ServiceProvider, alice, "alice-session");
        var aliceAuthentication = aliceScope.ServiceProvider.GetRequiredService<TestAuthenticationStateProvider>();
        var aliceHandler = aliceScope.ServiceProvider.GetRequiredService<OperatorApiCredentialCircuitHandler>();
        var aliceState = aliceScope.ServiceProvider.GetRequiredService<OperatorApiCredentialState>();
        await PublishAndAssertCredentialAsync(aliceAuthentication, aliceHandler, aliceState, alice);

        using var bobScope = services.CreateScope();
        accessor.HttpContext = CreateLocalContext(bobScope.ServiceProvider, bob, "bob-session");
        var bobAuthentication = bobScope.ServiceProvider.GetRequiredService<TestAuthenticationStateProvider>();
        var bobHandler = bobScope.ServiceProvider.GetRequiredService<OperatorApiCredentialCircuitHandler>();
        var bobState = bobScope.ServiceProvider.GetRequiredService<OperatorApiCredentialState>();
        await PublishAndAssertCredentialAsync(bobAuthentication, bobHandler, bobState, bob);

        accessor.HttpContext = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await SendDirectoryRequestAsync(aliceScope.ServiceProvider, timeout.Token);
        await SendDirectoryRequestAsync(bobScope.ServiceProvider, timeout.Token);
        await SendDirectoryRequestAsync(aliceScope.ServiceProvider, timeout.Token);

        Assert.Equal(
            new[] { "NetRatel.Local=alice-session", "NetRatel.Local=bob-session", "NetRatel.Local=alice-session" },
            api.Requests.Select(request => request.Cookie).ToArray());
        Assert.All(api.Requests, request => Assert.Equal("1", request.AccountRequest));
        Assert.All(api.Requests, request => Assert.Null(request.Authorization));
        GC.KeepAlive(aliceHandler);
        GC.KeepAlive(bobHandler);
    }

    [Fact]
    public async Task Typed_download_service_uses_the_circuit_scoped_credential_forwarder()
    {
        var requests = new ConcurrentQueue<ObservedRequest>();
        using var services = CreateServices(
            () => new RecordingApiHandler(requests),
            new Uri("https://netratel.test/"));
        var accessor = services.GetRequiredService<IHttpContextAccessor>();
        var alice = CreateOidcPrincipal("alice-download");
        using var scope = services.CreateScope();
        accessor.HttpContext = CreateOidcContext(scope.ServiceProvider, alice, CreateAccessToken("alice-download"));
        var authentication = scope.ServiceProvider.GetRequiredService<TestAuthenticationStateProvider>();
        var handler = scope.ServiceProvider.GetRequiredService<OperatorApiCredentialCircuitHandler>();
        var state = scope.ServiceProvider.GetRequiredService<OperatorApiCredentialState>();
        await PublishAndAssertCredentialAsync(authentication, handler, state, alice);
        accessor.HttpContext = null;

        await using var download = await scope.ServiceProvider.GetRequiredService<IWebClientDownloadService>()
            .DownloadAsync(4, ClientEnvironment.Dev, "linux-x64", false, null, null);

        Assert.Equal("alice-download", Assert.Single(requests).BearerSubject);
        GC.KeepAlive(handler);
    }

    [Fact]
    public async Task Missing_circuit_credential_returns_unauthorized_without_a_system_token_fallback()
    {
        var requests = new ConcurrentQueue<ObservedRequest>();
        using var services = CreateServices(
            () => new RecordingApiHandler(requests),
            new Uri("https://netratel.test/"));
        var accessor = services.GetRequiredService<IHttpContextAccessor>();
        accessor.HttpContext = null;
        using var scope = services.CreateScope();
        using var response = await scope.ServiceProvider.GetRequiredService<IHttpClientFactory>()
            .CreateClient("OrchestratorApi")
            .GetAsync("api/v2/client-presence");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(requests);
        Assert.Equal(0, services.GetRequiredService<StubSystemTokenService>().CallCount);
    }

    private static async Task SendDirectoryRequestAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        using var client = services.GetRequiredService<IHttpClientFactory>().CreateClient("OrchestratorApi");
        using var response = await client.GetAsync("api/v2/client-presence", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static ServiceProvider CreateServices(
        Func<HttpMessageHandler> primaryHandler,
        Uri baseAddress,
        IAuthenticationService? authenticationService = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpContextAccessor();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:Local:CookieName"] = "NetRatel.Local",
                ["Authentication:Oidc:Authority"] = "https://issuer.example.test",
                ["Authentication:Oidc:ClientId"] = "netratel-web",
                ["Authentication:Oidc:ApiScope"] = "netratel.api"
            })
            .Build());
        services.AddSingleton<IAuthenticationService>(authenticationService ?? new ContextAuthenticationService());
        services.AddSingleton<StubSystemTokenService>();
        services.AddSingleton<ISystemTokenService>(provider => provider.GetRequiredService<StubSystemTokenService>());
        services.AddScoped<OperatorApiCredentialState>();
        services.AddScoped<ITokenService, TokenService>();
        services.AddScoped<OperatorApiCredentialProvider>();
        services.AddScoped<TestAuthenticationStateProvider>();
        services.AddScoped<AuthenticationStateProvider>(provider => provider.GetRequiredService<TestAuthenticationStateProvider>());
        services.AddScoped<OperatorApiCredentialCircuitHandler>();
        services.AddTransient<RedirectReissueHandler>();
        services.AddTransient<TokenAuthorizationHandler>();
        AddOperatorApiClient(services, "OrchestratorApi", baseAddress, primaryHandler);
        var downloadClientName = typeof(IWebClientDownloadService).Name;
        AddOperatorApiClient(services, downloadClientName, baseAddress, primaryHandler);
        services.AddScoped<IWebClientDownloadService>(provider => new WebClientDownloadService(
            provider.GetRequiredService<IHttpClientFactory>().CreateClient(downloadClientName)));
        services.AddScoped<IHttpClientFactory, OperatorApiHttpClientFactory>();
        return services.BuildServiceProvider(validateScopes: true);
    }

    private static void AddOperatorApiClient(
        IServiceCollection services,
        string name,
        Uri baseAddress,
        Func<HttpMessageHandler> primaryHandler) =>
        services.AddHttpClient(name, client =>
            {
                client.BaseAddress = baseAddress;
                client.Timeout = Timeout.InfiniteTimeSpan;
            })
            .ConfigurePrimaryHttpMessageHandler(primaryHandler)
            .AddHttpMessageHandler<RedirectReissueHandler>()
            .AddHttpMessageHandler<TokenAuthorizationHandler>();

    private static async Task PublishAndAssertCredentialAsync(
        TestAuthenticationStateProvider authentication,
        OperatorApiCredentialCircuitHandler handler,
        OperatorApiCredentialState state,
        ClaimsPrincipal principal)
    {
        await handler.AuthenticationStateChangeAsync(authentication.PublishAsync(principal));
        Assert.NotNull(state.GetCredential(principal));
    }

    private static DefaultHttpContext CreateOidcContext(IServiceProvider services, ClaimsPrincipal principal, string accessToken)
    {
        var properties = new AuthenticationProperties
        {
            ExpiresUtc = DateTimeOffset.UtcNow.AddHours(8),
            IsPersistent = true
        };
        properties.StoreTokens(
        [
            new AuthenticationToken { Name = "access_token", Value = accessToken },
            new AuthenticationToken { Name = "expires_at", Value = DateTimeOffset.UtcNow.AddHours(1).UtcDateTime.ToString("o") }
        ]);
        var ticket = new AuthenticationTicket(principal, properties, CookieAuthenticationDefaults.AuthenticationScheme);
        var context = new DefaultHttpContext { RequestServices = services, User = principal };
        context.Items[ContextAuthenticationService.TicketItemKey] = ticket;
        return context;
    }

    private static DefaultHttpContext CreateLocalContext(IServiceProvider services, ClaimsPrincipal principal, string cookieValue)
    {
        var context = new DefaultHttpContext { RequestServices = services, User = principal };
        context.Request.Headers.Cookie = $"NetRatel.Local={cookieValue}";
        return context;
    }

    private static ClaimsPrincipal CreateOidcPrincipal(string subject) => new(new ClaimsIdentity(
    [
        new Claim(ClaimTypes.NameIdentifier, subject),
        new Claim(ClaimTypes.Name, $"{subject}@example.test"),
        new Claim("iss", "https://issuer.example.test/"),
        new Claim(ClaimTypes.Role, "Operator")
    ], CookieAuthenticationDefaults.AuthenticationScheme));

    private static ClaimsPrincipal CreateLocalPrincipal(string subject) => new(new ClaimsIdentity(
    [
        new Claim(ClaimTypes.NameIdentifier, subject),
        new Claim(ClaimTypes.Name, $"{subject}@example.test"),
        new Claim("auth_mode", "local"),
        new Claim(ClaimTypes.Role, "Operator")
    ], CookieAuthenticationDefaults.AuthenticationScheme));

    private static string CreateAccessToken(string subject)
    {
        var token = new JwtSecurityToken(
            issuer: "https://issuer.example.test/",
            audience: "netratel.api",
            claims: [new Claim("sub", subject)],
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddHours(1));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private sealed record ObservedRequest(string? BearerSubject, string? BearerToken, string? Cookie, string? AccountRequest);

    private sealed class RecordingApiHandler(ConcurrentQueue<ObservedRequest> requests) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var bearer = request.Headers.Authorization?.Parameter;
            var subject = string.IsNullOrWhiteSpace(bearer)
                ? null
                : new JwtSecurityTokenHandler().ReadJwtToken(bearer).Claims.FirstOrDefault(claim => claim.Type == "sub")?.Value;
            requests.Enqueue(new ObservedRequest(
                subject,
                bearer,
                request.Headers.TryGetValues("Cookie", out var cookieValues) ? string.Join("; ", cookieValues) : null,
                request.Headers.TryGetValues("X-NetRatel-Account-Request", out var accountValues) ? string.Join(",", accountValues) : null));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private sealed class DirectoryThenClearCredentialHandler(
        Action clearCredential,
        ConcurrentQueue<ObservedRequest> requests,
        Action recordTelemetryRequest) : HttpMessageHandler
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
        private static readonly Guid AgentId = Guid.Parse("8cbda984-a1cb-4d4a-ad79-b5f5ac38e851");

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath == "/api/v2/agent-telemetry")
            {
                recordTelemetryRequest();
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(Array.Empty<object>(), options: JsonOptions)
                });
            }

            var bearer = request.Headers.Authorization?.Parameter;
            var subject = string.IsNullOrWhiteSpace(bearer)
                ? null
                : new JwtSecurityTokenHandler().ReadJwtToken(bearer).Claims.FirstOrDefault(claim => claim.Type == "sub")?.Value;
            requests.Enqueue(new ObservedRequest(
                subject,
                bearer,
                request.Headers.TryGetValues("Cookie", out var cookieValues) ? string.Join("; ", cookieValues) : null,
                request.Headers.TryGetValues("X-NetRatel-Account-Request", out var accountValues) ? string.Join(",", accountValues) : null));

            var directory = new ClientPresenceListDto("Akka", 1,
            [
                new ClientPresenceDto("gateway:2:directory", 2, AgentId, "directory-agent", "directory-agent", "Linux", "x64",
                    true, true, DateTimeOffset.UtcNow, "0.4.101", [], "gateway", "akka", true, 1)
            ]);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(directory, options: JsonOptions)
            };

            clearCredential();
            return Task.FromResult(response);
        }
    }

    private sealed class TestAuthenticationStateProvider : AuthenticationStateProvider
    {
        private Task<AuthenticationState> _currentState = Task.FromResult(new AuthenticationState(new ClaimsPrincipal()));

        public override Task<AuthenticationState> GetAuthenticationStateAsync() => _currentState;

        public Task<AuthenticationState> PublishAsync(ClaimsPrincipal principal)
        {
            _currentState = Task.FromResult(new AuthenticationState(principal));
            NotifyAuthenticationStateChanged(_currentState);
            return _currentState;
        }

        public void PublishPending(Task<AuthenticationState> state)
        {
            _currentState = state;
            NotifyAuthenticationStateChanged(state);
        }
    }

    private sealed class ContextAuthenticationService : IAuthenticationService
    {
        public const string TicketItemKey = "OperatorApiCredentialIsolationTests.Ticket";

        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme) =>
            Task.FromResult(context.Items.TryGetValue(TicketItemKey, out var value) && value is AuthenticationTicket ticket
                ? AuthenticateResult.Success(ticket)
                : AuthenticateResult.NoResult());

        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;

        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;

        public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties) => Task.CompletedTask;

        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
    }

    private sealed class BlockingAuthenticationService(string oldAccessToken) : IAuthenticationService
    {
        private readonly TaskCompletionSource _oldAuthenticationStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseOldAuthentication = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource OldAuthenticationStarted => _oldAuthenticationStarted;

        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme)
        {
            if (!context.Items.TryGetValue(ContextAuthenticationService.TicketItemKey, out var value)
                || value is not AuthenticationTicket ticket)
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            if (!string.Equals(ticket.Properties.GetTokenValue("access_token"), oldAccessToken, StringComparison.Ordinal))
            {
                return Task.FromResult(AuthenticateResult.Success(ticket));
            }

            _oldAuthenticationStarted.TrySetResult();
            return CompleteOldAuthenticationAsync(ticket);
        }

        public void ReleaseOldAuthentication() => _releaseOldAuthentication.TrySetResult();

        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;

        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;

        public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties) => Task.CompletedTask;

        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;

        private async Task<AuthenticateResult> CompleteOldAuthenticationAsync(AuthenticationTicket ticket)
        {
            await _releaseOldAuthentication.Task.ConfigureAwait(false);
            return AuthenticateResult.Success(ticket);
        }
    }

    private sealed class StubSystemTokenService : ISystemTokenService
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public Task<string> GetTokenAsync()
        {
            Interlocked.Increment(ref _callCount);
            return Task.FromResult("fixture-system-token-unused");
        }
    }

    private sealed record ObservedHttpRequest(string? Authorization, string? Cookie, string? AccountRequest);

    private sealed class LoopbackApiServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _shutdown = new();
        private readonly Task _serveTask;
        private bool _expectedListenerShutdown;

        public Uri BaseAddress { get; }
        public ConcurrentQueue<ObservedHttpRequest> Requests { get; } = new();

        public LoopbackApiServer()
        {
            _listener.Start();
            var address = (IPEndPoint)_listener.LocalEndpoint;
            BaseAddress = new Uri($"http://127.0.0.1:{address.Port}/");
            _serveTask = ServeAsync(_shutdown.Token);
        }

        public async ValueTask DisposeAsync()
        {
            _shutdown.Cancel();
            _listener.Stop();
            await _serveTask;
            Assert.True(_expectedListenerShutdown, "The loopback API listener did not observe its requested shutdown.");
            _shutdown.Dispose();
        }

        private async Task ServeAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    using var client = await _listener.AcceptTcpClientAsync(cancellationToken);
                    await using var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                    _ = await reader.ReadLineAsync(cancellationToken);
                    string? line;
                    string? authorization = null;
                    string? cookie = null;
                    string? accountRequest = null;
                    while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(cancellationToken)))
                    {
                        var separator = line.IndexOf(':');
                        if (separator <= 0)
                        {
                            continue;
                        }

                        var headerName = line[..separator];
                        var headerValue = line[(separator + 1)..].Trim();
                        if (headerName.Equals("Authorization", StringComparison.OrdinalIgnoreCase)) authorization = headerValue;
                        if (headerName.Equals("Cookie", StringComparison.OrdinalIgnoreCase)) cookie = headerValue;
                        if (headerName.Equals("X-NetRatel-Account-Request", StringComparison.OrdinalIgnoreCase)) accountRequest = headerValue;
                    }

                    Requests.Enqueue(new ObservedHttpRequest(authorization, cookie, accountRequest));
                    var response = Encoding.ASCII.GetBytes(
                        "HTTP/1.1 200 OK\r\nSet-Cookie: ApiIssued=server-side; Path=/\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(response, cancellationToken);
                    await stream.FlushAsync(cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _expectedListenerShutdown = true;
            }
            catch (SocketException exception) when (cancellationToken.IsCancellationRequested
                                                    && exception.SocketErrorCode is SocketError.Interrupted or SocketError.OperationAborted)
            {
                _expectedListenerShutdown = true;
            }
        }
    }
}
