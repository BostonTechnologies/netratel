using System.Net;
using AwesomeAssertions;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Extensions;
using MudBlazor.Services;
using NetRatel.Application.Operations;
using NetRatel.Shared.Connectivity;
using NetRatel.Web.Components.Pages.Settings;
using NetRatel.Web.Services.OperatorPolicy;
using Xunit;

namespace NetRatel.Web.ComponentTests;

public sealed class OperatorPoliciesTests : AsyncBunitContext
{
    private IRenderedComponent<MudDialogProvider> _dialogs = null!;
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
        (page.Markup + _dialogs.Markup).Should().Contain("/tmp/example").And.NotContain(PolicyApi.PlanSecret).And.NotContain(PolicyApi.IdempotencySecret);
        api.Previewed.Should().ContainSingle().Which.Draft!.Constraints.MaxCommandDurationSeconds.Should().Be(60);
        api.Previewed[0].Draft!.Constraints.WorkingDirectories.Should().Equal("/tmp", "/tmp/example");
        Confirm(page);
        page.WaitForAssertion(() => _dialogs.Find("[data-testid='policy-success']").TextContent.Should().Contain("version 2"));
        api.Confirmed.Should().ContainSingle();
        api.Confirmed[0].Preview.PlanToken.Should().Be(PolicyApi.PlanSecret);
        api.Confirmed[0].Change.Should().Be(api.Previewed[0]);
        (page.Markup + _dialogs.Markup).Should().Contain("replace").And.NotContain(PolicyApi.PlanSecret).And.NotContain(PolicyApi.IdempotencySecret);
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
        page.WaitForAssertion(() => _dialogs.Find("[data-testid='policy-error']").TextContent.Should().Contain("new preview"));
        _dialogs.FindAll("[data-testid='policy-preview-review']").Should().BeEmpty();
        _dialogs.FindAll("[data-testid='policy-editor']").Should().HaveCount(1);
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
        page.WaitForAssertion(() => _dialogs.Find("[data-testid='policy-error']").TextContent.Should().Contain("same reviewed confirmation"));
        _dialogs.FindAll("[data-testid='policy-preview-review']").Should().HaveCount(1);
        api.ConfirmFailure = null;
        _dialogs.Find("[data-testid='policy-confirm']").Click();
        page.WaitForAssertion(() => api.Confirmed.Should().HaveCount(2));
        api.Confirmed[1].Should().Be(api.Confirmed[0]);
        (page.Markup + _dialogs.Markup).Should().NotContain(PolicyApi.PlanSecret).And.NotContain(PolicyApi.IdempotencySecret);
    }

    [Fact]
    public void Accepted_change_with_failed_readback_retains_receipt_and_retries_only_reads()
    {
        var api = new PolicyApi { ReadbackFailure = new HttpRequestException("Read interrupted") };
        var page = RenderPage(api);
        PrepareDirectoryChange(page, api);
        Confirm(page);
        page.WaitForAssertion(() => _dialogs.Find("[data-testid='policy-error']").TextContent.Should().Contain("readback is incomplete").And.NotContain("same reviewed confirmation"));
        _dialogs.Find("[data-testid='policy-success']").TextContent.Should().Contain("accepted at version 2").And.Contain("synthetic-correlation");
        _dialogs.FindAll("[data-testid='policy-preview-review']").Should().BeEmpty();
        api.ReadbackFailure = null;
        _dialogs.Find("[data-testid='policy-refresh-accepted']").Click();
        page.WaitForAssertion(() => _dialogs.Find("[data-testid='policy-success']").TextContent.Should().Contain("retained change audit match"));
        api.Confirmed.Should().ContainSingle();
        _dialogs.FindAll("[data-testid='policy-error']").Should().BeEmpty();
    }

    [Fact]
    public void Newer_readback_is_distinguished_from_the_accepted_version()
    {
        var api = new PolicyApi { AdvanceReadbackVersion = true };
        var page = RenderPage(api);
        PrepareDirectoryChange(page, api);
        Confirm(page);
        page.WaitForAssertion(() => _dialogs.Find("[data-testid='policy-readback-warning']").TextContent.Should().Contain("version 3").And.Contain("differs from accepted version 2"));
        _dialogs.Find("[data-testid='policy-success']").TextContent.Should().Contain("accepted at version 2").And.NotContain("readback and retained change audit match");
        api.Confirmed.Should().ContainSingle();
    }

    [Fact]
    public void Missing_accepted_change_audit_is_not_reported_as_verified_readback()
    {
        var api = new PolicyApi { OmitAcceptedAudit = true };
        var page = RenderPage(api);
        PrepareDirectoryChange(page, api);
        Confirm(page);
        page.WaitForAssertion(() => _dialogs.Find("[data-testid='policy-readback-warning']").TextContent.Should().Contain("audit for accepted version 2 is not present"));
        _dialogs.Find("[data-testid='policy-success']").TextContent.Should().Contain("accepted at version 2").And.NotContain("retained change audit match");
        api.Confirmed.Should().ContainSingle();
    }

    [Fact]
    public void Owned_credential_admission_uses_the_exact_target_profile_and_preserves_unverified_runtime_status()
    {
        var api = new PolicyApi();
        var page = RenderPage(api);
        page.WaitForAssertion(() => page.FindAll("[data-testid='policy-list']").Should().HaveCount(1));
        page.Find($"[data-testid='policy-inspect-{api.Policy.PolicyId:D}']").Click();
        page.WaitForAssertion(() => _dialogs.FindAll("[data-testid='selected-policy']").Should().HaveCount(1));
        _dialogs.Find("[data-testid='policy-admission-credential']").Change("owned-credential-id");
        _dialogs.Find("[data-testid='policy-admission-check']").Click();
        page.WaitForAssertion(() => _dialogs.Find("[data-testid='policy-admission-result']").TextContent.Should().Contain("conditional_runtime_unverified").And.Contain("not_checked"));
        api.AdmissionTarget.Should().Be(new ReviewedMcpAccessTarget(42, "netratel_job_runs", "start", api.Policy.TargetSelector.AgentId, 1));
        api.AdmissionCredential.Should().Be("owned-credential-id");
        api.Confirmed.Should().BeEmpty();
    }

    [Fact]
    public async Task Inspect_opens_a_loading_dialog_before_the_detail_request_completes()
    {
        var api = new PolicyApi { DetailPending = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var page = RenderPage(api);
        page.WaitForElement($"[data-testid='policy-inspect-{api.Policy.PolicyId:D}']");
        var inspecting = page.Find($"[data-testid='policy-inspect-{api.Policy.PolicyId:D}']").ClickAsync(new());
        await _dialogs.WaitForAssertionAsync(() => _dialogs.FindAll("[data-testid='policy-dialog-loading']").Should().ContainSingle());
        _dialogs.FindAll("[role='dialog']").Should().ContainSingle();
        page.FindAll("[data-testid='selected-policy']").Should().BeEmpty();
        api.DetailCalls.Should().Be(1);
        api.DetailPending.SetResult(api.Policy);
        await inspecting;
        await _dialogs.WaitForAssertionAsync(() => _dialogs.Find("[data-testid='selected-policy']").TextContent.Should().Contain(api.Policy.Name));
        page.Markup.Should().Contain("v1").And.NotContain("v@context.Version");
        _dialogs.FindAll("[data-testid='policy-dialog-loading']").Should().BeEmpty();
        api.Confirmed.Should().BeEmpty();
    }

    [Fact]
    public async Task Search_lifecycle_and_target_filters_page_the_authorized_list_and_reset_to_the_first_page()
    {
        var api = new PolicyApi();
        api.ExtraPolicies = Enumerable.Range(1, 24).Select(index => api.Policy with
        {
            PolicyId = Guid.NewGuid(), Name = $"Reviewed grant {index:D2}",
            LifecycleState = index % 2 == 0 ? McpOperatorPolicyLifecycleState.Revoked : McpOperatorPolicyLifecycleState.Active,
            TargetSelector = index % 3 == 0 ? new(McpOperatorTargetSelectorKind.ClientTag, 42, ClientTag: "qa-scope") : api.Policy.TargetSelector
        }).ToArray();
        var page = RenderPage(api);
        page.WaitForAssertion(() => page.Find("[data-testid='policy-result-count']").TextContent.Should().Contain("25 of 25"));
        var table = page.FindComponent<MudTable<McpOperatorPolicy>>();
        page.FindAll("[data-testid^='policy-inspect-']").Should().HaveCount(10);
        await page.InvokeAsync(() => table.Instance.NavigateTo(1));
        page.FindAll("[data-testid^='policy-inspect-']").Should().HaveCount(10);
        page.Find("[data-testid='policy-search']").Input("reviewed GRANT 23");
        page.WaitForAssertion(() => page.FindAll("[data-testid^='policy-inspect-']").Should().ContainSingle());
        table.Instance.CurrentPage.Should().Be(0);
        page.Find("[data-testid='policy-result-count']").TextContent.Should().Contain("1 of 25");
        page.Find("[data-testid='policy-search']").Input("");
        await page.InvokeAsync(() => page.FindComponent<MudSelect<McpOperatorPolicyLifecycleState?>>().Instance.ValueChanged.InvokeAsync(McpOperatorPolicyLifecycleState.Revoked));
        await page.InvokeAsync(() => page.FindComponent<MudSelect<McpOperatorTargetSelectorKind?>>().Instance.ValueChanged.InvokeAsync(McpOperatorTargetSelectorKind.ClientTag));
        page.WaitForAssertion(() => page.Find("[data-testid='policy-result-count']").TextContent.Should().Contain("4 of 25"));
        page.FindAll("[data-testid^='policy-inspect-']").Should().HaveCount(4);
        page.Find("[data-testid='policy-search']").Input("no matching identity");
        page.WaitForAssertion(() => page.FindAll("[data-testid^='policy-inspect-']").Should().BeEmpty());
        page.Find("[data-testid='policy-reset-filters']").Click();
        await page.InvokeAsync(() => table.Instance.SetRowsPerPage(25));
        page.WaitForAssertion(() => page.FindAll("[data-testid^='policy-inspect-']").Should().HaveCount(25));
        api.ListCalls.Should().Be(1);
        api.Confirmed.Should().BeEmpty();
    }

    [Fact]
    public async Task Closing_inspect_keeps_search_filters_page_and_selected_row_without_a_stale_preview()
    {
        var api = new PolicyApi();
        api.ExtraPolicies = Enumerable.Range(1, 24).Select(index => api.Policy with { PolicyId = Guid.NewGuid(), Name = $"Reviewed grant {index:D2}" }).ToArray();
        var page = RenderPage(api);
        page.WaitForElement("[data-testid='policy-search']");
        page.Find("[data-testid='policy-search']").Input("reviewed");
        await page.InvokeAsync(() => page.FindComponent<MudSelect<McpOperatorPolicyLifecycleState?>>().Instance.ValueChanged.InvokeAsync(McpOperatorPolicyLifecycleState.Active));
        await page.InvokeAsync(() => page.FindComponent<MudSelect<McpOperatorTargetSelectorKind?>>().Instance.ValueChanged.InvokeAsync(McpOperatorTargetSelectorKind.ExactAgent));
        var table = page.FindComponent<MudTable<McpOperatorPolicy>>();
        await page.InvokeAsync(() => table.Instance.NavigateTo(1));
        var selected = api.ExtraPolicies[12];
        page.Find($"[data-testid='policy-inspect-{selected.PolicyId:D}']").Click();
        _dialogs.WaitForElement("[data-testid='selected-policy']");
        _dialogs.Find("[data-testid='policy-edit']").Click();
        _dialogs.Find("[data-testid='policy-preview']").Click();
        _dialogs.WaitForElement("[data-testid='policy-preview-review']");
        _dialogs.Find("[data-testid='policy-dialog-close']").Click();
        _dialogs.WaitForAssertion(() => _dialogs.FindAll("[role='dialog']").Should().BeEmpty());
        table.Instance.CurrentPage.Should().Be(1);
        page.Find("[data-testid='policy-search']").GetAttribute("value").Should().Be("reviewed");
        page.FindComponent<MudSelect<McpOperatorPolicyLifecycleState?>>().Instance.GetState(select => select.Value).Should().Be(McpOperatorPolicyLifecycleState.Active);
        page.FindComponent<MudSelect<McpOperatorTargetSelectorKind?>>().Instance.GetState(select => select.Value).Should().Be(McpOperatorTargetSelectorKind.ExactAgent);
        page.Find(".policy-row-selected").TextContent.Should().Contain(selected.Name);
        page.Find($"[data-testid='policy-inspect-{selected.PolicyId:D}']").Click();
        _dialogs.WaitForElement("[data-testid='selected-policy']");
        _dialogs.FindAll("[data-testid='policy-preview-review'], [data-testid='policy-editor']").Should().BeEmpty();
        _dialogs.Find(".policy-dialog-body").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Escape" });
        _dialogs.WaitForAssertion(() => _dialogs.FindAll("[role='dialog']").Should().BeEmpty());
        api.Confirmed.Should().BeEmpty();
    }

    [Fact]
    public void Accepted_receipt_remains_visible_after_closing_and_refresh_only_reads()
    {
        var api = new PolicyApi();
        var page = RenderPage(api);
        PrepareDirectoryChange(page, api);
        Confirm(page);
        _dialogs.WaitForAssertion(() => _dialogs.Find("[data-testid='policy-success']").TextContent.Should().Contain("accepted at version 2"));
        _dialogs.Find("[data-testid='policy-dialog-close']").Click();
        page.WaitForAssertion(() => page.Find("[data-testid='policy-success']").TextContent.Should().Contain("accepted at version 2"));
        page.Find("[data-testid='policy-refresh-accepted']").Click();
        page.WaitForAssertion(() => api.DetailCalls.Should().Be(3));
        api.Confirmed.Should().ContainSingle();
        _dialogs.FindAll("[role='dialog']").Should().BeEmpty();
    }

    private IRenderedComponent<OperatorPolicies> RenderPage(PolicyApi api)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices(options => options.PopoverOptions.CheckForPopoverProvider = false);
        Services.AddSingleton<IMcpOperatorPolicyApiService>(api);
        SetRendererInfo(new RendererInfo("Server", true));
        _dialogs = Render<MudDialogProvider>();
        return Render<OperatorPolicies>();
    }

    private void PrepareDirectoryChange(IRenderedComponent<OperatorPolicies> page, PolicyApi api)
    {
        page.WaitForAssertion(() => page.FindAll("[data-testid='policy-list']").Should().HaveCount(1));
        page.Find($"[data-testid='policy-inspect-{api.Policy.PolicyId:D}']").Click();
        page.WaitForAssertion(() => _dialogs.FindAll("[data-testid='selected-policy']").Should().HaveCount(1));
        _dialogs.Find("[data-testid='policy-edit']").Click();
        _dialogs.Find("[data-testid='policy-working-directories']").Change("/tmp\n/tmp/example");
        _dialogs.Find("[data-testid='policy-preview']").Click();
        page.WaitForAssertion(() => _dialogs.FindAll("[data-testid='policy-preview-review']").Should().HaveCount(1));
    }

    private void Confirm(IRenderedComponent<OperatorPolicies> page)
    {
        _dialogs.Find("[data-testid='policy-confirm-checkbox']").Change(true);
        _dialogs.Find("[data-testid='policy-confirm']").Click();
    }

    private sealed class PolicyApi : IMcpOperatorPolicyApiService
    {
        public const string PlanSecret = "synthetic-private-plan-credential";
        public const string IdempotencySecret = "synthetic-private-idempotency-credential";
        public bool Authorized { get; init; } = true;
        public Exception? ConfirmFailure { get; set; }
        public Exception? ReadbackFailure { get; set; }
        public TaskCompletionSource<McpOperatorPolicy>? DetailPending { get; set; }
        public IReadOnlyList<McpOperatorPolicy> ExtraPolicies { get; set; } = [];
        public List<(McpOperatorEnvironment Environment, int? Tenant)> ListScopes { get; } = [];
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
        {
            ListCalls++;
            ListScopes.Add((environment, tenantId));
            return Task.FromResult(new McpOperatorPolicyPage(new[] { Policy }.Concat(ExtraPolicies)
                .Where(policy => policy.Environment == environment && (tenantId is null || policy.TargetSelector.TenantId == tenantId)).ToArray()));
        }
        public Task<McpOperatorPolicy> GetAsync(Guid policyId, CancellationToken cancellationToken = default)
        {
            DetailCalls++;
            if (DetailPending is { } pending) return pending.Task;
            var selected = new[] { Policy }.Concat(ExtraPolicies).Single(policy => policy.PolicyId == policyId);
            return _accepted is not null && ReadbackFailure is { } failure
                ? Task.FromException<McpOperatorPolicy>(failure) : Task.FromResult(selected);
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
