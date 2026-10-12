using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.API.Gateway;
using NetRatel.API.Security.Authorization;
using NetRatel.Application.Operations;
using NetRatel.Application.Presence;
using NetRatel.Infrastructure.Identity;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using NetRatel.Shared.Operations;
using Xunit;

namespace NetRatel.Tests.API;

public sealed partial class ManagedOperationIngressTests
{
    private const string FolderPath = "/srv/allowed/review-folder";

    [Theory]
    [InlineData("create-directory", "create_directory")]
    [InlineData("delete", "delete")]
    public async Task Real_api_credential_can_preview_confirm_and_replay_one_root_bound_folder_operation(string route, string operation)
    {
        await using var scenario = await Scenario.CreateAsync(includeFiles: true);
        var plan = await scenario.PreviewFileAsync(route);
        scenario.Files.Dispatched.Should().BeEmpty("preview does not mutate the client");
        (await scenario.AcceptedCountAsync()).Should().Be(0);

        using var confirmed = await scenario.FileAsync(route, "confirm", plan: plan);
        using var replayed = await scenario.FileAsync(route, "confirm", plan: plan);

        confirmed.StatusCode.Should().Be(HttpStatusCode.OK, await confirmed.Content.ReadAsStringAsync());
        replayed.StatusCode.Should().Be(HttpStatusCode.OK, await replayed.Content.ReadAsStringAsync());
        (await replayed.Content.ReadAsStringAsync()).Should().Contain("\"replayed\":true");
        scenario.Files.Dispatched.Should().ContainSingle().Which.Should().Be((operation, new ClientKey(7, scenario.AgentId), FolderPath));
        scenario.Files.Policies.Should().ContainSingle().Which.AllowedRoots.Should().Equal("/srv/allowed");
        (await scenario.AcceptedCountAsync()).Should().Be(1);
    }

    [Theory]
    [InlineData("create-directory", "credential")]
    [InlineData("create-directory", "owner")]
    [InlineData("create-directory", "policy")]
    [InlineData("delete", "credential")]
    [InlineData("delete", "owner")]
    [InlineData("delete", "policy")]
    public async Task Folder_confirmation_rechecks_current_owner_credential_and_operation_policy(string route, string removed)
    {
        await using var scenario = await Scenario.CreateAsync(includeFiles: true);
        var plan = await scenario.PreviewFileAsync(route);
        await scenario.RemoveFileAuthorityAsync(route, removed);

        using var denied = await scenario.FileAsync(route, "confirm", plan: plan);

        denied.StatusCode.Should().Be(HttpStatusCode.Forbidden, await denied.Content.ReadAsStringAsync());
        scenario.Files.Dispatched.Should().BeEmpty();
        (await scenario.AcceptedCountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData("create-directory", "/srv/allowed-sibling/folder")]
    [InlineData("delete", "/srv/allowed")]
    public async Task Direct_folder_preview_preserves_policy_root_boundaries(string route, string path)
    {
        await using var scenario = await Scenario.CreateAsync(includeFiles: true);

        using var denied = await scenario.FileAsync(route, "preview", path: path);

        denied.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await denied.Content.ReadAsStringAsync()).Should().Contain("file_policy_roots_missing");
        scenario.Files.Dispatched.Should().BeEmpty();
        await using var scope = scenario.App.Services.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>().McpOperatorConfirmationPlans.CountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData("tenant", HttpStatusCode.Forbidden)]
    [InlineData("agent", HttpStatusCode.NotFound)]
    public async Task Direct_folder_preview_cannot_replace_the_authorized_tenant_or_persisted_agent(string mismatch, HttpStatusCode expected)
    {
        await using var scenario = await Scenario.CreateAsync(includeFiles: true);

        using var denied = await scenario.FileAsync("create-directory", "preview",
            tenantId: mismatch == "tenant" ? 8 : 7, agentId: mismatch == "agent" ? Guid.NewGuid() : scenario.AgentId);

        denied.StatusCode.Should().Be(expected, await denied.Content.ReadAsStringAsync());
        scenario.Files.Dispatched.Should().BeEmpty();
        (await scenario.AcceptedCountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData("path")]
    [InlineData("credential")]
    public async Task Folder_plan_cannot_be_reused_for_a_changed_path_or_another_credential_of_the_same_owner(string changed)
    {
        await using var scenario = await Scenario.CreateAsync(includeFiles: true);
        var plan = await scenario.PreviewFileAsync("create-directory");
        var token = scenario.ApiCredential.Secret;
        if (changed == "credential")
        {
            await using var scope = scenario.App.Services.CreateAsyncScope();
            token = (await scope.ServiceProvider.GetRequiredService<IIntegrationCredentialService>().CreateAsync(scenario.Owner,
                new("Second CLI", IntegrationCredentialPurpose.Api, DateTimeOffset.UtcNow.AddHours(1),
                    [new(7, NetRatelPermissions.FileWrite)]))).Secret;
        }

        using var denied = await scenario.FileAsync("create-directory", "confirm", plan: plan, token: token,
            path: changed == "path" ? "/srv/allowed/different-folder" : FolderPath);

        denied.StatusCode.Should().Be(HttpStatusCode.Conflict, await denied.Content.ReadAsStringAsync());
        (await denied.Content.ReadAsStringAsync()).Should().Contain("confirmation_plan_stale");
        scenario.Files.Dispatched.Should().BeEmpty();
        (await scenario.AcceptedCountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Folder_authority_revoked_after_plan_consumption_is_rechecked_before_acceptance_or_dispatch()
    {
        await using var scenario = await Scenario.CreateAsync(includeFiles: true);
        var plan = await scenario.PreviewFileAsync("create-directory");
        scenario.AfterFileConfirmation = () => scenario.RemoveFileAuthorityAsync("create-directory", "credential");

        using var denied = await scenario.FileAsync("create-directory", "confirm", plan: plan);

        denied.StatusCode.Should().Be(HttpStatusCode.Forbidden, await denied.Content.ReadAsStringAsync());
        scenario.Files.Dispatched.Should().BeEmpty();
        (await scenario.AcceptedCountAsync()).Should().Be(0);
        await using var scope = scenario.App.Services.CreateAsyncScope();
        var receipt = await scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>().McpOperatorIdempotencyRecords.SingleAsync();
        receipt.Outcome.Should().Be(McpOperatorIdempotencyOutcome.Failed);
        receipt.ResultReference.Should().Be("permission_denied");
    }

    [Fact]
    public async Task Folder_http_mcp_relay_preserves_signed_operation_and_current_account_permission_checks()
    {
        await using var scenario = await Scenario.CreateAsync(includeFiles: true);
        var token = scenario.M2mToken();
        var plan = await scenario.PreviewFileAsync("create-directory", token, scenario.FileAssertion("preview_create_directory"));

        using var confirmed = await scenario.FileAsync("create-directory", "confirm", plan: plan, token: token,
            delegation: scenario.FileAssertion("confirm_create_directory"));

        confirmed.StatusCode.Should().Be(HttpStatusCode.OK, await confirmed.Content.ReadAsStringAsync());
        scenario.Files.Dispatched.Should().ContainSingle();
        (await scenario.AcceptedCountAsync()).Should().Be(1);
    }

    [Theory]
    [InlineData("missing-delegation", HttpStatusCode.Forbidden)]
    [InlineData("wrong-operation", HttpStatusCode.Forbidden)]
    [InlineData("direct-with-delegation", HttpStatusCode.Unauthorized)]
    public async Task Folder_ingress_does_not_turn_a_service_token_or_direct_credential_into_a_delegated_actor(string mismatch, HttpStatusCode expected)
    {
        await using var scenario = await Scenario.CreateAsync(includeFiles: true);
        var token = mismatch == "direct-with-delegation" ? scenario.ApiCredential.Secret : scenario.M2mToken();
        var assertion = mismatch == "missing-delegation" ? null : scenario.FileAssertion(
            mismatch == "wrong-operation" ? "preview_delete" : "preview_create_directory");

        using var denied = await scenario.FileAsync("create-directory", "preview", token: token, delegation: assertion);

        denied.StatusCode.Should().Be(expected, await denied.Content.ReadAsStringAsync());
        scenario.Files.Dispatched.Should().BeEmpty();
        (await scenario.AcceptedCountAsync()).Should().Be(0);
    }

    private sealed record FilePlan(string PlanToken, string IdempotencyKey);

    private sealed partial class Scenario
    {
        private static readonly string[] FilePermissions = [NetRatelPermissions.FileRead, NetRatelPermissions.FileWrite, NetRatelPermissions.FileDelete];
        public RecordingFileRegistry Files { get; } = new();
        public Func<Task>? AfterFileConfirmation { get; set; }

        private void AddFileServices(IServiceCollection services)
        {
            services.AddAuthorization(McpOperatorFileAuthorization.AddPolicies);
            McpOperatorFileAuthorization.AddHandlers(services);
            services.AddSingleton<IClientPresenceRouter>(new FilePresence());
            services.AddSingleton<IAgentFileGatewaySessionRegistry>(Files);
            services.AddScoped<IMcpOperatorFileArtifactStore, McpOperatorFileArtifactStore>();
            services.AddScoped<IMcpOperatorConfirmationService>(provider => new FileConfirmations(
                new McpOperatorConfirmationService(provider.GetRequiredService<OrchestratorDbContext>()), this));
        }

        private void SeedFilePolicies(OrchestratorDbContext db)
        {
            foreach (var operation in new[] { "create_directory", "delete" })
                db.McpOperatorPolicies.Add(new McpOperatorPolicyRecord
                {
                    Id = Guid.NewGuid(), Name = $"Folder {operation}", Environment = McpOperatorEnvironment.Production,
                    Effect = McpOperatorPolicyEffect.Allow, PrincipalSelectorKind = McpOperatorPrincipalSelectorKind.OAuthSubject,
                    PrincipalSelectorValue = Owner, TargetSelectorKind = McpOperatorTargetSelectorKind.ExactAgent,
                    TenantId = 7, AgentId = AgentId, OperationFamily = McpOperatorOperationFamily.FileWrite,
                    Operation = $"netratel_files/{operation}",
                    ConstraintsJson = JsonSerializer.Serialize(new McpOperatorConstraints(WriteRoots: ["/srv/allowed"]), McpOperatorJsonContext.Default.McpOperatorConstraints),
                    CreatedAtUtc = DateTimeOffset.UtcNow, CreatedBy = "policy-admin", Version = 1
                });
        }

        public async Task<FilePlan> PreviewFileAsync(string route, string? token = null, string? delegation = null)
        {
            using var response = await FileAsync(route, "preview", token: token, delegation: delegation);
            response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
            return (await response.Content.ReadFromJsonAsync<FilePlan>())!;
        }

        public async Task<HttpResponseMessage> FileAsync(string route, string phase, FilePlan? plan = null, string path = FolderPath,
            string? token = null, string? delegation = null, int tenantId = 7, Guid? agentId = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post,
                $"/api/v2/mcp/operator/agents/{tenantId}/{agentId ?? AgentId:D}/files/{route}/{phase}")
            {
                Content = JsonContent.Create(new { path, planToken = plan?.PlanToken, idempotencyKey = plan?.IdempotencyKey })
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token ?? ApiCredential.Secret);
            if (delegation is not null) request.Headers.Add(McpOperatorDelegationOptions.HeaderName, delegation);
            return await Client.SendAsync(request);
        }

        public string FileAssertion(string operation) => new McpOperatorDelegationTokenService(_delegation).Create(
            new McpOperatorDelegationIdentity(Owner, "operator-client", null, [], ["Operator"], ["netratel.mcp.write"]),
            new McpOperatorDelegationRequest("netratel_files", operation, Guid.NewGuid().ToString("N"), Resource, "prod", 7, AgentId,
                CorrelationId: Guid.NewGuid().ToString("N"))
            {
                IngressCredentialId = _httpCredential.CredentialId,
                IngressPermission = operation.Contains("delete", StringComparison.Ordinal) ? NetRatelPermissions.FileDelete : NetRatelPermissions.FileWrite
            });

        public async Task RemoveFileAuthorityAsync(string route, string removed)
        {
            await using var scope = App.Services.CreateAsyncScope();
            var identity = scope.ServiceProvider.GetRequiredService<NetRatelIdentityDbContext>();
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            var permission = route == "delete" ? NetRatelPermissions.FileDelete : NetRatelPermissions.FileWrite;
            if (removed == "credential")
                identity.IntegrationCredentialGrants.RemoveRange(await identity.IntegrationCredentialGrants
                    .Where(grant => grant.CredentialId == ApiCredential.CredentialId && grant.Permission == permission).ToArrayAsync());
            else if (removed == "owner")
                identity.AccessRolePermissions.RemoveRange(await identity.AccessRolePermissions.Where(grant => grant.Permission == permission).ToArrayAsync());
            else
                db.McpOperatorPolicies.RemoveRange(await db.McpOperatorPolicies
                    .Where(policy => policy.Operation == $"netratel_files/{route.Replace('-', '_')}").ToArrayAsync());
            await identity.SaveChangesAsync();
            await db.SaveChangesAsync();
        }
    }

    private sealed class FileConfirmations(McpOperatorConfirmationService inner, Scenario scenario) : IMcpOperatorConfirmationService
    {
        public Task<McpOperatorConfirmationPlan> CreatePlanAsync(McpOperatorConfirmationPlanRequest request, CancellationToken cancellationToken) => inner.CreatePlanAsync(request, cancellationToken);
        public async Task<McpOperatorConfirmationAdmission> ConfirmAsync(McpOperatorConfirmationRequest request, CancellationToken cancellationToken)
        {
            var result = await inner.ConfirmAsync(request, cancellationToken);
            if (result.IsNewDispatch && scenario.AfterFileConfirmation is { } after) await after();
            return result;
        }
        public Task CompleteAsync(Guid idempotencyId, McpOperatorIdempotencyOutcome outcome, string? resultReference, CancellationToken cancellationToken) => inner.CompleteAsync(idempotencyId, outcome, resultReference, cancellationToken);
    }

    private sealed class FilePresence : IClientPresenceRouter
    {
        public Task<GatewayPresenceSessionStarted> StartSessionAsync(StartGatewayPresenceSession message, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PresenceMessageResult> RecordHeartbeatAsync(RecordGatewayHeartbeat message, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PresenceMessageResult> EndSessionAsync(EndGatewayPresenceSession message, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ClientPresenceRouteStatus> ProbeAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ClientPresenceSnapshot> GetSnapshotAsync(ClientKey client, CancellationToken cancellationToken) => Task.FromResult(new ClientPresenceSnapshot(
            client, ClientPresenceStatus.Online, 1, Guid.NewGuid(), 1, DateTimeOffset.UtcNow, "1.0", ["file-gateway"], null, "test", true));
    }

    private sealed class RecordingFileRegistry : IAgentFileGatewaySessionRegistry
    {
        public List<(string Operation, ClientKey Client, string Path)> Dispatched { get; } = [];
        public List<GatewayFileAccessPolicy> Policies { get; } = [];
        public Task CreateDirectoryAsync(ClientKey client, string path, GatewayFileAccessPolicy accessPolicy, CancellationToken cancellationToken)
        {
            Dispatched.Add(("create_directory", client, path));
            Policies.Add(accessPolicy);
            return Task.CompletedTask;
        }
        public Task DeleteAsync(ClientKey client, string path, GatewayFileAccessPolicy accessPolicy, CancellationToken cancellationToken)
        {
            Dispatched.Add(("delete", client, path));
            Policies.Add(accessPolicy);
            return Task.CompletedTask;
        }
        public AgentFileGatewayRegistration Register(ClientKey client, Guid connectionId, ulong connectionEpoch) => throw new NotSupportedException();
        public Task<IReadOnlyList<GatewayFileEntry>> ListAsync(ClientKey client, string path, int pageSize, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<GatewayFileReadOperation> ReadAsync(ClientKey client, string path, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task WriteAsync(ClientKey client, string path, Stream source, CancellationToken cancellationToken) => throw new NotSupportedException();
        public bool TryAccept(ClientKey client, FileRequestAccepted accepted) => false;
        public bool TryAddPage(ClientKey client, FileListPage page) => false;
        public Task<bool> TryAddReadChunkAsync(ClientKey client, FileTransferChunk chunk, CancellationToken cancellationToken) => Task.FromResult(false);
        public bool TryComplete(ClientKey client, FileRequestCompleted completed) => false;
        public bool TryFail(ClientKey client, FileRequestFailed failed) => false;
    }
}
