using System.Net;
using AwesomeAssertions;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using NetRatel.Application.Operations;
using NetRatel.Shared.Connectivity;
using NetRatel.Web.Components.Pages.Settings;
using NetRatel.Web.Services.OperatorPolicy;
using Xunit;

namespace NetRatel.Web.ComponentTests;

public sealed class OperatorPoliciesTests : AsyncBunitContext
{
    [Fact]
    public void Missing_administration_authority_hides_policy_data_and_mutations()
    {
        var api = new PolicyApi { Authorized = false };
        var page = RenderPage(api);
        page.WaitForAssertion(() => page.FindAll("[data-testid='policy-access-denied']").Should().HaveCount(1));
        page.FindAll("[data-testid='policy-create'], [data-testid='policy-list'], [data-testid='policy-confirm']").Should().BeEmpty();
        api.ListCalls.Should().Be(0);
        api.Confirmed.Should().BeEmpty();
    }

    [Fact]
    public void Exact_directory_edit_preserves_other_bounds_and_keeps_plan_credentials_out_of_markup()
    {
        var api = new PolicyApi();
        var page = RenderPage(api);
        PrepareDirectoryChange(page, api);
        page.Markup.Should().Contain("/tmp/example").And.NotContain(PolicyApi.PlanSecret).And.NotContain(PolicyApi.IdempotencySecret);
        api.Previewed.Should().ContainSingle().Which.Draft!.Constraints.MaxCommandDurationSeconds.Should().Be(60);
        api.Previewed[0].Draft!.Constraints.WorkingDirectories.Should().Equal("/tmp", "/tmp/example");
        Confirm(page);
        page.WaitForAssertion(() => page.Find("[data-testid='policy-success']").TextContent.Should().Contain("version 2"));
        api.Confirmed.Should().ContainSingle();
        api.Confirmed[0].Preview.PlanToken.Should().Be(PolicyApi.PlanSecret);
        api.Confirmed[0].Change.Should().Be(api.Previewed[0]);
        page.Markup.Should().Contain("replace").And.NotContain(PolicyApi.PlanSecret).And.NotContain(PolicyApi.IdempotencySecret);
        api.DetailCalls.Should().Be(2);
        api.AuditCalls.Should().Be(2);
    }

    [Fact]
    public void Stale_confirmation_requires_new_review_without_automatic_resubmission()
    {
        var api = new PolicyApi { ConfirmFailure = new PolicyApiException(HttpStatusCode.Conflict, "confirmation_plan_stale") };
        var page = RenderPage(api);
        PrepareDirectoryChange(page, api);
        Confirm(page);
        page.WaitForAssertion(() => page.Find("[data-testid='policy-error']").TextContent.Should().Contain("new preview"));
        page.FindAll("[data-testid='policy-preview-review']").Should().BeEmpty();
        page.FindAll("[data-testid='policy-editor']").Should().HaveCount(1);
        api.Confirmed.Should().ContainSingle();
        api.Policy.Version.Should().Be(1);
    }

    [Fact]
    public void Interrupted_confirmation_retains_the_exact_plan_for_an_explicit_idempotent_retry()
    {
        var api = new PolicyApi { ConfirmFailure = new HttpRequestException("Interrupted") };
        var page = RenderPage(api);
        PrepareDirectoryChange(page, api);
        Confirm(page);
        page.WaitForAssertion(() => page.Find("[data-testid='policy-error']").TextContent.Should().Contain("same reviewed confirmation"));
        page.FindAll("[data-testid='policy-preview-review']").Should().HaveCount(1);
        api.ConfirmFailure = null;
        page.Find("[data-testid='policy-confirm']").Click();
        page.WaitForAssertion(() => api.Confirmed.Should().HaveCount(2));
        api.Confirmed[1].Should().Be(api.Confirmed[0]);
        page.Markup.Should().NotContain(PolicyApi.PlanSecret).And.NotContain(PolicyApi.IdempotencySecret);
    }

    [Fact]
    public void Accepted_change_with_failed_readback_retains_receipt_and_retries_only_reads()
    {
        var api = new PolicyApi { ReadbackFailure = new HttpRequestException("Read interrupted") };
        var page = RenderPage(api);
        PrepareDirectoryChange(page, api);
        Confirm(page);
        page.WaitForAssertion(() => page.Find("[data-testid='policy-error']").TextContent.Should().Contain("readback is incomplete").And.NotContain("same reviewed confirmation"));
        page.Find("[data-testid='policy-success']").TextContent.Should().Contain("accepted at version 2").And.Contain("synthetic-correlation");
        page.FindAll("[data-testid='policy-preview-review']").Should().BeEmpty();
        api.ReadbackFailure = null;
        page.Find("[data-testid='policy-refresh-accepted']").Click();
        page.WaitForAssertion(() => page.Find("[data-testid='policy-success']").TextContent.Should().Contain("retained change audit match"));
        api.Confirmed.Should().ContainSingle();
        page.FindAll("[data-testid='policy-error']").Should().BeEmpty();
    }

    [Fact]
    public void Newer_readback_is_distinguished_from_the_accepted_version()
    {
        var api = new PolicyApi { AdvanceReadbackVersion = true };
        var page = RenderPage(api);
        PrepareDirectoryChange(page, api);
        Confirm(page);
        page.WaitForAssertion(() => page.Find("[data-testid='policy-readback-warning']").TextContent.Should().Contain("version 3").And.Contain("differs from accepted version 2"));
        page.Find("[data-testid='policy-success']").TextContent.Should().Contain("accepted at version 2").And.NotContain("readback and retained change audit match");
        api.Confirmed.Should().ContainSingle();
    }

    [Fact]
    public void Missing_accepted_change_audit_is_not_reported_as_verified_readback()
    {
        var api = new PolicyApi { OmitAcceptedAudit = true };
        var page = RenderPage(api);
        PrepareDirectoryChange(page, api);
        Confirm(page);
        page.WaitForAssertion(() => page.Find("[data-testid='policy-readback-warning']").TextContent.Should().Contain("audit for accepted version 2 is not present"));
        page.Find("[data-testid='policy-success']").TextContent.Should().Contain("accepted at version 2").And.NotContain("retained change audit match");
        api.Confirmed.Should().ContainSingle();
    }

    [Fact]
    public void Owned_credential_admission_uses_the_exact_target_profile_and_preserves_unverified_runtime_status()
    {
        var api = new PolicyApi();
        var page = RenderPage(api);
        page.WaitForAssertion(() => page.FindAll("[data-testid='policy-list']").Should().HaveCount(1));
        page.Find($"[data-testid='policy-inspect-{api.Policy.PolicyId:D}']").Click();
        page.WaitForAssertion(() => page.FindAll("[data-testid='selected-policy']").Should().HaveCount(1));
        page.Find("[data-testid='policy-admission-credential']").Change("owned-credential-id");
        page.Find("[data-testid='policy-admission-check']").Click();
        page.WaitForAssertion(() => page.Find("[data-testid='policy-admission-result']").TextContent.Should().Contain("conditional_runtime_unverified").And.Contain("not_checked"));
        api.AdmissionTarget.Should().Be(new ReviewedMcpAccessTarget(42, "netratel_job_runs", "start", api.Policy.TargetSelector.AgentId, 1));
        api.AdmissionCredential.Should().Be("owned-credential-id");
        api.Confirmed.Should().BeEmpty();
    }

    private IRenderedComponent<OperatorPolicies> RenderPage(PolicyApi api)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices(options => options.PopoverOptions.CheckForPopoverProvider = false);
        Services.AddSingleton<IMcpOperatorPolicyApiService>(api);
        SetRendererInfo(new RendererInfo("Server", true));
        return Render<OperatorPolicies>();
    }

    private static void PrepareDirectoryChange(IRenderedComponent<OperatorPolicies> page, PolicyApi api)
    {
        page.WaitForAssertion(() => page.FindAll("[data-testid='policy-list']").Should().HaveCount(1));
        page.Find($"[data-testid='policy-inspect-{api.Policy.PolicyId:D}']").Click();
        page.WaitForAssertion(() => page.FindAll("[data-testid='selected-policy']").Should().HaveCount(1));
        page.Find("[data-testid='policy-edit']").Click();
        page.Find("[data-testid='policy-working-directories']").Change("/tmp\n/tmp/example");
        page.Find("[data-testid='policy-preview']").Click();
        page.WaitForAssertion(() => page.FindAll("[data-testid='policy-preview-review']").Should().HaveCount(1));
    }

    private static void Confirm(IRenderedComponent<OperatorPolicies> page)
    {
        page.Find("[data-testid='policy-confirm-checkbox']").Change(true);
        page.Find("[data-testid='policy-confirm']").Click();
    }

    private sealed class PolicyApi : IMcpOperatorPolicyApiService
    {
        public const string PlanSecret = "synthetic-private-plan-credential";
        public const string IdempotencySecret = "synthetic-private-idempotency-credential";
        public bool Authorized { get; init; } = true;
        public Exception? ConfirmFailure { get; set; }
        public Exception? ReadbackFailure { get; set; }
        public bool AdvanceReadbackVersion { get; init; }
        public bool OmitAcceptedAudit { get; init; }
        private PolicyMutationResult? _accepted;
        public int ListCalls { get; private set; }
        public int DetailCalls { get; private set; }
        public int AuditCalls { get; private set; }
        public ReviewedMcpAccessTarget? AdmissionTarget { get; private set; }
        public string? AdmissionCredential { get; private set; }
        public List<PreparedPolicyChange> Previewed { get; } = [];
        public List<(PreparedPolicyChange Change, PolicyMutationPreview Preview)> Confirmed { get; } = [];
        public McpOperatorPolicy Policy { get; private set; } = new(Guid.NewGuid(), "Campaign directory grant", McpOperatorEnvironment.Production,
            McpOperatorPolicyEffect.Allow, 10, new(McpOperatorPrincipalSelectorKind.OAuthSubject, "separate-campaign-credential"),
            new(McpOperatorTargetSelectorKind.ExactAgent, 42, Guid.NewGuid()), McpOperatorTargetClassification.DedicatedQa,
            McpOperatorOperationFamily.JobExecution, null, new(WorkingDirectories: ["/tmp"], MaxCommandDurationSeconds: 60),
            DateTimeOffset.UtcNow, "independent-admin", DateTimeOffset.UtcNow.AddHours(1), null, null, null, 1, "synthetic-change");

        public Task<PolicyAccountAuthority> GetAuthorityAsync(CancellationToken cancellationToken = default) => Authorized
            ? Task.FromResult(new PolicyAccountAuthority("policy-admin", McpOperatorEnvironment.Production))
            : Task.FromException<PolicyAccountAuthority>(new PolicyApiException(HttpStatusCode.Forbidden, "operator_policy_request_failed"));
        public Task<IReadOnlyList<PolicyCredentialOption>> GetOwnedCredentialsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PolicyCredentialOption>>([new("owned-credential-id", "Separate campaign credential", NetRatel.Infrastructure.Identity.IntegrationCredentialPurpose.HttpMcp)]);
        public Task<McpOperatorPolicyPage> ListAsync(McpOperatorEnvironment environment, int? tenantId, CancellationToken cancellationToken = default)
        { ListCalls++; return Task.FromResult(new McpOperatorPolicyPage([Policy])); }
        public Task<McpOperatorPolicy> GetAsync(Guid policyId, CancellationToken cancellationToken = default)
        {
            DetailCalls++;
            return _accepted is not null && ReadbackFailure is { } failure
                ? Task.FromException<McpOperatorPolicy>(failure) : Task.FromResult(Policy);
        }
        public Task<McpOperatorTargetProfile?> GetTargetAsync(int tenantId, Guid agentId, CancellationToken cancellationToken = default) =>
            Task.FromResult<McpOperatorTargetProfile?>(new(agentId, tenantId, McpOperatorTargetClassification.DedicatedQa, [], DateTimeOffset.UtcNow, "independent-admin", 1));
        public Task<IReadOnlyList<McpOperatorPolicyChangeAudit>> GetAuditsAsync(Guid policyId, CancellationToken cancellationToken = default)
        {
            AuditCalls++;
            return Task.FromResult<IReadOnlyList<McpOperatorPolicyChangeAudit>>(_accepted is not null && OmitAcceptedAudit ? [] :
                [new(Guid.NewGuid(), _accepted is null ? "policy_created" : "policy_replaced", policyId, Policy.TargetSelector.AgentId,
                    42, "policy-admin", _accepted?.Policy.Version ?? Policy.Version, DateTimeOffset.UtcNow)]);
        }
        public Task<CredentialMcpAdmission> CheckAdmissionAsync(string credentialId, ReviewedMcpAccessTarget target, CancellationToken cancellationToken = default)
        {
            AdmissionCredential = credentialId;
            AdmissionTarget = target;
            var result = new ReviewedMcpAccessAdmission(target, "netratel.mcp.execute", ["Operator"], true, true, null,
                "target_runtime_not_checked", [Policy.PolicyId]) { OwnerPermissionGranted = true, PolicyAdmissionStatus = "conditional_runtime_unverified" };
            return Task.FromResult(new CredentialMcpAdmission(credentialId, DateTimeOffset.UtcNow, "active", "current_credential_valid",
                "https://mcp.example.test/mcp", "prod", "Production", DateTimeOffset.UtcNow.AddHours(1), [], [], [result],
                ["Agent runtime readiness and operation capability remain unverified."], "Invoke the exact permitted operation."));
        }
        public Task<PolicyMutationPreview> PreviewAsync(PreparedPolicyChange change, CancellationToken cancellationToken = default)
        {
            Previewed.Add(change);
            return Task.FromResult(new PolicyMutationPreview(PlanSecret, IdempotencySecret, DateTimeOffset.UtcNow.AddMinutes(5),
                McpOperatorConfirmationClass.PolicyAdministration, 42, Policy.TargetSelector.AgentId, "synthetic-correlation"));
        }
        public Task<PolicyMutationResult> ConfirmAsync(PreparedPolicyChange change, PolicyMutationPreview preview, CancellationToken cancellationToken = default)
        {
            Confirmed.Add((change, preview));
            if (ConfirmFailure is { } failure) return Task.FromException<PolicyMutationResult>(failure);
            Policy = Policy with { Constraints = change.Draft!.Constraints, Version = Policy.Version + 1 };
            _accepted = new PolicyMutationResult(Policy, false, "synthetic-correlation");
            if (AdvanceReadbackVersion) Policy = Policy with { Version = Policy.Version + 1 };
            return Task.FromResult(_accepted);
        }
    }
}
