using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.API.Gateway;
using NetRatel.Application.Presence;
using NetRatel.Infrastructure.Identity.Authorization;
using Xunit;

[Trait("category", "integration")]
[Collection(ApiIntegrationCollection.Name)]
public sealed class LocalOperationalAuthorizationIntegrationTests
{
    [Fact]
    public async Task OperationalHealthRoutesAllowThePersistedLocalAdministratorAndDenyOtherCallers()
    {
        var factory = await CreateInitializedFactoryAsync();
        await using var factoryOwner = (IAsyncDisposable)factory;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var anonymous = factory.CreateClient();
        using var administrator = await factory.CreateLocalAdministratorClientAsync(TimeSpan.FromSeconds(45));
        using var unprivileged = await CreateUnprivilegedLocalClientAsync(factory, administrator, timeout.Token);
        var tenantId = await ReadOpenApiTenantIdAsync(administrator, timeout.Token);
        using var agent = factory.CreateClient();
        agent.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", factory.CreateAgentBearerToken(tenantId, Guid.NewGuid()));

        var anonymousLive = await ReadStatusAsync(anonymous, "/health/live", timeout.Token);
        var anonymousReady = await ReadStatusAsync(anonymous, "/health/ready", timeout.Token);
        var unprivilegedLive = await ReadStatusAsync(unprivileged, "/health/live", timeout.Token);
        var unprivilegedReady = await ReadStatusAsync(unprivileged, "/health/ready", timeout.Token);
        var agentLive = await ReadStatusAsync(agent, "/health/live", timeout.Token);
        var agentReady = await ReadStatusAsync(agent, "/health/ready", timeout.Token);

        administrator.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-valid-token");
        var invalidBearerWithAdministratorCookie = await ReadStatusAsync(administrator, "/health/live", timeout.Token);
        administrator.DefaultRequestHeaders.Authorization = null;
        var administratorLive = await ReadStatusAsync(administrator, "/health/live", timeout.Token);
        var administratorReady = await ReadReadinessStatusAsync(administrator, timeout.Token);

        new
        {
            AnonymousLive = anonymousLive,
            AnonymousReady = anonymousReady,
            UnprivilegedLive = unprivilegedLive,
            UnprivilegedReady = unprivilegedReady,
            AgentLive = agentLive,
            AgentReady = agentReady,
            InvalidBearerWithAdministratorCookie = invalidBearerWithAdministratorCookie,
            AdministratorLive = administratorLive,
            AdministratorReady = administratorReady
        }.Should().BeEquivalentTo(new
        {
            AnonymousLive = (int)HttpStatusCode.Unauthorized,
            AnonymousReady = (int)HttpStatusCode.Unauthorized,
            UnprivilegedLive = (int)HttpStatusCode.Forbidden,
            UnprivilegedReady = (int)HttpStatusCode.Forbidden,
            AgentLive = (int)HttpStatusCode.Forbidden,
            AgentReady = (int)HttpStatusCode.Forbidden,
            InvalidBearerWithAdministratorCookie = (int)HttpStatusCode.Unauthorized,
            AdministratorLive = (int)HttpStatusCode.OK,
            AdministratorReady = (int)HttpStatusCode.OK
        }, "observed health statuses (anonymous live/ready, Local no-role live/ready, signed Agent live/ready, invalid bearer with admin cookie, admin live/ready) were {0}",
            string.Join(",", new[]
            {
                anonymousLive, anonymousReady, unprivilegedLive, unprivilegedReady,
                agentLive, agentReady, invalidBearerWithAdministratorCookie, administratorLive, administratorReady
            }));
    }

    [Fact]
    public async Task GatewayTerminalSessionLookupUsesRealLocalAdministratorAndPreservesDenials()
    {
        var factory = await CreateInitializedFactoryAsync();
        await using var factoryOwner = (IAsyncDisposable)factory;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        using var anonymous = factory.CreateClient();
        using var administrator = await factory.CreateLocalAdministratorClientAsync(TimeSpan.FromSeconds(30));
        using var unprivileged = await CreateUnprivilegedLocalClientAsync(factory, administrator, timeout.Token);
        var tenantId = await ReadOpenApiTenantIdAsync(administrator, timeout.Token);
        using var agent = factory.CreateClient();
        agent.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", factory.CreateAgentBearerToken(tenantId, Guid.NewGuid()));

        var sessionId = Guid.NewGuid().ToString("D");
        var path = $"/api/v2/gateway-terminal/{sessionId}";
        var anonymousStatus = await ReadStatusAsync(anonymous, path, timeout.Token);
        var unprivilegedStatus = await ReadStatusAsync(unprivileged, path, timeout.Token);
        var agentStatus = await ReadStatusAsync(agent, path, timeout.Token);
        administrator.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-valid-token");
        var invalidBearerWithAdministratorCookie = await ReadStatusAsync(administrator, path, timeout.Token);
        administrator.DefaultRequestHeaders.Authorization = null;
        var administratorStatus = await ReadStatusAsync(administrator, path, timeout.Token);

        new
        {
            Anonymous = anonymousStatus,
            Unprivileged = unprivilegedStatus,
            SignedAgent = agentStatus,
            InvalidBearerWithAdministratorCookie = invalidBearerWithAdministratorCookie,
            Administrator = administratorStatus
        }.Should().BeEquivalentTo(new
        {
            Anonymous = (int)HttpStatusCode.Unauthorized,
            Unprivileged = (int)HttpStatusCode.Forbidden,
            SignedAgent = (int)HttpStatusCode.Forbidden,
            InvalidBearerWithAdministratorCookie = (int)HttpStatusCode.Unauthorized,
            Administrator = (int)HttpStatusCode.NotFound
        }, "observed terminal-route statuses (anonymous, Local no-role, signed Agent, invalid bearer with admin cookie, admin) were {0}",
            string.Join(",", new[]
            {
                anonymousStatus, unprivilegedStatus, agentStatus,
                invalidBearerWithAdministratorCookie, administratorStatus
            }));
    }

    [Fact]
    public async Task TerminalSessionsAuthorizeTheAssignedTenantForReadCloseAndStream()
    {
        var factory = await CreateInitializedFactoryAsync();
        await using var factoryOwner = (IAsyncDisposable)factory;
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        using var administrator = await factory.CreateLocalAdministratorClientAsync(TimeSpan.FromSeconds(45));
        var ownTenantId = await CreateTenantAsync(administrator, "Terminal access tenant", timeout.Token);
        var foreignTenantId = await CreateTenantAsync(administrator, "Foreign terminal tenant", timeout.Token);
        var operatorEmail = $"terminal-operator-{Guid.NewGuid():N}@example.test";
        using var tenantOperator = await CreateUnprivilegedLocalClientAsync(factory, administrator, timeout.Token, operatorEmail);
        await AssignBuiltInOperatorRoleAsync(administrator, operatorEmail, ownTenantId, timeout.Token);

        var openApiSchemes = await ReadTerminalSessionOpenApiSchemesAsync(administrator, timeout.Token);
        openApiSchemes.Should().BeEquivalentTo(
            ["Bearer", "LocalSession", "IntegrationCredential"],
            "the new session policy must preserve the established authenticated alternatives in the public contract");

        var terminals = factory.Services.GetRequiredService<IAgentTerminalSessionRegistry>();
        using var ownSession = await OpenRegisteredTerminalSessionAsync(terminals, ownTenantId, timeout.Token);
        using var foreignSession = await OpenRegisteredTerminalSessionAsync(terminals, foreignTenantId, timeout.Token);

        var ownRead = await ReadStatusAsync(tenantOperator, $"/api/v2/gateway-terminal/{ownSession.Session.SessionId}", timeout.Token);
        var foreignRead = await ReadStatusAsync(tenantOperator, $"/api/v2/gateway-terminal/{foreignSession.Session.SessionId}", timeout.Token);
        var unknownRead = await ReadStatusAsync(tenantOperator, $"/api/v2/gateway-terminal/{Guid.NewGuid():D}", timeout.Token);

        using var ownStream = await tenantOperator.GetAsync(
            $"/api/v2/gateway-terminal/{ownSession.Session.SessionId}/stream",
            HttpCompletionOption.ResponseHeadersRead,
            timeout.Token);
        using var foreignClose = await tenantOperator.PostAsync(
            $"/api/v2/gateway-terminal/{foreignSession.Session.SessionId}/close",
            JsonContent.Create(new { Reason = "integration-test" }),
            timeout.Token);
        using var foreignStream = await tenantOperator.GetAsync(
            $"/api/v2/gateway-terminal/{foreignSession.Session.SessionId}/stream",
            HttpCompletionOption.ResponseHeadersRead,
            timeout.Token);
        using var ownClose = await tenantOperator.PostAsync(
            $"/api/v2/gateway-terminal/{ownSession.Session.SessionId}/close",
            JsonContent.Create(new { Reason = "integration-test" }),
            timeout.Token);
        if (ownClose.StatusCode == HttpStatusCode.Accepted)
            await ownSession.AcknowledgeAgentCloseAsync(timeout.Token);

        new
        {
            OwnSessionRead = ownRead,
            ForeignSessionRead = foreignRead,
            UnknownSessionRead = unknownRead,
            OwnSessionStream = (int)ownStream.StatusCode,
            OwnSessionStreamMediaType = ownStream.Content.Headers.ContentType?.MediaType,
            OwnSessionClose = (int)ownClose.StatusCode,
            ForeignSessionClose = (int)foreignClose.StatusCode,
            ForeignSessionStream = (int)foreignStream.StatusCode,
            ForeignSessionState = terminals.Get(foreignSession.Session.SessionId)?.State
        }.Should().BeEquivalentTo(new
        {
            OwnSessionRead = (int)HttpStatusCode.OK,
            ForeignSessionRead = (int)HttpStatusCode.Forbidden,
            UnknownSessionRead = (int)HttpStatusCode.Forbidden,
            OwnSessionStream = (int)HttpStatusCode.OK,
            OwnSessionStreamMediaType = "text/event-stream",
            OwnSessionClose = (int)HttpStatusCode.Accepted,
            ForeignSessionClose = (int)HttpStatusCode.Forbidden,
            ForeignSessionStream = (int)HttpStatusCode.Forbidden,
            ForeignSessionState = "opened"
        }, "tenant-scoped TerminalAccess must follow the registered session's actual tenant and reject foreign or unknown sessions before endpoint actions");
    }

    private static async Task<ApiFactory> CreateInitializedFactoryAsync()
    {
        var factory = new ApiFactory();
        var factoryOwner = (IAsyncDisposable)factory;
        try
        {
            await factory.InitializeAsync();
            return factory;
        }
        catch (Exception initializationException)
        {
            try
            {
                await factoryOwner.DisposeAsync();
            }
            catch
            {
                initializationException.Data["ApiFactoryInitializationCleanup"] = "owned_factory_disposal_failed";
            }

            throw;
        }
    }

    private static async Task<HttpClient> CreateUnprivilegedLocalClientAsync(
        ApiFactory factory,
        HttpClient administrator,
        CancellationToken cancellationToken,
        string? requestedEmail = null)
    {
        var email = requestedEmail ?? $"operational-reader-{Guid.NewGuid():N}@example.test";
        const string password = "A1! unprivileged operational passphrase";

        using var created = await administrator.PostAsJsonAsync("/api/v2/local-auth/users", new
        {
            DisplayName = "Unprivileged operational integration user",
            Email = email
        }, cancellationToken);
        created.StatusCode.Should().Be(HttpStatusCode.Created,
            "the persisted unprivileged account must be created through the ordinary Local administration API");

        var activation = await created.Content.ReadFromJsonAsync<ActivationResponse>(cancellationToken);
        activation.Should().NotBeNull();
        activation!.Email.Should().Be(email);
        var activationToken = activation.ActivationToken;
        string.IsNullOrWhiteSpace(activationToken).Should().BeFalse(
            "ordinary Local account activation must return a token without exposing it in assertion output");

        using var activationClient = factory.CreateClient();
        using var activated = await activationClient.PostAsJsonAsync("/api/v2/local-auth/activate", new
        {
            Email = email,
            ActivationToken = activationToken,
            NewPassword = password
        }, cancellationToken);
        activated.StatusCode.Should().Be(HttpStatusCode.NoContent,
            "the unprivileged account must be activated through the normal Local activation route");

        var client = factory.CreateClient();
        try
        {
            using var login = await client.PostAsJsonAsync("/api/v2/local-auth/login", new
            {
                Email = email,
                Password = password,
                RememberMe = false
            }, cancellationToken);
            login.StatusCode.Should().Be(HttpStatusCode.NoContent,
                "the unprivileged principal must authenticate through the real Local cookie endpoint");
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static async Task<int> CreateTenantAsync(
        HttpClient administrator,
        string name,
        CancellationToken cancellationToken)
    {
        using var created = await administrator.PostAsJsonAsync("/api/v1/tenants/", new { Name = $"{name} {Guid.NewGuid():N}" }, cancellationToken);
        created.StatusCode.Should().Be(HttpStatusCode.Created,
            "the real Local administrator creates both tenant scopes through the ordinary tenant API");
        var tenant = await created.Content.ReadFromJsonAsync<TenantResponse>(cancellationToken);
        tenant.Should().NotBeNull();
        tenant!.TenantId.Should().BePositive();
        return tenant.TenantId;
    }

    private static async Task AssignBuiltInOperatorRoleAsync(
        HttpClient administrator,
        string email,
        int tenantId,
        CancellationToken cancellationToken)
    {
        using var usersResponse = await administrator.GetAsync("/api/v2/access/users", cancellationToken);
        usersResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            "the bootstrap administrator resolves the newly activated principal through access administration");
        using var users = await usersResponse.Content.ReadAsStreamAsync(cancellationToken);
        using var usersDocument = await JsonDocument.ParseAsync(users, cancellationToken: cancellationToken);
        var principalId = usersDocument.RootElement.EnumerateArray()
            .Single(user => string.Equals(user.GetProperty("email").GetString(), email, StringComparison.OrdinalIgnoreCase))
            .GetProperty("principalId")
            .GetString();
        principalId.Should().NotBeNullOrWhiteSpace();

        using var rolesResponse = await administrator.GetAsync($"/api/v2/access/roles?tenantId={tenantId}", cancellationToken);
        rolesResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            "the bootstrap administrator resolves the built-in tenant role through access administration");
        using var roles = await rolesResponse.Content.ReadAsStreamAsync(cancellationToken);
        using var rolesDocument = await JsonDocument.ParseAsync(roles, cancellationToken: cancellationToken);
        var role = rolesDocument.RootElement.EnumerateArray()
            .Single(candidate => candidate.GetProperty("name").GetString() == "Operator");
        role.GetProperty("permissions").EnumerateArray()
            .Select(permission => permission.GetString())
            .Should().Contain(NetRatelPermissions.TerminalAccess,
                "the role assigned in this test must carry the real terminal permission");
        var roleId = role.GetProperty("id").GetString();
        roleId.Should().NotBeNullOrWhiteSpace();

        using var assigned = await administrator.PutAsJsonAsync(
            $"/api/v2/access/principals/{Uri.EscapeDataString(principalId!)}/assignments",
            new { RoleId = roleId, TenantId = tenantId },
            cancellationToken);
        assigned.StatusCode.Should().Be(HttpStatusCode.Created,
            "the principal must receive a persisted tenant-scoped role through the ordinary assignment API");
    }

    private static async Task<IReadOnlySet<string>> ReadTerminalSessionOpenApiSchemesAsync(
        HttpClient administrator,
        CancellationToken cancellationToken)
    {
        using var response = await administrator.GetAsync("/openapi/v1.json", cancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "OpenAPI transformation must recognize the new policy instead of failing on an unknown policy name");
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var operation = document.RootElement
            .GetProperty("paths")
            .GetProperty("/api/v2/gateway-terminal/{sessionId}")
            .GetProperty("get");
        return operation.GetProperty("security").EnumerateArray()
            .SelectMany(requirement => requirement.EnumerateObject().Select(scheme => scheme.Name))
            .ToHashSet(StringComparer.Ordinal);
    }

    private static async Task<OpenedTerminalSession> OpenRegisteredTerminalSessionAsync(
        IAgentTerminalSessionRegistry terminals,
        int tenantId,
        CancellationToken cancellationToken)
    {
        var client = new ClientKey(tenantId, Guid.NewGuid());
        var registration = terminals.Register(
            client,
            Guid.NewGuid(),
            connectionEpoch: 1,
            availableShells: ["bash"],
            capabilities: [AgentTerminalSessionRegistry.IdempotentCloseCapability]);
        try
        {
            var session = await terminals.OpenAsync(client, "bash", workingDirectory: null, columns: 120, rows: 32, cancellationToken);
            var start = await registration.Reader.ReadAsync(cancellationToken);
            start.PayloadCase.Should().Be(GatewayTerminalFrame.PayloadOneofCase.Start,
                "the session must be created by the registered terminal transport rather than a fake registry");
            start.Start.SessionId.Should().Be(session.SessionId);
            start.Start.Generation.Should().Be(session.Generation);
            terminals.TryReceiveOpened(client, new TerminalSessionOpened
            {
                SessionId = session.SessionId,
                Generation = session.Generation
            }).Should().BeTrue("the real registry must accept the matching agent-open acknowledgment");
            terminals.Get(session.SessionId)!.State.Should().Be("opened");
            return new OpenedTerminalSession(terminals, client, registration, session);
        }
        catch
        {
            registration.Dispose();
            throw;
        }
    }

    private static async Task<int> ReadStatusAsync(
        HttpClient client,
        string path,
        CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(path, cancellationToken);
        return (int)response.StatusCode;
    }

    private static async Task<int> ReadReadinessStatusAsync(HttpClient administrator, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        using var pollInterval = new PeriodicTimer(TimeSpan.FromMilliseconds(150));

        while (true)
        {
            try
            {
                using var response = await administrator.GetAsync("/health/ready", deadline.Token);
                if (response.StatusCode != HttpStatusCode.ServiceUnavailable)
                {
                    return (int)response.StatusCode;
                }

                await pollInterval.WaitForNextTickAsync(deadline.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return (int)HttpStatusCode.ServiceUnavailable;
            }
        }
    }

    private static async Task<int> ReadOpenApiTenantIdAsync(HttpClient administrator, CancellationToken cancellationToken)
    {
        using var response = await administrator.GetAsync("/api/v2/access/tenants", cancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "the Local administrator must resolve tenant scope through the actual access API");
        using var tenants = await response.Content.ReadFromJsonAsync<JsonDocument>(cancellationToken);
        tenants.Should().NotBeNull();
        return tenants!.RootElement.EnumerateArray()
            .Single(tenant => tenant.GetProperty("name").GetString() == "OpenAPI tenant")
            .GetProperty("tenantId")
            .GetInt32();
    }

    private sealed record ActivationResponse(string UserId, string Email, string ActivationToken);
    private sealed record TenantResponse(int TenantId, string Name);

    private sealed class OpenedTerminalSession : IDisposable
    {
        private readonly IAgentTerminalSessionRegistry _terminals;
        private readonly ClientKey _client;
        private readonly AgentTerminalGatewayRegistration _registration;
        private readonly GatewayTerminalSession _session;

        public OpenedTerminalSession(
            IAgentTerminalSessionRegistry terminals,
            ClientKey client,
            AgentTerminalGatewayRegistration registration,
            GatewayTerminalSession session)
        {
            _terminals = terminals;
            _client = client;
            _registration = registration;
            _session = session;
        }

        public GatewayTerminalSession Session => _session;

        public async Task AcknowledgeAgentCloseAsync(CancellationToken cancellationToken)
        {
            var close = await _registration.Reader.ReadAsync(cancellationToken);
            close.PayloadCase.Should().Be(GatewayTerminalFrame.PayloadOneofCase.Close,
                "an authorized close request must be dispatched to the exact registered agent session");
            close.Close.SessionId.Should().Be(_session.SessionId);
            close.Close.Generation.Should().Be(_session.Generation);
            _terminals.TryReceiveClosed(_client, new TerminalSessionClosed
            {
                SessionId = _session.SessionId,
                Generation = _session.Generation,
                Reason = "integration_test_complete"
            }).Should().BeTrue("the matching agent close acknowledgement must close the authorized session");
        }

        public void Dispose()
        {
            try
            {
                _terminals.TryReceiveClosed(_client, new TerminalSessionClosed
                {
                    SessionId = _session.SessionId,
                    Generation = _session.Generation,
                    Reason = "integration_test_complete"
                });
            }
            finally
            {
                _registration.Dispose();
            }
        }
    }
}
