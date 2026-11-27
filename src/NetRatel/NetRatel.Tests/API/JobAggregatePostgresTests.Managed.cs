using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Claims;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;
using NetRatel.API.Services.Jobs;
using NetRatel.API.Services.Orchestration;
using NetRatel.API.Services.Secrets;
using NetRatel.Application.Events;
using NetRatel.Application.Jobs;
using NetRatel.Application.Presence;
using NetRatel.Application.Requests;
using NetRatel.Application.Scripts;
using NetRatel.Application.Secrets;
using NetRatel.Infrastructure.Events;
using NetRatel.Infrastructure.Identity;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Flows;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.ServiceIdentity;
using NetRatel.Infrastructure.Secrets;
using NetRatel.Infrastructure.Services;
using NetRatel.Shared.Contracts.Jobs;
using NetRatel.Shared.Contracts.Secrets;
using NetRatel.Shared.Contracts.Flows;
using NetRatel.Shared.ServiceIdentity;
using NetRatel.Shared.SystemPairing;
using NetRatel.Tests.Infrastructure;
using Xunit;

namespace NetRatel.Tests.API;

public sealed partial class JobAggregatePostgresTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Issued_pairing_token_admits_one_durable_parent_and_callbacks_wait_for_the_complete_target_plan(bool ha)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60)); var ct = deadline.Token;
        var connection = await postgres.CreateDatabaseAsync(ct);
        await using var h = await AggregateHarness.CreateAsync(connection, 2, ct, JobNodeFailurePolicy.ContinueRemaining);
        if (ha) await UseHaGroupAsync(h, 5, ct);
        var directory = Path.Combine(Path.GetTempPath(), "managed-aggregate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var key = ECDsa.Create();
        try
        {
            await using var app = await OrchestrationManagedServiceHttpTests.StartAsync(connection, directory, key, services =>
            {
                services.AddSingleton<TimeProvider>(h.Clock);
                services.AddSingleton<IClientConnectionEpochStore>(h.Services.GetRequiredService<IClientConnectionEpochStore>());
                services.AddOptions<JobAggregateOptions>(); services.AddNetRatelJobObservationPersistence();
                services.AddSingleton<IJobAggregateWakeup, NoWakeup>();
                services.AddScoped<IScriptService, NoScripts>(); services.AddScoped<IJobExecutionSnapshotCapture, PinnedJobExecutionSnapshotCapture>();
                services.AddScoped<IJobNodeDirectory, JobNodeDirectory>(); services.AddScoped<IJobTargetResolver, JobTargetResolver>();
                services.AddScoped<HaGroupWorkerSelector>(); services.AddScoped<JobAggregateOrchestrator>();
                services.AddScoped<IEventRecorder, OutboxEventRecorder>();
                services.AddScoped<SecretService>();
                services.AddScoped<ISecretProvider, LocalSecretProvider>();
                services.AddSingleton(new SecretProviderNetworkPolicy(new ConfigurationBuilder().Build(), new FixtureEnvironment()));
                services.AddScoped<ITenantSecretService, TenantSecretService>();
                services.AddScoped<ExecutionAccountService>();
                services.AddScoped<IAkkaJobAuthorityService>(provider => new AkkaJobAuthorityService(
                    provider.GetRequiredService<IJobDefinitionService>(), provider.GetRequiredService<IJobRunService>(),
                    new JobTaskBridge(provider.GetRequiredService<IScriptService>(), NullLogger<JobTaskBridge>.Instance), h.Gateway,
                    h.NativeRouter, new RecordingRealtimeFanoutSink(), provider.GetRequiredService<JobAuthorityIdGenerator>(), new FixtureEnvironment(),
                    managedGuard: provider.GetRequiredService<ManagedOrchestrationInvocationGuard>(),
                    db: provider.GetRequiredService<OrchestratorDbContext>(), clock: h.Clock,
                    snapshotCapture: provider.GetRequiredService<IJobExecutionSnapshotCapture>(), aggregates: provider.GetRequiredService<JobAggregateOrchestrator>()));
            }, ha ? null : builder => ConfigureRunAsReview(builder, key));
            h.UseServices((ServiceProvider)app.Services);
            CreatedServiceClient credential;
            await using (var scope = app.Services.CreateAsyncScope())
            {
                await scope.ServiceProvider.GetRequiredService<NetRatelIdentityDbContext>().Database.MigrateAsync(ct);
                await PairingBusinessAuthorityFixture.SeedAdministratorAsync(scope.ServiceProvider.GetRequiredService<NetRatelIdentityDbContext>(), ct: ct);
                if (!ha)
                    await SeedRunAsReviewIdentitiesAsync(scope.ServiceProvider.GetRequiredService<NetRatelIdentityDbContext>(), ct);
                credential = await PairingBusinessAuthorityFixture.CreateAsync(scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>(),
                    scope.ServiceProvider.GetRequiredService<IServicePrincipalRegistry>(), 7, ServiceIdentityScopes.Business, ct: ct,
                    protection: scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>());
            }
            var client = app.GetTestClient();
            var token = await OrchestrationManagedServiceHttpTests.TokenAsync(client, credential, string.Join(' ', ServiceIdentityScopes.Business));
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            client.DefaultRequestHeaders.Add("X-NetRatel-Automation-Contract", "2");
            var capabilities = await client.GetFromJsonAsync<NetRatelAutomationCapabilities>("/internal/catalog/capabilities", ct);
            capabilities!.AggregateExecution.Should().BeTrue();
            var choices = await client.GetFromJsonAsync<NetRatelAutomationTargetChoice[]>("/internal/catalog/target-choices", ct);
            choices!.Count(choice => choice.Kind == "explicitNodes").Should().Be(5);
            // A real canonical job created after pairing is immediately in the refreshed effective grant.
            var authoring = new CreateNetRatelRequestDefinitionDto
            {
                Name = "Created through paired authoring", TenantId = 7, Target = JobTargetDefinition.FromLegacy(h.AgentIds[0]),
                Runner = "bash", Command = "printf '%s' @option.message@",
                Inputs = [new() { Key = "message", Label = "Message to print", Order = 3, Type = "text", Required = true }]
            };
            using var create = await client.PostAsJsonAsync("/internal/catalog/request-definitions", authoring, ct);
            create.EnsureSuccessStatusCode();
            var authored = (await create.Content.ReadFromJsonAsync<NetRatelCatalogRequestDefinitionDto>(ct))!;
            authored.Inputs.Should().ContainSingle(input => input.Label == "Message to print" && input.Order == 3 && input.Required);
            (await client.PostAsJsonAsync("/internal/catalog/request-definitions", authoring, ct)).StatusCode.Should().Be(HttpStatusCode.OK);
            authoring.Inputs[0].Required = false;
            (await client.PostAsJsonAsync("/internal/catalog/request-definitions", authoring, ct)).StatusCode.Should().Be(HttpStatusCode.Conflict);
            await using (var independent = app.Services.CreateAsyncScope())
            {
                var details = await independent.ServiceProvider.GetRequiredService<IJobDefinitionService>().GetDetailsAsync(ulong.Parse(authored.RequestDefinitionId), ct);
                details!.Steps.Should().ContainSingle(step => step.Command == "printf '%s' @option.message@");
                details.Job.Target!.AgentIds.Should().Equal(h.AgentIds[0]);
            }
            var catalogue = (await client.GetFromJsonAsync<NetRatelCatalogRequestDefinitionDto[]>("/internal/catalog/request-definitions", ct))!;
            catalogue.Should().Contain(item => item.RequestDefinitionId == "41" && item.ClientIdentity == "" && item.Target != null);
            catalogue.Should().Contain(item => item.RequestDefinitionId == authored.RequestDefinitionId);
            using var sync = await client.PostAsJsonAsync($"/internal/catalog/request-definitions/{authored.RequestDefinitionId}/inputs/sync",
                new SyncNetRatelRequestDefinitionInputsDto { Inputs = [new() { Key = "message", Label = "Message", Type = "text", DefaultValue = "synthetic" }] }, ct);
            sync.EnsureSuccessStatusCode();
            var syncedDefinition = (await sync.Content.ReadFromJsonAsync<NetRatelCatalogRequestDefinitionDto>(ct))!;
            syncedDefinition.Target!.Definition!.AgentIds.Should().Equal(h.AgentIds[0]);
            var savedInputs = syncedDefinition.Inputs;
            var reviewPath = $"/internal/catalog/request-definitions/{authored.RequestDefinitionId}/inputs/review-sync";
            var reviewed = new SyncNetRatelRequestDefinitionInputsDto
            {
                ExpectedInputs = authored.Inputs,
                Inputs = [new() { Key = "message", Label = "Reviewed label", Type = "text", DefaultValue = "synthetic" }]
            };
            using var staleReview = await client.PostAsJsonAsync(reviewPath, reviewed, ct);
            staleReview.StatusCode.Should().Be(HttpStatusCode.Conflict);
            reviewed.ExpectedInputs = savedInputs;
            using var freshReview = await client.PostAsJsonAsync(reviewPath, reviewed, ct);
            freshReview.EnsureSuccessStatusCode();
            (await freshReview.Content.ReadFromJsonAsync<NetRatelCatalogRequestDefinitionDto>(ct))!.Inputs
                .Should().ContainSingle(input => input.Label == "Reviewed label" && input.DefaultValue == "synthetic");
            using var replayReview = await client.PostAsJsonAsync(reviewPath, reviewed, ct);
            replayReview.StatusCode.Should().Be(HttpStatusCode.Conflict);
            NetRatelIngestRequest Ingest(string job = "41", string correlation = "attempt-one", string payload = "{\"input\":{\"message\":\"synthetic\"}}") => new()
            { RequestId = "synthetic-request", RequestTaskId = "synthetic-task", CorrelationId = correlation,
                NetRatelRequestDefinitionId = job, NetRatelJobDefinitionId = job, PayloadJson = payload };
            using var accepted = await client.PostAsJsonAsync("/internal/ingest", Ingest(), ct);
            accepted.EnsureSuccessStatusCode();
            var receipt = (await accepted.Content.ReadFromJsonAsync<NetRatelIngestResponse>(ct))!;
            var parent = ulong.Parse(receipt.ExecutionId!);
            (await h.ReadAsync(parent, ct)).TargetCount.Should().Be(ha ? 1 : 5);
            h.Gateway.Dispatches.Should().BeEmpty();
            using var duplicate = await client.PostAsJsonAsync("/internal/ingest", Ingest(), ct);
            duplicate.EnsureSuccessStatusCode();
            (await duplicate.Content.ReadFromJsonAsync<NetRatelIngestResponse>(ct))!.ExecutionId.Should().Be(receipt.ExecutionId);
            (await client.PostAsJsonAsync("/internal/ingest", Ingest(payload: "{\"changed\":true}"), ct)).StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await client.PostAsJsonAsync("/internal/ingest", Ingest(correlation: "attempt-too-early"), ct)).StatusCode.Should().Be(HttpStatusCode.Conflict);
            await using (var descriptiveEdit = app.Services.CreateAsyncScope())
            {
                var db = descriptiveEdit.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
                (await db.Jobs.SingleAsync(row => row.Id == 41, ct)).Description = "Description updated while admitted work retains its protected source";
                await db.SaveChangesAsync(ct);
            }
            await h.FillAsync(parent, ct);
            var first = h.Gateway.Active.Keys.Min();
            await h.SucceedNodeAsync(first, ct); await h.FillAsync(parent, ct);
            if (!ha)
            {
                (await h.ReadAsync(parent, ct)).Status.Should().Be(JobRunState.Running);
                await using var proof = app.Services.CreateAsyncScope();
                var request = await proof.ServiceProvider.GetRequiredService<IRequestService>().GetAsync(int.Parse(receipt.RequestId!), ct);
                request!.Status.Should().NotBe("Completed");
            }
            while (h.Gateway.Active.Count > 0)
            { await h.SucceedNodeAsync(h.Gateway.Active.Keys.Min(), ct); await h.FillAsync(parent, ct); }
            (await h.ReadAsync(parent, ct)).Status.Should().Be(JobRunState.Succeeded);
            h.Gateway.Dispatches.Select(item => item.Client.AgentId).Distinct().Count().Should().Be(ha ? 1 : 5);
            await using (var proof = app.Services.CreateAsyncScope())
            {
                var db = proof.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
                var binding = await db.Set<ManagedOrchestrationRequestBinding>().AsNoTracking().SingleAsync(ct);
                binding.AgentId.Should().BeNull(); binding.TargetPlanDigest.Should().NotBeNull();
                var request = await ManagedOrchestrationRecovery.RecoverAsync(db, proof.ServiceProvider.GetRequiredService<IRequestService>(),
                    proof.ServiceProvider.GetRequiredService<IJobRunService>(), binding, ct);
                var details = (await proof.ServiceProvider.GetRequiredService<IJobRunService>().GetDetailsAsync(parent, ct))!;
                OrchestrationCallbackProjection.Matches(request!, details.Run, binding).Should().BeTrue();
                (await OrchestrationCallbackProjection.TerminalReadyAsync(db, details, ct)).Should().BeTrue();
                var callback = await OrchestrationCallbackProjection.BuildAsync(db, proof.ServiceProvider.GetRequiredService<IJobRunService>(), request!, details, binding, ct);
                callback.Status.Should().Be("succeeded"); callback.ResultJson.Should().Contain("parentRunId").And.Contain("childResults");
                (await OrchestrationCallbackProjection.ProjectRequestStatusAsync(db, proof.ServiceProvider.GetRequiredService<IRequestService>(),
                    binding.RequestId, details.Run.Status, callback.Message, callback.ResultJson, DateTimeOffset.UtcNow, ct)).Should().BeTrue();
                var events = await db.OutboxMessages.AsNoTracking().Where(row => row.Type == FlowExecutionEventLimits.OutboxType).ToArrayAsync(ct);
                var requestFact = events.Select(row => JsonSerializer.Deserialize(row.PayloadJson, FlowExecutionEventJsonContext.Default.FlowExecutionEventDto)!)
                    .Single(value => value.SourceKind == FlowExecutionSourceKind.Request);
                requestFact.Aggregate!.TargetCount.Should().Be(ha ? 1 : 5);
                requestFact.Aggregate.Counts.Succeeded.Should().Be(ha ? 1 : 5);
                requestFact.Target.AgentId.Should().BeNull(); requestFact.Replay!.TargetPlanDigest.Should().Be(binding.TargetPlanDigest);
                FlowExecutionEventValidator.Validate(requestFact).Valid.Should().BeTrue();
                (await db.Set<JobAggregateControlRecord>().SingleAsync(row => row.ParentRunId == checked((long)parent), ct)).TerminalResultHash = "invalid";
                await db.SaveChangesAsync(ct);
                (await OrchestrationCallbackProjection.TerminalReadyAsync(db, details, ct)).Should().BeFalse();
                // Restore the original committed hash to verify a legitimate subsequent attempt.
                (await db.Set<JobAggregateControlRecord>().SingleAsync(row => row.ParentRunId == checked((long)parent), ct)).TerminalResultHash =
                    JobRunProjectionHash.ComputeAggregate(details, await JobRunNodeProjection.ReadAsync(db, checked((long)parent), ct));
                await db.SaveChangesAsync(ct);
            }
            using var next = await client.PostAsJsonAsync("/internal/ingest", Ingest(authored.RequestDefinitionId, "attempt-two"), ct);
            next.EnsureSuccessStatusCode();
            var nextId = ulong.Parse((await next.Content.ReadFromJsonAsync<NetRatelIngestResponse>(ct))!.ExecutionId!);
            nextId.Should().NotBe(parent); await h.FillAsync(nextId, ct);
            await h.SucceedNodeAsync(h.Gateway.Active.Keys.Single(), ct); await h.FillAsync(nextId, ct);
            (await h.ReadAsync(nextId, ct)).Status.Should().Be(JobRunState.Succeeded);
            using var runtimeCreate = await client.PostAsJsonAsync("/internal/catalog/request-definitions", new CreateNetRatelRequestDefinitionDto
            {
                Name = "Declared runtime workers", TenantId = 7,
                Target = new() { Source = JobTargetSource.RuntimeInput, RuntimeInput = new(JobRuntimeTargetKind.ClientIds, "workers") },
                Runner = "bash", Command = "printf runtime"
            }, ct);
            runtimeCreate.EnsureSuccessStatusCode();
            var runtimeJob = (await runtimeCreate.Content.ReadFromJsonAsync<NetRatelCatalogRequestDefinitionDto>(ct))!;
            runtimeJob.Target!.Readiness.Should().Be("requires-input");
            runtimeJob.Inputs.Should().Contain(input => input.Key == "workers" && input.Required);
            using var runtimeAccepted = await client.PostAsJsonAsync("/internal/ingest", Ingest(runtimeJob.RequestDefinitionId, "runtime-attempt",
                JsonSerializer.Serialize(new { input = new { workers = new[] { h.AgentIds[0].ToString("D") } } })), ct);
            runtimeAccepted.EnsureSuccessStatusCode();
            var runtimeId = ulong.Parse((await runtimeAccepted.Content.ReadFromJsonAsync<NetRatelIngestResponse>(ct))!.ExecutionId!);
            await h.FillAsync(runtimeId, ct); await h.SucceedNodeAsync(h.Gateway.Active.Keys.Single(), ct); await h.FillAsync(runtimeId, ct);
            (await h.ReadAsync(runtimeId, ct)).Status.Should().Be(JobRunState.Succeeded);
            if (!ha)
                await VerifyPairedRunAsAsync(h, client, app.GetTestClient(), key, credential, ulong.Parse(authored.RequestDefinitionId), ct);
            using var drifted = await client.PostAsJsonAsync("/internal/ingest", Ingest(correlation: "drift-attempt"), ct);
            drifted.EnsureSuccessStatusCode();
            var driftId = ulong.Parse((await drifted.Content.ReadFromJsonAsync<NetRatelIngestResponse>(ct))!.ExecutionId!);
            var beforeDrift = h.Gateway.Dispatches.Count;
            await using (var edit = app.Services.CreateAsyncScope())
            {
                var db = edit.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
                if (ha) (await db.Set<NetRatel.Infrastructure.SystemPairing.PairingConnectionRecord>().SingleAsync(ct)).Active = false;
                else (await db.Jobs.SingleAsync(row => row.Id == 41, ct)).TargetJson = JsonSerializer.Serialize(new JobTargetDefinition { Source = JobTargetSource.ExplicitNodes, AgentIds = h.AgentIds.Take(4).ToArray() });
                await db.SaveChangesAsync(ct);
            }
            await h.FillAsync(driftId, ct);
            h.Gateway.Dispatches.Count.Should().Be(beforeDrift);
            (await h.ReadAsync(driftId, ct)).Status.Should().Be(JobRunState.Failed);
        }
        finally { Directory.Delete(directory, true); }
    }

    private const string ReviewerIssuer = "https://identity.example.test";
    private const string ReviewerGroup = "synthetic-oidc-administrators";
    private const string OidcReviewer = "synthetic-oidc-reviewer";
    private const string TenantReviewer = "synthetic-tenant-reviewer";
    private const string OtherTenantReviewer = "synthetic-other-tenant-reviewer";

    private static void ConfigureRunAsReview(WebApplicationBuilder builder, ECDsa key)
    {
        builder.Configuration["Authorization:Oidc:AdminGroupId"] = ReviewerGroup;
        builder.Services.AddScoped<IEffectiveAccessService, EffectiveAccessService>();
        var authentication = builder.Services.AddAuthentication();
        foreach (var scheme in new[] { "Oidc", "Local" })
        {
            authentication.AddJwtBearer("Review" + scheme, options =>
            {
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new()
                {
                    AuthenticationType = scheme, ValidIssuer = ReviewerIssuer, ValidAudience = "review-" + scheme,
                    IssuerSigningKey = new ECDsaSecurityKey(key), ValidateLifetime = true
                };
            });
        }
        builder.Services.AddAuthorization(options => options.AddPolicy("InteractiveAccount", policy =>
            policy.AddAuthenticationSchemes("ReviewOidc", "ReviewLocal").RequireAuthenticatedUser()));
    }

    private static async Task SeedRunAsReviewIdentitiesAsync(NetRatelIdentityDbContext identity, CancellationToken ct)
    {
        identity.Users.Add(new LocalUser { Id = "local-owner", UserName = "local-owner", IsEnabled = true,
            PrincipalId = PairingBusinessAuthorityFixture.Administrator, IsInstanceAdministrator = true });
        identity.ApplicationPrincipals.AddRange(new ApplicationPrincipal { Id = OidcReviewer, ExternalIssuer = ReviewerIssuer, ExternalSubject = OidcReviewer },
            new ApplicationPrincipal { Id = TenantReviewer }, new ApplicationPrincipal { Id = OtherTenantReviewer });
        var tenantRole = new AccessRole { Name = "Fixture tenant integration administrator" };
        foreach (var permission in new[] { NetRatelPermissions.IntegrationManagement, NetRatelPermissions.JobManagement,
                     NetRatelPermissions.TenantAdministration, NetRatelPermissions.SecretUse, NetRatelPermissions.RunAs })
            tenantRole.Permissions.Add(new() { Permission = permission });
        identity.AccessRoles.Add(tenantRole);
        identity.PrincipalRoleAssignments.AddRange(new PrincipalRoleAssignment { PrincipalId = TenantReviewer, TenantId = 7, RoleId = tenantRole.Id },
            new PrincipalRoleAssignment { PrincipalId = OtherTenantReviewer, TenantId = 8, RoleId = tenantRole.Id });
        await identity.SaveChangesAsync(ct);
    }

    private static void SignInReviewer(HttpClient browser, ECDsa key, string id, string? subject = null)
    {
        var claims = new List<Claim> { new("netratel_principal_id", id), new("sub", subject ?? id) };
        if (id == OidcReviewer) claims.Add(new("groups", ReviewerGroup));
        else claims.Add(new("auth_mode", "local"));
        var audience = id == OidcReviewer ? "review-Oidc" : "review-Local";
        var token = new JwtSecurityToken(ReviewerIssuer, audience, claims, expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: new SigningCredentials(new ECDsaSecurityKey(key), SecurityAlgorithms.EcdsaSha256));
        browser.DefaultRequestHeaders.Authorization = new("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
    }

    private static async Task VerifyPairedRunAsAsync(AggregateHarness h, HttpClient client, HttpClient browser, ECDsa key,
        CreatedServiceClient credential, ulong jobId, CancellationToken ct)
    {
        var mapping = Guid.Parse(credential.Principal.LinkId!);
        browser.DefaultRequestHeaders.Add("X-NetRatel-Account-Request", "1");
        var path = $"{PairingProtocol.AdminRoute}/mappings/{mapping:D}/jobs/{jobId}/run-as";
        var readinessPath = $"/internal/catalog/jobs/{jobId}/execution-readiness";
        var agentId = h.AgentIds[0];
        CredentialProfile fleet;
        CredentialProfile job;
        const string fixtureSecret = "synthetic-Windows-password-for-test-only";
        await using (var setup = h.Services.CreateAsyncScope())
        {
            var vault = setup.ServiceProvider.GetRequiredService<ITenantSecretService>();
            var local = (await vault.ListConnectionsAsync(7, false, ct)).First();
            var secret = await vault.WriteAsync(7, local.Id, new() { Name = "Fixture password", Value = fixtureSecret }, ct);
            var password = new SecretReference(1, local.Id, secret.Locator, null, SecretVersionMode.Pinned, secret.Version);
            fleet = await vault.SaveProfileAsync(7, null, new("Fleet fixture", ExecutionIdentityMode.WindowsPassword,
                "fleet-fixture", null, null, password, new([agentId], null, true, false), null), ct);
            job = await vault.SaveProfileAsync(7, null, new("Job fixture", ExecutionIdentityMode.WindowsPassword,
                "job-fixture", null, null, password, new([agentId], null, true, false), null), ct);
            var db = setup.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            var agent = await db.Agents.SingleAsync(row => row.Id == agentId, ct);
            agent.DeviceInfoJson = "{\"os\":\"Windows\"}";
            await db.SaveChangesAsync(ct);
        }

        async Task SelectAccountAsync(Guid? fleetId, Guid? jobProfileId)
        {
            await using var scope = h.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            (await db.Agents.SingleAsync(row => row.Id == agentId, ct)).DefaultJobCredentialProfileId = fleetId;
            var definition = await db.Jobs.SingleAsync(row => row.Id == (long)jobId, ct);
            definition.ExecutionPolicyJson = jobProfileId is { } profile
                ? JsonSerializer.Serialize(new JobExecutionPolicy { RunAs = new(ExecutionIdentityMode.WindowsPassword, profile) },
                    JobTargetJsonContext.Default.JobExecutionPolicy)
                : null;
            await db.SaveChangesAsync(ct);
        }

        Task<HttpResponseMessage> SubmitAsync(string attempt) => client.PostAsJsonAsync("/internal/ingest", new NetRatelIngestRequest
        {
            RequestId = $"request-{attempt}", RequestTaskId = $"task-{attempt}", CorrelationId = attempt,
            NetRatelJobDefinitionId = jobId.ToString(), PayloadJson = "{\"input\":{\"message\":\"synthetic\"}}",
            ExpectedRuntimeSeconds = 120, GraceSeconds = 30, HardTimeoutSeconds = 150
        }, ct);

        async Task VerifyDenialAsync(string attempt, string reason)
        {
            using var denied = await SubmitAsync(attempt);
            denied.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
            var body = await denied.Content.ReadFromJsonAsync<JsonElement>(ct);
            body.GetProperty("code").GetString().Should().Be("run_as_denied");
            var diagnostic = body.GetProperty("diagnostic");
            diagnostic.GetProperty("executionCreated").GetBoolean().Should().BeFalse();
            diagnostic.GetProperty("reason").GetString().Should().Be(reason);
            await using var scope = h.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            Assert.False(await db.Set<ManagedOrchestrationRequestBinding>().AnyAsync(row => row.CorrelationId == attempt, ct));
            var rejection = await db.OutboxMessages.SingleAsync(row => row.CorrelationId == attempt &&
                row.Type == "Orchestration.PairedAdmissionRejected", ct);
            rejection.PayloadJson.Should().Contain(reason).And.NotContain(fixtureSecret).And.NotContain("fleet-fixture");
        }

        async Task<Guid> ReviewAndApproveAsync(string reviewer, CredentialProfile profile, string reason)
        {
            SignInReviewer(browser, key, reviewer);
            var reviewed = (await browser.GetFromJsonAsync<PairedJobRunAsReview>(path, ct))!;
            reviewed.Readiness.Reason.Should().Be(reason);
            reviewed.Targets.Should().ContainSingle(target => target.AgentId == agentId && target.ProfileId == profile.Id
                && target.ProfileName == profile.DisplayName && target.Mode == ExecutionIdentityMode.WindowsPassword);
            using var approved = await browser.PutAsJsonAsync(path, new ApprovePairedJobRunAsRequest(reviewed.ScopeDigest!), ct);
            approved.EnsureSuccessStatusCode();
            var approval = (await approved.Content.ReadFromJsonAsync<PairedJobRunAsReview>(ct))!;
            approval.Readiness.Ready.Should().BeTrue();
            var raw = await client.GetStringAsync(readinessPath, ct);
            raw.Should().Contain("run_as_approved").And.NotContain(profile.DisplayName).And.NotContain(profile.Id.ToString("D"));
            return approval.ApprovalId!.Value;
        }

        async Task FinishAsync(string attempt, Guid? expectedProfile)
        {
            using var accepted = await SubmitAsync(attempt);
            accepted.EnsureSuccessStatusCode();
            var parent = ulong.Parse((await accepted.Content.ReadFromJsonAsync<NetRatelIngestResponse>(ct))!.ExecutionId!);
            var accountCount = h.Gateway.ExecutionAccounts.Count;
            await h.FillAsync(parent, ct);
            h.Gateway.ExecutionAccounts.Skip(accountCount).Should().Equal(expectedProfile is { } profile ? [(agentId, profile)] : []);
            await h.SucceedNodeAsync(h.Gateway.Active.Keys.Single(), ct);
            await h.FillAsync(parent, ct);
            (await h.ReadAsync(parent, ct)).Status.Should().Be(JobRunState.Succeeded);
        }

        (await client.GetFromJsonAsync<PairedJobExecutionReadiness>(readinessPath, ct))!.Code.Should().Be("agent_account");
        await FinishAsync("run-as-agent-account", null);
        await SelectAccountAsync(fleet.Id, null);
        await VerifyDenialAsync("run-as-before-approval", PairedRunAsReasons.ApprovalRequired);
        (await client.GetFromJsonAsync<PairedJobExecutionReadiness>(readinessPath, ct))!.Reason.Should().Be(PairedRunAsReasons.ApprovalRequired);

        SignInReviewer(browser, key, OtherTenantReviewer);
        using var wrongTenant = await browser.GetAsync(path, ct);
        wrongTenant.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        SignInReviewer(browser, key, OidcReviewer, "unbound-subject");
        using var wrongIdentity = await browser.PutAsJsonAsync(path, new ApprovePairedJobRunAsRequest("invalid"), ct);
        wrongIdentity.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        h.Gateway.ProtectedParametersSupported = true;
        await ReviewAndApproveAsync(TenantReviewer, fleet, PairedRunAsReasons.ApprovalRequired);
        await FinishAsync("run-as-inherited-fleet", fleet.Id);
        await SelectAccountAsync(null, job.Id);
        await VerifyDenialAsync("run-as-stale-scope", PairedRunAsReasons.ScopeChanged);
        await ReviewAndApproveAsync(PairingBusinessAuthorityFixture.Administrator, job, PairedRunAsReasons.ScopeChanged);
        await FinishAsync("run-as-explicit", job.Id);

        // Adding a Fleet default leaves the effective explicit override and reviewed digest unchanged.
        await SelectAccountAsync(fleet.Id, job.Id);
        await FinishAsync("run-as-explicit-precedence", job.Id);
        await SelectAccountAsync(fleet.Id, null);
        var approvalId = await ReviewAndApproveAsync(OidcReviewer, fleet, PairedRunAsReasons.ScopeChanged);
        await FinishAsync("run-as-oidc-approved", fleet.Id);

        using var pending = await SubmitAsync("run-as-revoked-before-dispatch");
        pending.EnsureSuccessStatusCode();
        var pendingParent = ulong.Parse((await pending.Content.ReadFromJsonAsync<NetRatelIngestResponse>(ct))!.ExecutionId!);
        using var revoke = await browser.DeleteAsync($"{path}/{approvalId:D}", ct);
        revoke.StatusCode.Should().Be(HttpStatusCode.NoContent);
        await h.FillAsync(pendingParent, ct);
        (await h.ReadAsync(pendingParent, ct)).Status.Should().Be(JobRunState.Failed);
        h.Gateway.ExecutionAccounts.Count.Should().Be(4);
        await VerifyDenialAsync("run-as-revoked", PairedRunAsReasons.ApprovalRevoked);
        await using (var owner = h.Services.CreateAsyncScope())
        {
            var identity = owner.ServiceProvider.GetRequiredService<NetRatelIdentityDbContext>();
            var user = await identity.Users.SingleAsync(row => row.PrincipalId == PairingBusinessAuthorityFixture.Administrator, ct);
            user.IsEnabled = false;
            await identity.SaveChangesAsync(ct);
            using var unavailable = await browser.GetAsync(path, ct);
            unavailable.StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await unavailable.Content.ReadAsStringAsync(ct)).Should().Contain(PairedRunAsReasons.ConnectionUnavailable);
            user.IsEnabled = true;
            await identity.SaveChangesAsync(ct);
        }
        await SelectAccountAsync(null, null);
    }
}
